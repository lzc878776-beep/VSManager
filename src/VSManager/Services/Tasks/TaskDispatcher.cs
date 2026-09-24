using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 任务调度器依赖的宿主能力（由主窗口实现）：查找 VS、判断能否发布、发送消息、读取回复、状态提示与通知 AI 助手。
    /// 单元测试可用模拟实现替换。
    /// Host capabilities the task dispatcher depends on (implemented by the main window): find a VS, check whether it can
    /// take a task, send a message, read the reply, show status and notify the AI assistant. Unit tests use a fake.
    /// </summary>
    public interface ITaskDispatchHost
    {
        VsInstance FindVs(string vsKey);
        /// <summary>该 VS 当前能否接收新任务。/ Whether the VS can take a new task now.</summary>
        bool CanDispatch(VsInstance v);
        string NameOf(VsInstance v);
        /// <summary>是否有消息正在发送（同一时刻只发送一条）。/ Whether a message is being sent (one at a time).</summary>
        bool IsSending { get; }
        /// <summary>启动后多久才开始跟踪执行中的任务（等待 Copilot 状态稳定）。/ When tracking of running tasks starts after launch.</summary>
        DateTime TrackingReadyAt { get; }
        /// <summary>发送到 Copilot，返回结果文字（以「已发送」开头表示送达）。/ Sends to Copilot; a result starting with "已发送" means delivered.</summary>
        Task<string> SendAsync(VsInstance v, string text);
        /// <summary>读取 Copilot 最新回复文本，读取失败返回 null。/ Reads the latest Copilot answer; null when it cannot be read.</summary>
        Task<string> ReadAnswerAsync(VsInstance v, QueuedTask expectedTask);
        void SetStatus(string text);
        void LogEvent(string vsName, string text);
        void NotifyAgent(string title, string body);
        /// <summary>每轮调度结束后调用：是否仍有未完成任务（用于开关调度计时器）。/ Called after each round: whether unfinished tasks remain.</summary>
        void QueueActivityChanged(bool anyActive);
        /// <summary>
        /// 查找暂存任务的目标 VS（按解决方案路径 / 登记表别名），尚未打开时返回 null。
        /// Finds the target VS of a parked task (by solution path / registry alias); null while it is not open.
        /// </summary>
        VsInstance FindTargetVs(QueuedTask t);
        /// <summary>目标 VS 打开后延迟多久再推送暂存任务。/ Delay after the target VS opens before parked tasks are pushed.</summary>
        TimeSpan TargetSettleDelay { get; }
        /// <summary>暂存 / 推送等新状态的弹窗与语音播报（中文与英文文案）。/ Notification and voice announcement of new states such as parked / pushed (Chinese and English text).</summary>
        void AnnounceTask(QueuedTask t, string zh, string en);
    }

    /// <summary>
    /// 可选宿主能力：发送带附件的任务（图片粘贴到 Copilot、文本文件内联、其他文件发送路径）。未实现时带附件的任务按普通文字发送。
    /// Optional host capability: sends a task with attachments (images pasted into Copilot, text files inlined, other files as
    /// paths). Without it, tasks with attachments are sent as plain text.
    /// </summary>
    public interface ITaskAttachmentDispatchHost
    {
        /// <summary>返回结果文字（以「已发送」开头表示任务文字已送达）。/ Returns the result (starting with "已发送" when the task text was delivered).</summary>
        Task<string> SendTaskAsync(VsInstance v, QueuedTask t);
    }

    /// <summary>
    /// 任务调度器：跟踪执行中的任务，并把每个空闲 VS 最早排队的任务发布出去；失败、完成、取消、重试也在这里处理。
    /// 状态流转统一交给 <see cref="TaskStateMachine"/>，只在界面线程调用。
    /// Task dispatcher: tracks running tasks and publishes the oldest waiting task of every idle VS; also handles failure,
    /// completion, cancellation and retry. Status changes go through <see cref="TaskStateMachine"/>. UI thread only.
    /// </summary>
    public sealed class TaskDispatcher
    {
        private readonly TaskQueue _tasks;
        private readonly ITaskDispatchHost _host;
        private readonly Func<DateTime> _clock;
        private readonly IWorktreeTaskService _worktrees;
        private bool _pumping;
        private readonly HashSet<QueuedTask> _finishing = new HashSet<QueuedTask>();
        private readonly HashSet<QueuedTask> _integrating = new HashSet<QueuedTask>();
        private readonly Dictionary<QueuedTask, (TimeSpan? Duration, string Token)> _pendingCompletions
            = new Dictionary<QueuedTask, (TimeSpan?, string)>();

        public const string WaitingForStart = "等待手动开始：请点击任务清单「开始流程 / Start」/ Waiting for manual start: click Start in the task list";
        public bool IsStarted { get; private set; }

        /// <summary>仅本次会话有效；只能由用户开始，不持久化。/ User opt-in for this session only; never persisted.</summary>
        public void Start()
        {
            if (IsStarted) return;
            IsStarted = true;
            _host.SetStatus("任务流程已启动 / Task workflow started");
            _host.QueueActivityChanged(_tasks.Items.Any(x => QueueStatus.Active(x.Status)));
            Pump();
        }

        public TaskDispatcher(TaskQueue tasks, ITaskDispatchHost host, Func<DateTime> clock = null, IWorktreeTaskService worktrees = null)
        {
            _tasks = tasks;
            _host = host;
            _clock = clock ?? (() => DateTime.Now);
            _worktrees = worktrees;
        }

        /// <summary>触发一轮调度（不等待）；异常只显示在状态栏。/ Starts a round without awaiting; errors only go to the status bar.</summary>
        public async void Pump()
        {
            try { await PumpAsync(); }
            catch (Exception ex) { _host.SetStatus("任务清单调度出错：" + ex.Message); }
        }

        /// <summary>执行一轮调度。/ Runs one dispatch round.</summary>
        public async Task PumpAsync()
        {
            if (!IsStarted)
            {
                _host.QueueActivityChanged(false);
                return;
            }
            if (_pumping) return;
            _pumping = true;
            try
            {
                var now = _clock();
                if (now >= _host.TrackingReadyAt)
                {
                    foreach (var pending in _pendingCompletions.ToArray())
                    {
                        var task = pending.Key;
                        var completion = pending.Value;
                        if (task.Status != QueueStatus.Running || _tasks.Find(task.Id) != task || task.CompletionToken != completion.Token)
                        {
                            _pendingCompletions.Remove(task);
                            continue;
                        }
                        var target = _host.FindVs(task.VsKey);
                        if (target == null || target.Copilot == CopilotState.Busy || !_host.CanDispatch(target)) continue;
                        _pendingCompletions.Remove(task);
                        await FinishAsync(task, target, completion.Duration);
                    }
                }
                PushParked(now);
                foreach (var t in _tasks.Items.Where(x => x.Status == QueueStatus.Running).ToList())
                {
                    if (now < _host.TrackingReadyAt) break;
                    if (_finishing.Contains(t)) continue;
                    var v = _host.FindVs(t.VsKey);
                    switch (TaskStateMachine.CheckRunning(t, v != null, v?.Copilot ?? CopilotState.Unknown, () => _host.CanDispatch(v), now))
                    {
                        case RunningVerdict.VsClosed: Fail(t, TaskStateMachine.VsClosedError); break;
                        case RunningVerdict.SawBusy: t.SawBusy = true; break;
                        case RunningVerdict.Unconfirmed: await FinishAsync(t, v, null); break;
                    }
                }

                foreach (var t in _tasks.NextToDispatch(now))
                {
                    if (_host.IsSending) break;
                    var v = _host.FindVs(t.VsKey);
                    if (t.Worktree != null) v = _host.FindTargetVs(t);
                    if (v == null || !_host.CanDispatch(v) || t.Status != QueueStatus.Waiting
                        || _tasks.Find(t.Id) != t || _tasks.BlockingTask(t) != null) continue;
                    if (t.Worktree != null && !SolutionMatcher.SamePath(v.SolutionPath, t.Worktree.SolutionPath)) continue;
                    var skipped = _tasks.Items.Where(x => x.Id < t.Id && x.Status == QueueStatus.Failed
                        && ResentTaskMatcher.SameTarget(x, t)).OrderBy(x => x.Id).Select(x => x.Id).ToArray();
                    TaskStateMachine.BeginSend(t, _host.NameOf(v));
                    if (t.Worktree != null) t.VsKey = v.Key;
                    _tasks.Commit();
                    _host.LogEvent(t.VsName, $"任务清单：发布任务 #{t.Id}（第 {t.Attempts} 次）");
                    string r;
                    try
                    {
                        if (t.IsWorktreeMerge && t.Worktree == null)
                            throw new InvalidOperationException("缺少工作树元数据 / Missing worktree metadata");
                        if (t.Worktree != null)
                        {
                            if (_tasks.SaveError != null) throw new InvalidOperationException("任务未持久化，工作树操作已暂停 / Task persistence failed");
                            if (_worktrees == null) throw new InvalidOperationException("Worktree service unavailable");
                            if (t.IsWorktreeMerge)
                            {
                                if (!await _worktrees.IntegrateAsync(t.Worktree))
                                {
                                    t.Status = QueueStatus.Running;
                                    t.Started = _clock();
                                    t.Result = "已验证主项目本地快进；未推送远程 / Verified local fast-forward; no remote push";
                                    TaskStateMachine.Complete(t, _clock());
                                    _tasks.Commit();
                                    _host.NotifyAgent($"Worktree 合并任务 #{t.Id} 已完成 / Integration completed", t.Result);
                                    continue;
                                }
                            }
                            else await _worktrees.CheckDevelopmentAsync(t.Worktree);
                            if (!SolutionMatcher.SamePath(v.SolutionPath, t.Worktree.SolutionPath) || !_host.CanDispatch(v))
                                throw new InvalidOperationException("目标 VS 已变化 / Target VS changed");
                        }
                        r = t.HasAttachments && _host is ITaskAttachmentDispatchHost attachmentHost
                            ? await attachmentHost.SendTaskAsync(v, t)
                            : await _host.SendAsync(v, TaskStateMachine.DispatchText(t));
                    }
                    catch (Exception ex)
                    {
                        Fail(t, (t.Worktree == null ? "发送异常，送达状态未知，请检查后重试："
                            : "Worktree 任务失败，请检查 Git 和 VS 后重试 / Worktree task failed; inspect Git and VS: ") + ex.Message);
                        continue;
                    }
                    switch (TaskStateMachine.ApplySendResult(t, r, _clock()))
                    {
                        case SendDecision.Delivered:
                            AnnounceDelivery(t, skipped);
                            break;
                        case SendDecision.Fail:
                            AfterFail(t, r);
                            break;
                        default:
                            _tasks.Commit();
                            _host.SetStatus(SendRetryPolicy.IsBlocked(r)
                                ? $"任务清单：#{t.Id} {r}；处理后自动继续，未消耗重试次数"
                                : $"任务清单：#{t.Id} 发送失败（{r}），30 秒后重试");
                            break;
                    }
                }
            }
            finally { _pumping = false; }
            _host.QueueActivityChanged(_tasks.Items.Any(x => QueueStatus.Active(x.Status)));
        }

        /// <summary>
        /// 暂存任务的目标 VS 已打开：转为排队（延迟 <see cref="ITaskDispatchHost.TargetSettleDelay"/> 后发布），记录日志并通知。
        /// The target VS of a parked task has opened: back to waiting (published after <see cref="ITaskDispatchHost.TargetSettleDelay"/>),
        /// logged and announced.
        /// </summary>
        private void PushParked(DateTime now)
        {
            foreach (var t in _tasks.Items.Where(x => x.Status == QueueStatus.WaitingVs).ToList())
            {
                var v = _host.FindTargetVs(t);
                if (v == null) continue;
                string target = t.Target ?? t.VsName;
                var delay = _host.TargetSettleDelay;
                if (!TaskStateMachine.TargetOpened(t, v.Key, _host.NameOf(v), now + delay)) continue;
                _tasks.Commit();
                int secs = (int)Math.Max(0, delay.TotalSeconds);
                _host.LogEvent(t.VsName, $"任务清单：目标「{target}」已打开，暂存任务 #{t.Id} 转为排队，{secs} 秒后自动推送 / target opened, parked task #{t.Id} queued, pushed in {secs} s");
                _host.SetStatus($"任务清单：「{target}」已打开，暂存任务 #{t.Id} 将在 {secs} 秒后自动推送 / \"{target}\" opened, parked task #{t.Id} will be pushed in {secs} s");
                _host.AnnounceTask(t, $"{target}已打开，暂存任务即将自动推送", $"{target} is open, the parked task will be pushed shortly");
                if (t.FromAgent)
                    _host.NotifyAgent($"📋 任务 #{t.Id} 目标已打开 · {t.VsName}",
                        $"[任务通知] 暂存任务 #{t.Id} 的目标「{target}」已打开（VS：{t.VsName}），任务将在 {secs} 秒后自动推送，完成后会再通知你。仅供知悉，无需重复发布。");
            }
        }

        /// <summary>任务失败：记录错误并通知 AI 助手（仅 AI 发布的任务）。/ Fails a task and notifies the AI assistant (AI tasks only).</summary>
        public void Fail(QueuedTask t, string error)
        {
            TaskStateMachine.Fail(t, error, _clock());
            AfterFail(t, error);
        }

        private string FailurePolicyText => "Worktree 合并失败或取消始终阻塞该工作线，请处理后重试同一任务 / Failed or cancelled worktree integration always blocks its lane; resolve and retry the same task. " + (_tasks.SkipFailedPredecessors
            ? "已启用跳过失败前序，后续排队任务可继续；失败记录保留 / Failed predecessors are skipped; queued successors may continue and failure history is retained"
            : "严格模式：未被取代的失败前序会暂停该目标后续任务；失败记录保留 / Strict mode: unsuperseded failures pause this target's successors; failure history is retained");

        private void AnnounceDelivery(QueuedTask t, int[] skipped)
        {
            string status = $"任务清单：#{t.Id} 已发布到「{t.VsName}」/ Task #{t.Id} delivered to \"{t.VsName}\"";
            string ids = string.Join(", ", skipped.Select(id => "@" + id));
            string zh = $"前序 {ids} 失败，已跳过继续";
            string en = $"Predecessor {ids} failed; skipped and continued";
            t.PredecessorNotice = skipped.Length > 0 ? zh + " / " + en : null;
            _tasks.Commit();
            if (skipped.Length > 0)
            {
                string message = $"任务 @{t.Id} / Task @{t.Id}: {t.PredecessorNotice}";
                _host.LogEvent(t.VsName, message);
                AppLog.Write(AppLog.TasksFile, message);
                _host.SetStatus(status + "；" + t.PredecessorNotice);
                _host.AnnounceTask(t, zh, en);
            }
            else _host.SetStatus(status);
        }

        private void AfterFail(QueuedTask t, string error)
        {
            _tasks.Commit();
            _host.SetStatus($"任务清单：#{t.Id}「{t.VsName}」失败 / Task #{t.Id} failed: {error}；{FailurePolicyText}");
            if (t.FromAgent)
                _host.NotifyAgent($"📋 任务 #{t.Id} 失败 · {t.VsName} / Task #{t.Id} failed",
                    $"[任务失败通知] / [Task failure] 任务 #{t.Id} 在「{t.VsName}」发布或执行失败 / Send or execution failed: {error}。" +
                    $"任务内容 / Task: {TextUtil.Clip(t.Text, 300)}。{FailurePolicyText}。" +
                    "请仅汇报失败，不要重复发布已排队任务，也不要自动重试失败任务 / Report the failure only; do not duplicate queued tasks or automatically retry failed tasks.");
        }

        /// <summary>任务完成：读取 Copilot 最新回复作为结果，并通知 AI 助手。/ Completes a task: stores the latest Copilot answer and notifies the AI assistant.</summary>
        public async void Finish(QueuedTask t, VsInstance v, TimeSpan? dur)
        {
            try { await FinishAsync(t, v, dur); }
            catch (Exception ex) { _host.SetStatus("任务结果处理出错：" + ex.Message); }
        }

        public async Task FinishAsync(QueuedTask t, VsInstance v, TimeSpan? dur)
        {
            if (t == null || t.Status != QueueStatus.Running || _tasks.Find(t.Id) != t) return;
            if (!IsStarted)
            {
                // 忙→闲事件可能仅触发一次；开始后继续核验，不重发。/ Keep one-shot completion events for verification after Start, never resend.
                _pendingCompletions[t] = (dur, t.CompletionToken);
                return;
            }
            if (!_finishing.Add(t)) return;
            _pendingCompletions.Remove(t);
            try
            {
                string answer;
                try { answer = await _host.ReadAnswerAsync(v, t); }
                catch (Exception ex)
                {
                    if (t.Status == QueueStatus.Running && _tasks.Find(t.Id) == t)
                        Fail(t, "读取任务结果失败 / Failed to read task result: " + ex.Message);
                    return;
                }
                if (t.Status != QueueStatus.Running || _tasks.Find(t.Id) != t) return;
                if (!TaskStateMachine.TryReadSuccess(t, answer, out string result))
                {
                    t.Result = TextUtil.Clip(answer, 1500);
                    Fail(t, "未收到本次任务的成功回执 / No success receipt for this task attempt: " + TextUtil.Clip(answer, 300));
                    return;
                }
                if (t.Worktree != null)
                {
                    try
                    {
                        if (v == null || !SolutionMatcher.SamePath(v.SolutionPath, t.Worktree.SolutionPath))
                            throw new InvalidOperationException("目标 VS 已变化 / Target VS changed");
                        if (_worktrees == null) throw new InvalidOperationException("Worktree service unavailable");
                        if (t.IsWorktreeMerge)
                        {
                            if (_tasks.SaveError != null) throw new InvalidOperationException("任务保存失败 / Task persistence failed");
                            _integrating.Add(t);
                            if (await _worktrees.IntegrateAsync(t.Worktree))
                                throw new InvalidOperationException("冲突仍未解决，主项目未更新 / Conflicts remain; main unchanged");
                            result += "\n已验证本地主项目快进，未推送远程 / Verified local integration; no remote push";
                        }
                        else await _worktrees.CheckDevelopmentAsync(t.Worktree);
                    }
                    catch (Exception ex)
                    {
                        if (t.Status == QueueStatus.Running && _tasks.Find(t.Id) == t) Fail(t, ex.Message);
                        return;
                    }
                    if (t.Status != QueueStatus.Running || _tasks.Find(t.Id) != t) return;
                }
                t.Result = TextUtil.Clip(result, 1500);
                TaskStateMachine.Complete(t, _clock());
                _tasks.Commit();
                if (t.FromAgent)
                {
                    string took = TextUtil.FormatDuration(dur ?? (t.Finished.Value - (t.Started ?? t.Finished.Value)));
                    int left = _tasks.Items.Count(x => QueueStatus.Active(x.Status));
                    _host.NotifyAgent($"📋 任务 #{t.Id} 已完成 · {t.VsName}（{took}）",
                        $"[任务完成通知] 任务 #{t.Id} 已在「{t.VsName}」返回结果（用时 {took}）。任务：{TextUtil.Clip(t.Text, 300)}\n" +
                        "Copilot 回复：" + t.Result +
                        (string.IsNullOrEmpty(t.PredecessorNotice) ? "" : "\n" + t.PredecessorNotice) +
                        $"\n任务清单中还有 {left} 个未完成任务。请向用户简要汇报，不要重复发布清单中已有的任务。");
                }
            }
            finally { _finishing.Remove(t); _integrating.Remove(t); }
            Pump();
        }

        /// <summary>取消排队中或执行中的任务。/ Cancels a waiting or running task.</summary>
        public bool Cancel(QueuedTask t)
        {
            if (t != null && _integrating.Contains(t)) return false;
            if (!TaskStateMachine.Cancel(t, _clock())) return false;
            _tasks.Commit();
            return true;
        }

        /// <summary>重新排队并立即尝试发布。/ Requeues a task and tries to publish it right away.</summary>
        public void Retry(QueuedTask t)
        {
            if (!IsStarted) { _host.SetStatus(WaitingForStart); return; }
            if (t == null || _tasks.Find(t.Id) != t || _finishing.Contains(t)
                || (t.Status != QueueStatus.Failed && t.Status != QueueStatus.Cancelled))
            {
                _host.SetStatus("任务已被替换、正在处理或不可重试，请刷新任务清单");
                return;
            }
            TaskStateMachine.Requeue(t);
            _tasks.Commit();
            Pump();
        }

        /// <summary>立即发布（跳过重试等待）；不能立即发布时说明原因。/ Publishes now (skips the retry delay); explains why when it cannot.</summary>
        public void DispatchNow(QueuedTask t)
        {
            if (!IsStarted) { _host.SetStatus(WaitingForStart); return; }
            t.NextTry = DateTime.MinValue;
            if (t.Status == QueueStatus.WaitingVs)
            {
                if (_host.FindTargetVs(t) == null)
                    _host.SetStatus($"「{t.Target ?? t.VsName}」尚未打开，任务 #{t.Id} 已暂存，打开后自动推送 / not open yet; task #{t.Id} is parked and pushed once it opens");
                Pump();
                return;
            }
            var target = _host.FindVs(t.VsKey);
            if (target == null) _host.SetStatus($"「{t.VsName}」当前未打开，任务会在它打开并空闲后发布");
            else if (!_host.CanDispatch(target) || _tasks.Items.Any(x => x.VsKey == t.VsKey && (x.Status == QueueStatus.Running || x.Status == QueueStatus.Sending)))
                _host.SetStatus($"「{t.VsName}」仍在忙，任务 #{t.Id} 会在空闲后自动发布");
            else if (_tasks.BlockingTask(t) is QueuedTask blocker)
                _host.SetStatus($"任务 #{t.Id} 等待前序 #{blocker.Id}（{TaskStateMachine.StatusText(blocker, _clock())}）/ Task #{t.Id} is blocked by predecessor #{blocker.Id}");
            Pump();
        }
    }
}
