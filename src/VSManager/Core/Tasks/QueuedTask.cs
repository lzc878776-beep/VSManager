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

        public static bool Known(string s) => Active(s) || s == Done || s == Failed || s == Cancelled;
    }

    /// <summary>
    /// 任务清单中的一项：一律按编号排队，前序结束且目标可用时按策略自动发布。字段名即 tasks.json 的字段名。
    /// One task-list entry: always queued by ID and dispatched under policy after predecessors finish and the target is ready.
    /// Field names are the tasks.json field names.
    /// </summary>
    [DataContract]
    public sealed class QueuedTask
    {
        [DataMember] public int Id;
        [DataMember] public string VsKey;
        [DataMember] public string VsName;
        [DataMember] public string Text;
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

        public bool HasAttachments => Attachments != null && Attachments.Length > 0;

        /// <summary>运行期：执行中是否观察到 Copilot 忙碌。/ Runtime only: whether Copilot was seen busy while running.</summary>
        [IgnoreDataMember] public bool SawBusy;
        /// <summary>运行期：下次重试时间。/ Runtime only: next retry time.</summary>
        [IgnoreDataMember] public DateTime NextTry;
        /// <summary>运行期：已送达任务跳过失败前序的提示。/ Runtime only: notice that a delivered task skipped failed predecessors.</summary>
        [IgnoreDataMember] public string PredecessorNotice;

        public bool FromAgent => Source == "AI";

        /// <summary>复制持久化字段（不含运行期字段）。/ Copies the persisted fields (runtime fields excluded).</summary>
        public QueuedTask Clone() => new QueuedTask
        {
            Id = Id, VsKey = VsKey, VsName = VsName, Text = Text, Source = Source, Status = Status, Created = Created,
            Started = Started, Finished = Finished, Result = Result, Error = Error, Attempts = Attempts, Target = Target,
            QueueOrder = QueueOrder, Replaces = Replaces == null ? null : (int[])Replaces.Clone(),
            CompletionToken = CompletionToken, Worktree = Worktree?.Clone(), IsWorktreeMerge = IsWorktreeMerge,
            WorktreeCounted = WorktreeCounted, WorktreeBatch = WorktreeBatch,
            Attachments = Attachments?.Select(a => a?.Clone()).ToArray(), AttachmentNote = AttachmentNote
        };
    }
}
