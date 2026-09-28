using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 任务失败时保存的本轮 Copilot 完整回复：包含最后一条用户消息之后的全部回答与过程步骤，供主控 AI 分析原因。
    /// The whole Copilot turn kept when a task fails: every answer and step after the last user message, so the main AI
    /// can analyze the cause.
    /// </summary>
    public static class TaskReply
    {
        /// <summary>保存的最大长度；超出时保留开头与结尾（错误提示通常在结尾）。/ Maximum stored length; beyond it the head and tail are kept (errors usually appear at the end).</summary>
        public const int StoreMax = 40000;
        private const int StoreHead = 8000;
        /// <summary>失败通知中附带的最大长度。/ Maximum length included in a failure notice.</summary>
        public const int NoticeMax = 6000;
        private const int NoticeHead = 1500;
        /// <summary>read_task_reply 每页长度。/ Page size of read_task_reply.</summary>
        public const int PageSize = 8000;
        /// <summary>过程步骤行的前缀。/ Prefix of step lines.</summary>
        public const string StepPrefix = "▸ ";

        /// <summary>
        /// 取本轮（最后一条用户消息之后）的全部 Copilot 内容：正文原样保留，过程步骤加 <see cref="StepPrefix"/>；没有内容时返回 null。
        /// Collects the whole turn (after the last user message): answer text verbatim, steps prefixed with <see cref="StepPrefix"/>;
        /// null when empty.
        /// </summary>
        public static string Round(ChatTranscript chat, TurnLog log = null)
        {
            var sb = new StringBuilder();
            var steps = log?.Steps?.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Header)).ToList() ?? new List<StepLog>();
            var used = new bool[steps.Count];
            void Line(string s) { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); }
            void Step(string header, string detail)
            {
                Line(StepPrefix + OneLine(header));
                foreach (string d in (LogHead(detail) ?? "").Split('\n').Where(x => x.Trim().Length > 0)) Line(DetailPrefix + d.TrimEnd());
            }
            if (chat?.Messages != null)
            {
                int start = chat.Messages.FindLastIndex(m => m.Role == ChatRole.User) + 1;
                foreach (var m in chat.Messages.Skip(start).Where(m => m.Role == ChatRole.Assistant))
                    foreach (var p in m.Parts)
                    {
                        string text = p?.Text?.Trim();
                        if (string.IsNullOrEmpty(text)) continue;
                        if (!p.IsStep) { Line(text); continue; }
                        // 按顺序把展开读取到的日志开头配到同名步骤 / Pair the expanded log heads with same-named steps in order
                        int i = -1;
                        for (int k = 0; k < steps.Count && i < 0; k++)
                            if (!used[k] && SameHeader(steps[k].Header, text)) i = k;
                        if (i >= 0) used[i] = true;
                        Step(text, i >= 0 ? steps[i].Detail : null);
                    }
            }
            for (int k = 0; k < steps.Count; k++)
                if (!used[k]) Step(steps[k].Header, steps[k].Detail);
            foreach (string n in log?.Notices ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(n)) Line(NoticePrefix + OneLine(n));
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>步骤日志内容行的前缀。/ Prefix of step log lines.</summary>
        public const string DetailPrefix = "  │ ";
        /// <summary>Copilot 界面提示（如「此响应被截断」）的前缀。/ Prefix of Copilot UI notices (such as "the response was truncated").</summary>
        public const string NoticePrefix = "⚠ ";
        /// <summary>每个步骤日志只取开头的行数与字数：失败的主要原因通常在日志最顶层。/ Only the first lines and characters of each step log are kept: the main cause usually sits at the top of the log.</summary>
        public const int LogHeadLines = 20, LogHeadChars = 1500;

        /// <summary>取日志开头（最顶层内容）：去掉空行，最多 <see cref="LogHeadLines"/> 行、<see cref="LogHeadChars"/> 字。/ Takes the head (top-level content) of a log: blank lines dropped, at most <see cref="LogHeadLines"/> lines and <see cref="LogHeadChars"/> characters.</summary>
        public static string LogHead(string log, int maxLines = LogHeadLines, int maxChars = LogHeadChars)
        {
            if (string.IsNullOrWhiteSpace(log)) return null;
            var lines = log.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(x => x.TrimEnd()).Where(x => x.Trim().Length > 0).ToList();
            var sb = new StringBuilder();
            int taken = 0;
            foreach (string line in lines)
            {
                if (taken == maxLines || sb.Length + line.Length > maxChars)
                {
                    if (sb.Length == 0) sb.Append(line.Substring(0, Math.Min(line.Length, maxChars)));
                    sb.Append("\n…");
                    break;
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                taken++;
            }
            return sb.ToString();
        }

        private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s*\n\s*", " ").Trim();

        private static bool SameHeader(string a, string b)
        {
            a = Regex.Replace(a ?? "", @"\s+", " ").Trim();
            b = Regex.Replace(b ?? "", @"\s+", " ").Trim();
            return a.Length > 0 && (a == b || b.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(b, StringComparison.Ordinal));
        }

        /// <summary>保存前裁剪：过长时保留开头与结尾并注明省略字数。/ Trims before storing: keeps head and tail and notes the omitted length.</summary>
        public static string Store(string reply) => HeadTail(reply, StoreMax, StoreHead);

        /// <summary>失败通知使用的摘录（开头 + 结尾）。/ Excerpt used in a failure notice (head + tail).</summary>
        public static string Excerpt(string reply) => HeadTail(reply, NoticeMax, NoticeHead);

        private static string HeadTail(string s, int max, int head)
        {
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            if (s.Length <= max) return s;
            int tail = max - head;
            int omitted = s.Length - head - tail;
            return s.Substring(0, head) + $"\n…（中间省略 {omitted} 字 / {omitted} characters omitted）…\n" + s.Substring(s.Length - tail);
        }

        /// <summary>
        /// 分页读取已保存的回复（<paramref name="page"/> 从 1 开始）。
        /// Reads a page of the stored reply (<paramref name="page"/> starts at 1).
        /// </summary>
        public static string Page(QueuedTask t, int page)
        {
            if (t == null) return "任务不存在 / Task not found.";
            string reply = t.Reply;
            if (string.IsNullOrEmpty(reply))
                return string.IsNullOrWhiteSpace(t.Result)
                    ? $"任务 #{t.Id} 没有保存本轮 Copilot 回复（只在失败时保存，重新排队后清除）；可用 read_vs_chat 读取该 VS 当前对话。/ Task #{t.Id} has no stored Copilot turn (kept only on failure and cleared on requeue); use read_vs_chat to read the VS's current chat."
                    : $"任务 #{t.Id} 没有保存完整回复，以下是结果摘要 / No full turn stored; result summary:\n{t.Result}";
            int pages = (reply.Length + PageSize - 1) / PageSize;
            page = Math.Min(Math.Max(1, page), pages);
            string body = reply.Substring((page - 1) * PageSize, Math.Min(PageSize, reply.Length - (page - 1) * PageSize));
            string issue = RunIssue.Label(t.RunIssue);
            return $"任务 #{t.Id} 本轮 Copilot 完整回复 第 {page}/{pages} 页（共 {reply.Length} 字；「{StepPrefix.Trim()}」开头为过程步骤）/ Task #{t.Id} full Copilot turn, page {page}/{pages} ({reply.Length} characters; lines starting with \"{StepPrefix.Trim()}\" are steps)"
                + (issue == null ? "" : $"\n检测到的执行问题 / Detected run issue：{issue}")
                + "\n" + body
                + (page < pages ? $"\n…还有 {pages - page} 页，用 page={page + 1} 继续读取 / {pages - page} more page(s); read on with page={page + 1}" : "");
        }
    }

    /// <summary>
    /// 本轮日志：展开读取到的步骤日志开头与 Copilot 界面提示（仅失败时读取）。
    /// Turn log: heads of expanded step logs and Copilot UI notices (read only on failure).
    /// </summary>
    public sealed class TurnLog
    {
        public List<StepLog> Steps = new List<StepLog>();
        public List<string> Notices = new List<string>();
    }

    /// <summary>一个步骤的标题与日志内容。/ One step's header and log content.</summary>
    public sealed class StepLog
    {
        public string Header;
        public string Detail;
    }

    /// <summary>
    /// Copilot 本轮执行异常（不是任务内容问题）：返回中断、未预期的 EOF、返回体过大、达到单轮迭代上限、网络 / 服务错误。
    /// Copilot run problems that are not task-content problems: interrupted responses, unexpected EOF, oversized payloads,
    /// the per-turn iteration limit, network / service errors.
    /// </summary>
    public static class RunIssue
    {
        public const string Eof = "eof", TooLarge = "too_large", IterationLimit = "iteration_limit", Network = "network";

        // 回复结尾处出现即可采信的特征 / Markers trusted anywhere near the end of the turn
        private static readonly (string Code, Regex Pattern)[] Specific =
        {
            (Eof, new Regex(@"unexpected\s+(EOF|end\s+of\s+(file|stream|input|JSON))|\bEOF\b|意外的\s*EOF|未预期的\s*EOF|意外结束|流意外终止|stream\s+(ended|closed)\s+unexpectedly|premature\s+end", RegexOptions.IgnoreCase)),
            (TooLarge, new Regex(@"(payload|request|response|body|entity|message|input|prompt)\s+(is\s+|was\s+)?too\s+(large|long|big)|\b413\b|context\s+(length|window)|maximum\s+context|token\s+limit|too\s+many\s+tokens|exceed(s|ed)?\s+(the\s+)?(maximum|max|limit)|truncated\s+because\s+it\s+(was|is)\s+too\s+long|返回体过大|响应过大|响应被截断|回复被截断|因为它太长|请求过大|内容过长|超出上下文|上下文长度|超过.{0,6}(token|令牌)|令牌上限", RegexOptions.IgnoreCase)),
            (IterationLimit, new Regex(@"(maximum|max)\s+(number\s+of\s+)?(iterations|tool\s+calls|requests|steps)|iteration\s+limit|tool[-\s]call\s+limit|been\s+working\s+on\s+this\s+(problem|task)\s+for\s+a\s+while|continue\s+to\s+iterate|迭代(上限|次数上限|次数已达)|达到.{0,8}(迭代|工具调用|请求).{0,4}上限|单轮.{0,6}上限|是否继续迭代|要继续吗", RegexOptions.IgnoreCase)),
        };

        // 通用网络 / 中断词：只在回复结尾的短段落中采信 / Generic network / interruption words: trusted only in a short final paragraph
        private static readonly Regex Generic = new Regex(
            @"network\s+error|request\s+failed|timed?\s*out|connection\s+(was\s+)?(closed|reset|lost|aborted)|ECONNRESET|socket\s+hang\s+up|service\s+unavailable|temporarily\s+unavailable|rate\s+limit|too\s+many\s+requests|internal\s+server\s+error|bad\s+gateway|\b50[234]\b|response\s+(was\s+)?(interrupted|cancell?ed|stopped|truncated)|something\s+went\s+wrong|an\s+error\s+occurred|网络错误|网络异常|网络连接|请求失败|请求超时|连接超时|连接已断开|服务不可用|暂时不可用|速率限制|发生错误|出现错误|出了点问题|回复被中断|响应被中断|已被中断|稍后再试|稍后重试",
            RegexOptions.IgnoreCase);

        /// <summary>检查范围：回复结尾的字符数。/ Inspected range: characters at the end of the turn.</summary>
        public const int TailLength = 1500;
        /// <summary>通用词只在不超过该长度的最后一段中采信。/ Generic words are trusted only in a final paragraph up to this length.</summary>
        public const int GenericParagraphMax = 400;

        /// <summary>
        /// 从本轮完整回复的结尾识别执行异常；没有时返回 null。
        /// Detects a run issue from the end of the whole turn; null when none.
        /// </summary>
        public static string Detect(string round)
        {
            string s = round?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            string tail = s.Length > TailLength ? s.Substring(s.Length - TailLength) : s;
            foreach (var (code, pattern) in Specific)
                if (pattern.IsMatch(tail)) return code;
            string last = s.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";
            return last.Length <= GenericParagraphMax && Generic.IsMatch(last) ? Network : null;
        }

        public static string Label(string code)
        {
            switch (code)
            {
                case Eof: return "返回中断（未预期的 EOF）/ Response cut off (unexpected EOF)";
                case TooLarge: return "返回体或上下文过大 / Payload or context too large";
                case IterationLimit: return "达到单轮迭代上限 / Per-turn iteration limit reached";
                case Network: return "网络 / 服务错误或回复被中断 / Network / service error or interrupted reply";
                default: return null;
            }
        }

        /// <summary>
        /// 针对执行异常给主控 AI 的处理建议（中英）。
        /// Handling advice for the main AI per run issue (zh + en).
        /// </summary>
        public static string Advice(string code)
        {
            switch (code)
            {
                case Eof:
                case Network:
                    return "返回被中断：多为偶发，可直接用 retry_task 让 Copilot 在已有进度上继续（note 可写明从哪一步接着做）。/ The response was cut off, usually transient: call retry_task so Copilot continues from its progress (note may say where to resume).";
                case TooLarge:
                    return "返回体或上下文过大：用 retry_task 并在 note 中要求 Copilot 分步完成、每次只处理一部分文件、减少大段输出（不要贴整文件或完整日志），必要时先用 new_copilot_thread 开新线程再重试（重试时设 fresh_context=true）。/ Payload or context too large: call retry_task with a note asking Copilot to work in smaller steps, handle fewer files at a time and avoid large output (no whole files or full logs); open a new thread with new_copilot_thread first if needed (then retry with fresh_context=true).";
                case IterationLimit:
                    return "达到单轮迭代上限：任务可能已完成一部分，用 retry_task 让 Copilot 在已有进度上继续，note 中写明剩余工作或要求缩小每轮范围。/ Iteration limit reached: the task may be partly done; call retry_task so Copilot continues from its progress, with a note naming the remaining work or asking for smaller steps.";
                default: return null;
            }
        }
    }
}
