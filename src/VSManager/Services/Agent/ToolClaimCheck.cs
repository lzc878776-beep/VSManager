using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 工具真实性核查的结论。/ Verdict of the tool-truthfulness check.
    /// </summary>
    internal enum ClaimVerdict
    {
        /// <summary>没有需要工具证实的说法，或说法已被工具证实。/ No claim that needs a tool, or the claim is backed by one.</summary>
        Ok,
        /// <summary>声称入队 / 核实清单，但本轮没有调用对应工具（无法证伪，仅提醒）。/ Claims enqueueing / checking the list without the matching tool this round (cannot be disproved, warn only).</summary>
        Unconfirmed,
        /// <summary>声称的任务编号在任务清单中不可能存在（编号尚未分配），属于虚报。/ Claimed task IDs cannot exist (never assigned): a fabricated claim.</summary>
        Fabricated,
    }

    /// <summary>工具真实性核查结果。/ Result of the tool-truthfulness check.</summary>
    internal sealed class ClaimCheckResult
    {
        public ClaimVerdict Verdict;
        /// <summary>尚未分配、却被声称已入队 / 存在的任务编号。/ Task IDs claimed but never assigned.</summary>
        public IReadOnlyList<int> MissingIds = new int[0];
        /// <summary>显示在回复下方的核查步骤。/ Step shown under the reply.</summary>
        public string Step;
    }

    /// <summary>
    /// 核查助手回复中的「已入队 / 已发布 / 已核实清单」等说法是否有本轮工具调用支撑：
    /// 模型有时会模仿历史回复的格式直接写出入队结果（含编造的 @编号），而没有调用 send_task。
    /// Checks whether "queued / published / list verified" claims in a reply are backed by tool calls in the same round:
    /// models sometimes imitate earlier replies and write enqueue results (with invented @IDs) without calling send_task.
    /// </summary>
    internal static class ToolClaimCheck
    {
        /// <summary>自动更正消息的开头标记；带此标记的一轮不会再次触发更正，避免循环。/ Prefix of auto-correction messages; such rounds never trigger another correction (no loops).</summary>
        public const string Marker = "[VSManager 自动核查 / Auto check]";

        /// <summary>会新建或重新排队任务的工具。/ Tools that create or requeue tasks.</summary>
        internal static readonly HashSet<string> EnqueueTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "send_task", "request_vsmanager_improvement", "retry_task", "retry_task_with_info",
        };

        /// <summary>会返回任务清单实际状态的工具。/ Tools that return the actual task-list state.</summary>
        internal static readonly HashSet<string> QueueReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "list_tasks", "send_task", "request_vsmanager_improvement", "retry_task", "retry_task_with_info", "read_task_reply",
        };

        private static readonly Regex EnqueueClaim = new Regex(
            @"已(?:确认|重新|成功|实际|真正|逐条)*(?:入队|加入(?:任务)?清单)" +
            @"|已(?:确认|重新|成功|实际|真正|逐条)*(?:发布|发送|推送|派发)(?:到|至|给)\s*[*「]*(?:#\d|Copilot)" +
            @"|已(?:确认|重新|成功|实际|真正|逐条)*(?:发布|派发)(?!说明|版本|的)" +
            @"|(?:入队|发布)成功|推送成功" +
            // 表格 / 列表中「@编号 … 排队中 / 前面 n 个」也是入队结果 / "@ID … queued / n ahead" rows are enqueue results too
            @"|@\d+[^。\n]{0,60}(?:排队中|前面\s*\d+\s*个)" +
            @"|\b(?:enqueued|queued as @|published to #|pushed to (?:#|Copilot))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex VerifyClaim = new Regex(
            @"(?:核实|核对|查看|查)(?:了|过)?(?:一下)?(?:当前)?(?:的)?(?:任务)?清单|确实(?:已经?)?存在" +
            @"|\b(?:verified|checked) the (?:task )?list\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ForgedLog = new Regex(@"〔\s*工具记录|Tool log \(added by VSManager", RegexOptions.CultureInvariant);

        private static readonly Regex TaskId = new Regex(@"([@#])(\d{1,6})\b", RegexOptions.CultureInvariant);

        /// <summary>承认「不存在 / 没有入队」的句子：其中的编号不算虚报。/ Sentences admitting "does not exist / not queued": their IDs are not false claims.</summary>
        private static readonly Regex Denial = new Regex(
            @"不存在|从未|没有(?:真正|实际)?(?:入队|成功|发布|加入|调用)|未(?:能)?(?:入队|发布|推送|加入|调用)|并未|没入队|虚报|失败|已取消|已删除" +
            @"|\b(?:not (?:queued|in the (?:task )?list)|never queued|does not exist|do not exist|failed|cancel(?:l)?ed|deleted)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SentenceBreak = new Regex(@"[。！？!?\n]+", RegexOptions.CultureInvariant);

        /// <summary>「#n」也常用作 VS 编号，只有明显超出 VS 编号范围时才当作任务编号。/ "#n" also numbers VS instances; treat it as a task ID only well beyond that range.</summary>
        private const int MinHashTaskId = 21;

        /// <summary>
        /// 核查一条回复。nextTaskId 为任务清单下一个将分配的编号（≤0 表示未知，不做编号核查）；existingIds 为任务清单中现有的编号（null 表示未知）。
        /// Checks one reply. nextTaskId is the next ID the task list will assign (≤0 = unknown, skip the ID check); existingIds are the IDs now in the task list (null = unknown).
        /// </summary>
        public static ClaimCheckResult Check(string reply, ICollection<string> calledTools, int nextTaskId, ICollection<int> existingIds = null)
        {
            var result = new ClaimCheckResult { Verdict = ClaimVerdict.Ok };
            reply = reply ?? "";
            var tools = calledTools ?? new string[0];
            // 只看没有承认失败 / 不存在的句子（转述或检讨之前的虚报不算新说法）/ Only sentences that do not admit failure count (quoting an earlier false claim is not a new one)
            var affirmed = SentenceBreak.Split(reply).Where(x => !Denial.IsMatch(x)).ToList();
            bool claimsEnqueue = affirmed.Any(x => EnqueueClaim.IsMatch(x));
            bool claimsVerify = affirmed.Any(x => VerifyClaim.IsMatch(x));
            // 回复里自己写出「工具记录」：只有 VSManager 能生成，模型手写即为伪造 / A "tool log" in the reply text: only VSManager produces it, so the model forged it
            bool forged = ForgedLog.IsMatch(reply);
            if (!claimsEnqueue && !claimsVerify && !forged) return result;

            // 编号 ≥ 下一个待分配编号 → 从未分配过，必然不存在；承认不存在的句子不计 / ID ≥ next ID → never assigned, cannot exist; admitting sentences do not count
            var ids = new SortedSet<int>();
            var denied = new HashSet<int>();
            foreach (var sentence in SentenceBreak.Split(reply))
            {
                bool denies = Denial.IsMatch(sentence);
                foreach (Match m in TaskId.Matches(sentence))
                {
                    int id = int.Parse(m.Groups[2].Value);
                    if (m.Groups[1].Value == "#" && (nextTaskId <= 0 || id < Math.Max(nextTaskId, MinHashTaskId))) continue;
                    if (denies) denied.Add(id); else ids.Add(id);
                }
            }
            // 同一编号先承认不存在、后又声称已入队，仍按声称核查 / An ID first admitted missing and then claimed again is still checked
            int oldest = existingIds != null && existingIds.Count > 0 ? existingIds.Min() : int.MaxValue;
            var missing = nextTaskId > 0
                ? ids.Where(i => i >= nextTaskId || (existingIds != null && i > oldest && !existingIds.Contains(i))).ToList()
                : new List<int>();
            bool enqueued = tools.Any(EnqueueTools.Contains);
            bool readQueue = tools.Any(QueueReadTools.Contains);
            string called = tools.Count == 0 ? "无 / none" : string.Join(", ", tools.Distinct(StringComparer.OrdinalIgnoreCase));

            // 调用过查询清单的工具、且只是在转述核实结果（没有声称入队）时，以工具结果为准 / After a list-reading tool, a pure verification report follows the tool result
            if (missing.Count > 0 && (claimsEnqueue || !readQueue))
            {
                result.Verdict = ClaimVerdict.Fabricated;
                result.MissingIds = missing;
                string list = string.Join("、", missing.Select(i => "@" + i));
                result.Step = "⚠ 核查未通过：" + list + " 不在任务清单中（下一个编号为 @" + nextTaskId + "；本轮工具：" + called + "），这些任务并未入队" +
                              " / Check failed: " + string.Join(", ", missing.Select(i => "@" + i)) + " are not in the task list (next ID @" + nextTaskId + "; tools this round: " + called + "); these tasks were never queued";
                return result;
            }
            if (forged && !enqueued)
            {
                result.Verdict = ClaimVerdict.Fabricated;
                result.Step = "⚠ 核查未通过：回复中的「工具记录」是助手自己写的（本轮工具：" + called + "），不是 VSManager 生成的，所述结果不可信" +
                              " / Check failed: the \"tool log\" in the reply was written by the assistant (tools this round: " + called + "), not by VSManager; its results cannot be trusted";
                return result;
            }
            if (claimsEnqueue && !enqueued)
            {
                result.Verdict = ClaimVerdict.Unconfirmed;
                result.Step = "⚠ 核查：本轮未调用 send_task，回复中的「已入队 / 已发布」说法未经本轮工具确认，请以任务清单为准" +
                              " / Check: send_task was not called this round; the \"queued / published\" claim is unconfirmed, trust the task list";
                return result;
            }
            if (claimsVerify && !readQueue)
            {
                result.Verdict = ClaimVerdict.Unconfirmed;
                result.Step = "⚠ 核查：本轮未调用 list_tasks，回复中的「已核实清单」说法未经工具确认，请以任务清单为准" +
                              " / Check: list_tasks was not called this round; the \"list verified\" claim is unconfirmed, trust the task list";
            }
            return result;
        }

        /// <summary>交给模型的更正消息。/ Correction message for the model.</summary>
        public static string Correction(ClaimCheckResult r, int nextTaskId)
        {
            if (r.MissingIds.Count == 0)
                return Marker + " 你上一条回复自己写了「工具记录」并声称了工具结果，但本轮实际没有调用任何入队工具，那些结果都不存在。" +
                       "请先向用户如实更正；如果用户确实要求发布任务，现在调用 send_task 实际发布，只汇报工具返回的编号与推送结果。今后绝不手写工具记录或工具结果。" +
                       " / Your previous reply wrote its own \"tool log\" and claimed tool results, but no enqueue tool was called, so those results do not exist. " +
                       "Correct this with the user first; if the user did ask for tasks, call send_task now and report only what it returns. Never write tool logs or tool results yourself.";
            string zh = string.Join("、", r.MissingIds.Select(i => "@" + i));
            string en = string.Join(", ", r.MissingIds.Select(i => "@" + i));
            return Marker + " 你上一条回复声称 " + zh + " 已入队或已核实，但任务清单中没有这些编号（下一个待分配的编号是 @" + nextTaskId +
                   "），相关任务没有入队。原因是你没有真正调用 send_task（或 list_tasks），而是直接写出了结果。" +
                   "请先向用户如实更正；如果用户确实要求发布这些任务，现在逐个调用 send_task 实际发布，只汇报工具返回的编号与推送结果。" +
                   "今后没有工具返回就不得声称入队、推送或核实。" +
                   " / Your previous reply claimed " + en + " were queued or verified (next ID to assign: @" + nextTaskId + ")" +
                   "; the task list has no such IDs and the tasks were never queued: you wrote the result without actually calling send_task (or list_tasks). " +
                   "Correct this with the user first; if the user did ask for these tasks, call send_task for each one now and report only the IDs and push results the tool returns. " +
                   "Never claim a task was queued, pushed or verified without a tool result.";
        }

        /// <summary>
        /// 替换进模型上下文的撤回说明：虚报的原文若留在上下文里，模型会照着格式继续虚报。
        /// Retraction put into the model context instead of a false reply: left in place, the false text is imitated in later rounds.
        /// </summary>
        public const string Retraction = "（VSManager 已撤回这条助手回复：它声称了入队 / 推送 / 核实结果，但本轮没有调用对应工具，内容不属实，没有任何任务因此入队。" +
                                         "/ VSManager retracted this assistant reply: it claimed enqueue / push / verification results without calling the matching tool; it was false and queued nothing.）";

        /// <summary>
        /// 恢复对话时该条回复是否应以撤回说明代替：被标记过「⚠ 核查」，或声称入队 / 核实却没有任何工具步骤。
        /// Whether a restored reply should be replaced by the retraction: it was flagged by a check, or it claims enqueue / verification with no tool step.
        /// </summary>
        public static bool ShouldRetract(string text, IList<string> steps)
        {
            var list = (steps ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            if (list.Any(x => x.StartsWith("⚠ 核查", StringComparison.Ordinal))) return true;
            if (list.Any(x => x.StartsWith("⚙", StringComparison.Ordinal))) return false;
            // 没有工具步骤时只撤回自己写出「工具记录」的回复；普通转述（如完成汇报）照常保留 / Without tool steps only forged tool logs are retracted; plain recaps stay
            return ForgedLog.IsMatch(text ?? "");
        }

        /// <summary>去掉回复中伪造的「工具记录」段落（旧版本曾在上下文中附加，模型会模仿）。/ Strips forged "tool log" blocks (an older build added them to the context and the model copied them).</summary>
        public static string StripForgedLogs(string text) =>
            string.IsNullOrEmpty(text) ? text ?? "" : Regex.Replace(text, @"\s*〔\s*工具记录[^〕]*〕?", "", RegexOptions.CultureInvariant).TrimEnd();
    }
}