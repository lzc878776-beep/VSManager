using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VSManager
{
    /// <summary>编排层需要执行层提供的能力。/ Capabilities the orchestration layer needs from the execution layer.</summary>
    internal interface IPlanHost
    {
        SolutionLookup ResolveSolution(string query);
        /// <summary>把任务加入现有任务清单（已打开则排队，未打开则暂存），返回任务编号。/ Enqueues into the existing task list; returns the task id.</summary>
        Task<int?> EnqueuePlanTask(SolutionEntry entry, string text);
        QueuedTask FindTask(int id);
        bool SkipFailedPredecessors { get; }
        int MaxTaskText { get; }
    }

    /// <summary>计划记录与本地任务的幂等关联（持久化）。/ Persisted idempotency link between a plan record and a local task.</summary>
    internal sealed class PlanLink
    {
        public string PageId { get; set; }
        public string DatabaseId { get; set; }
        public string Title { get; set; }
        public string Hash { get; set; }
        public string Alias { get; set; }
        public string Text { get; set; }
        public List<string> DependsOn { get; set; } = new List<string>();
        public string StatusType { get; set; }
        /// <summary>本地任务编号；null 表示因依赖未完成而由编排层暂缓。/ Local task id; null while held by the orchestrator for dependencies.</summary>
        public int? TaskId { get; set; }
        public string Desired { get; set; }
        public string Written { get; set; }
        public bool Closed { get; set; }
    }

    /// <summary>
    /// Notion 计划编排层：读取 → 解析 → 映射 → 预览 → 用户确认后派发到现有任务清单；按页面 ID + 内容哈希幂等；
    /// 跨项目依赖在编排层暂缓，同项目顺序交给任务清单；结果只回写专用状态字段，失败的回写保留待重试。
    /// Notion plan orchestration: read → parse → map → preview → dispatch into the existing task list after user confirmation;
    /// idempotent by page id + content hash; cross-project dependencies are held here while same-project order is left to the
    /// task list; results are written back to the dedicated status property only, and failed write-backs are retried.
    /// </summary>
    internal sealed class NotionPlanService
    {
        private readonly IPlanHost _host;
        private readonly INotionClient _notion;
        private readonly Func<string> _statusProperty;
        private readonly string _path;
        private readonly object _lock = new object();
        private readonly Dictionary<string, PlanLink> _links;
        private readonly Dictionary<string, List<PlanItem>> _previews = new Dictionary<string, List<PlanItem>>();
        private int _syncing;

        public NotionPlanService(IPlanHost host, INotionClient notion, Func<string> statusProperty, string path = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _notion = notion ?? throw new ArgumentNullException(nameof(notion));
            _statusProperty = statusProperty ?? (() => null);
            _path = path ?? Path.Combine(AppPaths.DataFolder, "notion-plans.json");
            _links = Load(_path).ToDictionary(l => l.PageId);
        }

        private string StatusProperty => string.IsNullOrWhiteSpace(_statusProperty()) ? NotionPlanParser.DefaultStatusProperty : _statusProperty().Trim();

        internal IReadOnlyList<PlanLink> Links { get { lock (_lock) return _links.Values.ToList(); } }

        /// <summary>读取并解析计划，生成供用户确认的清单；不派发、不回写。/ Reads and parses the plan into a list for user confirmation; no dispatch, no write-back.</summary>
        public async Task<string> PreviewAsync(string database, CancellationToken ct)
        {
            string dbId = NotionPlanParser.NormalizeId(database);
            if (dbId == null) return "请提供 Notion 数据库链接或 32 位 ID / Provide a Notion database URL or 32-character id";
            var pages = await _notion.QueryDatabaseAsync(dbId, ct).ConfigureAwait(false);
            var items = NotionPlanParser.ParseDatabase(pages, StatusProperty);
            foreach (var item in items)
                item.Detail = NotionPlanParser.BlocksToText(await _notion.ReadBlocksAsync(item.PageId, ct).ConfigureAwait(false));
            var ordered = NotionPlanParser.Validate(items, _host.ResolveSolution, _host.MaxTaskText);
            lock (_lock) _previews[dbId] = ordered;
            return Describe(ordered);
        }

        /// <summary>
        /// 派发用户已确认的条目：selection 为 "all"（全部无问题且未派发的条目）或逗号分隔的序号；内容变更的条目必须按序号显式选择。
        /// Dispatches confirmed items: selection is "all" (every clear, undispatched item) or comma-separated numbers; changed items must be selected explicitly.
        /// </summary>
        public async Task<string> DispatchAsync(string database, string selection, CancellationToken ct)
        {
            string dbId = NotionPlanParser.NormalizeId(database);
            List<PlanItem> preview;
            lock (_lock) if (dbId == null || !_previews.TryGetValue(dbId, out preview)) return "请先预览该计划并让用户确认 / Preview the plan and get user confirmation first";
            var chosen = Select(preview, selection, out string error);
            if (error != null) return error;
            var sb = new StringBuilder();
            foreach (var item in chosen)
            {
                PlanLink link;
                lock (_lock)
                {
                    _links.TryGetValue(item.PageId, out var old);
                    if (old != null && old.Hash == item.Hash && !old.Closed) { sb.AppendLine($"跳过「{item.Title}」：已派发 / Skipped: already dispatched"); continue; }
                    if (old != null && old.TaskId is int running && QueueStatus.Active(_host.FindTask(running)?.Status))
                    { sb.AppendLine($"跳过「{item.Title}」：旧版本任务 #{running} 仍在执行 / Skipped: previous task still active"); continue; }
                    link = new PlanLink
                    {
                        PageId = item.PageId, DatabaseId = dbId, Title = item.Title, Hash = item.Hash, Alias = item.Entry.Alias,
                        Text = NotionPlanParser.TaskText(item), DependsOn = item.DependsOn.ToList(), StatusType = item.StatusType,
                        Desired = Status("待依赖完成", "Waiting for dependencies")
                    };
                    _links[item.PageId] = link;
                }
                sb.AppendLine($"「{item.Title}」→ {link.Alias}：" + await ReleaseAsync(link).ConfigureAwait(false));
            }
            Save();
            await SyncAsync(ct).ConfigureAwait(false);
            lock (_lock) _previews.Remove(dbId);
            return sb.Length == 0 ? "没有可派发的条目 / Nothing to dispatch" : sb.ToString().TrimEnd();
        }

        /// <summary>任务清单变化时调用：释放依赖已满足的条目并回写状态。/ Called on task-list changes: releases ready items and writes back statuses.</summary>
        public async Task SyncAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _syncing, 1) == 1) return;
            try
            {
                List<PlanLink> open;
                lock (_lock) open = _links.Values.Where(l => !l.Closed || l.Desired != l.Written).ToList();
                foreach (var link in open.Where(l => l.TaskId == null && !l.Closed)) await ReleaseAsync(link).ConfigureAwait(false);
                foreach (var link in open.Where(l => l.TaskId != null)) UpdateDesired(link);
                Save();
                foreach (var link in open.Where(l => l.Desired != l.Written))
                {
                    string value = link.Desired;
                    try
                    {
                        await _notion.UpdateStatusAsync(link.PageId, StatusProperty, link.StatusType, value, ct).ConfigureAwait(false);
                        lock (_lock) link.Written = value;
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.Net.WebException)
                    {
                        AppLog.Error(AppLog.TasksFile, "Notion 状态回写失败，稍后重试 / Notion status write-back failed; will retry", ex);
                        break;
                    }
                }
                Save();
            }
            finally { Interlocked.Exchange(ref _syncing, 0); }
        }

        private async Task<string> ReleaseAsync(PlanLink link)
        {
            List<PlanLink> deps;
            lock (_lock) deps = link.DependsOn.Select(d => _links.TryGetValue(d, out var l) ? l : null).ToList();
            if (deps.Any(d => d == null)) return "等待依赖条目被确认派发 / Held until dependencies are confirmed";
            bool skip = _host.SkipFailedPredecessors;
            bool anyFailed = false, ready = true;
            foreach (var d in deps)
            {
                string s = d.TaskId is int depId ? _host.FindTask(depId)?.Status : null;
                bool failed = s == QueueStatus.Failed || s == QueueStatus.Cancelled || d.Closed && !QueueStatus.Delivered(s);
                anyFailed |= failed;
                // 同一目标项目的依赖交给任务清单按编号调度（含失败跳过策略）。/ Same-target dependencies are ordered by the task list (including its skip policy).
                bool ok = d.TaskId != null && SameTarget(d, link) || QueueStatus.Delivered(s) || failed && skip;
                ready &= ok;
            }
            if (anyFailed && !skip && !deps.All(d => d.TaskId != null && SameTarget(d, link)))
            {
                lock (_lock) { link.Closed = true; link.Desired = Status("已阻塞：依赖失败", "Blocked: dependency failed"); }
                return "依赖失败，已阻塞 / Blocked by a failed dependency";
            }
            if (!ready) return "等待依赖完成 / Held until dependencies finish";
            var entry = _host.ResolveSolution(link.Alias)?.Hit;
            if (entry == null)
            {
                lock (_lock) { link.Closed = true; link.Desired = Status("失败：目标项目已不在登记表", "Failed: target no longer registered"); }
                return "目标项目已不在登记表 / Target no longer registered";
            }
            int? id = await _host.EnqueuePlanTask(entry, link.Text).ConfigureAwait(false);
            lock (_lock)
            {
                if (id == null) { link.Closed = true; link.Desired = Status("失败：无法加入任务清单", "Failed: could not enqueue"); return "无法加入任务清单 / Could not enqueue"; }
                link.TaskId = id;
                link.Desired = Status($"已排队 #{id}", $"Queued #{id}");
            }
            return $"已加入任务清单 #{id} / Queued as #{id}";
        }

        // 同一目标项目内的顺序交给任务清单按编号调度。/ Same-target order is left to the task list's ID-ordered dispatch.
        private static bool SameTarget(PlanLink a, PlanLink b) => string.Equals(a.Alias, b.Alias, StringComparison.OrdinalIgnoreCase);

        private void UpdateDesired(PlanLink link)
        {
            var t = _host.FindTask(link.TaskId.Value);
            string desired;
            bool closed = true;
            if (t == null) desired = Status($"#{link.TaskId} 已从任务清单移除", $"#{link.TaskId} removed from the task list");
            else if (t.Status == QueueStatus.Done) desired = Status($"已完成 #{t.Id}", $"Done #{t.Id}");
            else if (t.Status == QueueStatus.Unverified) desired = Status($"待验证 #{t.Id}", $"Unverified #{t.Id}");
            else if (t.Status == QueueStatus.Failed) desired = Status($"失败 #{t.Id}：{t.Error}", $"Failed #{t.Id}");
            else if (t.Status == QueueStatus.Cancelled) desired = Status($"已取消 #{t.Id}", $"Cancelled #{t.Id}");
            else { closed = false; desired = t.Status == QueueStatus.Running || t.Status == QueueStatus.Sending ? Status($"执行中 #{t.Id}", $"Running #{t.Id}") : Status($"已排队 #{t.Id}", $"Queued #{t.Id}"); }
            lock (_lock) { link.Desired = desired; link.Closed = closed; }
        }

        private static string Status(string zh, string en) => zh + " / " + en;

        private string Describe(List<PlanItem> items)
        {
            if (items.Count == 0) return "计划中没有任务记录 / The plan has no task records";
            var sb = new StringBuilder("计划预览（尚未派发，请逐条与用户确认）/ Plan preview (not dispatched; confirm with the user):\n");
            int n = 0;
            foreach (var item in items)
            {
                n++;
                PlanLink link;
                lock (_lock) _links.TryGetValue(item.PageId, out link);
                string state = link == null ? "新 / new"
                    : link.Hash == item.Hash ? $"已派发，跳过 / already dispatched ({link.Desired})"
                    : "内容已变更，需按序号显式选择才会重新派发 / changed; select its number explicitly to redispatch";
                sb.Append(n).Append(". ").Append(item.Title).Append(" → ").Append(item.Entry?.Alias ?? item.Target).Append(" | ").Append(state);
                if (item.DependsOn.Count > 0)
                    sb.Append(" | 依赖 / depends on: ").Append(string.Join("、", item.DependsOn.Select(d => items.FindIndex(x => x.PageId == d) + 1).Where(i => i > 0)));
                sb.AppendLine();
                foreach (var issue in item.Issues) sb.Append("   ⚠ ").AppendLine(issue);
            }
            int blocked = items.Count(i => !i.Clear);
            if (blocked > 0) sb.AppendLine($"{blocked} 条需用户澄清，不会派发；请在 Notion 中修改后重新预览 / {blocked} item(s) need clarification and will not be dispatched; edit in Notion and preview again");
            return sb.ToString().TrimEnd();
        }

        private List<PlanItem> Select(List<PlanItem> preview, string selection, out string error)
        {
            error = null;
            string s = (selection ?? "").Trim();
            var chosen = new List<PlanItem>();
            if (string.Equals(s, "all", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock) chosen.AddRange(preview.Where(i => i.Clear && !_links.ContainsKey(i.PageId)));
                return chosen;
            }
            foreach (var part in s.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, out int n) || n < 1 || n > preview.Count) { error = $"无效序号「{part}」/ Invalid number \"{part}\""; return null; }
                var item = preview[n - 1];
                if (!item.Clear) { error = $"第 {n} 条仍有待澄清的问题，不能派发 / Item {n} still has open issues"; return null; }
                if (!chosen.Contains(item)) chosen.Add(item);
            }
            if (chosen.Count == 0) error = "未选择条目 / No items selected";
            return chosen;
        }

        private static List<PlanLink> Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<PlanLink>();
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<PlanLink>>(File.ReadAllText(path, Encoding.UTF8))?
                    .Where(l => l?.PageId != null).GroupBy(l => l.PageId).Select(g => g.Last()).ToList() ?? new List<PlanLink>();
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                AppLog.Error(AppLog.TasksFile, "读取 Notion 计划关联失败 / Failed to read Notion plan links", ex);
                return new List<PlanLink>();
            }
        }

        private void Save()
        {
            string json;
            lock (_lock) json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(_links.Values.ToList());
            var r = AtomicFile.Write(_path, s => { var b = Encoding.UTF8.GetBytes(json); s.Write(b, 0, b.Length); }, backupBeforeOverwrite: true, skipFallbackOnSerializationError: true);
            if (!r.Ok) AppLog.Error(AppLog.TasksFile, "保存 Notion 计划关联失败 / Failed to save Notion plan links", r.FallbackError ?? r.Error);
        }
    }
}
