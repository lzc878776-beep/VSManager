using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VSManager
{
    /// <summary>
    /// 一次发送的六个步骤：排队 → 定位输入框 → 填入 → 提交 → 确认送达 → 恢复焦点。
    /// The six steps of one send: queue → locate input → fill → submit → confirm delivery → restore focus.
    /// </summary>
    public enum SendStage { Queue, Locate, Fill, Submit, Confirm, Focus }

    /// <summary>步骤状态。/ State of a step.</summary>
    public enum SendStageState
    {
        /// <summary>未执行到。/ Not reached.</summary>
        NotReached,
        /// <summary>已完成。/ Completed.</summary>
        Ok,
        /// <summary>尚未提交，等待条件解除后自动继续。/ Not submitted; continues automatically once the condition clears.</summary>
        Waiting,
        /// <summary>失败（未提交或确定未送达）。/ Failed (not submitted or definitely not delivered).</summary>
        Failed,
        /// <summary>送达不确定：禁止盲目重发。/ Delivery uncertain: blind resending is forbidden.</summary>
        Uncertain,
        /// <summary>不需要执行（例如用户本来就在该 VS 中）。/ Not needed (e.g. the user was already in that VS).</summary>
        Skipped
    }

    /// <summary>
    /// 一次发送的诊断记录：各步骤状态、出问题的步骤、处理建议与原始步骤日志。
    /// Diagnosis of one send: state of each step, the problem step, advice and the raw step log.
    /// </summary>
    public sealed class SendDiagnosisRecord
    {
        public long Seq { get; internal set; }
        public DateTime Time { get; internal set; }
        public int Pid { get; internal set; }
        public string VsName { get; internal set; }
        public int? TaskId { get; internal set; }
        public string Result { get; internal set; }
        public long ElapsedMs { get; internal set; }
        public SendStage Reached { get; internal set; }
        public SendStageState[] States { get; internal set; } = new SendStageState[SendDiagnosis.StageCount];
        public string FocusDetail { get; internal set; }
        public IReadOnlyList<string> Steps { get; internal set; } = Array.Empty<string>();

        public bool Delivered => SendRetryPolicy.IsDelivered(Result);

        /// <summary>送达不确定（禁止盲目重发）。/ Delivery uncertain (blind resending is forbidden).</summary>
        public bool Uncertain => States.Contains(SendStageState.Uncertain);

        /// <summary>出问题的步骤：优先送达相关步骤，其次焦点；全部正常时为 null。/ Problem step: delivery steps first, then focus; null when all is fine.</summary>
        public SendStage? ProblemStage
        {
            get
            {
                for (int i = 0; i < States.Length; i++)
                    if (States[i] == SendStageState.Failed || States[i] == SendStageState.Uncertain || States[i] == SendStageState.Waiting)
                        return (SendStage)i;
                return null;
            }
        }

        public string Hint => SendDiagnosis.Hint(this);

        /// <summary>单行阶段摘要，例如「排队✓ → 定位✓ → 填入✗ → …」。/ One-line stage summary.</summary>
        public string StageLine => SendDiagnosis.StageLine(States);

        /// <summary>完整诊断文本（可复制给 AI 或写入日志）。/ Full diagnosis text (can be copied to the AI or logged).</summary>
        public string Describe(bool withSteps = true)
        {
            var sb = new StringBuilder();
            sb.Append(Time.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ").Append(VsName ?? ("pid " + Pid));
            if (TaskId.HasValue) sb.Append("  任务 / Task #").Append(TaskId.Value);
            sb.Append("  ").Append(ElapsedMs).Append("ms\r\n");
            sb.Append("结论 / Verdict：").Append(SendDiagnosis.Verdict(this)).Append("\r\n");
            sb.Append("步骤 / Steps：").Append(StageLine).Append("\r\n");
            for (int i = 0; i < SendDiagnosis.StageCount; i++)
            {
                var s = (SendStage)i;
                sb.Append("  ").Append(SendDiagnosis.Mark(States[i])).Append(' ').Append(SendDiagnosis.Name(s))
                  .Append(" — ").Append(SendDiagnosis.StateName(States[i]));
                if (s == SendStage.Focus && !string.IsNullOrEmpty(FocusDetail)) sb.Append("（").Append(FocusDetail).Append("）");
                sb.Append("\r\n");
            }
            sb.Append("结果 / Result：").Append(Result).Append("\r\n");
            if (Hint != null) sb.Append("建议 / Advice：").Append(Hint).Append("\r\n");
            if (withSteps && Steps.Count > 0)
            {
                sb.Append("明细 / Details：\r\n");
                foreach (var line in Steps) sb.Append("  ").Append(line).Append("\r\n");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 发送诊断：按发送过程中进入的最后一步与发送结果推断各步骤状态，并保存最近的记录供「发送诊断」面板与 AI 读取。
    /// Send diagnosis: infers the state of each step from the last step entered and the send result, and keeps recent records
    /// for the "Send diagnostics" panel and the AI.
    /// </summary>
    public static class SendDiagnosis
    {
        public const int StageCount = 6;

        /// <summary>内存中保留的最近记录数。/ Number of recent records kept in memory.</summary>
        public const int MaxRecords = 100;

        private static readonly object Lock = new object();
        private static readonly List<SendDiagnosisRecord> Items = new List<SendDiagnosisRecord>();
        private static long _seq;

        /// <summary>记录新增或更新时触发（可能在任意线程）。/ Raised when a record is added or updated (any thread).</summary>
        public static event Action Changed;

        public static long LastSeq { get { lock (Lock) return _seq; } }

        public static string Name(SendStage s)
        {
            switch (s)
            {
                case SendStage.Queue: return "排队 / Queue";
                case SendStage.Locate: return "定位输入框 / Locate input";
                case SendStage.Fill: return "填入 / Fill";
                case SendStage.Submit: return "提交 / Submit";
                case SendStage.Confirm: return "确认送达 / Confirm delivery";
                default: return "恢复焦点 / Restore focus";
            }
        }

        private static string ShortName(SendStage s)
        {
            switch (s)
            {
                case SendStage.Queue: return "排队";
                case SendStage.Locate: return "定位";
                case SendStage.Fill: return "填入";
                case SendStage.Submit: return "提交";
                case SendStage.Confirm: return "确认";
                default: return "焦点";
            }
        }

        public static string Mark(SendStageState s)
        {
            switch (s)
            {
                case SendStageState.Ok: return "✓";
                case SendStageState.Waiting: return "⏳";
                case SendStageState.Failed: return "✗";
                case SendStageState.Uncertain: return "⚠";
                case SendStageState.Skipped: return "–";
                default: return "○";
            }
        }

        public static string StateName(SendStageState s)
        {
            switch (s)
            {
                case SendStageState.Ok: return "完成 / done";
                case SendStageState.Waiting: return "等待中，未提交 / waiting, not submitted";
                case SendStageState.Failed: return "失败 / failed";
                case SendStageState.Uncertain: return "送达不确定 / delivery uncertain";
                case SendStageState.Skipped: return "无需执行 / not needed";
                default: return "未执行到 / not reached";
            }
        }

        public static string StageLine(SendStageState[] states) =>
            string.Join(" → ", Enumerable.Range(0, StageCount).Select(i => ShortName((SendStage)i) + Mark(states[i])));

        /// <summary>
        /// 送达不确定的结果：带「待核实」前缀、提示可能未送达，或已提交后才失败。
        /// Uncertain results: the "verify" prefix, a "may not be delivered" note, or a failure after submission.
        /// </summary>
        public static bool IsUncertainResult(string result) =>
            result != null && (result.StartsWith(ManualChatProtection.UncertainPrefix, StringComparison.Ordinal)
                || result.IndexOf("可能未送达", StringComparison.Ordinal) >= 0
                || result.IndexOf("可能已送达", StringComparison.Ordinal) >= 0
                || result.IndexOf("不要重复发送", StringComparison.Ordinal) >= 0);

        /// <summary>
        /// 按进入的最后一步与结果推断各步骤状态。focus 为 null 表示焦点步骤未记录。
        /// Infers each step's state from the last step entered and the result; a null focus means the focus step was not recorded.
        /// </summary>
        public static SendStageState[] Evaluate(SendStage reached, string result, SendStageState? focus)
        {
            var states = new SendStageState[StageCount];
            int last = Math.Min((int)reached, (int)SendStage.Confirm);
            if (SendRetryPolicy.IsDelivered(result))
            {
                for (int i = 0; i <= (int)SendStage.Confirm; i++) states[i] = SendStageState.Ok;
            }
            else
            {
                SendStageState problem;
                if (IsUncertainResult(result)) problem = SendStageState.Uncertain;
                else if (SendRetryPolicy.IsBlocked(result)) problem = SendStageState.Waiting;
                else if (last == (int)SendStage.Confirm
                    || (last == (int)SendStage.Submit && result != null && result.StartsWith("发送失败", StringComparison.Ordinal)))
                    problem = SendStageState.Uncertain;
                else problem = SendStageState.Failed;
                for (int i = 0; i < last; i++) states[i] = SendStageState.Ok;
                states[last] = problem;
            }
            states[(int)SendStage.Focus] = focus ?? SendStageState.NotReached;
            return states;
        }

        /// <summary>一句话结论。/ One-sentence verdict.</summary>
        public static string Verdict(SendDiagnosisRecord r)
        {
            var p = r.ProblemStage;
            if (r.Delivered)
                return p == SendStage.Focus ? "已送达，但未能切回原窗口 / Delivered, but the original window was not restored" : "已送达 / Delivered";
            if (p == null) return "未完成 / Not completed";
            var state = r.States[(int)p.Value];
            string at = Name(p.Value);
            if (state == SendStageState.Waiting) return $"尚未提交，停在「{at}」等待 / Not submitted; waiting at \"{at}\"";
            if (state == SendStageState.Uncertain) return $"送达不确定（{at}），禁止盲目重发 / Delivery uncertain ({at}); do not resend blindly";
            return $"失败于「{at}」，未送达 / Failed at \"{at}\"; not delivered";
        }

        /// <summary>针对出问题步骤的处理建议；全部正常时为 null。/ Advice for the problem step; null when all is fine.</summary>
        public static string Hint(SendDiagnosisRecord r)
        {
            var p = r.ProblemStage;
            if (p == null) return null;
            var state = r.States[(int)p.Value];
            if (state == SendStageState.Uncertain)
                return "先在目标 VS 的 Copilot 对话中查看这条消息是否已出现、输入框里是否还有草稿；确认没有收到后再在任务清单中手动重新排队。AI 助手不能对这类任务直接重试。"
                    + " / First check the target VS Copilot chat for this message and the input box for a leftover draft; requeue manually from the task list only after confirming it was not received. The AI assistant cannot retry such tasks directly.";
            if (state == SendStageState.Waiting)
                return "尚未提交任何内容，条件解除后会自动继续（目标忙、有草稿、弹窗、用户正在操作或剪贴板被占用），无需重发。"
                    + " / Nothing was submitted; it continues automatically once the condition clears (target busy, draft, dialog, user activity or clipboard in use). Do not resend.";
            switch (p.Value)
            {
                case SendStage.Queue:
                    return "发送前检查未通过（目标 VS 已关闭、Copilot 正在运行、另一条消息正在发送或任务已变化），未触碰 VS；处理后重新排队即可。"
                        + " / Pre-send checks failed (target VS closed, Copilot running, another send in progress or the task changed); VS was not touched. Requeue after fixing.";
                case SendStage.Locate:
                    return "找不到 Copilot 对话窗格或输入框：在 VS 中打开 Copilot 对话，确认不是停在历史列表，关闭遮挡的弹窗后重新排队；未提交任何内容。"
                        + " / The Copilot pane or input box was not found: open the Copilot chat in VS, make sure it is not showing the history list, close covering dialogs, then requeue. Nothing was submitted.";
                case SendStage.Fill:
                    return "内容未能完整写入输入框：检查 VS 输入框里的残留草稿、剪贴板是否被其他程序占用、输入法状态；清理草稿后再重新排队。"
                        + " / The content could not be fully written into the input: check the VS input for a leftover draft, whether another program holds the clipboard, and the IME state; clear the draft before requeueing.";
                case SendStage.Submit:
                    return "未能提交（回车 / 发送按钮未生效或焦点被切走）：内容通常保留在 VS 输入框中，可在 VS 中直接按发送，或清理草稿后重新排队。"
                        + " / Submission did not happen (Enter / Send had no effect or focus moved away): the content usually stays in the VS input; press Send in VS, or clear the draft and requeue.";
                case SendStage.Confirm:
                    return "已提交但未确认送达：先查看 VS 对话，不要直接重发。"
                        + " / Submitted but not confirmed: check the VS chat before resending.";
                default:
                    return (r.Delivered ? "消息已送达，只是 VS 留在了前台" : "焦点未能切回")
                        + "：可能被前台锁拒绝或用户正在操作；不影响送达，无需重发。"
                        + " / " + (r.Delivered ? "The message was delivered; VS just stayed in front" : "Focus was not restored")
                        + ": the foreground lock may have refused or the user was busy; delivery is unaffected, do not resend.";
            }
        }

        /// <summary>新增一条记录并返回。/ Adds a record and returns it.</summary>
        public static SendDiagnosisRecord Add(int pid, string vsName, SendStage reached, string result, SendStageState? focus,
            string focusDetail, long elapsedMs, IEnumerable<string> steps, DateTime? time = null)
        {
            var r = new SendDiagnosisRecord
            {
                Time = time ?? DateTime.Now,
                Pid = pid,
                VsName = vsName,
                Result = result ?? "",
                ElapsedMs = elapsedMs,
                Reached = reached,
                States = Evaluate(reached, result, focus),
                FocusDetail = focusDetail,
                Steps = (steps ?? Enumerable.Empty<string>()).ToList()
            };
            lock (Lock)
            {
                r.Seq = ++_seq;
                Items.Add(r);
                if (Items.Count > MaxRecords) Items.RemoveRange(0, Items.Count - MaxRecords);
            }
            Raise();
            return r;
        }

        /// <summary>最近的记录（新的在前）。/ Recent records, newest first.</summary>
        public static IReadOnlyList<SendDiagnosisRecord> Recent(int max = MaxRecords)
        {
            lock (Lock) return Items.AsEnumerable().Reverse().Take(Math.Max(0, max)).ToList();
        }

        /// <summary>某任务最近一次发送的记录；没有时为 null。/ Latest send record of a task; null when none.</summary>
        public static SendDiagnosisRecord LatestForTask(int taskId)
        {
            lock (Lock) return Items.LastOrDefault(x => x.TaskId == taskId);
        }

        /// <summary>某 VS 在 sinceSeq 之后的记录。/ Records of a VS added after sinceSeq.</summary>
        public static IReadOnlyList<SendDiagnosisRecord> Since(int pid, long sinceSeq)
        {
            lock (Lock) return Items.Where(x => x.Pid == pid && x.Seq > sinceSeq).ToList();
        }

        /// <summary>为 sinceSeq 之后的记录补充显示名与任务编号。/ Adds the display name and task id to records added after sinceSeq.</summary>
        public static void Annotate(int pid, long sinceSeq, string vsName, int? taskId)
        {
            bool changed = false;
            lock (Lock)
                foreach (var r in Items.Where(x => x.Pid == pid && x.Seq > sinceSeq))
                {
                    if (!string.IsNullOrEmpty(vsName) && r.VsName != vsName) { r.VsName = vsName; changed = true; }
                    if (taskId.HasValue && r.TaskId != taskId) { r.TaskId = taskId; changed = true; }
                }
            if (changed) Raise();
        }

        /// <summary>
        /// 更新最近一条记录的焦点步骤（外层切回或延迟切回的结果），以最新结果为准并追加说明。
        /// Updates the focus step of the latest record (outer or delayed switch-back); the latest outcome wins and its note is appended.
        /// </summary>
        public static void UpdateFocus(int pid, long sinceSeq, bool ok, string detail)
        {
            SendDiagnosisRecord r;
            lock (Lock)
            {
                r = Items.LastOrDefault(x => x.Pid == pid && x.Seq > sinceSeq);
                if (r == null) return;
                r.States[(int)SendStage.Focus] = ok ? SendStageState.Ok : SendStageState.Failed;
                r.FocusDetail = string.IsNullOrEmpty(r.FocusDetail) ? detail : r.FocusDetail + "；" + detail;
            }
            Raise();
        }

        /// <summary>清空内存中的记录（测试用）。/ Clears in-memory records (for tests).</summary>
        internal static void Clear()
        {
            lock (Lock) Items.Clear();
            Raise();
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); } catch { }
        }

        /// <summary>
        /// 失败原因是否表示送达不确定：此类任务禁止 AI 直接重试，只能由用户核实后手动重新排队。
        /// Whether a failure means uncertain delivery: the AI may not retry such tasks directly; only the user may requeue after verifying.
        /// </summary>
        public static bool IsUncertainFailure(QueuedTask t) =>
            t != null && t.Status == QueueStatus.Failed && IsUncertainResult(t.Error);

        /// <summary>拒绝 AI 盲目重发的说明。/ Refusal text for blind AI resends.</summary>
        public static string UncertainRetryRefusal(int id) =>
            $"已拒绝：任务 #{id} 的送达结果不确定（可能已提交到 Copilot），原样或补充后重发都可能重复执行。请先用 read_vs_chat 查看目标 VS 对话确认是否已收到、用 read_send_diagnostics 查看出错步骤，再把结论交给用户，由用户在任务清单中手动重新排队。"
            + $" / Refused: delivery of task #{id} is uncertain (it may already have been submitted to Copilot); resending, with or without extra info, could run it twice. Check the target VS chat with read_vs_chat and the failing step with read_send_diagnostics, then hand the conclusion to the user, who may requeue it manually from the task list.";
    }

    /// <summary>发送诊断的文字报告（供 AI 工具 read_send_diagnostics 使用）。/ Text report of send diagnoses (for the AI tool read_send_diagnostics).</summary>
    public static class SendDiagnosisReport
    {
        public const int MaxCount = 20;

        /// <param name="records">记录，新的在前。/ Records, newest first.</param>
        /// <param name="taskId">大于 0 时只看该任务。/ When positive, only this task.</param>
        /// <param name="count">返回条数。/ Number of records returned.</param>
        public static string Format(IEnumerable<SendDiagnosisRecord> records, int taskId, int count)
        {
            count = Math.Max(1, Math.Min(MaxCount, count <= 0 ? 5 : count));
            var list = (records ?? Enumerable.Empty<SendDiagnosisRecord>())
                .Where(r => taskId <= 0 || r.TaskId == taskId).Take(count).ToList();
            if (list.Count == 0)
                return taskId > 0
                    ? $"没有任务 #{taskId} 的发送诊断记录（只保留本次运行以来最近 {SendDiagnosis.MaxRecords} 次发送；更早的请看发送日志）/ No send diagnosis for task #{taskId} (only the latest {SendDiagnosis.MaxRecords} sends since startup are kept; see the send log for older ones)"
                    : "暂无发送诊断记录 / No send diagnoses yet";
            var sb = new StringBuilder();
            foreach (var r in list)
            {
                sb.Append(r.Describe(withSteps: list.Count == 1 || r.ProblemStage != null));
                if (r.Uncertain) sb.Append("⚠ 禁止重发：先用 read_vs_chat 核实目标对话，再交给用户决定 / Do not resend: verify the target chat with read_vs_chat, then let the user decide\r\n");
                sb.Append("\r\n");
            }
            return sb.ToString().TrimEnd();
        }
    }
}