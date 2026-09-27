using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>按不可变身份查找提及目标。/ Resolve a mentioned target by immutable identity.</summary>
    public interface IExplicitTaskDispatchHost
    {
        VsInstance FindExplicitTarget(QueuedTask task);
    }
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

    /// <summary>可选宿主能力：任务成功后、发布下一个任务前整理目标 VS（如保存并关闭文档）。/ Optional host capability: tidies the target VS (e.g. save and close documents) after success, before the next task is published.</summary>
    public interface ITaskCompletionHost
    {
        Task AfterTaskCompletedAsync(QueuedTask t, VsInstance v);
    }

    /// <summary>
    /// 任务调度器：跟踪执行中的任务，并把每个空闲 VS 最早排队的任务发布出去；失败、完成、取消、重试也在这里处理。
    /// 状态流转统一交给 <see cref="TaskStateMachine"/>，只在界面线程调用。
    /// Task dispatcher: tracks running tasks and publishes the oldest waiting task of every idle VS; also handles failure,
    /// completion, cancellation and retry. Status changes go through <see cref="TaskStateMachine"/>. UI thread only.
    /// </summary>
    public sealed partial class TaskDispatcher
    {
        private readonly TaskQueue _tasks;
        private readonly ITaskDispatchHost _host;
        private readonly Func<DateTime> _clock;
        private readonly IWorktreeTaskService _worktrees;
        private VsInstance ResolveTarget(QueuedTask task, bool useWorktree = false)
        {
            if (!task.HasExplicitTarget) return !useWorktree || task.Worktree == null ? _host.FindVs(task.VsKey) : _host.FindTargetVs(task);
            var target = (_host as IExplicitTaskDispatchHost)?.FindExplicitTarget(task);
            return task.MatchesExplicitTarget(target) ? target : null;
        }
        private bool _pumping;
        private readonly HashSet<QueuedTask> _finishing = new HashSet<QueuedTask>();
        private readonly HashSet<QueuedTask> _integrating = new HashSet<QueuedTask>();
        private readonly Dictionary<QueuedTask, (TimeSpan? Duration, string Token)> _pendingCompletions
            = new Dictionary<QueuedTask, (TimeSpan?, string)>();

        public const string WaitingForStart = "等待手动授权：点击「开始流程 / Start」或右键当前任务重新检查 / Waiting for manual start or task authorization: click Start or recheck this task from its context menu";
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

        public TaskDispatcher(TaskQueue tasks, ITaskDispatchHost host, Func<DateTime> clock = null, IWorktreeTaskService worktrees = null, Func<AppSettings> startSettings = null)
        {
            _tasks = tasks;
            _host = host;
            _clock = clock ?? (() => DateTime.Now);
            _worktrees = worktrees;
            _startSettings = startSettings;
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
            if (_pumping) return;
            ForgetRemovedGrants();
            GrantSavedMaintenance();
            PruneManualWaits();
            if (!HasDispatchActivity)
            {
                _host.QueueActivityChanged(false);
                return;
            }
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
                        if (!CanRun(task)) continue;
                        var target = ResolveTarget(task);
                        if (target == null || target.Copilot == CopilotState.Busy || !_host.CanDispatch(target)) continue;
                        _pendingCompletions.Remove(task);
                        await FinishAsync(task, target, completion.Duration);
                    }
                }
                PushParked(now);
                foreach (var t in _tasks.Items.Where(x => x.Status == QueueStatus.Running).ToList())
                {
                    if (now < _host.TrackingReadyAt) break;
                    if (_finishing.Contains(t) || !CanRun(t)) continue;
                    var v = ResolveTarget(t);
                    if (v == null && t.HasExplicitTarget) { Fail(t, VsMentionSession.MissingError); continue; }
                    switch (TaskStateMachine.CheckRunning(t, v != null, v?.Copilot ?? CopilotState.Unknown, () => _host.CanDispatch(v), now))
                    {
                        case RunningVerdict.VsClosed: Fail(t, TaskStateMachine.VsClosedError, FailureKind.VsClosed); break;
                        case RunningVerdict.SawBusy: t.SawBusy = true; break;
                        case RunningVerdict.Unconfirmed: await FinishAsync(t, v, null); break;
                    }
                }

                foreach (var t in _tasks.NextToDispatch(now))
                {
                    if (_host.IsSending) break;
                    if (!CanRun(t) || (!IsStarted && _tasks.SaveError != null)) continue;
                    var v = ResolveTarget(t, true);
                    if (v == null && t.HasExplicitTarget) { Fail(t, VsMentionSession.MissingError); continue; }
                    if (v == null || t.Status != QueueStatus.Waiting
                        || _tasks.Find(t.Id) != t || DispatchBlocker(t) != null) continue;
                    if (t.Worktree != null && !SolutionMatcher.SamePath(v.SolutionPath, t.Worktree.SolutionPath)) continue;
                    string targetPath = v.SolutionPath, targetIdentity = v.InstanceKey;
                    if (await WaitForManualAsync(t, v)) continue;
                    if (!_host.CanDispatch(v) || _host.IsSending || t.Status != QueueStatus.Waiting || !CanRun(t)
                        || !SameTarget(t, v, targetPath, targetIdentity) || DispatchBlocker(t) != null || (!IsStarted && _tasks.SaveError != null)) continue;
                    var skipped = _tasks.Items.Where(x => x.Id < t.Id && x.Status == QueueStatus.Failed
                        && TaskStateMachine.SharesDispatchTarget(x, t)).OrderBy(x => x.Id).Select(x => x.Id).ToArray();
                    TaskStateMachine.BeginSend(t, t.HasExplicitTarget ? t.VsName : _host.NameOf(v));
                    if (t.Worktree != null) t.VsKey = v.Key;
                    _tasks.Commit();
                    _host.LogEvent(t.VsName, $"任务清单：发布任务 #{t.Id}（第 {t.Attempts} 次）");
                    string r;
                    try
                    {
                        if (_tasks.SaveError != null) throw new InvalidOperationException("任务保存失败，未发送 / Task save failed; not sent");
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
                                    if (_manualWaits.ContainsKey(t)) { _yielded.Add(t); ClearManualWait(t); }
                                    TaskStateMachine.Complete(t, _clock());
                                    CommitCompletion(t);
                                    _host.NotifyAgent($"工作树合并任务 #{t.Id} 已完成 / Worktree integration completed", t.Result + AutomaticCompletionText(t));
                                    continue;
                                }
                            }
                            else await _worktrees.CheckDevelopmentAsync(t.Worktree);
                            if (!SolutionMatcher.SamePath(v.SolutionPath, t.Worktree.SolutionPath) || !_host.CanDispatch(v))
                                throw new InvalidOperationException("目标 VS 已变化 / Target VS changed");
                        }
                        if (t.HasExplicitTarget && !SameTarget(t, v, targetPath, targetIdentity))
                            throw new InvalidOperationException(VsMentionSession.MissingError);
                        if (t.Status != QueueStatus.Sending || !CanRun(t)) continue;
                        r = _host is IManualChatDispatchHost protectedHost
                            ? await protectedHost.SendQueuedAsync(v, t, () => t.Status == QueueStatus.Sending && CanRun(t)
                                && SameTarget(t, v, targetPath, targetIdentity) && DispatchBlocker(t) == null && _tasks.SaveError == null && _host.CanDispatch(v))
                            : WaitForManualChat ? ManualChatProtection.WaitPrefix + ManualChatProtection.Reason(ManualChatObservation.Unknown)
                            : t.HasAttachments && _host is ITaskAttachmentDispatchHost attachmentHost
                                ? await attachmentHost.SendTaskAsync(v, t)
                                : await _host.SendAsync(v, TaskStateMachine.DispatchText(t));
                    }
                    catch (Exception ex)
                    {
Fail(t, (t.Worktree == null ? ManualChatProtection.UncertainPrefix + "发送异常 / Send exception: "
    : "Worktree 任务失败，请检查 Git 和 VS 后重试 / Worktree task failed; inspect Git and VS: ") + ex.Message,
    t.Worktree == null ? FailureKind.Delivery : null);
                        continue;
                    }
                    switch (TaskStateMachine.ApplySendResult(t, r, _clock()))
                    {
                        case SendDecision.Delivered:
                            ManualDelivery(t);
                            AnnounceDelivery(t, skipped);
                            break;
                        case SendDecision.Fail:
                            AfterFail(t, r);
                            break;
                        default:
                            if (ManualChatProtection.IsWait(r) && WaitForManualChat && SameTarget(t, v, targetPath))
                                RecordManualWait(t, v, ManualChatObservation.Unknown);
                            _tasks.Commit();
                            _host.SetStatus($"任务清单：#{t.Id} {r}；尚未提交，处理后继续等待检查 / Not submitted; waiting for protection to clear");
                            break;
                    }
                }
            }
            finally { _pumping = false; }
            _host.QueueActivityChanged(HasDispatchActivity);
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
                if (!CanRun(t) || _tasks.SaveError != null) continue;
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
        public void Fail(QueuedTask t, string error, string kind = null)
        {
            TaskStateMachine.Fail(t, error, _clock(), kind);
            AfterFail(t, error);
        }

        private string FailurePolicyText => "Worktree 合并失败或取消始终阻塞该工作线，请处理后重试同一任务 / Failed or cancelled worktree integration always blocks its lane; resolve and retry the same task. "
            + (_tasks.ReleaseLevel == ReleaseLevel.Failed
                ? "放行等级「失败」：后续排队任务可继续；失败记录保留 / Release level \"Failed\": queued successors may continue and failure history is retained"
                : $"放行等级「{ReleaseLevels.ShortName(_tasks.ReleaseLevel)}」：未被取代、未放行的失败会暂停该目标后续任务；失败记录保留 / Release level \"{ReleaseLevels.ShortNameEn(_tasks.ReleaseLevel)}\": unsuperseded, unreleased failures pause this target's successors; failure history is retained");

        /// <summary>当前等级下该任务结果是否正在暂停同一目标的后续任务。/ Whether this outcome currently pauses successors on the same target.</summary>
        private bool HoldsSuccessors(QueuedTask t) =>
            ReleaseLevels.Blocks(_tasks.ReleaseLevel, t) && !t.Released
            && _tasks.Items.Any(x => x.Id > t.Id && QueueStatus.Active(x.Status) && ResentTaskMatcher.SameTarget(x, t));

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
            ClearManualWait(t);
            _yielded.Remove(t);
            _tasks.Commit();
            string diagnostic = $"任务 #{t.Id} 失败即终止 / Failed; automatic retry stopped; " +
                $"阶段 / Stage={(t.Started.HasValue ? "Result" : "Send")}; 次数 / Attempts={t.Attempts}; " +
                $"送达待核实 / Uncertain={error?.StartsWith(ManualChatProtection.UncertainPrefix, StringComparison.Ordinal) == true}";
            AppLog.Write(AppLog.TasksFile, diagnostic);
            _host.LogEvent("任务 / Task", diagnostic);
            _host.SetStatus($"任务清单：#{t.Id}「{t.VsName}」失败 / Task #{t.Id} failed: {error}；已停止自动重试，可手动重新排队 / Automatic retry stopped; manual requeue available；{FailurePolicyText}");
            if (t.FromAgent)
            {
                string reply = FailureKind.IsContent(t.FailureKind) && !string.IsNullOrWhiteSpace(t.Result)
                    ? "\nCopilot 回复 / Copilot reply：" + TextUtil.Clip(t.Result, 1200) : "";
                _host.NotifyAgent($"📋 任务 #{t.Id} 失败 · {t.VsName} / Task #{t.Id} failed",
                    $"[任务失败通知] / [Task failure] 任务 #{t.Id} 在「{t.VsName}」失败 / failed（{FailureKind.Label(t.FailureKind)}）：{TextUtil.Clip(error, 300)}。" +
                    $"任务内容 / Task: {TextUtil.Clip(t.Text, 300)}。{FailurePolicyText}。" + reply + "\n" +
                    TaskFailureAnalyzer.Guidance(t) + "\n" +
                    (ReleaseLevels.Blocks(_tasks.ReleaseLevel, t) ? BlockedFailureAdvice(t) :
                    "不要重复发布已排队任务；未经用户同意不要重试 / Do not duplicate queued tasks; do not retry without the user's consent."));
            }
        }

        /// <summary>
        /// 失败阻塞后续时给 AI 的决策建议：自行补充信息重试，或转交用户。
        /// Advice for the AI when a failure pauses successors: retry with self-supplied info, or hand over to the user.
        /// </summary>
        private string BlockedFailureAdvice(QueuedTask t)
        {
            int left = TaskStateMachine.MaxSupplements - t.SupplementCount;
            string waiting = HoldsSuccessors(t) ? "该失败正在暂停同一 VS 的后续任务 / This failure is pausing successors on the same VS. " : "";
            return waiting +
                "请根据 Copilot 回复判断：① 若失败原因明确且你能从已有信息（回复、对话、目录、常识）补齐所需内容，" +
                $"调用 retry_task_with_info 插入补充信息重试该任务（还可补充 {Math.Max(0, left)} 次）；" +
                "② 若需要用户决定、需要只有用户知道的信息、涉及取舍或风险，或补充次数已用完，就把失败原因和需要的信息告诉用户，" +
                "由用户补充（之后用 retry_task_with_info 带上）、放行（release_task）或取消。不要重复发布已排队任务。 / " +
                "Judge from the Copilot reply: (1) if the cause is clear and you can supply what is missing from existing information, " +
                $"call retry_task_with_info to retry the task with supplementary info ({Math.Max(0, left)} left); " +
                "(2) if it needs a user decision, information only the user has, involves trade-offs or risk, or the supplement limit is used up, " +
                "tell the user the cause and what is needed; the user may supplement (then pass it via retry_task_with_info), release (release_task) or cancel. " +
                "Do not duplicate queued tasks.";
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
            if (t.HasExplicitTarget && (!t.MatchesExplicitTarget(v) || ResolveTarget(t) != v)) return;
            if (!CanRun(t))
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
                        Fail(t, "读取任务结果失败 / Failed to read task result: " + ex.Message, FailureKind.ReadError);
                    return;
                }
                if (t.Status != QueueStatus.Running || _tasks.Find(t.Id) != t) return;
