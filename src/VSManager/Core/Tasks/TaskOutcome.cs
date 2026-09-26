using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    /// <summary>Copilot 最终回复中的任务回执。/ Task receipt found at the end of Copilot's final reply.</summary>
    public enum TaskReceipt
    {
        /// <summary>没有本次任务的有效回执。/ No valid receipt for this attempt.</summary>
        None,
        /// <summary>本任务已完成。/ The task is complete.</summary>
        Success,
        /// <summary>改动已完成，但需要用户测试或确认（VS 无法自行验证）。/ Changes are done but need user testing or confirmation.</summary>
        NeedsUser,
        /// <summary>本任务本身未能完成。/ The task itself could not be completed.</summary>
        Failed
    }

    /// <summary>失败类别（写入 tasks.json 的取值）。/ Failure categories (values stored in tasks.json).</summary>
    public static class FailureKind
    {
        /// <summary>未能送达 Copilot，与任务内容无关。/ Not delivered to Copilot; unrelated to the task content.</summary>
        public const string Delivery = "delivery";
        /// <summary>目标 VS 已关闭。/ The target VS was closed.</summary>
        public const string VsClosed = "vs_closed";
        /// <summary>读取回复失败。/ Reading the reply failed.</summary>
        public const string ReadError = "read";
        /// <summary>有回复但没有本次回执。/ A reply exists but carries no receipt for this attempt.</summary>
        public const string NoReceipt = "no_receipt";
        /// <summary>Copilot 明确回报失败并说明了原因。/ Copilot explicitly reported failure with a reason.</summary>
        public const string Reported = "reported";

        /// <summary>失败是否与任务内容有关（有 Copilot 的回复可供分析）。/ Whether the failure concerns the task content (a Copilot reply exists).</summary>
        public static bool IsContent(string kind) => kind == NoReceipt || kind == Reported;

        public static string Label(string kind)
        {
            switch (kind)
            {
                case Delivery: return "投递失败 / delivery failure";
                case VsClosed: return "目标 VS 已关闭 / target VS closed";
                case ReadError: return "读取回复失败 / reply read failure";
                case NoReceipt: return "缺少回执 / missing receipt";
                case Reported: return "Copilot 回报失败 / reported by Copilot";
                default: return "未分类 / unclassified";
            }
        }
    }

    /// <summary>从失败回复中识别出的线索。/ Clues found in a failure reply.</summary>
    public sealed class FailureHints
    {
        /// <summary>提到与本任务无关的遗留问题。/ Mentions pre-existing issues unrelated to the task.</summary>
        public bool PreExisting;
        /// <summary>提到需要用户测试、运行或确认。/ Mentions that the user must test, run or confirm.</summary>
        public bool NeedsUser;
        /// <summary>提到需要用户补充信息或做决定。/ Mentions that the user must supply information or decide.</summary>
        public bool NeedsInput;

        public bool Any => PreExisting || NeedsUser || NeedsInput;
    }

    /// <summary>
    /// 失败分析：识别回复中的线索，生成给 AI 助手的处理建议，并阻止原样重发内容类失败的任务（纯逻辑，便于测试）。
    /// Failure analysis: finds clues in the reply, builds handling guidance for the AI assistant and blocks verbatim resends
    /// of content failures (pure logic, testable).
    /// </summary>
    public static class TaskFailureAnalyzer
    {
        /// <summary>写入下一次尝试的前次反馈最大长度。/ Maximum length of the previous feedback carried into the next attempt.</summary>
        public const int PriorFailureMax = 600;

        private static readonly string[] PreExistingWords =
        {
            "遗留", "原有", "既有", "已有的", "已存在的", "之前就", "此前就", "本来就", "与本任务无关", "与本次任务无关", "与本次改动无关", "无关的错误", "无关的问题",
            "pre-existing", "preexisting", "already existed", "existing error", "existing failure", "unrelated"
        };

        private static readonly string[] NeedsUserWords =
        {
            "需要用户", "需要您", "请您", "请用户", "手动测试", "手动验证", "手动运行", "手动确认", "人工测试", "人工验证", "自行测试", "无法自行", "无法在此", "无法直接运行", "无法运行", "请测试", "请验证",
            "manual test", "manually", "please test", "please verify", "user to test", "user needs to", "cannot run", "unable to run", "can't run"
        };

        private static readonly string[] NeedsInputWords =
        {
            "请确认", "请提供", "请告知", "需要确认", "需要更多信息", "需要您提供", "请选择",
            "please confirm", "please provide", "need more information", "which option"
        };

        public static FailureHints Analyze(string answer)
        {
            var h = new FailureHints();
            if (string.IsNullOrWhiteSpace(answer)) return h;
            h.PreExisting = ContainsAny(answer, PreExistingWords);
            h.NeedsUser = ContainsAny(answer, NeedsUserWords);
            h.NeedsInput = ContainsAny(answer, NeedsInputWords);
            return h;
        }

        private static bool ContainsAny(string text, string[] words) =>
            words.Any(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// 给 AI 助手的失败处理建议（中英）：先分析原因，再决定汇报、询问还是发布修正任务，禁止原样重发。
        /// Guidance for the AI assistant (zh + en): analyze the cause first, then report, ask, or publish a revised task; never resend verbatim.
        /// </summary>
        public static string Guidance(QueuedTask t)
        {
            string kind = t?.FailureKind;
            if (!FailureKind.IsContent(kind))
                return "处理建议：这是投递类问题，与任务内容无关；向用户说明原因（VS 未打开 / 弹窗 / 输入框异常等），用户同意后可原样重新排队。"
                    + " / Guidance: this is a delivery problem unrelated to the task content; explain the cause to the user, and requeue unchanged once the user agrees.";
            var h = Analyze(t.Result ?? t.Error);
            var sb = new System.Text.StringBuilder("处理建议 / Guidance：先阅读上面的 Copilot 回复，判断失败的真实原因 / First read the Copilot reply above and determine the real cause。");
            if (h.PreExisting)
                sb.Append("回复提到与本任务无关的遗留问题：本任务可能已完成，只是被旧问题误判为失败；向用户说明哪些是遗留问题，不要重发同一需求。/ The reply mentions pre-existing issues: the task may be done and merely misjudged; tell the user which issues are pre-existing and do not resend the same request。");
            if (h.NeedsUser)
                sb.Append("回复提到需要用户测试或运行：请把需要用户验证的内容转告用户，等待用户反馈，不要重发。/ The reply says the user must test or run something: relay what to verify and wait for feedback; do not resend。");
            if (h.NeedsInput)
                sb.Append("回复需要用户补充信息或做决定：向用户提问，拿到答复后再发布包含该答复的修正任务。/ The reply needs user input or a decision: ask the user, then publish a revised task that includes the answer。");
            if (!h.Any)
                sb.Append("若确需再次执行，先征得用户同意，再以「重发 @" + t.Id + "：」发布修正任务，正文须针对失败原因写明调整（例如先解决哪个阻碍、忽略哪些无关问题、缩小到哪部分）。/ If another attempt is needed, get the user's consent and publish a revised task prefixed 'resend @" + t.Id + ":' whose text addresses the cause (which blocker to solve first, which unrelated issues to ignore, which part to narrow to)。");
            sb.Append("禁止原样重发同一内容。/ Never resend the same text verbatim.");
            return sb.ToString();
        }

        /// <summary>
        /// 把失败任务的反馈压缩成下一次尝试可用的摘要；投递类失败没有可用反馈，返回 null。
        /// Summarizes a failed task's feedback for the next attempt; delivery-type failures carry no useful feedback and return null.
        /// </summary>
        public static string PriorFailureSummary(QueuedTask failed)
        {
            if (failed == null || failed.Status != QueueStatus.Failed || !FailureKind.IsContent(failed.FailureKind)) return null;
            string text = !string.IsNullOrWhiteSpace(failed.Result) ? failed.Result : failed.Error;
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
            return "#" + failed.Id + "：" + TextUtil.Clip(text, PriorFailureMax);
        }

        /// <summary>
        /// 查找同目标、正文相同（忽略重发标记与空白）且因任务内容失败的历史任务：AI 助手不得原样重发它。
        /// Finds a same-target failed task with identical text (ignoring resend markers and whitespace) that failed on its
        /// content: the AI assistant must not resend it verbatim.
        /// </summary>
        public static QueuedTask FindVerbatimResend(IEnumerable<QueuedTask> items, string vsKey, string text)
        {
            if (items == null || string.IsNullOrWhiteSpace(text)) return null;
            string norm = ResentTaskMatcher.Normalize(ResentTaskMatcher.StripMarker(text.Trim(), out _));
            if (norm.Length == 0) return null;
            var probe = new QueuedTask { VsKey = vsKey ?? "" };
            return items.Where(t => t != null && t.Status == QueueStatus.Failed && FailureKind.IsContent(t.FailureKind)
                    && ResentTaskMatcher.SameTarget(t, probe)
                    && ResentTaskMatcher.Normalize(ResentTaskMatcher.StripMarker(t.Text, out _)) == norm)
                .OrderByDescending(t => t.Id).FirstOrDefault();
        }
    }
}
