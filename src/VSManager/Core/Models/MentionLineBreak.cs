using System;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 输入框换行规则：已确认的 @[…] 提及令牌整体不可拆行；其余在空白后及中日韩字符之间可换行（避开行首标点）。
    /// Line-break rules for the input box: a confirmed @[…] mention token never splits across lines; elsewhere breaks are allowed after
    /// whitespace and between CJK characters (avoiding leading punctuation).
    /// </summary>
    public static class MentionLineBreak
    {
        public const int WbLeft = 0, WbRight = 1, WbIsDelimiter = 2;
        private const int MaxTokenLookBehind = 200;
        private static readonly Regex Token = new Regex(@"\G@\[#\d+ [^\]\|\r\n]*\|[0-9a-f]{6,12}\]", RegexOptions.Compiled);
        private const string NoBreakBeforeChars = "，。、？！：；）」』】》〉,.;:!?)]}%…～";

        /// <summary>位置 p 是否落在某个令牌内部（起点与终点之间）。/ Whether position p lies strictly inside a token.</summary>
        public static bool InsideToken(string s, int p)
        {
            if (string.IsNullOrEmpty(s) || p <= 0 || p >= s.Length) return false;
            for (int i = p - 1, limit = Math.Max(0, p - MaxTokenLookBehind); i >= limit; i--)
            {
                char c = s[i];
                if (c == '\r' || c == '\n') return false;
                if (c == '@' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    var m = Token.Match(s, i);
                    if (m.Success) return p < i + m.Length;
                }
            }
            return false;
        }

        private static bool IsCjk(char c) => c >= '\u2E80' && !char.IsSurrogate(c);

        /// <summary>能否在位置 p 之前换行。/ Whether a line may break before position p.</summary>
        public static bool CanBreakBefore(string s, int p)
        {
            if (string.IsNullOrEmpty(s) || p <= 0 || p >= s.Length) return false;
            char prev = s[p - 1], cur = s[p];
            if (char.IsWhiteSpace(cur) || char.IsLowSurrogate(cur)) return false;
            if (InsideToken(s, p)) return false;
            if (char.IsWhiteSpace(prev)) return true;
            if (NoBreakBeforeChars.IndexOf(cur) >= 0) return false;
            return IsCjk(prev) || IsCjk(cur) || (cur == '@' && p + 1 < s.Length && s[p + 1] == '[');
        }

        /// <summary>
        /// EditWordBreakProc 的纯实现：WB_LEFT 返回 ich 左侧最近的断点，WB_RIGHT 返回右侧（大于 ich）的下一个断点，WB_ISDELIMITER 返回 ich 处是否为分隔符。
        /// Pure EditWordBreakProc: WB_LEFT returns the nearest break left of ich, WB_RIGHT the next break greater than ich, WB_ISDELIMITER whether ich is a delimiter.
        /// </summary>
        public static int Evaluate(string s, int ich, int code)
        {
            s = s ?? "";
            ich = Math.Max(0, Math.Min(ich, s.Length));
            switch (code)
            {
                case WbLeft:
                    for (int p = ich - 1; p > 0; p--) if (CanBreakBefore(s, p)) return p;
                    return 0;
                case WbRight:
                    for (int p = ich + 1; p < s.Length; p++) if (CanBreakBefore(s, p)) return p;
                    return s.Length;
                case WbIsDelimiter:
                    return ich < s.Length && char.IsWhiteSpace(s[ich]) && !InsideToken(s, ich) ? 1 : 0;
                default:
                    return 0;
            }
        }
    }
}
