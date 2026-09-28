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
            @"已(?:确认|重新|成功)?(?:入队|加入(?:任务)?清单)" +
            @"|已(?:确认|重新|成功)?(?:发布|发送|推送|派发)(?:到|至|给)\s*[*「]*(?:#\d|Copilot)" +
            @"|(?:入队|发布)成功|推送成功" +
            @"|\b(?:enqueued|queued as @|published to #|pushed to (?:#|Copilot))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex VerifyClaim = new Regex(
            @"(?:核实|核对|查看|查)(?:了|过)?(?:一下)?(?:当前)?(?:的)?(?:任务)?清单|确实(?:已经?)?存在" +
            @"|\b(?:verified|checked) the (?:task )?list\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TaskId = new Regex(@"([@#])(\d{1,6})\b", RegexOptions.CultureInvariant);

        /// <summary>承认「不存在 / 没有入队」的句子：其中的编号不算虚报。/ Sentences admitting "does not exist / not queued": their IDs are not false claims.</summary>
        private static readonly Regex Denial = new Regex(
            @"不存在|没有(?:真正|实际)?(?:入队|成功|发布|加入)|未(?:能)?(?:入队|发布|推送|加入)|并未|没入队|失败|已取消|已删除" +
            @"|\b(?:not (?:queued|in the (?:task )?list)|never queued|does not exist|do not exist|failed|cancel(?:l)?ed|deleted)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SentenceBreak = new Regex(@"[。！？!?\n]+", RegexOptions.CultureInvariant);

        /// <summary>「#n」也常用作 VS 编号，只有明显超出 VS 编号范围时才当作任务编号。/ "#n" also numbers VS instances; treat it as a task ID only well beyond that range.</summary>
        private const int MinHashTaskId = 21;

        /// <summary>
        /// 核查一条回复。nextTaskId 为任务清单下一个将分配的编号（≤0 表示未知，不做编号核查）。
        /// Checks one reply. nextTaskId is the next ID the task list will assign (≤0 = unknown, skip the ID check).
        /// </summary>
        public static ClaimCheckResult Check(string reply, ICollection<string> calledTools, int nextTaskId)
        {
            var result = new ClaimCheckResult { Verdict = ClaimVerdict.Ok };
            reply = reply ?? "";
            var tools = calledTools ?? new string[0];
            // 只看没有承认失败 / 不存在的句子（转述或检讨之前的虚报不算新说法）/ Only sentences that do not admit failure count (quoting an earlier false claim is not a new one)
            var affirmed = SentenceBreak.Split(reply).Where(x => !Denial.IsMatch(x)).ToList();
            bool claimsEnqueue = affirmed.Any(x => EnqueueClaim.IsMatch(x));
            bool claimsVerify = affirmed.Any(x => VerifyClaim.IsMatch(x));
            if (!claimsEnqueue && !claimsVerify) return result;

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
            var missing = nextTaskId > 0 ? ids.Where(i => i >= nextTaskId && !denied.Contains(i)).ToList() : new List<int>();
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
            string zh = string.Join("、", r.MissingIds.Select(i => "@" + i));
            string en = string.Join(", ", r.MissingIds.Select(i => "@" + i));
            return Marker + " 你上一条回复声称 " + zh + " 已入队或已核实，但任务清单下一个待分配的编号是 @" + nextTaskId +
                   "，这些编号根本不存在，相关任务没有入队。原因是你没有真正调用 send_task（或 list_tasks），而是直接写出了结果。" +
                   "请先向用户如实更正；如果用户确实要求发布这些任务，现在逐个调用 send_task 实际发布，只汇报工具返回的编号与推送结果。" +
                   "今后没有工具返回就不得声称入队、推送或核实。" +
                   " / Your previous reply claimed " + en + " were queued or verified, but the next ID the task list will assign is @" + nextTaskId +
                   ", so these IDs do not exist and the tasks were never queued: you wrote the result without actually calling send_task (or list_tasks). " +
                   "Correct this with the user first; if the user did ask for these tasks, call send_task for each one now and report only the IDs and push results the tool returns. " +
                   "Never claim a task was queued, pushed or verified without a tool result.";
        }

        /// <summary>
        /// 接续对话时附加到历史回复末尾的工具记录，让模型看到当时是否真的调用过工具，而不是只看到「已入队」文字去模仿。
        /// Tool log appended to restored replies so the model sees whether tools were really called, instead of imitating bare "queued" text.
        /// </summary>
        public static string HistoryAnnotation(string text, IList<string> steps)
        {
            var lines = (steps ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Where(s => s.StartsWith("⚙", StringComparison.Ordinal) || s.StartsWith("↳", StringComparison.Ordinal) || s.StartsWith("⚠ 核查", StringComparison.Ordinal))
                .Select(s => s.Replace('\r', ' ').Replace('\n', ' '))
                .Select(s => s.Length > 120 ? s.Substring(0, 120) + "…" : s)
                .ToList();
            if (lines.Count > 8) lines = lines.Take(4).Concat(new[] { "…" }).Concat(lines.Skip(lines.Count - 4)).ToList();
            if (lines.Count > 0)
                return "\n〔工具记录（VSManager 自动附加，不可手写模仿）/ Tool log (added by VSManager, never write it yourself)：" + string.Join("；", lines) + "〕";
            if (Check(text, new string[0], 0).Verdict != ClaimVerdict.Ok)
                return "\n〔VSManager 核查：这一轮没有调用任何工具，上面的入队 / 核实说法没有工具依据 / VSManager check: no tool was called in this round; the claims above have no tool backing〕";
            return "";
        }
    }
}