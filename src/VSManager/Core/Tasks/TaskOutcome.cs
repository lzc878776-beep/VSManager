using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VSManager
{
    /// <summary>Copilot 最终回复中的任务回执。/ Task receipt found at the end of Copilot's final reply.</summary>
    public enum TaskReceipt
    {
        /// <summary>没有本次任务的有效回执。/ No valid receipt for this attempt.</summary>
        None,
        /// <summary>本任务已完成。/ The task is complete.</summary>
        Success,
        /// <summary>
        /// 待验证：改动已完成，但尚未在运行环境中验证，或需要用户测试、运行或确认（旧回执 NEEDS_USER 也归入此类）。
        /// Awaiting verification: changes are done but not yet verified at runtime, or need user testing, running or confirmation
        /// (the legacy NEEDS_USER receipt maps here too).
        /// </summary>
        Unverified,
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
        /// <summary>Copilot 本轮没有正常执行完（网络 / 服务错误、被中断、没有回复），与任务内容无关。/ Copilot's run did not finish (network / service error, interrupted, no reply); unrelated to the task content.</summary>
        public const string Interrupted = "interrupted";

        /// <summary>失败是否与任务内容有关（有 Copilot 的回复可供分析）。/ Whether the failure concerns the task content (a Copilot reply exists).</summary>
        public static bool IsContent(string kind) => kind == NoReceipt || kind == Reported;

        /// <summary>
        /// 已知的非内容类失败（没送达、没读到或 Copilot 本轮没执行完）：可原样重试，不计入执行次数。未分类（如 Worktree 失败）不在其内。
        /// Known non-content failures (not delivered, not read, or Copilot's run unfinished): may be retried unchanged and do
        /// not count as runs. Unclassified failures (such as worktree failures) are excluded.
        /// </summary>
        public static bool IsRecoverable(string kind) => kind == Delivery || kind == VsClosed || kind == ReadError || kind == Interrupted;

        public static string Label(string kind)
        {
            switch (kind)
            {
                case Delivery: return "投递失败 / delivery failure";
                case VsClosed: return "目标 VS 已关闭 / target VS closed";
                case ReadError: return "读取回复失败 / reply read failure";
                case NoReceipt: return "缺少回执 / missing receipt";
                case Reported: return "Copilot 回报失败 / reported by Copilot";
                case Interrupted: return "Copilot 本轮未执行完 / Copilot run interrupted";
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
            if (kind == FailureKind.Interrupted)
                return "处理建议：Copilot 本轮没有正常执行完（网络 / 服务错误或被中断），不是任务内容的问题，不计入该需求的执行次数；可直接用 retry_task 重试，或发送「继续」让 Copilot 接着做，不必总结新内容；连续多次仍中断时请用户检查网络或 Copilot 状态。"
                    + " / Guidance: Copilot's run did not finish (network / service error or interruption). This is not a task-content problem and does not count toward the request's runs; retry it with retry_task or send \"continue\" without composing new content; if it keeps getting interrupted, ask the user to check the network or Copilot.";
            if (!FailureKind.IsContent(kind) && !FailureKind.IsRecoverable(kind))
                return "处理建议：这是未分类的问题（如 Worktree 失败），与 Copilot 回复无关；向用户说明原因，用户处理并同意后可原样重新排队。"
                    + " / Guidance: this is an unclassified problem (such as a worktree failure) unrelated to a Copilot reply; explain the cause to the user, and requeue unchanged once the user has resolved it and agrees.";
            if (!FailureKind.IsContent(kind))
                return "处理建议：这是投递类问题（未送达或未读到结果），与任务内容无关，不计入执行次数；原因已排除或可能是偶发问题时可用 retry_task 原样重试，VS 未打开 / 弹窗 / 输入框异常等需要用户处理时先告诉用户。"
                    + " / Guidance: this is a delivery problem (not delivered or result not read), unrelated to the task content and not counted as a run; retry unchanged with retry_task when the cause is gone or likely transient, and tell the user first when it needs them (VS not open, a dialog, input box problems).";
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

        /// <summary>AI 自主执行次数上限的默认值与可调范围。/ Default and allowed range of the AI self-run limit.</summary>
        public const int DefaultAiAttempts = 3, MinAiAttemptsLimit = 1, MaxAiAttemptsLimit = 10;

        /// <summary>设置中的上限来源（程序启动时接入）；未接入时用默认值。/ Limit source from settings (wired at startup); the default applies when unset.</summary>
        public static Func<int> AttemptsLimitSource;

        public static int ClampAttempts(int value) =>
            value <= 0 ? DefaultAiAttempts : Math.Min(Math.Max(MinAiAttemptsLimit, value), MaxAiAttemptsLimit);

        /// <summary>
        /// 同一需求（重发链 + 补充重试）允许 AI 助手自主触发的 Copilot 执行次数上限；达到后必须交给用户决定。只统计因任务内容失败或待验证的执行。
        /// Maximum Copilot runs the AI assistant may trigger on its own for one request (resend chain + retries with info);
        /// beyond it the user must decide. Only runs that ended as content failures or awaiting verification count.
        /// </summary>
        public static int MaxAiAttempts
        {
            get
            {
                try { return ClampAttempts(AttemptsLimitSource?.Invoke() ?? DefaultAiAttempts); }
                catch { return DefaultAiAttempts; }
            }
        }

        /// <summary>
        /// 非内容类失败（投递、读取、Copilot 本轮中断）每个任务允许 AI 直接重试的次数，防止网络持续异常时无限循环；不计入 <see cref="MaxAiAttempts"/>。
        /// Direct AI retries allowed per task after non-content failures (delivery, read, interrupted run), preventing endless
        /// loops during a lasting outage; not counted toward <see cref="MaxAiAttempts"/>.
        /// </summary>
        public const int MaxRecoveryRetries = 5;

        /// <summary>供特性描述使用的文字形式（特性参数须为常量），须与 <see cref="MaxRecoveryRetries"/> 一致。/ Text form for attribute descriptions (attribute arguments must be constants); must match <see cref="MaxRecoveryRetries"/>.</summary>
        public const string MaxRecoveryRetriesText = "5";

        // Copilot 自身的错误 / 中断提示（只在回复很短时采信）/ Copilot's own error or interruption notices (trusted only in short replies)
        private static readonly string[] InterruptedWords =
        {
            "网络错误", "网络连接", "网络异常", "请求失败", "请求超时", "连接超时", "连接已断开", "服务不可用", "暂时不可用", "速率限制", "发生错误", "出现错误", "出了点问题",
            "已取消", "已停止", "被中断", "稍后再试", "稍后重试",
            "network error", "network connection", "request failed", "your request failed", "timed out", "service unavailable", "temporarily unavailable",
            "rate limit", "too many requests", "an error occurred", "something went wrong", "internal server error", "canceled", "cancelled",
            "stopped", "interrupted", "try again later", "connection was closed", "connection reset"
        };

        /// <summary>视为「短回复」的最大长度。/ Maximum length of a reply treated as short.</summary>
        public const int InterruptedReplyMax = 500;

        /// <summary>
        /// 没有回执的回复是否只是 Copilot 本轮没执行完：空回复，或很短且是网络 / 服务错误、取消、中断之类的提示。
        /// Whether a reply without receipt only shows that Copilot's run did not finish: empty, or short and reading like a
        /// network / service error, cancellation or interruption notice.
        /// </summary>
        public static bool LooksInterrupted(string answer)
        {
            string s = (answer ?? "").Trim();
            if (s.Length == 0) return true;
            return s.Length <= InterruptedReplyMax && ContainsAny(s, InterruptedWords);
        }

        /// <summary>判定为「有实质修改」所需的最少新增字符二元组数。/ Minimum new character bigrams for a revision to count as substantive.</summary>
        public const int MinNovelBigrams = 8;

        // 只表达「再试一次」的套话，不算修改 / Filler that only says "try again"; not a revision
        private static readonly System.Text.RegularExpressions.Regex RetryFiller = new System.Text.RegularExpressions.Regex(
            "请?(?:再|重新)(?:试|执行|尝试|运行|做)(?:一次|一遍|一下)?|请?重试|继续完成|认真|仔细|务必|一定要|please|try\\s*again|retry|again|carefully|make\\s*sure",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string Canon(string text)
        {
            string s = ResentTaskMatcher.StripMarker((text ?? "").Trim(), out _);
            s = RetryFiller.Replace(s, "");
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static HashSet<string> Bigrams(string s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < s.Length; i++) set.Add(s.Substring(i, 2));
            if (s.Length == 1) set.Add(s);
            return set;
        }

        /// <summary>
        /// <paramref name="next"/> 相对 <paramref name="previous"/> 是否只是原样或近似原样的重复：忽略重发标记、空白、标点与「请再试一次」类套话后，
        /// 新增内容不足 <see cref="MinNovelBigrams"/> 个二元组；明显删减（缩小范围）不算重复。
        /// Whether <paramref name="next"/> merely repeats <paramref name="previous"/>: ignoring resend markers, whitespace,
        /// punctuation and "try again" filler, it adds fewer than <see cref="MinNovelBigrams"/> new bigrams; a clear cut
        /// (narrowed scope) is not a repeat.
        /// </summary>
        public static bool IsNearRepeat(string previous, string next)
        {
            string a = Canon(previous), b = Canon(next);
            if (a.Length == 0) return false;
            if (b.Length == 0 || a == b) return true;
            if (b.Length < a.Length * 0.7) return false;
            var old = Bigrams(a);
            return Bigrams(b).Count(g => !old.Contains(g)) < MinNovelBigrams;
        }

        /// <summary>
        /// 查找同目标、因任务内容失败、且新正文只是原样或近似原样重复的历史任务：AI 助手不得这样重发。
        /// Finds a same-target task that failed on its content and that the new text only repeats (verbatim or nearly so):
        /// the AI assistant must not resend it like that.
        /// </summary>
        public static QueuedTask FindRepeatedResend(IEnumerable<QueuedTask> items, string vsKey, string text)
        {
            if (items == null || string.IsNullOrWhiteSpace(text)) return null;
            var probe = new QueuedTask { VsKey = vsKey ?? "" };
            return items.Where(t => t != null && t.Status == QueueStatus.Failed && FailureKind.IsContent(t.FailureKind)
                    && ResentTaskMatcher.SameTarget(t, probe) && IsNearRepeat(t.Text, text))
                .OrderByDescending(t => t.Id).FirstOrDefault();
        }

        /// <summary>
        /// 一个需求已让 Copilot 执行的次数：从种子任务沿「取代」关系回溯，累计每个任务以内容失败或待验证结束的执行次数（<see cref="QueuedTask.ContentRuns"/>）。
        /// 投递、读取失败和 Copilot 本轮中断不计（不是任务内容的问题）。
        /// Copilot runs already spent on a request: walks back from the seeds along "replaces" links and adds each task's runs
        /// that ended as content failures or awaiting verification (<see cref="QueuedTask.ContentRuns"/>). Delivery and read
        /// failures and interrupted runs are not counted (not a task-content problem).
        /// </summary>
        public static int LineageAttempts(IEnumerable<QueuedTask> items, IEnumerable<QueuedTask> seeds)
        {
            var all = items?.Where(x => x != null).ToList() ?? new List<QueuedTask>();
            var seen = new HashSet<int>();
            var stack = new Stack<QueuedTask>((seeds ?? Enumerable.Empty<QueuedTask>()).Where(s => s != null));
            int n = 0;
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (!seen.Add(t.Id)) continue;
                bool ran = (t.Status == QueueStatus.Failed && FailureKind.IsContent(t.FailureKind)) || t.Status == QueueStatus.Unverified || (t.Status == QueueStatus.Done && t.NeedsUser);
                // 旧数据没有计数时至少计最近一次 / Older records without the counter count at least the latest run
                n += Math.Max(Math.Max(0, t.ContentRuns), ran ? 1 : 0);
                if (t.Replaces == null) continue;
                foreach (int id in t.Replaces)
                {
                    var r = all.FirstOrDefault(x => x.Id == id && ResentTaskMatcher.SameTarget(x, t));
                    if (r != null) stack.Push(r);
                }
            }
            return n;
        }

        /// <summary>
        /// AI 助手以新任务重发前的检查：近似原样重复、或该需求已达 <see cref="MaxAiAttempts"/> 次时返回拒绝原因，否则 null。
        /// Check before the AI assistant resends as a new task: returns the refusal when the text nearly repeats a failed task or
        /// the request has reached <see cref="MaxAiAttempts"/> runs; null otherwise.
        /// </summary>
        public static string CheckAiResend(IEnumerable<QueuedTask> items, string vsKey, string text)
        {
            var list = items?.Where(x => x != null).ToList() ?? new List<QueuedTask>();
            if (FindRepeatedResend(list, vsKey, text) is QueuedTask same)
                return $"未入队：与失败任务 #{same.Id}（{FailureKind.Label(same.FailureKind)}）的内容相同或只差「请再试一次」之类的措辞，重复发送大概率仍会失败并白白消耗 Copilot 用量。"
                    + $"请先根据其 Copilot 回复判断原因：遗留问题或需用户测试时向用户说明；确需重试时以「重发 @{same.Id}：」发布针对失败原因写明具体调整的任务，或让用户在任务清单中重新排队。"
                    + $"Copilot 回复：{TextUtil.Clip(same.Result ?? same.Error, 400)} / "
                    + $"Not queued: the text repeats failed task #{same.Id} (verbatim or only reworded like 'try again'), which would likely fail again and waste Copilot usage. "
                    + $"Analyze its Copilot reply first: report pre-existing issues or needed user tests to the user; if a retry is needed, publish a task prefixed 'resend @{same.Id}:' that states concrete changes addressing the cause, or let the user requeue it.";
            var probe = new QueuedTask { Id = int.MaxValue, VsKey = vsKey ?? "", Text = text ?? "" };
            var replaced = ResentTaskMatcher.Find(list, probe).Hide;
            int spent = LineageAttempts(list, replaced);
            if (spent >= MaxAiAttempts)
                return LimitText(replaced.Max(x => x.Id), spent);
            return null;
        }

        /// <summary>
        /// AI 助手补充信息重试前的检查：该需求已达 <see cref="MaxAiAttempts"/> 次，或补充信息没有新内容时返回拒绝原因，否则 null。
        /// Check before the AI assistant retries with info: returns the refusal when the request has reached
        /// <see cref="MaxAiAttempts"/> runs or the info adds nothing new; null otherwise.
        /// </summary>
        public static string CheckAiRetry(IEnumerable<QueuedTask> items, QueuedTask t, string info)
        {
            if (t == null) return null;
            // 本轮没执行完或没送达：允许「继续」「再试一次」，不查新内容也不占次数 / Run unfinished or undelivered: allow "continue" / "try again" without novelty or limit checks
            if (t.Status == QueueStatus.Failed && FailureKind.IsRecoverable(t.FailureKind)) return null;
            int spent = LineageAttempts(items, new[] { t });
            if (spent >= MaxAiAttempts) return LimitText(t.Id, spent);
            if (IsNearRepeat(t.Text + " " + t.Supplement, t.Text + " " + t.Supplement + " " + info))
                return "补充信息没有新内容（与任务正文或已有补充重复，或只是「请再试一次」）：请根据 Copilot 回复写明具体的新信息或调整，否则把情况交给用户。"
                    + $"Copilot 回复：{TextUtil.Clip(t.Result ?? t.Error, 400)}"
                    + " / The info adds nothing new (repeats the task or earlier info, or only says 'try again'): state concrete new information or changes from the Copilot reply, or hand over to the user.";
            return null;
        }

        /// <summary>
        /// 新任务的目标 VS 上正在阻塞后续的失败 / 待验证任务（按接续等级；未放行、未被该文本以「重发 @编号」取代）。
        /// Failed / awaiting-verification tasks that currently block successors on the new task's target (per continuation level;
        /// not released and not superseded by this text as a "resend @id").
        /// </summary>
        public static List<QueuedTask> HeldBlockers(IEnumerable<QueuedTask> items, string vsKey, string text, ReleaseLevel level)
        {
            var list = items?.Where(x => x != null).ToList() ?? new List<QueuedTask>();
            var probe = new QueuedTask { Id = int.MaxValue, VsKey = vsKey ?? "", Text = text ?? "" };
            var superseded = new HashSet<int>(ResentTaskMatcher.Find(list, probe).Hide.Select(x => x.Id));
            return TaskStateMachine.BlockingTasks(list, probe, level)
                .Where(x => TaskStateMachine.IsHoldOutcome(x) && !superseded.Contains(x.Id)).ToList();
        }

        /// <summary>
        /// AI 向被失败 / 待验证任务阻塞的 VS 发布新任务时的提示：应向阻塞任务补充信息，而不是另起新任务。
        /// Notice when the AI publishes to a VS blocked by a failed / awaiting-verification task: supplement the blocker instead of
        /// adding a new task.
        /// </summary>
        public static string BlockedSendText(IReadOnlyList<QueuedTask> blockers, string targetName)
        {
            if (blockers == null || blockers.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append($"未入队：「{targetName}」的后续任务正被以下任务按接续等级暂停，新任务只会排在它后面继续等待，无法解决阻塞 / "
                + $"Not queued: successors on \"{targetName}\" are paused by the tasks below; a new task would only wait behind them and cannot clear the block:");
            foreach (var b in blockers)
            {
                string note = b.Status == QueueStatus.Failed ? b.FailureReason ?? b.Error : b.PendingNote;
                sb.Append($"\n#{b.Id}（{TaskStateMachine.StatusText(b, DateTime.Now)}）{TextUtil.Clip(b.Text, 120)}");
                if (!string.IsNullOrWhiteSpace(note)) sb.Append(" ｜ ").Append(TextUtil.Clip(note, 200));
            }
            int id = blockers[0].Id;
            sb.Append($"\n若这条内容是对 #{id} 的补充、修正、追加要求或验证反馈，请改用 retry_task_with_info(id={id}, info=…) 发给该任务，它会带着前次反馈在原 VS 重试；"
                + "内容来自用户本轮消息时设 from_user=true（不受 AI 自主重试次数限制）。用户确认验证通过或忽略它时用 release_task 放行，不再需要时用 cancel_task 取消。"
                + "只有确实与阻塞任务无关、且愿意排在其后等待的新任务，才再次调用 send_task 并设 queue_behind_blocked=true。 / "
                + $"If this content supplements, corrects, extends or gives verification feedback on #{id}, call retry_task_with_info(id={id}, info=…) instead; it retries on the same VS with the previous feedback. "
                + "Set from_user=true when the content comes from the user's message (not capped by the AI self-retry limit). Use release_task when the user confirms or waives it, or cancel_task when it is no longer needed. "
                + "Only for a genuinely unrelated task that may wait behind the blocker, call send_task again with queue_behind_blocked=true.");
            return sb.ToString();
        }

        private static string LimitText(int id, int spent) =>
            $"已拒绝：任务 #{id} 所属需求已让 Copilot 执行 {spent} 次仍未完成（AI 自主重试上限 {MaxAiAttempts} 次）。不要再自行重发、改写后重发或补充重试；"
            + "请把失败原因、各次尝试做过的调整和你判断需要的信息整理给用户，由用户补充信息、放行、取消或亲自在任务清单中重试。"
            + $" / Refused: the request of task #{id} has already used {spent} Copilot runs without success (AI self-retry limit {MaxAiAttempts}). "
            + "Do not resend, reword-and-resend or retry with info on your own; summarize the cause, what each attempt changed and what you think is needed for the user, "
            + "who may supplement, release, cancel or retry it from the task list.";

        /// <summary>
        /// AI 助手直接重试（retry_task）前的检查：只允许非内容类失败，且每个任务最多 <see cref="MaxRecoveryRetries"/> 次；允许时返回 null。
        /// Check before a direct AI retry (retry_task): only non-content failures qualify, at most <see cref="MaxRecoveryRetries"/>
        /// times per task; null when allowed.
        /// </summary>
        public static string CheckRecoveryRetry(QueuedTask t)
        {
            if (t == null) return "任务不存在 / Task not found.";
            if (t.Status != QueueStatus.Failed)
                return $"任务 #{t.Id} 没有失败，无需重试 / Task #{t.Id} has not failed; nothing to retry.";
            if (!FailureKind.IsRecoverable(t.FailureKind))
                return $"已拒绝：任务 #{t.Id} 是「{FailureKind.Label(t.FailureKind)}」，不是投递、读取或 Copilot 本轮中断这类可原样重试的失败，原样重试大概率仍会失败。请根据其 Copilot 回复分析原因，用 retry_task_with_info 写明新信息，或交给用户。"
                    + $" / Refused: task #{t.Id} is '{FailureKind.Label(t.FailureKind)}', not a delivery, read or interrupted-run failure that can be retried unchanged; an unchanged retry would likely fail again. Analyze its Copilot reply and use retry_task_with_info with new information, or hand it to the user.";
            if (t.RecoveryRetries >= MaxRecoveryRetries)
                return $"已拒绝：任务 #{t.Id} 已直接重试 {t.RecoveryRetries} 次仍未执行完（上限 {MaxRecoveryRetries} 次），可能是网络、Copilot 服务或 VS 持续异常；请让用户检查后在任务清单中重试。"
                    + $" / Refused: task #{t.Id} has been retried directly {t.RecoveryRetries} times without finishing (limit {MaxRecoveryRetries}); the network, the Copilot service or VS may be failing persistently. Ask the user to check and retry it from the task list.";
            return null;
        }

        /// <summary>给失败通知用的次数说明。/ Attempt summary for failure notices.</summary>
        public static string AttemptsNote(IEnumerable<QueuedTask> items, QueuedTask t)
        {
            int spent = LineageAttempts(items, new[] { t });
            int left = Math.Max(0, MaxAiAttempts - spent);
            return left == 0
                ? $"该需求已让 Copilot 执行 {spent} 次，已达 AI 自主重试上限：只能交给用户决定。/ This request has used {spent} Copilot runs and reached the AI self-retry limit: hand it over to the user."
                : $"该需求已让 Copilot 执行 {spent} 次，AI 自主重试还剩 {left} 次；每次重试都会消耗 Copilot 用量，没有新信息就不要重试。/ This request has used {spent} Copilot runs; {left} AI self-retries left. Each retry costs Copilot usage, so do not retry without new information.";
        }

        /// <summary>非内容类失败通知用的直接重试说明。/ Direct-retry summary for non-content failure notices.</summary>
        public static string RecoveryNote(QueuedTask t)
        {
            int left = Math.Max(0, MaxRecoveryRetries - (t?.RecoveryRetries ?? 0));
            return left == 0
                ? $"本任务已直接重试 {MaxRecoveryRetries} 次，请交给用户检查。/ This task has been retried directly {MaxRecoveryRetries} times; hand it to the user."
                : $"不计入执行次数；可用 retry_task 直接重试（还剩 {left} 次）。/ Not counted as a run; retry_task can retry it directly ({left} left).";
        }
    }
}
