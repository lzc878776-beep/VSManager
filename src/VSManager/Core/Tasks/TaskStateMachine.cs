using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    /// <summary>执行中任务的检查结论。/ Verdict of checking a running task.</summary>
    public enum RunningVerdict
    {
        /// <summary>继续等待。/ Keep waiting.</summary>
        None,
        /// <summary>观察到 Copilot 忙碌（记录下来，等它回到空闲时即完成）。/ Copilot was seen busy (remember it; finished when idle again).</summary>
        SawBusy,
        /// <summary>目标 VS 已关闭超过 15 秒：任务失败。/ Target VS has been closed for more than 15 s: the task fails.</summary>
        VsClosed,
        /// <summary>No execution was observed; the result is unconfirmed, not successful.</summary>
        Unconfirmed
    }

    /// <summary>
    /// 任务状态机：集中定义任务状态的全部流转，界面与调度器只调用这里的方法修改状态。
    /// 排队(waiting) → 发送中(sending) → 执行中(running) → 已完成(done) 或 未验证(unverified，已实现但未实际运行验证)；
    /// 发送失败 → 失败(failed)；未提交的保护状态 → 排队等待；失败 / 已取消仅由用户重新排队。
    /// 等待目标 VS(waiting_vs) → 目标打开后转为排队(waiting)，或 → 已取消。
    /// Task state machine: the single place that defines every status transition; the UI and the dispatcher only change
    /// status through these methods.
    /// waiting → sending → running → done or unverified (implemented, not yet verified at runtime); send failure → failed; pre-submission protection → waiting; waiting / running → cancelled;
    /// failed / cancelled → waiting again (retry); waiting_vs → waiting once the target VS opens, or → cancelled.
    /// </summary>
    public static class TaskStateMachine
    {
        /// <summary>VS 关闭多久后判定任务失败。/ How long a closed VS is tolerated before the task fails.</summary>
        public static readonly TimeSpan VsClosedTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Timeout for an unconfirmed start; never treated as successful completion.</summary>
        public static readonly TimeSpan NeverBusyTimeout = TimeSpan.FromSeconds(30);

        /// <summary>VS 已关闭时记录的错误。/ Error recorded when the VS was closed.</summary>
        public const string VsClosedError = "目标 VS 已关闭，无法确认任务结果";

        /// <summary>排队 → 发送中，并累计尝试次数。/ waiting → sending, counting the attempt.</summary>
        public static bool BeginSend(QueuedTask t, string vsName)
        {
            if (t == null || t.Status != QueueStatus.Waiting) return false;
            t.Status = QueueStatus.Sending;
            t.VsName = vsName;
            t.Attempts++;
            t.CompletionToken = Guid.NewGuid().ToString("N");
            return true;
        }

        /// <summary>
        /// 送达 → 执行中；保护状态 → 等待；其他结果立即失败。
        /// Delivered → running; protection → waiting; every other result fails immediately.
        /// </summary>
        public static SendDecision ApplySendResult(QueuedTask t, string result, DateTime now)
        {
            var d = SendRetryPolicy.Decide(t.Attempts, result);
            // 窗格未就绪：每轮都已诊断并尝试修复；超过上限就停止自动重发，改为失败并给出原因
            // Pane not ready: every round already diagnosed and tried a repair; past the cap, stop resending and fail with the reason
            if (d == SendDecision.Retry && SendRetryPolicy.IsPaneNotReady(result) && ++t.PaneRepairRounds >= SendRetryPolicy.MaxPaneRepairs)
            {
                result += SendRetryPolicy.PaneRepairExhausted(t.PaneRepairRounds);
                d = SendDecision.Fail;
            }
            switch (d)
            {
                case SendDecision.Delivered:
                    t.Status = QueueStatus.Running;
                    t.Started = now;
                    t.SawBusy = false;
                    t.Error = null;
                    t.Interrupted = false;
                    t.ResumeNote = null;
                    t.FreshContext = false;
                    t.PaneRepairRounds = 0;
                    break;
                case SendDecision.Fail:
                    t.PaneRepairRounds = 0;
                    Fail(t, result, now, FailureKind.Delivery);
                    break;
                default:
                    t.Status = QueueStatus.Waiting;
                    t.Error = result;
                    t.Attempts = Math.Max(0, t.Attempts - 1);
                    if (SendRetryPolicy.IsPaneNotReady(result)) t.NextTry = now + SendRetryPolicy.PaneRepairDelay(t.PaneRepairRounds);
                    else if (!ManualChatProtection.IsWait(result)) t.NextTry = now + SendRetryPolicy.BlockedRetryDelay;
                    break;
            }
            return d;
        }

        public static string SuccessReceipt(QueuedTask t) => "[VSManager:" + t.CompletionToken + ":SUCCESS]";
        /// <summary>旧版「需要用户验证」回执，仍按待验证识别（新规则只使用 UNVERIFIED）。/ Legacy needs-user receipt, still read as awaiting verification (new rules only use UNVERIFIED).</summary>
        public static string NeedsUserReceipt(QueuedTask t) => "[VSManager:" + t.CompletionToken + ":NEEDS_USER]";
        public static string FailureReceipt(QueuedTask t) => "[VSManager:" + t.CompletionToken + ":FAILED]";
        /// <summary>待验证回执。/ Awaiting-verification receipt.</summary>
        public static string UnverifiedReceipt(QueuedTask t) => "[VSManager:" + t.CompletionToken + ":UNVERIFIED]";

        /// <summary>
        /// 发给 Copilot 的正文：任务 + 重试上下文（接续 / 新对话说明、轮次、前次反馈、补充信息，见 <see cref="RetryContext"/>）+ 完整回执规则。
        /// FAILED 只用于本任务本身未完成，尚未验证或需用户测试用 UNVERIFIED，无关的遗留问题不算失败。
        /// Text sent to Copilot: task + retry context (continuation / fresh-conversation note, round, previous feedback,
        /// supplementary info, see <see cref="RetryContext"/>) + the full receipt rules. FAILED only means the task itself was not
        /// completed; UNVERIFIED covers pending verification and user testing; unrelated pre-existing issues are not failures.
        /// </summary>
        public static string DispatchText(QueuedTask t) => t.Text + " "
            + (t.Worktree != null && !t.IsWorktreeMerge ? WorktreeInfo.DevelopmentInstructions + " " : "")
            + RetryContext(t)
            + SuggestedPromptRule + " "
            + FullRules(t);

        /// <summary>
        /// 每个发布的任务都附加的执行方式：按自动推荐的提示词执行，把任务推进到完成，不停下来等确认。
        /// Execution rule appended to every published task: follow the automatically suggested prompts and drive the task to completion without stopping for confirmation.
        /// </summary>
        public const string SuggestedPromptRule =
            "【执行方式】请按自动推荐的提示词执行：过程中出现自动推荐的提示词（建议的下一步 / 后续操作）时，直接按推荐内容继续执行，把本任务推进到完成，不要停下来询问或等待确认；只有确实无法继续时才停下，并说明需要用户提供什么。";

        /// <summary>
        /// 发送日志中的附加规则记录：原样写出本次发送正文附加的「【执行方式】…」全文，便于在 send 日志中核对（发送过程只截取正文开头）。
        /// Send-log record of the appended rule: writes the "【执行方式】…" text appended to this dispatch verbatim so it can be checked in the send log
        /// (the send trace only keeps the start of the text).
        /// </summary>
        public static string DispatchRuleLog(QueuedTask t, string dispatchText) =>
            $"任务清单：任务 #{t.Id} 发送正文 {dispatchText?.Length ?? 0} 字，已附加执行方式 / Execution rule appended："
            + ((dispatchText ?? "").Contains(SuggestedPromptRule) ? SuggestedPromptRule : "（未附加 / not appended）");

        /// <summary>本需求当前是第几轮（只算内容类执行）。/ Current round of the request (content runs only).</summary>
        public static int Round(QueuedTask t) => Math.Max(0, t.PriorRuns) + Math.Max(0, t.ContentRuns) + 1;

        /// <summary>
        /// 重试上下文：只放一段接续说明（本轮中断 > 对话已重置 > 用户暂停），各段信息后只给一次处理要求，
        /// 补充信息优先于前次反馈，避免多段说明各自重复要求、机械堆叠。
        /// Retry context: a single continuation note (interrupted run > conversation reset > user pause), then the pieces of
        /// information followed by one instruction; supplementary info takes precedence over earlier feedback, so the sections
        /// do not each repeat their own demands and pile up mechanically.
        /// </summary>
        internal static string RetryContext(QueuedTask t)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(t.ResumeNote)) sb.Append(t.ResumeNote).Append(' ');
            else if (t.FreshContext) sb.Append(FreshContextNote).Append(' ');
            else if (t.Interrupted) sb.Append(InterruptedNote).Append(' ');
            int round = Round(t);
            bool prior = !string.IsNullOrEmpty(t.PriorFailure), sup = !string.IsNullOrEmpty(t.Supplement);
            if (round > 1) sb.Append("【第 ").Append(round).Append(" 轮】本需求此前已执行 ").Append(round - 1).Append(" 次未通过。");
            if (prior) sb.Append("【前次尝试反馈】").Append(t.PriorFailure).Append(' ');
            if (sup) sb.Append("【补充信息】").Append(t.Supplement).Append(' ');
            if (sup) sb.Append("请以补充信息为准（与前次反馈或此前做法冲突时以补充信息为准）继续完成本任务，")
                .Append(prior ? "前次反馈中无关的遗留问题可忽略，" : "").Append("不要原样重复上次的步骤。 ");
            else if (prior) sb.Append("请先判断上述反馈中哪些问题属于本任务范围、哪些是无关的遗留问题，针对反馈调整做法，不要原样重复上次的步骤。 ");
            else if (round > 1) sb.Append("请调整做法，不要原样重复上次的步骤。 ");
            return sb.ToString();
        }

        /// <summary>AI 助手自主为每个任务补充信息重试的次数上限；用户提供的补充不受此限。/ Cap on AI-initiated retries with info per task; supplements provided by the user are not capped.</summary>
        public const int MaxSupplements = 3;

        private static string FullRules(QueuedTask t) =>
            "任务队列回执（仅用于确认本次结果，三选一，在最终回复最后单独一行输出）：本任务要求的内容已完成且已验证时输出 " + SuccessReceipt(t)
            + "；改动已完成，但尚未在运行中的程序里实际验证，或需要用户手动测试、运行或确认（你无法自行验证）时，以「" + TaskHoldNote.PendingTag + "」开头单独成段，列出需要用户验证或处理的内容后输出 " + UnverifiedReceipt(t)
            + "；只有本任务本身未能完成（要求无法实现、改动未完成、本任务引入的错误未解决、缺少必要信息）时，以「" + TaskHoldNote.ReasonTag + "」开头单独成段说明失败原因，再说明已完成的部分与建议的下一步，然后输出 " + FailureReceipt(t)
            + "。输出待验证回执时，请把需要用户在运行环境中测试的内容写成测试清单，每项单独一行、使用「- [ ] 具体操作与预期结果」格式；" + TaskTestChecklist.TagRule
            + "与本任务无关的遗留编译错误、已有的测试失败或环境问题不算本任务失败，单独说明即可。不要在过程消息中输出回执；回执行之后不要再输出任何文字（包括括号内的补充说明）。";

        public static bool TryReadSuccess(QueuedTask t, string answer, out string result) =>
            ReadReceipt(t, answer, out result) == TaskReceipt.Success;

        /// <summary>
        /// 读取本次回执：回执须在最后单独一行且属于当前尝试；<paramref name="result"/> 为去掉回执后的正文（无回执时为原文）。
        /// Reads this attempt's receipt: it must be alone on the last line and match the current attempt; <paramref name="result"/>
        /// is the text without the receipt (the original text when there is none).
        /// </summary>
        public static TaskReceipt ReadReceipt(QueuedTask t, string answer, out string result)
        {
            result = StripTrailingNoReplyNote(answer)?.Trim();
            if (string.IsNullOrEmpty(t.CompletionToken) || string.IsNullOrEmpty(result)) return TaskReceipt.None;
            string text = result;
            // 最后一行去掉首尾空白、零宽字符与 Markdown 修饰后须恰好是回执 / The last line, stripped of whitespace, zero-width characters and Markdown decoration, must be exactly the receipt
            int nl = text.LastIndexOf('\n');
            string lastLine = ReceiptLine(text.Substring(nl + 1));
            string head = nl < 0 ? "" : text.Substring(0, nl).TrimEnd();
            bool EndsWith(string receipt, out string body)
            {
                body = null;
                if (!string.Equals(lastLine, receipt, StringComparison.Ordinal)) return false;
                body = head;
                return true;
            }
            if (EndsWith(FailureReceipt(t), out string failed))
            {
                // 反馈说明已实现、仅未实际运行验证时按未验证处理 / Treat as unverified when the feedback says only runtime verification is missing
                result = failed;
                return TaskReplyVerdict.OnlyRuntimeUnverified(failed) ? TaskReceipt.Unverified : TaskReceipt.Failed;
            }
            // 另一种回执也出现在正文中时无法判定 / Ambiguous when another receipt also appears in the text
            if (text.Contains(FailureReceipt(t))) return TaskReceipt.None;
            string[] positive = { SuccessReceipt(t), NeedsUserReceipt(t), UnverifiedReceipt(t) };
            bool Only(string receipt, out string body)
            {
                if (!EndsWith(receipt, out body)) return false;
                string b = body;
                return !positive.Any(p => p != receipt && b.Contains(p));
            }
            if (Only(UnverifiedReceipt(t), out string unverified))
            {
                result = unverified.Length == 0 ? "改动已完成，待验证" : unverified;
                return TaskReceipt.Unverified;
            }
            // 旧规则的 NEEDS_USER 与 UNVERIFIED 已合并为待验证 / The legacy NEEDS_USER receipt is merged into awaiting verification
            if (Only(NeedsUserReceipt(t), out string needs))
            {
                result = needs.Length == 0 ? "改动已完成，待验证" : needs;
                return TaskReceipt.Unverified;
            }
            if (Only(SuccessReceipt(t), out string ok))
            {
                result = ok.Length == 0 ? "任务已成功完成" : ok;
                return TaskReceipt.Success;
            }
            return TaskReceipt.None;
        }

        private static readonly char[] ReceiptDecoration = { ' ', '\t', '\r', '\u00A0', '\u200B', '\u200C', '\u200D', '\u2060', '\uFEFF', '*', '_', '`', '>' };

        /// <summary>
        /// 规范化回执所在行：去掉首尾空白、零宽字符、Markdown 修饰（粗体 / 代码 / 引用）以及末尾句号。
        /// Normalizes the receipt line: trims whitespace, zero-width characters, Markdown decoration (bold / code / quote) and a trailing period.
        /// </summary>
        internal static string ReceiptLine(string line)
        {
            string s = (line ?? "").Trim(ReceiptDecoration);
            while (s.Length > 0 && (s[s.Length - 1] == '.' || s[s.Length - 1] == '。')) s = s.Substring(0, s.Length - 1).Trim(ReceiptDecoration);
            return s;
        }

        /// <summary>
        /// Copilot 有时在回执之后自动追加「(This turn has no user-facing reply.)」一类的占位说明（最后一次输出没有正文时生成），
        /// 它不是任务内容；读取回执前去掉末尾的这类行，回执仍须是其余内容的最后一行。
        /// Copilot sometimes appends a placeholder such as "(This turn has no user-facing reply.)" after the receipt (generated when
        /// its last output has no text). It is not task content, so such trailing lines are dropped before reading the receipt;
        /// the receipt must still be the last line of what remains.
        /// </summary>
        internal static string StripTrailingNoReplyNote(string answer)
        {
            if (string.IsNullOrEmpty(answer)) return answer;
            string text = answer.TrimEnd();
            while (true)
            {
                int nl = text.LastIndexOf('\n');
                string last = text.Substring(nl + 1);
                if (!NoReplyNote.IsMatch(last)) return text;
                if (nl < 0) return "";
                text = text.Substring(0, nl).TrimEnd();
            }
        }

        private static readonly System.Text.RegularExpressions.Regex NoReplyNote = new System.Text.RegularExpressions.Regex(
            @"^\s*[\(（\[【_*]*\s*(this\s+turn\s+has\s+no\s+user[-\s]?facing\s+(reply|response|output|message)|no\s+user[-\s]?facing\s+(reply|response|output|message)|本轮(对话)?(没有|无)(面向用户的)?(回复|输出|消息))[^\n]{0,40}?[\)）\]】_*\s\.。]*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>把回执归并为成功 / 待验证 / 失败三类。/ Collapses the receipt into succeeded / awaiting verification / failed.</summary>
        public static TaskReplyOutcome ReadOutcome(QueuedTask t, string answer, out string result)
        {
            switch (ReadReceipt(t, answer, out result))
            {
                case TaskReceipt.Success: return TaskReplyOutcome.Succeeded;
                case TaskReceipt.Unverified: return TaskReplyOutcome.Unverified;
                default: return TaskReplyOutcome.Failed;
            }
        }

        /// <summary>→ 失败。/ → failed.</summary>
        public static void Fail(QueuedTask t, string error, DateTime now, string kind = null)
        {
            t.Status = QueueStatus.Failed;
            t.Error = error;
            t.Finished = now;
            t.FailureKind = kind;
            t.NeedsUser = false;
            t.Released = false;
            t.TestItems = null;
            t.PendingNote = null;
            t.FailureReason = TaskHoldNote.ForFailure(kind, error, t.Result);
            if (FailureKind.IsContent(kind)) t.ContentRuns++;
        }

        /// <summary>
        /// 补齐待处理 / 失败说明（旧记录或未经状态机写入的结果），并清除与当前状态不符的说明。
        /// Fills in hold notes (older records or outcomes not written by the state machine) and clears notes that no longer match the status.
        /// </summary>
        public static void FillHoldNote(QueuedTask t)
        {
            if (t == null) return;
            if (t.Status == QueueStatus.Failed)
            {
                t.PendingNote = null;
                if (string.IsNullOrEmpty(t.FailureReason)) t.FailureReason = TaskHoldNote.ForFailure(t.FailureKind, t.Error, t.Result);
            }
            else if (TaskHoldNote.IsPending(t))
            {
                t.FailureReason = null;
                if (string.IsNullOrEmpty(t.PendingNote)) t.PendingNote = TaskHoldNote.Pending(t.Result);
            }
            else if (!QueueStatus.Active(t.Status))
                t.PendingNote = t.FailureReason = null;
        }

        /// <summary>
        /// 旧记录迁移：「已完成（待用户验证）」并入「待验证」状态，放行标记保留。
        /// Legacy migration: "done, awaiting user verification" becomes the awaiting-verification status; the released flag is kept.
        /// </summary>
        public static void MigrateLegacy(QueuedTask t)
        {
            if (t == null || !t.NeedsUser) return;
            if (t.Status == QueueStatus.Done) t.Status = QueueStatus.Unverified;
            t.NeedsUser = false;
        }

        /// <summary>
        /// 执行中 → 已完成，或（<paramref name="pending"/>）改动已完成但待验证 → 待验证。
        /// running → done, or (<paramref name="pending"/>) changes done but awaiting verification → unverified.
        /// </summary>
        public static bool Complete(QueuedTask t, DateTime now, bool pending = false)
        {
            if (t == null || t.Status != QueueStatus.Running) return false;
            t.Status = pending ? QueueStatus.Unverified : QueueStatus.Done;
            t.Finished = now;
            t.NeedsUser = false;
            t.FailureKind = null;
            t.Released = false;
            t.TestItems = null;
            t.FailureReason = null;
            t.PendingNote = pending ? TaskHoldNote.Pending(t.Result) : null;
            if (pending) t.ContentRuns++;
            return true;
        }

        /// <summary>
        /// 待验证 → 已完成：用户确认已在运行环境中测试，测试清单全部勾选。
        /// awaiting verification → done: the user confirms testing; every checklist item is checked.
        /// </summary>
        public static bool MarkVerified(QueuedTask t)
        {
            if (!TaskTestChecklist.Pending(t)) return false;
            t.Status = QueueStatus.Done;
            t.NeedsUser = false;
            t.PendingNote = null;
            if (t.TestItems != null)
                foreach (var item in t.TestItems) if (item != null) item.Checked = true;
            return true;
        }

        /// <summary>
        /// 勾选 / 取消勾选测试项；<paramref name="allChecked"/> 表示清单已全部勾选（由调用方决定是否标记完成）。
        /// Checks / unchecks a test item; <paramref name="allChecked"/> reports whether every item is now checked (the caller decides whether to complete).
        /// </summary>
        public static bool SetTestItem(QueuedTask t, int index, bool done, out bool allChecked)
        {
            allChecked = false;
            var items = TaskTestChecklist.Ensure(t);
            if (index < 0 || index >= items.Length || items[index] == null) return false;
            items[index].Checked = done;
            allChecked = TaskTestChecklist.Remaining(t) == 0;
            return true;
        }

        /// <summary>
        /// 取消任务：排队
        /// 发送中的任务可能已送达，不能取消；已取消的不重复取消。
        /// Cancels a task: waiting / waiting for target / running (tracking stops), and finished failed / unverified / done tasks
        /// (their result and failure record are kept; they no longer block successors). Sending tasks may already be delivered and
        /// cannot be cancelled; cancelled tasks are not cancelled again.
        /// </summary>
        public static bool Cancel(QueuedTask t, DateTime now)
        {
            if (!CanCancel(t)) return false;
            bool finished = t.Status == QueueStatus.Failed || QueueStatus.Delivered(t.Status);
            t.Status = QueueStatus.Cancelled;
            if (!finished || t.Finished == null) t.Finished = now;
            return true;
        }

        /// <summary>是否可以取消（发送中与已取消除外）。/ Whether the task can be cancelled (not while sending or already cancelled).</summary>
        public static bool CanCancel(QueuedTask t) => t != null && (t.Status == QueueStatus.Waiting || t.Status == QueueStatus.WaitingVs
            || t.Status == QueueStatus.Running || t.Status == QueueStatus.Failed || QueueStatus.Delivered(t.Status));

        /// <summary>
        /// 等待目标 VS → 排队：改用已打开 VS 的键与名称，并在 <paramref name="notBefore"/> 之后才发布（等待解决方案加载）。
        /// waiting_vs → waiting: switches to the opened VS's key and name, publishable only after <paramref name="notBefore"/>
        /// (lets the solution finish loading).
        /// </summary>
        public static bool TargetOpened(QueuedTask t, string vsKey, string vsName, DateTime notBefore)
        {
            if (t == null || t.Status != QueueStatus.WaitingVs) return false;
            t.Status = QueueStatus.Waiting;
            if (!string.IsNullOrEmpty(vsKey)) t.VsKey = vsKey;
            if (!string.IsNullOrEmpty(vsName)) t.VsName = vsName;
            t.NextTry = notBefore;
            return true;
        }

        /// <summary>重新排队：清空尝试次数、结果与时间，立即可发布。/ Requeue: clears attempts, result and times; publishable at once.</summary>
        public static void Requeue(QueuedTask t) => Requeue(t, false);

        /// <summary>
        /// 重新排队；<paramref name="freshContext"/> 为 true 表示 Copilot 对话已清空或换成新线程，下一次发送提示其重新阅读相关内容。
        /// Requeue; <paramref name="freshContext"/> true means the Copilot conversation was cleared or replaced by a new thread,
        /// so the next send tells Copilot to re-read the relevant content.
        /// </summary>
        public static void Requeue(QueuedTask t, bool freshContext)
        {
            // 把本次失败的反馈带到下一次尝试 / Carry this failure's feedback into the next attempt
            t.PriorFailure = TaskFailureAnalyzer.PriorFailureSummary(t) ?? t.PriorFailure;
            // 本轮中断（EOF、过大、迭代上限等）后重试：提示 Copilot 在已有进度上继续 / Retry after an interrupted run: tell Copilot to continue from its progress
            t.ResumeNote = t.Status == QueueStatus.Failed && t.FailureKind == FailureKind.Interrupted ? ResumeNoteFor(t.RunIssue, freshContext) : null;
            t.FreshContext = freshContext;
            t.Reply = t.RunIssue = null;
            t.FailureKind = null;
            t.NeedsUser = false;
            t.Released = false;
            t.Status = QueueStatus.Waiting;
            t.Attempts = 0;
            t.Error = null;
            t.Result = null;
            t.PendingNote = t.FailureReason = null;
            t.Started = t.Finished = null;
            t.NextTry = DateTime.MinValue;
            t.PredecessorNotice = null;
            t.TestItems = null;
        }

        /// <summary>是否为可放行 / 可补充的结束结果：失败或待验证。/ Whether the outcome can be released / supplemented: failed or awaiting verification.</summary>
        public static bool IsHoldOutcome(QueuedTask t) =>
            t != null && (t.Status == QueueStatus.Failed || t.Status == QueueStatus.Unverified);

        /// <summary>
        /// 放行：失败或待验证的任务不再暂停后续，结果保持不变。
        /// Release: a failed or awaiting-verification task stops pausing successors; its outcome is kept.
        /// </summary>
        public static bool Release(QueuedTask t)
        {
            if (!IsHoldOutcome(t) || t.Released) return false;
            t.Released = true;
            return true;
        }

        /// <summary>
        /// 插入补充信息并重新排队（失败或待验证；<paramref name="enforceLimit"/> 为 true 时最多 <see cref="MaxSupplements"/> 次，用户补充不限）；前次反馈一并带上。
        /// <paramref name="replace"/> 为 true 时 info 是整合后的完整重试说明，替换此前累积的补充信息、前次反馈与接续说明；
        /// <paramref name="freshContext"/> 为 true 表示 Copilot 对话已清空，提示其重新阅读相关内容。
        /// Adds supplementary info and requeues (failed or awaiting verification; at most <see cref="MaxSupplements"/> times when
        /// <paramref name="enforceLimit"/> is true, unlimited for user supplements); the previous feedback is carried along.
        /// With <paramref name="replace"/> the info is a consolidated retry brief replacing the accumulated supplements, previous
        /// feedback and continuation note; <paramref name="freshContext"/> means the Copilot conversation was cleared and Copilot
        /// should re-read the relevant content.
        /// </summary>
        public static bool Supplement(QueuedTask t, string info, out string error, bool enforceLimit = true, bool replace = false, bool freshContext = false)
        {
            error = null;
            info = info?.Trim();
            if (string.IsNullOrEmpty(info)) { error = "补充信息不能为空 / Supplementary info is empty"; return false; }
            if (!IsHoldOutcome(t)) { error = "只有失败或待验证的任务可以补充信息重试 / Only failed or awaiting-verification tasks can be retried with info"; return false; }
            if (enforceLimit && t.SupplementCount >= MaxSupplements)
            {
                error = $"已补充 {t.SupplementCount} 次，达到上限，请把情况告诉用户由用户决定 / Supplement limit reached ({t.SupplementCount}); hand over to the user";
                return false;
            }
            t.Supplement = replace || string.IsNullOrEmpty(t.Supplement) ? info : t.Supplement + " ｜ " + info;
            t.SupplementCount++;
            Requeue(t, freshContext);
            // 整合说明已包含需要保留的反馈与接续要求 / The consolidated brief already carries whatever feedback and continuation matter
            if (replace) t.PriorFailure = t.ResumeNote = null;
            return true;
        }

        /// <summary>
        /// Copilot 对话已清空 / 换成新线程后再次发送时附加的说明。
        /// Note added when the task is sent again after the Copilot conversation was cleared / replaced by a new thread.
        /// </summary>
        public const string FreshContextNote = "【对话已重置】此前的 Copilot 对话记录已清空或换成了新线程，之前的讨论与进度在对话中已不可用：请先重新阅读本任务涉及的代码、文档与 Git 状态（如 git status / git diff / git log）了解已有进度，在此基础上继续完成剩余部分，不要重复或回滚已完成的部分，也不要假设之前对话中的内容仍然可见。";

        /// <summary>中断后再次发送时附加的说明。/ Note added when an interrupted task is sent again.</summary>
        public const string InterruptedNote = "【继续执行】本任务上次执行到一半时被用户暂停中断，可能已完成部分改动：请先检查当前代码与对话中的已有进度，在此基础上继续完成本任务，不要重复或回滚已完成的部分。";

        /// <summary>
        /// Copilot 本轮执行异常后重试时附加的接续说明（发给 Copilot）。
        /// Continuation note (sent to Copilot) for a retry after a Copilot run problem.
        /// </summary>
        public static string ResumeNoteFor(string runIssue, bool freshContext = false)
        {
            string cause;
            switch (runIssue)
            {
                case RunIssue.Eof: cause = "返回中断（未预期的 EOF）"; break;
                case RunIssue.TooLarge: cause = "返回体或上下文过大"; break;
                case RunIssue.IterationLimit: cause = "达到单轮迭代上限"; break;
                default: cause = "网络 / 服务错误或回复被中断"; break;
            }
            return "【继续执行】本任务上次执行因「" + cause + "」没有完成，可能已完成部分改动："
                + (freshContext
                    ? "此前的 Copilot 对话已清空或换成了新线程，请先重新阅读本任务涉及的代码、文档与 Git 状态（如 git status / git diff / git log）了解已有进度，"
                    : "请先检查当前代码与对话中的已有进度，")
                + "在此基础上继续完成剩余部分，不要重复或回滚已完成的部分。"
                + (runIssue == RunIssue.TooLarge ? "请分步完成，每次只处理少量文件，避免输出整文件或大段日志。" : "")
                + (runIssue == RunIssue.IterationLimit ? "请优先完成剩余的关键步骤，减少不必要的探索。" : "");
        }

        /// <summary>
        /// 为接续说明追加主控 AI 的补充（针对中断原因的做法调整）。
        /// Appends the main AI's addition (an approach change for the interruption) to the continuation note.
        /// </summary>
        public static void AddResumeNote(QueuedTask t, string note)
        {
            note = note?.Trim();
            if (t == null || string.IsNullOrEmpty(note)) return;
            t.ResumeNote = (string.IsNullOrEmpty(t.ResumeNote) ? "" : t.ResumeNote + " ") + "【重试说明】" + note;
        }

        /// <summary>
        /// 暂停时中断执行中的任务：重新排队（内容、附件与补充信息不变），恢复后按编号重新发送并提示 Copilot 接着做。
        /// Interrupts a running task on pause: back to the queue (text, attachments and supplements unchanged), re-sent in ID
        /// order after resuming with a note telling Copilot to continue from where it stopped.
        /// </summary>
        public static bool Interrupt(QueuedTask t)
        {
            if (t == null || t.Status != QueueStatus.Running) return false;
            t.Status = QueueStatus.Waiting;
            t.Interrupted = true;
            t.Attempts = 0;
            t.Started = null;
            t.SawBusy = false;
            t.Error = null;
            t.NextTry = DateTime.MinValue;
            return true;
        }

        /// <summary>上次退出时正在发送的任务重新排队。/ A task that was being sent when the app exited goes back to the queue.</summary>
        public static void RecoverAfterRestart(QueuedTask t)
        {
            if (t.Status == QueueStatus.Sending) t.Status = QueueStatus.Waiting;
        }

        /// <summary>发送中的任务不能移除。/ A task that is being sent cannot be removed.</summary>
        public static bool CanRemove(QueuedTask t) => t != null && t.Status != QueueStatus.Sending;

        /// <summary>
        /// 检查执行中的任务。<paramref name="canDispatch"/> 只在需要时才调用。
        /// Checks a running task. <paramref name="canDispatch"/> is only evaluated when needed.
        /// </summary>
        public static RunningVerdict CheckRunning(QueuedTask t, bool vsOpen, CopilotState copilot, Func<bool> canDispatch, DateTime now)
        {
            var elapsed = now - (t.Started ?? now);
            if (!vsOpen) return elapsed > VsClosedTimeout ? RunningVerdict.VsClosed : RunningVerdict.None;
            if (copilot == CopilotState.Busy) return RunningVerdict.SawBusy;
            if (!t.SawBusy && copilot == CopilotState.Idle && canDispatch() && elapsed > NeverBusyTimeout) return RunningVerdict.Unconfirmed;
            return RunningVerdict.None;
        }

        /// <summary>同一目标的活动任务阻塞后续任务（旧版布尔开关：true = 失败级，false = 待验证级）。/ Active work blocks its target (legacy switch: true = Failed level, false = NeedsUser level).</summary>
        public static QueuedTask BlockingTask(IEnumerable<QueuedTask> items, QueuedTask task, bool skipFailedPredecessors = true) =>
            BlockingTasks(items, task, ReleaseLevels.FromSkipFailed(skipFailedPredecessors)).FirstOrDefault();

/// <summary>
/// 按放行等级
        /// Finds blockers by release level: sending / running always block; earlier active tasks block; earlier failed /
        /// awaiting-verification tasks block per level, unless released or superseded.
        /// </summary>
        public static QueuedTask BlockingTask(IEnumerable<QueuedTask> items, QueuedTask task, ReleaseLevel level) =>
            BlockingTasks(items, task, level).FirstOrDefault();

        internal static IEnumerable<QueuedTask> BlockingTasks(IEnumerable<QueuedTask> items, QueuedTask task, bool skipFailedPredecessors) =>
            BlockingTasks(items, task, ReleaseLevels.FromSkipFailed(skipFailedPredecessors));

        /// <summary>
        /// 普通任务按解决方案保守阻塞提及任务；两个显式任务按实例区分。同一解决方案被多个 VS 打开时各实例的 Copilot 相互独立，
        /// 已记录不同实例的任务互不阻塞，可以并行。
        /// Legacy solution tasks conservatively block mentions; two explicit tasks distinguish instances. With one solution open
        /// in several VS instances each instance has its own Copilot, so tasks recorded for different instances never block each other and run in parallel.
        /// </summary>
        internal static bool SharesDispatchTarget(QueuedTask a, QueuedTask b)
        {
            if (DifferentInstances(a, b)) return false;
            if (a.HasExplicitTarget == b.HasExplicitTarget) return ResentTaskMatcher.SameTarget(a, b);
            return !string.IsNullOrEmpty(a.VsKey) && string.Equals(a.VsKey, b.VsKey, StringComparison.OrdinalIgnoreCase)
                || a.HasExplicitTarget && SolutionMatcher.SamePath(a.ExplicitSolutionPath, b.VsKey)
                || b.HasExplicitTarget && SolutionMatcher.SamePath(b.ExplicitSolutionPath, a.VsKey);
        }

        /// <summary>任务记录的目标实例（显式提及优先）；未记录时为 null。/ The task's recorded target instance (explicit mention first); null when none.</summary>
        internal static string InstanceOf(QueuedTask t) =>
            !string.IsNullOrEmpty(t?.ExplicitInstanceKey) ? t.ExplicitInstanceKey : string.IsNullOrEmpty(t?.TargetInstanceKey) ? null : t.TargetInstanceKey;

        /// <summary>两个任务都记录了实例且不同。任一方未记录时保守视为可能相同。/ Both tasks record an instance and they differ. A side without one conservatively counts as possibly the same.</summary>
        internal static bool DifferentInstances(QueuedTask a, QueuedTask b)
        {
            string x = InstanceOf(a), y = InstanceOf(b);
            return x != null && y != null && !string.Equals(x, y, StringComparison.Ordinal);
        }

        internal static IEnumerable<QueuedTask> BlockingTasks(IEnumerable<QueuedTask> items, QueuedTask task, ReleaseLevel level)
        {
            var all = items.ToList();
            return all.Where(x => x != task &&
                ((x.IsWorktreeMerge || task.IsWorktreeMerge)
                    && (WorktreeInfo.Same(x.Worktree, task.Worktree) || SharesDispatchTarget(x, task))
                    ? WorktreeBlocks(x, task)
                    : (WorktreeInfo.Same(x.Worktree, task.Worktree) || SharesDispatchTarget(x, task))
                        && (x.Status == QueueStatus.Sending || x.Status == QueueStatus.Running
                            || (x.Id < task.Id && (QueueStatus.Active(x.Status)
                                || (!x.Released && ReleaseLevels.Blocks(level, x)
                                    && !all.Any(replacement => replacement.Id > x.Id
                                        && ResentTaskMatcher.SameTarget(x, replacement)
                                        && replacement.Replaces != null && replacement.Replaces.Contains(x.Id))))))))
                .OrderBy(x => x.Id);
        }

        private static bool WorktreeBlocks(QueuedTask predecessor, QueuedTask task)
        {
            if (predecessor.Status == QueueStatus.Sending || predecessor.Status == QueueStatus.Running) return true;
            if (predecessor.IsWorktreeMerge && !QueueStatus.Delivered(predecessor.Status))
                return !task.IsWorktreeMerge || predecessor.Id < task.Id;
            if (task.IsWorktreeMerge) return false;
            return predecessor.Id < task.Id && QueueStatus.Active(predecessor.Status);
        }

        public static List<QueuedTask> NextToDispatch(IEnumerable<QueuedTask> items, DateTime now, bool skipFailedPredecessors = true) =>
            NextToDispatch(items, now, ReleaseLevels.FromSkipFailed(skipFailedPredecessors));

        public static List<QueuedTask> NextToDispatch(IEnumerable<QueuedTask> items, DateTime now, ReleaseLevel level)
        {
            var all = items.ToList();
            return all.Where(x => x.Status == QueueStatus.Waiting && x.NextTry <= now && BlockingTask(all, x, level) == null)
                .OrderBy(x => x.Id).ToList();
        }

        /// <summary>Ignore resend markers and formatting when detecting an already queued task.</summary>
        public static QueuedTask FindActiveDuplicate(IEnumerable<QueuedTask> items, string vsKey, string text)
        {
            string normalized = ResentTaskMatcher.Normalize(ResentTaskMatcher.StripMarker(text, out _));
            return items.FirstOrDefault(t => string.Equals(t.VsKey, vsKey, StringComparison.OrdinalIgnoreCase)
                && QueueStatus.Active(t.Status)
                && ResentTaskMatcher.Normalize(ResentTaskMatcher.StripMarker(t.Text, out _)) == normalized);
        }

        public static string StatusText(QueuedTask t, DateTime now, IEnumerable<QueuedTask> items, bool skipFailedPredecessors = true) =>
            StatusText(t, now, items, ReleaseLevels.FromSkipFailed(skipFailedPredecessors));

        public static string StatusText(QueuedTask t, DateTime now, IEnumerable<QueuedTask> items, ReleaseLevel level)
        {
            var blocker = t.Status == QueueStatus.Waiting ? BlockingTask(items, t, level) : null;
            if (blocker != null && blocker.IsWorktreeMerge)
                return $"等待 Worktree 合并 #{blocker.Id}（{StatusText(blocker, now)}）/ Waiting for worktree integration #{blocker.Id}";
            return PausedText(blocker) ?? StatusText(t, now);
        }

        /// <summary>被失败 / 待验证前序暂停时的状态文字；不是这类阻塞时返回 null。/ Status text when paused by a failed / awaiting-verification predecessor; null otherwise.</summary>
        public static string PausedText(QueuedTask blocker)
        {
            if (blocker?.Status == QueueStatus.Failed)
                return $"已暂停（前序 #{blocker.Id} 失败）/ Paused (predecessor #{blocker.Id} failed)";
            if (blocker?.Status == QueueStatus.Unverified)
                return $"已暂停（前序 #{blocker.Id} 待验证）/ Paused (predecessor #{blocker.Id} awaiting verification)";
            return null;
        }

        /// <summary>任务状态的简短文字（界面、AI 工具返回共用）。/ Short status text (shared by the UI and AI tool results).</summary>
        public static string StatusText(QueuedTask t, DateTime now)
        {
            if (t.Status == QueueStatus.Waiting && !string.IsNullOrEmpty(t.ManualChatWaitReason)) return t.ManualChatWaitReason;
            switch (t.Status)
            {
                case QueueStatus.Waiting: return SendRetryPolicy.IsUserBusy(t.Error) ? "等待用户操作空闲" : SendRetryPolicy.IsBlocked(t.Error) ? "等待处理 VS 弹窗" : t.Attempts > 0 ? "等待重试" : "排队中";
                case QueueStatus.WaitingVs: return "等待目标 VS（等待打开「" + (t.Target ?? t.VsName) + "」）";
                case QueueStatus.Sending: return "发送中";
                case QueueStatus.Running: return "执行中（" + TextUtil.FormatDuration(now - (t.Started ?? now)) + "）";
case QueueStatus.Done: return "已完成" + (t.Released ? "·已放行" : "");
case QueueStatus.Unverified: return "待验证" + (t.Released ? "·已放行" : "");
case QueueStatus.Failed: return t.Released ? "失败·已放行" : "失败";
                default: return "已取消";
            }
        }
    }
}
