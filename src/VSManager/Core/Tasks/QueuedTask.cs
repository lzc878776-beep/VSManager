using System;
using System.Linq;
using System.Runtime.Serialization;

namespace VSManager
{
    /// <summary>
    /// 任务状态常量（写入 tasks.json 的取值，不可更改）。
    /// Task status constants (the values stored in tasks.json; must not change).
    /// </summary>
    public static class QueueStatus
    {
        public const string Waiting = "waiting", Sending = "sending", Running = "running", Done = "done", Failed = "failed", Cancelled = "cancelled";

        /// <summary>
        /// 等待目标 VS：目标解决方案尚未打开，任务已暂存；对应 VS 打开后转为排队并自动推送。
        /// Waiting for the target VS: the target solution is not open yet and the task is parked; once that VS opens the task
        /// goes back to waiting and is pushed automatically.
        /// </summary>
        public const string WaitingVs = "waiting_vs";

        /// <summary>未结束（排队 / 等待目标 VS / 发送中 / 执行中）。/ Not finished yet (waiting / waiting for VS / sending / running).</summary>
        public static bool Active(string s) => s == Waiting || s == WaitingVs || s == Sending || s == Running;

        /// <summary>
        /// 待验证：改动已完成，但尚未在运行环境中验证，或需要用户测试、运行或确认；与「已完成」「失败」并列的结束状态。
        /// Awaiting verification: changes are done but not yet verified at runtime, or need user testing, running or confirmation; a terminal status alongside done and failed.
        /// </summary>
        public const string Unverified = "unverified";

        /// <summary>已产出结果（已完成或未验证）；是否阻塞后续由接续等级决定。/ Produced a result (done or unverified); whether it blocks successors depends on the continuation level.</summary>
        public static bool Delivered(string s) => s == Done || s == Unverified;

        public static bool Known(string s) => Active(s) || s == Done || s == Unverified || s == Failed || s == Cancelled;
    }

    /// <summary>
    /// 任务清单中的一项：一律按编号排队，前序结束且目标可用时按策略自动发布。字段名即 tasks.json 的字段名。
    /// One task-list entry: always queued by ID and dispatched under policy after predecessors finish and the target is ready.
    /// Field names are the tasks.json field names.
    /// </summary>
    /// <summary>任务题目规则：单行，最多 20 字。/ Task title rules: one line, at most 20 characters.</summary>
    public static class TaskTitle
    {
        public const int MaxLength = 20;