if (t.HasExplicitTarget && (!t.MatchesExplicitTarget(v) || ResolveTarget(t) != v))
{
    Fail(t, VsMentionSession.MissingError);
    return;
}
var receipt = TaskStateMachine.ReadReceipt(t, answer, out string result);
if (receipt == TaskReceipt.Failed || receipt == TaskReceipt.None)
{
    bool reported = receipt == TaskReceipt.Failed;
    t.Result = TextUtil.Clip(reported ? result : answer, 1500);
    Fail(t, (reported
        ? "Copilot 回报本任务未完成 / Copilot reported the task as not completed: " + TextUtil.Clip(result, 300)
        : "未收到本次任务的成功回执 / No success receipt for this task attempt: " + TextUtil.Clip(StripReceipt(t, answer), 300)),
        reported ? FailureKind.Reported : FailureKind.NoReceipt);
    // Copilot 已结束本轮回复，文件同样需要保存。/ Copilot finished this turn, so its edits still need saving.
    await TidyAsync(t, v);
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
                bool needsUser = receipt == TaskReceipt.NeedsUser;
                t.Result = TextUtil.Clip(result, 1500);
t.FullResult = result;
bool unverified = receipt == TaskReceipt.Unverified;
if (unverified) _host.LogEvent(t.VsName, $"任务 #{t.Id} 已实现但未实际验证 / Task implemented but not verified at runtime");
TaskStateMachine.Complete(t, _clock(), needsUser, unverified);
                if (needsUser || unverified) t.TestItems = TaskTestChecklist.Parse(result);
                CommitCompletion(t);
                if (t.FromAgent)
                {
                    string took = TextUtil.FormatDuration(dur ?? (t.Finished.Value - (t.Started ?? t.Finished.Value)));
                    int left = _tasks.Items.Count(x => QueueStatus.Active(x.Status));
string label = unverified ? "未验证" : needsUser ? "已完成（待用户验证）" : "已完成";
string labelEn = unverified ? "unverified" : needsUser ? "completed (awaiting verification)" : "completed";
_host.NotifyAgent($"📋 任务 #{t.Id} {label} · {t.VsName}（{took}）/ Task {labelEn}",
    $"[任务完成通知 / Task completed] 任务 #{t.Id} 已在「{t.VsName}」返回结果（用时 {took}）/ Task #{t.Id} returned its result in {took}. 任务 / Task: {TextUtil.Clip(t.Text, 300)}\n" +
    "Copilot 回复 / Reply: " + t.Result + AutomaticCompletionText(t) + ManualCompletionText(t) +
    (unverified ? "\n结论：未验证（不是失败）。Copilot 说明功能已实现，只是尚未在运行中的程序里实际验证；请按「未验证」汇报并列出未验证项，不要判为失败，也不要说成已实测成功。/ Verdict: unverified, not failed. The work is implemented but not yet verified in the running app; report it as unverified with the pending checks, neither as a failure nor as a verified success." : "") +
                        (needsUser ? "\n改动已完成，但需要用户测试或确认：请把需要验证的内容转告用户并等待反馈，不要重发，也不要把它当作已验证的依赖。/ Changes are done but need user testing or confirmation: relay what to verify and wait for feedback; do not resend or treat it as a verified dependency." : "") +
                        (needsUser && ReleaseLevels.Blocks(_tasks.ReleaseLevel, t)
                            ? "\n放行等级为「已完成」：同一 VS 的后续任务已暂停，用户确认验证通过后调用 release_task 放行；验证不通过时用 retry_task_with_info 带上问题重试。/ Release level \"Completed\": successors on the same VS are paused; call release_task once the user confirms, or retry_task_with_info with the problems if verification fails." : "") +
                        (string.IsNullOrEmpty(t.PredecessorNotice) ? "" : "\n" + t.PredecessorNotice) +
                        $"\n任务清单中还有 {left} 个未完成任务。请向用户简要汇报，不要重复发布清单中已有的任务。/ {left} unfinished tasks remain. Briefly report to the user; do not duplicate queued tasks.");
                }
                await TidyAsync(t, v);
            }
            finally { _finishing.Remove(t); _integrating.Remove(t); }
            Pump();
        }

        /// <summary>任务回复结束后整理目标 VS；异常只记录，不影响任务状态。/ Tidies the target VS after the reply ends; errors are logged only.</summary>
        private async Task TidyAsync(QueuedTask t, VsInstance v)
        {
            if (!(_host is ITaskCompletionHost completion) || v == null) return;
            try { await completion.AfterTaskCompletedAsync(t, v); }
            catch (Exception ex) { _host.LogEvent(t.VsName, "任务后整理失败 / Post-task tidy failed: " + ex.Message); }
        }

        private static string StripReceipt(QueuedTask t, string answer)
        {
            string text = (answer ?? "").Trim();
            if (!string.IsNullOrEmpty(t.CompletionToken))
                text = text.Replace(TaskStateMachine.FailureReceipt(t), "").Replace(TaskStateMachine.SuccessReceipt(t), "").Trim();
            return text;
        }

        /// <summary>用户确认待测试任务已实际验证：转为已完成，并继续发布被它暂停的后续任务。/ The user confirms a task awaiting tests was verified: it becomes done and paused successors continue.</summary>
        public bool MarkVerified(QueuedTask t)
        {
            if (t == null || _tasks.Find(t.Id) != t || !TaskStateMachine.MarkVerified(t)) return false;
            _host.LogEvent(t.VsName, $"任务 #{t.Id} 已由用户确认验证 / Task verified by the user");
            _tasks.Commit();
            Pump();
            return true;
        }

        /// <summary>
        /// 勾选 / 取消勾选测试项；全部勾选后任务自动标记为已完成。任务不在待测试状态时返回 false。
        /// Checks / unchecks a test item; the task is marked done once every item is checked. Returns false when the task no longer awaits tests.
        /// </summary>
        public bool SetTestItem(QueuedTask t, int index, bool done)
        {
            if (t == null || _tasks.Find(t.Id) != t || !TaskTestChecklist.Pending(t)) return false;
            if (!TaskStateMachine.SetTestItem(t, index, done, out bool allChecked)) return false;
            if (allChecked && MarkVerified(t)) return true;
            _tasks.Commit();
            return true;
        }

        /// <summary>取消排队中或执行中的任务。/ Cancels a waiting or running task.</summary>
        public bool Cancel(QueuedTask t)
        {
            if (t != null && _integrating.Contains(t)) return false;
            if (!TaskStateMachine.Cancel(t, _clock())) return false;
            ClearManualWait(t);
            _yielded.Remove(t);
            _tasks.Commit();
            return true;
        }

        /// <summary>重新排队并立即尝试发布。/ Requeues a task and tries to publish it right away.</summary>
        public void Retry(QueuedTask t)
        {
            if (t == null || _tasks.Find(t.Id) != t || _finishing.Contains(t)
                || (t.Status != QueueStatus.Failed && t.Status != QueueStatus.Cancelled && t.Status != QueueStatus.Unverified))
            {
                _host.SetStatus("任务已被替换、正在处理或不可重试，请刷新任务清单");
                return;
            }
            PrepareManualRecheck(t);
            TaskStateMachine.Requeue(t);
            _tasks.Commit();
            Pump();
        }

        /// <summary>放行失败 / 待验证的任务，让后续继续发布。/ Releases a failed / awaiting-verification task so successors can dispatch.</summary>
        public bool Release(QueuedTask t, out string error)
        {
            error = null;
            if (t == null || _tasks.Find(t.Id) != t) { error = "任务不存在 / Task not found"; return false; }
            if (t.Released) { error = "任务已放行 / Task already released"; return false; }
            if (!TaskStateMachine.Release(t)) { error = "只有失败或待验证的任务可以放行 / Only failed or awaiting-verification tasks can be released"; return false; }
            _tasks.Commit();
            _host.LogEvent(t.VsName, $"任务清单：#{t.Id} 已放行，后续任务可继续 / Task #{t.Id} released");
            Pump();
            return true;
        }

        /// <summary>插入补充信息后重试失败 / 待验证的任务。/ Retries a failed / awaiting-verification task with supplementary info.</summary>
        public bool RetryWithInfo(QueuedTask t, string info, out string error)
        {
            error = null;
            if (t == null || _tasks.Find(t.Id) != t || _finishing.Contains(t)) { error = "任务已被替换或正在处理 / Task was replaced or is being processed"; return false; }
            if (!TaskStateMachine.Supplement(t, info, out error)) return false;
            _tasks.Commit();
            _host.LogEvent(t.VsName, $"任务清单：#{t.Id} 补充信息后重新排队（第 {t.SupplementCount} 次）/ Task #{t.Id} requeued with info");
            Pump();
            return true;
        }

        /// <summary>用户只授权当前任务重新检查；仍保护草稿、忙状态和前序任务。/ User authorizes only this task for rechecking; drafts, busy targets and predecessors remain protected.</summary>
        public void DispatchNow(QueuedTask t)
        {
            if (t == null || _tasks.Find(t.Id) != t || (t.Status != QueueStatus.Waiting && t.Status != QueueStatus.WaitingVs)) return;
            PrepareManualRecheck(t);
            t.NextTry = DateTime.MinValue;
            if (t.Status == QueueStatus.WaitingVs)
            {
                if (_host.FindTargetVs(t) == null)
                    _host.SetStatus($"「{t.Target ?? t.VsName}」尚未打开，任务 #{t.Id} 已暂存，打开后自动推送 / not open yet; task #{t.Id} is parked and pushed once it opens");
                Pump();
                return;
            }
            var target = ResolveTarget(t);
            if (target == null && t.HasExplicitTarget) { Fail(t, VsMentionSession.MissingError); return; }
            if (target == null) _host.SetStatus($"「{t.VsName}」当前未打开，任务会在它打开并空闲后发布");
            else if (!_host.CanDispatch(target) || _tasks.Items.Any(x => x.VsKey == t.VsKey && (x.Status == QueueStatus.Running || x.Status == QueueStatus.Sending)))
                _host.SetStatus($"「{t.VsName}」仍在忙，任务 #{t.Id} 会在空闲后自动发布");
            else if (DispatchBlocker(t) is QueuedTask blocker)
                _host.SetStatus($"任务 #{t.Id} 等待前序 #{blocker.Id}（{TaskStateMachine.StatusText(blocker, _clock())}）/ Task #{t.Id} is blocked by predecessor #{blocker.Id}");
            Pump();
        }

        private void PrepareManualRecheck(QueuedTask task)
        {
            if (!CanRun(task)) _manual.Add(task);
            ClearManualWait(task);
            _yielded.Remove(task);
            var target = task.Status == QueueStatus.WaitingVs ? _host.FindTargetVs(task) : ResolveTarget(task, true);
            (_host as IManualChatRefreshHost)?.RefreshManualChat(target);
            AppLog.Write(AppLog.TasksFile, $"任务 #{task.Id} 手动重新检查，未发送；仍保护草稿及前序 / Manual recheck requested; not sent; drafts and predecessors protected");
            _host.SetStatus($"任务 #{task.Id} 重新检查并推送：请先核实历史送达并处理目标草稿，不会强制覆盖 / Recheck and send: verify prior delivery and resolve target drafts; never force overwrite");
            _host.QueueActivityChanged(HasDispatchActivity);
        }
    }
}
