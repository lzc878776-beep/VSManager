using System;
using System.Globalization;
using System.Text;

namespace VSManager
{
    /// <summary>粘贴到 Copilot 输入框后的校验结论。/ Verdict after pasting into the Copilot input box.</summary>
    public enum PasteCheck
    {
        /// <summary>规范化后与消息完全一致。/ Identical to the message after normalization.</summary>
        Confirmed,
        /// <summary>首尾一致、长度仅有极小差异（视为已确认，但记录下来）。/ Head and tail match with a tiny length difference (treated as confirmed, but logged).</summary>
        NearMatch,
        /// <summary>内容是消息的前缀：仍在粘贴中。/ The content is a prefix of the message: still pasting.</summary>
        Pending,
        /// <summary>输入框为空或与粘贴前相同：确实没写进去。/ Empty or unchanged since before the paste: nothing was written.</summary>
        NotWritten,
        /// <summary>内容已变化但与消息不一致（例如混入旧草稿）。/ Changed but different from the message (e.g. mixed with an old draft).</summary>
        Mismatch,
        /// <summary>无法通过 UI Automation 读取输入框文本。/ The input text cannot be read through UI Automation.</summary>
        Unreadable
    }

    /// <summary>
    /// 粘贴确认的纯逻辑：文本规范化、结果判定与轮询退避。不依赖 UI Automation，便于单元测试。
    /// Pure logic of paste confirmation: text normalization, verdicts and polling back-off. No UI Automation, so it is unit-testable.
    /// </summary>
    public static class PasteVerifier
    {
        /// <summary>默认确认超时（秒）。/ Default confirmation timeout in seconds.</summary>
        public const int DefaultTimeoutSeconds = 10;
        /// <summary>首尾比对取的字符数。/ Number of characters compared at the head and the tail.</summary>
        public const int EdgeLength = 32;

        /// <summary>
        /// 规范化用于比对的文本：NFKC（全角 → 半角、兼容字符统一），去掉所有空白（含换行、全角空格、不换行空格）与零宽 / 控制字符。
        /// Normalizes text for comparison: NFKC (full-width → half-width, compatibility characters unified), then drops all
        /// whitespace (line breaks, ideographic and no-break spaces) and zero-width / control characters.
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string n;
            // 含孤立代理项时无法规范化，按原文比对 / Lone surrogates cannot be normalized; compare the raw text
            try { n = s.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { n = s; }
            var sb = new StringBuilder(n.Length);
            foreach (char c in n)
            {
                if (char.IsWhiteSpace(c)) continue;
                var cat = char.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.Format || cat == UnicodeCategory.Control) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <param name="want">要发送的消息。/ Message to send.</param>
        /// <param name="before">粘贴前输入框的文本（读取失败为 null）。/ Input text before the paste (null if unreadable).</param>
        /// <param name="current">当前读取到的文本（读取失败为 null）。/ Text read now (null if unreadable).</param>
        public static PasteCheck Classify(string want, string before, string current)
        {
            if (current == null) return PasteCheck.Unreadable;
            string w = Normalize(want), c = Normalize(current);
            if (c == w) return PasteCheck.Confirmed;
            if (c.Length == 0 || (before != null && c == Normalize(before))) return PasteCheck.NotWritten;
            if (w.StartsWith(c, StringComparison.Ordinal)) return PasteCheck.Pending;
            if (IsNear(w, c)) return PasteCheck.NearMatch;
            return PasteCheck.Mismatch;
        }

        /// <summary>是否可以视为已写入正确内容。/ Whether the verdict counts as the right content being written.</summary>
        public static bool IsConfirmed(PasteCheck c) => c == PasteCheck.Confirmed || c == PasteCheck.NearMatch;

        /// <summary>首尾各 <see cref="EdgeLength"/> 字一致，且长度差不超过 max(2, 1%)。/ Same head and tail, length differs by at most max(2, 1%).</summary>
        private static bool IsNear(string w, string c)
        {
            int edge = Math.Min(EdgeLength, w.Length / 3);
            if (edge < 8 || c.Length < edge) return false;
            if (Math.Abs(w.Length - c.Length) > Math.Max(2, w.Length / 100)) return false;
            return string.CompareOrdinal(w, 0, c, 0, edge) == 0 &&
                   string.CompareOrdinal(w, w.Length - edge, c, c.Length - edge, edge) == 0;
        }

        /// <summary>轮询退避：50、100、200、400 毫秒，之后每 500 毫秒。/ Polling back-off: 50, 100, 200, 400 ms, then every 500 ms.</summary>
        public static int NextDelayMs(int step) => Math.Min(500, 50 << Math.Min(Math.Max(step, 0), 4));

        /// <summary>日志用的文本片段：开头 + 结尾 + 长度，换行替换为 ⏎。/ Snippet for logs: head + tail + length, line breaks shown as ⏎.</summary>
        public static string Snippet(string s, int head = 30, int tail = 20)
        {
            if (s == null) return "(无法读取 / unreadable)";
            string one = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "⏎");
            if (one.Length > head + tail + 1) one = one.Substring(0, head) + "…" + one.Substring(one.Length - tail);
            return "「" + one + "」(" + s.Length + " 字 / chars)";
        }
    }
}
