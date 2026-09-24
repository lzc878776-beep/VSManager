using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VSManager
{
    /// <summary>
    /// 任务流水归档的接收方（默认写入 ArchiveRoot\tasks\*.jsonl）。
    /// Receiver of task journal events (by default written to ArchiveRoot\tasks\*.jsonl).
    /// </summary>
    public interface ITaskArchiveSink
    {
        void TaskEvent(QueuedTask task, string evt);
    }

    /// <summary>默认归档实现：转发到 <see cref="Archive"/>。/ Default sink: forwards to <see cref="Archive"/>.</summary>
    public sealed class ArchiveTaskSink : ITaskArchiveSink
    {
        public static readonly ArchiveTaskSink Instance = new ArchiveTaskSink();
        public void TaskEvent(QueuedTask task, string evt) => Archive.TaskEvent(task, evt);
    }

    /// <summary>
    /// 任务清单：持久化到 %APPDATA%\VSManager\tasks.json（上一版本保存在 tasks.json.bak），只在界面线程修改。
    /// 默认保留全部历史；保存失败时保留内存中的清单、记录日志并由界面提示，之后自动重试。
    /// Task list persisted to %APPDATA%\VSManager\tasks.json (previous version in tasks.json.bak); modified on the UI thread only.
    /// Keeps the full history by default; when saving fails the in-memory list is kept, the error is logged and shown in
    /// the UI, and saving is retried automatically.
    /// </summary>
    public sealed class TaskQueue
    {
        private readonly List<QueuedTask> _items = new List<QueuedTask>();
        private readonly AppSettings _settings;
        private readonly ITaskStore _store;
        private readonly ITaskArchiveSink _archive;
        private readonly Func<DateTime> _clock;
        private readonly object _saveLock = new object();
        private int _nextId = 1;
        private bool _dirty;
        private DateTime _lastSaveAttempt;
        /// <summary>上次归档时各任务的状态，用于在 Commit 时找出新增 / 变化 / 移除的任务。/ Last archived state per task, used to detect changes on Commit.</summary>
        private readonly Dictionary<int, QueuedTask> _archived = new Dictionary<int, QueuedTask>();

        public event Action Changed;

        public static string FilePath => Path.Combine(AppPaths.DataFolder, "tasks.json");
        public static string LogPath => AppLog.PathOf(AppLog.TasksFile);

        public IReadOnlyList<QueuedTask> Items => _items;
        public Func<string, WorktreeInfo> ResolveWorktree { get; set; }

        /// <summary>实时读取失败前序策略；修改后下轮调度生效。/ Live failed-predecessor policy; changes apply on the next dispatch round.</summary>
        public bool SkipFailedPredecessors
        {
            get => _settings.SkipFailedPredecessors;
            set => _settings.SkipFailedPredecessors = value;
        }

        public QueuedTask BlockingTask(QueuedTask task) => TaskStateMachine.BlockingTask(_items, task, SkipFailedPredecessors);
        public List<QueuedTask> NextToDispatch(DateTime now) => TaskStateMachine.NextToDispatch(_items, now, SkipFailedPredecessors);
        public string StatusText(QueuedTask task, DateTime now) => TaskStateMachine.StatusText(task, now, _items, SkipFailedPredecessors);

        /// <summary>最近一次保存失败的原因；保存成功后为 null。/ Reason of the last failed save; null after a successful save.</summary>
        public string SaveError { get; private set; }

        /// <summary>启动时读取任务记录遇到的问题（文件损坏、部分记录无法识别等），没有问题时为 null。/ Problems found while loading; null when none.</summary>
        public string LoadWarning { get; private set; }

        public TaskQueue(AppSettings settings) : this(settings, new JsonTaskStore(FilePath), ArchiveTaskSink.Instance, null)
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { if (_dirty) Save(); };
        }

        /// <summary>
        /// 可替换存储、归档与时钟的构造函数（用于单元测试）。
        /// Constructor with a replaceable store, archive sink and clock (for unit tests).
        /// </summary>
        public TaskQueue(AppSettings settings, ITaskStore store, ITaskArchiveSink archive, Func<DateTime> clock)
        {
            _settings = settings;
            _store = store;
            _archive = archive;
            _clock = clock ?? (() => DateTime.Now);
            Load();
            foreach (var t in _items) _archived[t.Id] = t.Clone();
            if (ReconcileWorktreeBatches()) Commit();
        }

        private void Load()
        {
            var problems = new List<string>();
            _items.AddRange(_store.Load(problems));

            _interrupted.AddRange(_items.Where(t => t.Status == QueueStatus.Sending));
            foreach (var t in _items) TaskStateMachine.RecoverAfterRestart(t);

            // 编号去重：缺失或重复的编号重新分配，保证之后新建的任务编号不与历史重复
            // De-duplicate ids: missing or repeated ids are renumbered so that new tasks never reuse a historical id
            int max = Math.Max(_items.Count == 0 ? 0 : _items.Max(t => t.Id), Math.Max(0, _settings.TaskNextId - 1));
            var seen = new HashSet<int>();
            int fixedIds = 0;
            foreach (var t in _items.OrderBy(t => t.Created))
                if (t.Id <= 0 || !seen.Add(t.Id)) { t.Id = ++max; seen.Add(t.Id); fixedIds++; }
            if (fixedIds > 0) problems.Add(fixedIds + " 条任务的编号缺失或重复，已重新编号");
            _nextId = max + 1;
            _items.Sort((a, b) => a.Id.CompareTo(b.Id));

            if (problems.Count > 0)
            {
                LoadWarning = string.Join("；", problems);
                Log("读取任务记录：" + LoadWarning + "（共恢复 " + _items.Count + " 条）");
                _dirty = true;
            }
        }

        private readonly List<QueuedTask> _interrupted = new List<QueuedTask>();

        /// <summary>
        /// 读取时处于「发送中」的任务（上次退出时正在发送，已按原规则恢复为排队）。
        /// Tasks that were "sending" when loaded (being sent at the last exit; recovered to waiting as before).
        /// </summary>
        public IReadOnlyList<QueuedTask> InterruptedSends => _interrupted;

        /// <summary>
        /// 异常退出后重启时调用：把仍在排队的「上次正在发送」任务标记为失败（可能已送达 VS），避免重复发布；返回处理条数。
        /// Call after a restart from an abnormal exit: marks the still-waiting "was sending" tasks as failed (they may have
        /// reached VS) to avoid publishing them twice; returns how many were changed.
        /// </summary>
        public int PauseInterruptedSends(string reason)
        {
            int n = 0;
            foreach (var t in _interrupted)
                if (t.Status == QueueStatus.Waiting && _items.Contains(t)) { TaskStateMachine.Fail(t, reason, _clock()); n++; }
            _interrupted.Clear();
            if (n > 0) Commit();
            return n;
        }

        /// <summary>下一个将分配的任务编号。/ The next task id to be assigned.</summary>
        public int NextId => _nextId;

        public QueuedTask Find(int id) => _items.FirstOrDefault(t => t.Id == id);

        public QueuedTask Add(string vsKey, string vsName, string text, string source) =>
            Add(vsKey, vsName, text, source, QueueStatus.Waiting, null);

        /// <summary>
        /// 暂存任务：目标解决方案未打开，状态为「等待目标 VS」，<paramref name="vsKey"/> 为解决方案完整路径。
        /// Parks a task whose target solution is not open: status "waiting for target VS", <paramref name="vsKey"/> is the full solution path.
        /// </summary>
        public QueuedTask AddParked(string vsKey, string alias, string text, string source) =>
            Add(vsKey, alias, text, source, QueueStatus.WaitingVs, alias);

        /// <summary>
        /// 添加带附件的任务；只有文字与附件（按哈希）都相同才复用已有任务。<paramref name="parked"/> 为 true 时暂存。
        /// Adds a task with attachments; an existing task is reused only when both the text and the attachments (by hash) match.
        /// Parks it when <paramref name="parked"/> is true.
        /// </summary>
        public QueuedTask Add(string vsKey, string vsName, string text, string source, AttachmentRef[] attachments, bool parked = false) =>
            Add(vsKey, vsName, text, source, parked ? QueueStatus.WaitingVs : QueueStatus.Waiting, parked ? vsName : null, attachments);

        /// <summary>两组附件是否相同（按哈希与文件名）。/ Whether two attachment sets match (by hash and file name).</summary>
        public static bool SameAttachments(AttachmentRef[] a, AttachmentRef[] b)
        {
            string Key(AttachmentRef[] x) => string.Join("|", (x ?? new AttachmentRef[0]).Where(r => r != null)
                .Select(r => (r.Sha256 ?? "") + ":" + (r.Name ?? "")).OrderBy(s => s, StringComparer.Ordinal));
            return Key(a) == Key(b);
        }

        private QueuedTask Add(string vsKey, string vsName, string text, string source, string status, string target, AttachmentRef[] attachments = null)
        {
            if (attachments != null && attachments.Length == 0) attachments = null;
            var duplicate = TaskStateMachine.FindActiveDuplicate(_items.Where(i => SameAttachments(i.Attachments, attachments)), vsKey, text);
            if (duplicate != null) return duplicate;
            var t = new QueuedTask
            {
                Id = _nextId++, VsKey = vsKey, VsName = vsName, Text = text, Source = source,
                Status = status, Created = _clock(), Target = target, Attachments = attachments,
                Worktree = ResolveWorktree?.Invoke(vsKey)?.Clone()
                    ?? _items.LastOrDefault(x => string.Equals(x.VsKey, vsKey, StringComparison.OrdinalIgnoreCase) && x.Worktree != null)?.Worktree.Clone()
            };
            ReplaceFailed(t);
            _items.Add(t);
            // 编号计数同时记在设置中，清除历史后新任务也不会复用旧编号
            // The id counter is also stored in the settings so ids are never reused, even after the history is cleared
            _settings.TaskNextId = _nextId;
            _settings.Save();
            Commit();
            return t;
        }

        private bool ReplaceFailed(QueuedTask t)
        {
            var matches = ResentTaskMatcher.Find(_items.Where(x => !x.IsWorktreeMerge), t);
            foreach (var kept in matches.Kept)
                Log($"任务 #{t.Id} 保留失败任务 #{kept.Task.Id} / Task #{t.Id} retains failed task #{kept.Task.Id}: {kept.Reason}");
            if (matches.Hide.Count == 0) return false;
            t.Replaces = (t.Replaces ?? new int[0]).Concat(matches.Hide.Select(x => x.Id)).Distinct().ToArray();
            return true;
        }

        /// <summary>按当前策略阻塞该任务的同目标任务数。/ Number of same-target tasks blocking this task under the current policy.</summary>
        public int Ahead(QueuedTask task) => TaskStateMachine.BlockingTasks(_items, task, SkipFailedPredecessors).Count();

        public bool Remove(int id)
        {
            var t = Find(id);
            if (!TaskStateMachine.CanRemove(t) || t.Worktree != null) return false;
            _items.Remove(t);
            Commit();
            return true;
        }

        /// <summary>提交修改：裁剪历史（若配置了上限）、写归档流水、保存并通知界面。/ Commits changes: trims history (if limited), archives, saves and notifies.</summary>
        public void Commit()
        {
            ReconcileWorktreeBatches();
            // 历史上限：TaskHistoryLimit <= 0 表示保留全部（默认）/ History limit: TaskHistoryLimit <= 0 keeps everything (default)
            int limit = _settings.TaskHistoryLimit;
            if (limit > 0)
            {
                // 保留重发关系，防止裁剪后严格模式再次被旧失败阻塞。/ Preserve resend links so trimming cannot resurrect a strict-mode failure barrier.
                var failures = _items.Where(t => t.Status == QueueStatus.Failed).ToList();
                var old = _items.Where(t => t.Worktree == null && (t.Status == QueueStatus.Done || t.Status == QueueStatus.Cancelled)
                        && !(t.Replaces != null && failures.Any(f => t.Replaces.Contains(f.Id) && ResentTaskMatcher.SameTarget(f, t))))
                    .OrderByDescending(t => t.Finished ?? t.Created).Skip(limit).ToList();
                foreach (var t in old) _items.Remove(t);
            }
            ArchiveChanges();
            _dirty = true;
            Save();
            Changed?.Invoke();
        }

        /// <summary>把自上次提交以来新增、状态变化、被移除的任务追加到任务流水归档。/ Archives tasks added, changed or removed since the last commit.</summary>
        private void ArchiveChanges()
        {
            try
            {
                var alive = new HashSet<int>();
                foreach (var t in _items)
                {
                    alive.Add(t.Id);
                    _archived.TryGetValue(t.Id, out var prev);
                    string evt = ArchiveEventFor(prev, t);
                    if (evt == null) continue;
                    _archive.TaskEvent(t, evt);
                    _archived[t.Id] = t.Clone();
                }
                foreach (var id in _archived.Keys.Where(k => !alive.Contains(k)).ToList())
                {
                    _archive.TaskEvent(_archived[id], "removed");
                    _archived.Remove(id);
                }
            }
            catch (Exception ex) { Log("归档任务流水失败：" + ex.Message); }
        }

        /// <summary>
        /// 归档事件名：created / retry / update / 新状态；没有变化时返回 null。
        /// Archive event name: created / retry / update / the new status; null when nothing changed.
        /// </summary>
        internal static string ArchiveEventFor(QueuedTask prev, QueuedTask t)
        {
            if (prev == null) return "created";
            if (prev.Status == t.Status && prev.Attempts == t.Attempts && prev.Result == t.Result && prev.Error == t.Error && prev.Text == t.Text && prev.VsKey == t.VsKey) return null;
            if (prev.Status == QueueStatus.WaitingVs && t.Status == QueueStatus.Waiting) return "target_opened";
            if (t.Status == QueueStatus.Waiting && prev.Status != QueueStatus.Waiting) return "retry";
            if (prev.Status == t.Status) return "update";
            return t.Status;
        }

        /// <summary>保存失败后定期重试（由界面计时器调用）。/ Retries a failed save periodically (called by a UI timer).</summary>
        public void RetrySaveIfNeeded()
        {
            if (!_dirty || SaveError == null || (_clock() - _lastSaveAttempt).TotalSeconds < 10) return;
            if (Save()) Changed?.Invoke();
        }

        /// <summary>立即写盘（退出程序时调用）。失败不会清空内存中的清单。/ Saves now (called on exit). A failure never clears the in-memory list.</summary>
        public bool Save()
        {
            string error;
            lock (_saveLock)
            {
                _lastSaveAttempt = _clock();
                try { error = _store.Save(_items); }
                catch (Exception ex) { error = ex.GetType().Name + "：" + ex.Message; }
                bool wasFailing = SaveError != null;
                SaveError = error;
                if (error == null)
                {
                    _dirty = false;
                    if (wasFailing) Log("保存已恢复正常（" + _items.Count + " 条）");
                }
                else if (!wasFailing) Log("保存任务清单失败：" + error + "（内存中的 " + _items.Count + " 条任务已保留，将自动重试）");
            }
            return error == null;
        }

        private bool ReconcileWorktreeBatches()
        {
            bool changed = false;
            foreach (var t in _items.Where(t => t.Worktree != null && !t.IsWorktreeMerge && t.Status == QueueStatus.Done && !t.WorktreeCounted))
            {
                t.WorktreeCounted = true;
                changed = true;
            }
            foreach (var group in _items.Where(t => t.Worktree != null).GroupBy(t => t.Worktree.Root, StringComparer.OrdinalIgnoreCase).ToList())
            {
                int batches = group.Count(t => t.WorktreeCounted && !t.IsWorktreeMerge) / 5;
                for (int batch = 1; batch <= batches; batch++)
                {
                    if (group.Any(t => t.IsWorktreeMerge && t.WorktreeBatch == batch)) continue;
                    var last = group.Where(t => !t.IsWorktreeMerge).OrderBy(t => t.Id).Last();
                    _items.Add(new QueuedTask
                    {
                        Id = _nextId++, VsKey = last.VsKey, VsName = last.VsName, Target = last.Target,
                        Text = "Worktree 本地推送 / Local integration — batch " + batch + ". " + WorktreeInfo.MergeInstructions,
                        Source = "AI", Created = _clock(), Status = QueueStatus.Waiting,
                        Worktree = last.Worktree.Clone(), IsWorktreeMerge = true, WorktreeBatch = batch
                    });
                    changed = true;
                }
            }
            if (changed) { _settings.TaskNextId = _nextId; _settings.Save(); }
            return changed;
        }

        private static void Log(string text) => AppLog.Write(AppLog.TasksFile, text);
    }
}