        /// <summary>规范为单行并截断到上限；为空时返回 null。/ Normalizes to one line and truncates to the limit; null when empty.</summary>
        public static string Normalize(string title)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in title ?? "") sb.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
            string result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " +", " ").Trim();
            if (result.Length > MaxLength) result = result.Substring(0, MaxLength).TrimEnd();
            return result.Length == 0 ? null : result;
        }
    }

    [DataContract]
    public sealed class QueuedTask
    {
        [DataMember] public int Id;
        [DataMember] public string VsKey;
        [DataMember] public string VsName;
        // 显式提及固定进程会话与解决方案，不回退到同名实例。/ Explicit mentions pin process session and solution, never a namesake.
        [DataMember(EmitDefaultValue = false)] public string ExplicitInstanceKey;
        [DataMember(EmitDefaultValue = false)] public string ExplicitSolutionPath;
        public bool HasExplicitTarget => ExplicitInstanceKey != null || ExplicitSolutionPath != null;
        /// <summary>
        /// 普通任务入队 / 发送时所选的 VS 实例（见 <see cref="TaskTarget"/>）：同一解决方案多开时据此回到原实例，不改投其他实例。
        /// The VS instance chosen when an ordinary task was queued / sent (see <see cref="TaskTarget"/>): with one solution open in
        /// several instances the task returns to that instance and is never redirected to another one.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public string TargetInstanceKey;
        public bool MatchesExplicitTarget(VsInstance v) => v != null && !string.IsNullOrEmpty(ExplicitInstanceKey)
            && v.InstanceKey == ExplicitInstanceKey && ExplicitSolutionPath != null
            && string.Equals(v.SolutionPath ?? "", ExplicitSolutionPath, StringComparison.OrdinalIgnoreCase);
        [DataMember] public string Text;
        /// <summary>任务题目（AI 发布时总结，最多 <see cref="TaskTitle.MaxLength"/> 字）；未提供时为 null。/ Task title (summarized by the AI on submission, at most <see cref="TaskTitle.MaxLength"/> characters); null when not provided.</summary>
        [DataMember(EmitDefaultValue = false)] public string Title;
        [DataMember] public string Source;
        [DataMember] public string Status;
        [DataMember] public DateTime Created;
        [DataMember] public DateTime? Started;
        [DataMember] public DateTime? Finished;
        [DataMember] public string Result;
        [DataMember] public string Error;
        [DataMember] public int Attempts;
        // 保留旧文件字段；新任务和重发均按编号排在队尾。/ Keep the legacy field; new tasks and resends are ordered by id at the tail.
        [DataMember(EmitDefaultValue = false)] public int QueueOrder;
        [DataMember(EmitDefaultValue = false)] public int[] Replaces;
        [DataMember(EmitDefaultValue = false)] public string CompletionToken;
        [DataMember(EmitDefaultValue = false)] public WorktreeInfo Worktree;
        [DataMember(EmitDefaultValue = false)] public bool IsWorktreeMerge;
        [DataMember(EmitDefaultValue = false)] public bool WorktreeCounted;
        [DataMember(EmitDefaultValue = false)] public int WorktreeBatch;
        public int Order => Id;
        /// <summary>
        /// 目标解决方案别名（按登记表别名分派时记录，用于显示等待原因）；普通任务为 null，不写入 tasks.json。
        /// Target solution alias (recorded when dispatched by a registry alias, used to show the waiting reason); null for
        /// ordinary tasks and then not written to tasks.json.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public string Target;
        /// <summary>
        /// 任务附件引用（只含元数据与相对路径，不含二进制内容）；无附件时为 null，不写入 tasks.json。
        /// Task attachment references (metadata and relative paths only, no binary content); null without attachments and then
        /// not written to tasks.json.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public AttachmentRef[] Attachments;
        /// <summary>最近一次发送的附件送达说明（例如图片未送达的原因）。/ Attachment delivery note of the last send (e.g. why images were not delivered).</summary>
        [DataMember(EmitDefaultValue = false)] public string AttachmentNote;
        /// <summary>失败类别（见 <see cref="VSManager.FailureKind"/>），未失败时为 null。/ Failure category (see <see cref="VSManager.FailureKind"/>); null unless failed.</summary>
        [DataMember(EmitDefaultValue = false)] public string FailureKind;
        /// <summary>旧字段：已完成但需要用户验证；加载时并入「待验证」状态，新记录不再使用。/ Legacy field: done but awaiting user verification; migrated to the awaiting-verification status on load and no longer set.</summary>
        [DataMember(EmitDefaultValue = false)] public bool NeedsUser;
        /// <summary>前一次失败尝试的反馈摘要，随下一次发送附给 Copilot。/ Feedback summary of the previous failed attempt, sent to Copilot with the next attempt.</summary>
        [DataMember(EmitDefaultValue = false)] public string PriorFailure;
        /// <summary>
        /// 已手动放行：失败或待验证的条目不再暂停后续任务（结果与历史保持不变）；重新排队时清除。
        /// Released manually: a failed or awaiting-verification entry no longer pauses successors (outcome and history are kept);
        /// cleared on requeue.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public bool Released;
        /// <summary>重试时插入的补充信息（多次补充按顺序累积），随任务发送给 Copilot。/ Supplementary info inserted on retries (accumulated in order), sent to Copilot with the task.</summary>
        [DataMember(EmitDefaultValue = false)] public string Supplement;
        /// <summary>已插入补充信息重试的次数。/ Number of retries with supplementary info.</summary>
        [DataMember(EmitDefaultValue = false)] public int SupplementCount;
        /// <summary>本任务以内容失败或待验证结束的 Copilot 执行次数（投递、读取失败与本轮中断不计）。/ Copilot runs of this task that ended as content failures or awaiting verification (delivery, read failures and interrupted runs excluded).</summary>
        [DataMember(EmitDefaultValue = false)] public int ContentRuns;
        /// <summary>AI 助手对非内容类失败直接重试的次数。/ Direct AI retries after non-content failures.</summary>
        [DataMember(EmitDefaultValue = false)] public int RecoveryRetries;
        /// <summary>创建时所取代的重发链已用掉的执行次数，用于告诉 Copilot 当前是第几轮。/ Runs already spent by the replaced resend chain at creation; tells Copilot which round this is.</summary>
        [DataMember(EmitDefaultValue = false)] public int PriorRuns;
        /// <summary>待确认时需要用户处理 / 验证的内容，其他状态为 null。/ What the user must handle or verify when awaiting confirmation; null otherwise.</summary>
        [DataMember(EmitDefaultValue = false)] public string PendingNote;
        /// <summary>失败时的原因说明，其他状态为 null。/ Why the task failed; null otherwise.</summary>
        [DataMember(EmitDefaultValue = false)] public string FailureReason;
        /// <summary>
        /// 测试清单：未验证 / 待用户验证的任务需要用户在环境中实测的项目；全部勾选后任务转为已完成。没有时为 null。
        /// Test checklist: items the user must test in the environment for unverified / awaiting-verification tasks; checking all of them
        /// completes the task. Null when there are none.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public TaskTestItem[] TestItems;
        /// <summary>
        /// 执行中被用户暂停中断后重新排队：再次发送时提示 Copilot 在已有改动基础上继续；该次送达后清除。
        /// Interrupted by the user's pause while running and requeued: the next send tells Copilot to continue from the existing
        /// changes; cleared once that send is delivered.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public bool Interrupted;
        /// <summary>
        /// 失败时本轮 Copilot 的完整回复（含过程步骤，见 <see cref="TaskReply"/>），供主控 AI 分析原因；重新排队时清除。
        /// The whole Copilot turn on failure (steps included, see <see cref="TaskReply"/>) for the main AI to analyze; cleared on requeue.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public string Reply;
        /// <summary>从完整回复识别出的执行异常（见 <see cref="VSManager.RunIssue"/>），没有时为 null。/ Run issue detected from the whole turn (see <see cref="VSManager.RunIssue"/>); null when none.</summary>
        [DataMember(EmitDefaultValue = false)] public string RunIssue;
        /// <summary>
        /// 本轮中断后重试时的接续说明（含主控 AI 的补充），随下一次发送附给 Copilot；该次送达后清除。
        /// Continuation note for a retry after an interrupted run (including the main AI's additions), sent with the next
        /// attempt and cleared once delivered.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public string ResumeNote;
        /// <summary>
        /// Copilot 对话已被清空或换成新线程：下一次发送改为提示 Copilot 重新阅读相关代码与文档了解进度，而不是依赖之前的对话；该次送达后清除。
        /// The Copilot conversation was cleared or replaced by a new thread: the next send tells Copilot to re-read the relevant
        /// code and docs to learn the progress instead of relying on the earlier conversation; cleared once delivered.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public bool FreshContext;

        public bool HasAttachments => Attachments != null && Attachments.Length > 0;

        /// <summary>本次完成的完整回复（仅内存，不写入 tasks.json）。/ Full reply of this completion (memory only, not written to tasks.json).</summary>
        public string FullResult;

        /// <summary>运行期：执行中是否观察到 Copilot 忙碌。/ Runtime only: whether Copilot was seen busy while running.</summary>
        [IgnoreDataMember] public bool SawBusy;
        /// <summary>运行期：下次重试时间。/ Runtime only: next retry time.</summary>
        [IgnoreDataMember] public DateTime NextTry;
        /// <summary>运行期：已送达任务跳过失败前序的提示。/ Runtime only: notice that a delivered task skipped failed predecessors.</summary>
        [IgnoreDataMember] public string PredecessorNotice;
        /// <summary>会话内等待原因，不持久化、不改变业务状态。/ Session wait reason; never persisted and never changes business state.</summary>
        [IgnoreDataMember] public string ManualChatWaitReason;

        public bool FromAgent => Source == "AI";

        /// <summary>复制持久化字段（不含运行期字段）。/ Copies the persisted fields (runtime fields excluded).</summary>
        public QueuedTask Clone() => new QueuedTask
        {
            Id = Id, VsKey = VsKey, VsName = VsName, Text = Text, Title = Title, Source = Source, Status = Status, Created = Created,
            ExplicitInstanceKey = ExplicitInstanceKey, ExplicitSolutionPath = ExplicitSolutionPath, TargetInstanceKey = TargetInstanceKey,
            Started = Started, Finished = Finished, Result = Result, Error = Error, Attempts = Attempts, Target = Target,
            QueueOrder = QueueOrder, Replaces = Replaces == null ? null : (int[])Replaces.Clone(),
            CompletionToken = CompletionToken, Worktree = Worktree?.Clone(), IsWorktreeMerge = IsWorktreeMerge,
            WorktreeCounted = WorktreeCounted, WorktreeBatch = WorktreeBatch,
            Attachments = Attachments?.Select(a => a?.Clone()).ToArray(), AttachmentNote = AttachmentNote,
            FailureKind = FailureKind, NeedsUser = NeedsUser, PriorFailure = PriorFailure,
            Released = Released, Supplement = Supplement, SupplementCount = SupplementCount,
            ContentRuns = ContentRuns, RecoveryRetries = RecoveryRetries, PriorRuns = PriorRuns,
            PendingNote = PendingNote, FailureReason = FailureReason,
            TestItems = TestItems?.Select(i => i?.Clone()).ToArray(), Interrupted = Interrupted,
            Reply = Reply, RunIssue = RunIssue, ResumeNote = ResumeNote, FreshContext = FreshContext
        };
    }

    /// <summary>测试清单中的一项（由用户手动勾选）。/ One test checklist item (checked off manually by the user).</summary>
    [DataContract]
    public sealed class TaskTestItem
    {
        [DataMember] public string Text;
        [DataMember(EmitDefaultValue = false)] public bool Checked;

        public TaskTestItem Clone() => new TaskTestItem { Text = Text, Checked = Checked };
    }
}
