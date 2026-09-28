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
        /// <summary>说法未经本轮工具确认，但可能只是转述已有结果（仅提醒）。/ Claim not confirmed by a tool this round, but possibly a recap of earlier results (warn only).</summary>
        Unconfirmed,
        /// <summary>虚报：编号不存在、伪造工具记录、没调用入队工具却声称刚入队，或工具已拒绝却声称成功。/ False claim: IDs that do not exist, a forged tool log, a fresh enqueue claim without an enqueue tool, or success claimed after the tool refused.</summary>
        Fabricated,
    }

    /// <summary>核查未通过的具体原因。/ Why the check failed.</summary>
    internal enum ClaimReason
    {
        None,
        /// <summary>声称的编号不在任务清单中。/ Claimed IDs are not in the task list.</summary>
        MissingIds,
        /// <summary>回复里手写了「工具记录」。/ The reply wrote its own "tool log".</summary>
        ForgedLog,
        /// <summary>声称刚入队 / 推送，但本轮没有调用任何入队工具。/ Claims a fresh enqueue / push without calling any enqueue tool.</summary>
        NoEnqueueTool,
        /// <summary>入队工具全部返回拒绝 / 失败，回复却声称成功。/ Every enqueue tool call was refused or failed, yet the reply claims success.</summary>
        EnqueueRejected,
        /// <summary>转述入队结果但本轮未经工具确认（可能只是回顾）。/ Recaps an enqueue result without a tool this round (may be a recap).</summary>
        RecapUnconfirmed,
        /// <summary>声称核实了清单但没有调用 list_tasks。/ Claims the list was verified without list_tasks.</summary>
        VerifyUnconfirmed,
    }

    /// <summary>工具真实性核查结果。/ Result of the tool-truthfulness check.</summary>
    internal sealed class ClaimCheckResult
    {
        public ClaimVerdict Verdict;
        /// <summary>尚未分配、却被声称已入队 / 存在的任务编号。/ Task IDs claimed but never assigned.</summary>
        public IReadOnlyList<int> MissingIds = new int[0];
        /// <summary>未通过的原因。/ Why the check failed.</summary>
        public ClaimReason Reason;
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
            "send_task", "request_vsmanager_improvement", "retry_task", "retry_task_with_info", "continue_task",
        };

        /// <summary>会返回任务清单实际状态的工具。/ Tools that return the actual task-list state.</summary>
        internal static readonly HashSet<string> QueueReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "list_tasks", "list_test_checklists", "send_task", "request_vsmanager_improvement", "retry_task", "retry_task_with_info", "continue_task", "read_task_reply",
        };

        /// <summary>「已」后常见的修饰词。/ Common modifiers after "已".</summary>
        private const string Mods = "(?:经|确认|重新|成功|实际|真正|逐条|逐个|分别|全部|都|均|同时|顺利|直接|原样|依次)*";

        private static readonly Regex EnqueueClaim = new Regex(
            @"已" + Mods + @"(?:入队|排队|排入队列|加入(?:任务)?(?:清单|队列))" +
            // 「已经把 3 个任务加入清单」「加入了任务队列」/ "added the tasks to the list"
            @"|(?:已|成功)" + Mods + @"(?:把|将)?[^。\n，,；;]{0,20}?(?:加入|添加|放入|排入|加)(?:到|进|至)?了?\s*(?:任务)?(?:清单|队列)" +
            @"|(?:加入|添加|放入|排入)(?:到|进)?了\s*(?:任务)?(?:清单|队列)" +
            @"|(?<!(?:代码|改动|修改|更改|提交|分支|commit)\s*)已" + Mods + @"(?:(?:把|将)[^。\n，,；;]{0,20}?)?(?:发布|发送|推送|派发|下发|转发|提交|发|交)(?:到|至|给)了?\s*[*「""]*[^。\n，,]{0,24}?(?:#\d|Copilot|」|VS\b|实例)" +
            @"|已" + Mods + @"(?:发布|派发|下发)(?!说明|版本|的|日志|记录)" +
            @"|(?:任务|需求)(?:\s*[@#]\d+)?(?:\s*[「""][^」""]{0,30}[」""])?\s*(?:均|都|也|全部)?已" + Mods + @"(?:创建|入队|排队|发送|发出|派发|下发|加入|发布)" +
            @"|(?:入队|发布|派发|排队)成功|推送成功|重新排队了|正在排队" +
            // 表格 / 列表中「@编号 … 排队中 / 前面 n 个」也是入队结果 / "@ID … queued / n ahead" rows are enqueue results too
            @"|@\d+[^。\n]{0,60}(?:排队中|前面\s*\d+\s*个)" +
            @"|\b(?:enqueued|queued as @|published to #|pushed to (?:#|Copilot))" +
            @"|\b(?:queued|enqueued|dispatched|submitted|pushed|sent|added|published)\b[^.\n]{0,40}?\b(?:to|into|for|in)\s+(?:the\s+)?(?:Copilot|task list|task queue|queue)\b" +
            @"|\b(?:has|have)\s+been\s+(?:queued|enqueued|dispatched|requeued)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 入队工具返回里的固定说明（如「送达或失败后会另行通知」「尚未推送到 Copilot」）：照抄进回复时不能当作「承认失败」而放过。
        /// Boilerplate from enqueue results ("notified on delivery or failure", "not yet pushed"): copied into a reply it must not count as an admission.
        /// </summary>
        private static readonly Regex ToolBoilerplate = new Regex(
            @"送达或失败后[^。\n；;]*|失败(?:后|时)(?:会)?(?:另行|再)?通知[^。\n；;]*|失败跳过|失败则[^。\n；;]*" +
            @"|(?:you will be )?notified on delivery or failure|(?:on|after) delivery or failure",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>「已入队，（尚）未推送」：入队说法里的「未推送」不是否认入队。/ "Queued, not yet pushed": the "not pushed" part does not deny the enqueue.</summary>
        private static readonly Regex QueuedNotPushed = new Regex(
            @"(?:尚|还)?未(?:能)?推送(?:到\s*Copilot)?|not yet pushed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex QueuedWords = new Regex(
            @"已" + Mods + @"(?:入队|排队|加入(?:任务)?(?:清单|队列))|排队中|\bqueued\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>明确转述之前结果的句子（如「之前已入队的 @12」）。/ Sentences explicitly recapping earlier results ("@12, queued earlier").</summary>
        private static readonly Regex RecapWords = new Regex(
            @"之前|此前|先前|早先|刚才|上一轮|上轮|上次|前面已|原先|原来|\b(?:earlier|previously|before)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>测试清单行（「- [ ] 操作与预期」）描述的是预期效果，不是本轮结果。/ Checklist lines ("- [ ] step and expectation") describe expectations, not results.</summary>
        private static readonly Regex ChecklistLine = new Regex(@"^\s*[-*]\s*\[[ xX]\]", RegexOptions.CultureInvariant);

        /// <summary>引号内的短语（如「⏳ 已加入任务清单」）是在引用提示文字。/ Quoted phrases (e.g. "⏳ queued") cite UI text rather than claim a result.</summary>
        private static readonly Regex Quoted = new Regex(@"「[^」\n]{0,80}」|“[^”\n]{0,80}”|""[^""\n]{0,80}""|`[^`\n]{0,80}`", RegexOptions.CultureInvariant);

        private static readonly Regex Decoration = new Regex(@"[\s*`_>|]+", RegexOptions.CultureInvariant);

        /// <summary>去掉引号内本身就是入队说法的短语。/ Removes quoted phrases that are themselves enqueue wording.</summary>
        private static string WithoutQuotedClaims(string sentence) =>
            Quoted.Replace(sentence ?? "", m => EnqueueClaim.IsMatch(m.Value) ? " " : m.Value);

        /// <summary>入队工具成功的返回文字。/ Result texts of a successful enqueue tool call.</summary>
        private static readonly Regex EnqueueSuccess = new Regex(
            @"已加入任务清单|已原样重新排队|在原条目重新排队|已重新排队为|已有相同任务\s*@|✅\s*推送成功|已暂存",
            RegexOptions.CultureInvariant);

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

        /// <summary>句子里的入队说法是否原样出现在本轮输入中（转述通知内容）。/ Whether the sentence's enqueue wording already appears in this round's input (recapping a notice).</summary>
        private static bool InInput(string input, string sentence)
        {
            if (input.Length == 0) return false;
            string claim = Decoration.Replace(EnqueueClaim.Match(WithoutQuotedClaims(sentence)).Value, "");
            return claim.Length > 0 && input.IndexOf(claim, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 句子是否承认失败 / 不存在（先去掉工具返回的固定说明）。
        /// Whether a sentence admits failure / absence (tool boilerplate removed first).
        /// </summary>
        private static bool Denies(string sentence)
        {
            string s = ToolBoilerplate.Replace(sentence ?? "", " ");
            if (QueuedWords.IsMatch(s)) s = QueuedNotPushed.Replace(s, " ");
            return Denial.IsMatch(s);
        }

        /// <summary>入队工具的返回是否表示成功入队 / 重新排队。/ Whether an enqueue tool result means the task was queued / requeued.</summary>
        internal static bool EnqueueSucceeded(string result) =>
            !string.IsNullOrWhiteSpace(result) && !PushCheck.IsRejected(result.TrimStart()) && EnqueueSuccess.IsMatch(result);

        private static readonly Regex SentenceBreak = new Regex(@"[。！？!?\n]+", RegexOptions.CultureInvariant);

        /// <summary>「#n」也常用作 VS 编号，只有明显超出 VS 编号范围时才当作任务编号。/ "#n" also numbers VS instances; treat it as a task ID only well beyond that range.</summary>
        private const int MinHashTaskId = 21;

        /// <summary>
        /// 核查一条回复。nextTaskId 为任务清单下一个将分配的编号（≤0 表示未知，不做编号核查）；existingIds 为任务清单中现有的编号（null 表示未知）。
        /// Checks one reply. nextTaskId is the next ID the task list will assign (≤0 = unknown, skip the ID check); existingIds are the IDs now in the task list (null = unknown).
        /// </summary>
        /// <param name="toolResults">本轮工具调用的（工具名, 返回文字）；null 表示未知，不核对返回。/ (tool name, result text) of this round's calls; null = unknown, results not checked.</param>
        /// <param name="userRound">用户发起的一轮；通知轮里只转述已有编号的说法仅提醒。/ A user-started round; in notice rounds claims naming only existing IDs just warn.</param>
        public static ClaimCheckResult Check(string reply, ICollection<string> calledTools, int nextTaskId, ICollection<int> existingIds = null,
            IList<KeyValuePair<string, string>> toolResults = null, bool userRound = true, string roundInput = null)
        {
            var result = new ClaimCheckResult { Verdict = ClaimVerdict.Ok };
            reply = reply ?? "";
            var tools = calledTools ?? new string[0];
            // 只看没有承认失败 / 不存在的句子（转述或检讨之前的虚报不算新说法）/ Only sentences that do not admit failure count (quoting an earlier false claim is not a new one)
            var affirmed = SentenceBreak.Split(reply).Where(x => !Denies(x)).ToList();
            // 照搬本轮输入（如完成通知中 Copilot 的原文）的说法不是助手的新说法 / Wording copied from this round's input (e.g. Copilot's text in a notice) is not a new claim
            string input = Decoration.Replace(roundInput ?? "", "");
            var claimSentences = affirmed.Where(x => !ChecklistLine.IsMatch(x) && EnqueueClaim.IsMatch(WithoutQuotedClaims(x)) && !InInput(input, x)).ToList();
            bool claimsEnqueue = claimSentences.Count > 0;
            // 没有回顾字样的入队说法，视为「本轮刚入队 / 推送」/ Enqueue claims without recap words count as "just queued / pushed"
            var fresh = claimSentences.Where(x => !RecapWords.IsMatch(x)).ToList();
            bool claimsVerify = affirmed.Any(x => VerifyClaim.IsMatch(x));
            // 回复里自己写出「工具记录」：只有 VSManager 能生成，模型手写即为伪造 / A "tool log" in the reply text: only VSManager produces it, so the model forged it
            bool forged = ForgedLog.IsMatch(reply);
            if (!claimsEnqueue && !claimsVerify && !forged) return result;

            // 编号 ≥ 下一个待分配编号 → 从未分配过，必然不存在；承认不存在的句子不计 / ID ≥ next ID → never assigned, cannot exist; admitting sentences do not count
            var ids = new SortedSet<int>();
            var denied = new HashSet<int>();
            foreach (var sentence in SentenceBreak.Split(reply))
            {
                bool denies = Denies(sentence);
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
                result.Reason = ClaimReason.MissingIds;
                result.MissingIds = missing;
                string list = string.Join("、", missing.Select(i => "@" + i));
                result.Step = "⚠ 核查未通过：" + list + " 不在任务清单中（下一个编号为 @" + nextTaskId + "；本轮工具：" + called + "），这些任务并未入队" +
                              " / Check failed: " + string.Join(", ", missing.Select(i => "@" + i)) + " are not in the task list (next ID @" + nextTaskId + "; tools this round: " + called + "); these tasks were never queued";
                return result;
            }
            if (forged && !enqueued)
            {
                result.Verdict = ClaimVerdict.Fabricated;
                result.Reason = ClaimReason.ForgedLog;
                result.Step = "⚠ 核查未通过：回复中的「工具记录」是助手自己写的（本轮工具：" + called + "），不是 VSManager 生成的，所述结果不可信" +
                              " / Check failed: the \"tool log\" in the reply was written by the assistant (tools this round: " + called + "), not by VSManager; its results cannot be trusted";
                return result;
            }
            if (claimsEnqueue && !enqueued)
            {
                // 用户轮里任何「刚入队」说法、通知轮里不带现有编号的「刚入队」说法，都没有工具支撑，按虚报更正
                // A fresh claim in a user round, or one naming no existing ID in a notice round, has no tool behind it: correct it as false
                bool namesKnownId = fresh.Any(x => TaskId.Matches(x).Cast<Match>().Any(m => m.Groups[1].Value == "@" || ids.Contains(int.Parse(m.Groups[2].Value))));
                // 调用 list_tasks 后逐条转述现有任务的状态，以工具结果为准 / After list_tasks, describing existing tasks follows the tool result
                bool allNameKnownIds = fresh.All(x => TaskId.Matches(x).Cast<Match>().Any(m => m.Groups[1].Value == "@" || ids.Contains(int.Parse(m.Groups[2].Value))));
                if (readQueue && allNameKnownIds) return result;
                if (fresh.Count > 0 && (userRound || !namesKnownId))
                {
                    result.Verdict = ClaimVerdict.Fabricated;
                    result.Reason = ClaimReason.NoEnqueueTool;
                    result.Step = "⚠ 核查未通过：本轮没有调用任何入队工具（本轮工具：" + called + "），回复却声称任务已入队 / 已推送，没有任务因此入队" +
                                  " / Check failed: no enqueue tool was called this round (tools: " + called + "), yet the reply claims tasks were queued / pushed; nothing was queued";
                    return result;
                }
                result.Verdict = ClaimVerdict.Unconfirmed;
                result.Reason = ClaimReason.RecapUnconfirmed;
                result.Step = "⚠ 核查：本轮未调用 send_task，回复中的「已入队 / 已发布」说法未经本轮工具确认，请以任务清单为准" +
                              " / Check: send_task was not called this round; the \"queued / published\" claim is unconfirmed, trust the task list";
                return result;
            }
            if (fresh.Count > 0 && enqueued && toolResults != null)
            {
                // 入队工具都被拒绝 / 失败，回复却声称成功 / Every enqueue call was refused or failed, yet the reply claims success
                var enqueueResults = toolResults.Where(x => EnqueueTools.Contains(x.Key ?? "")).ToList();
                if (enqueueResults.Count > 0 && !enqueueResults.Any(x => EnqueueSucceeded(x.Value)))
                {
                    string first = OneLine(enqueueResults[0].Value, 80);
                    result.Verdict = ClaimVerdict.Fabricated;
                    result.Reason = ClaimReason.EnqueueRejected;
                    result.Step = "⚠ 核查未通过：本轮入队工具没有成功（返回：" + first + "），回复却声称任务已入队 / 已推送" +
                                  " / Check failed: the enqueue tool did not succeed this round (result: " + first + "), yet the reply claims tasks were queued / pushed";
                    return result;
                }
            }
            if (claimsVerify && !readQueue)
            {
                result.Verdict = ClaimVerdict.Unconfirmed;
                result.Reason = ClaimReason.VerifyUnconfirmed;
                result.Step = "⚠ 核查：本轮未调用 list_tasks，回复中的「已核实清单」说法未经工具确认，请以任务清单为准" +
                              " / Check: list_tasks was not called this round; the \"list verified\" claim is unconfirmed, trust the task list";
            }
            return result;
        }

        /// <summary>交给模型的更正消息。/ Correction message for the model.</summary>
        public static string Correction(ClaimCheckResult r, int nextTaskId)
        {
            if (r.Reason == ClaimReason.NoEnqueueTool)
                return Marker + " 你上一条回复声称任务已入队 / 已推送，但本轮实际没有调用 send_task（或其他入队工具），任务清单里没有因此新增任何任务。" +
                       "请先向用户如实更正；如果用户确实要求发布任务，现在调用 send_task 实际发布；如果只是想说明已有任务的状态，调用 list_tasks 核实后再汇报。只汇报工具返回的编号与推送结果。" +
                       " / Your previous reply claimed tasks were queued / pushed, but no send_task (or other enqueue tool) was called this round, so nothing was added to the task list. " +
                       "Correct this with the user first; if the user did ask for tasks, call send_task now; if you only meant to describe existing tasks, call list_tasks and report what it returns. Report only IDs and push results a tool returns.";
            if (r.Reason == ClaimReason.EnqueueRejected)
                return Marker + " 你上一条回复声称任务已入队 / 已推送，但本轮入队工具返回的是拒绝或失败（以「❌」开头或说明了未执行的原因），任务并没有入队。" +
                       "请先向用户如实更正并转述工具返回的原因；按原因调整后可以再调用 send_task，不要在没有成功返回时声称入队或推送。" +
                       " / Your previous reply claimed tasks were queued / pushed, but the enqueue tool returned a refusal or failure (starting with \"❌\" or stating why it did nothing); nothing was queued. " +
                       "Correct this with the user and relay the tool's reason; adjust and call send_task again if appropriate, and never claim a queue or push without a successful result.";
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

        /// <summary>通知栏里的简短说明。/ Short text for the status notice.</summary>
        public static string Notice(ClaimCheckResult r)
        {
            switch (r.Reason)
            {
                case ClaimReason.NoEnqueueTool:
                    return "⚠ 自动核查：助手声称已入队 / 推送，但本轮没有调用入队工具，已要求助手更正 / Auto check: the assistant claimed a queue / push without calling an enqueue tool; asked it to correct";
                case ClaimReason.EnqueueRejected:
                    return "⚠ 自动核查：入队工具返回失败，助手却声称成功，已要求助手更正 / Auto check: the enqueue tool failed but the assistant claimed success; asked it to correct";
                case ClaimReason.ForgedLog:
                    return "⚠ 自动核查：助手手写了工具记录，已要求助手更正 / Auto check: the assistant wrote its own tool log; asked it to correct";
                default:
                    return "⚠ 自动核查：" + string.Join("、", r.MissingIds.Select(i => "@" + i)) + " 不在任务清单中，已要求助手更正 / Auto check: claimed tasks are not in the task list; asked the assistant to correct";
            }
        }

        /// <summary>
        /// 虚报回复在模型上下文中的替代文字：原文不再留在上下文里，避免模型照着「已入队」的写法继续模仿。
        /// Replacement for a fabricated reply in the model context: the original text is dropped so the model cannot keep imitating it. Same text as <see cref="Retraction"/>.
        /// </summary>
        public const string DiscardedReply = Retraction;

        /// <summary>模型自己手写的「工具记录」块的开头。/ Start of a tool-log block the model wrote by itself.</summary>
        private const string ToolLogStart = "〔工具记录";

        /// <summary>该条记录是否已被核查判定为虚报。/ Whether the record was flagged as fabricated by the check.</summary>
        public static bool IsFabricated(IList<string> steps) =>
            (steps ?? new List<string>()).Any(s => s != null && s.TrimStart().StartsWith("⚠ 核查未通过", StringComparison.Ordinal));

        /// <summary>
        /// 去掉回复中模型手写的「〔工具记录…〕」块（真实工具记录从不出现在正文里）；返回是否有删除。
        /// Removes "〔工具记录…〕" tool-log blocks the model wrote into its reply (real tool logs never appear in the text); returns whether anything was removed.
        /// </summary>
        public static bool StripFakeToolLog(ref string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            bool removed = false;
            int i;
            while ((i = text.IndexOf(ToolLogStart, StringComparison.Ordinal)) >= 0)
            {
                int end = text.IndexOf('〕', i);
                text = text.Substring(0, i).TrimEnd() + (end < 0 ? "" : text.Substring(end + 1));
                removed = true;
            }
            if (removed) text = text.Trim();
            return removed;
        }

        /// <summary>
        /// 接续对话时恢复到模型上下文的助手文字：虚报回复换成作废说明，模型手写的工具记录会被去掉；
        /// 真实的工具调用另行以结构化消息恢复（见 AgentService.RestoreConversation），不再以文字附在回复后面，以免被模型模仿。
        /// Assistant text restored into the model context when resuming: fabricated replies become the discard note and model-written
        /// tool logs are removed; real tool calls are restored as structured messages (see AgentService.RestoreConversation) rather than
        /// appended as text, which the model would imitate.
        /// </summary>
        public static string HistoryText(string text, IList<string> steps, bool hadToolCalls)
        {
            if (IsFabricated(steps)) return DiscardedReply;
            text = (text ?? "").Trim();
            StripFakeToolLog(ref text);
            bool calledTool = hadToolCalls || (steps ?? new List<string>()).Any(s => s != null && s.TrimStart().StartsWith("⚙", StringComparison.Ordinal));
            if (!calledTool && text.Length > 0 && Check(text, new string[0], 0).Verdict != ClaimVerdict.Ok)
                text += "\n（VSManager 核查：这一轮没有调用任何工具，上面的入队 / 核实说法没有工具依据 / VSManager check: no tool was called in this round; the claims above have no tool backing）";
            return text;
        }

        private static string OneLine(string s, int max)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
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