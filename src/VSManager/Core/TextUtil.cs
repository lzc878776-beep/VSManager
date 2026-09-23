using System;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 通用文本工具（界面、AI 工具返回、通知共用）。
    /// Shared text helpers (used by the UI, AI tool results and notifications).
    /// </summary>
    public static class TextUtil
    {
        /// <summary>把连续空白压成一个空格，超过 <paramref name="max"/> 时截断并加「…」。
        /// Collapses whitespace into single spaces and truncates with "…" beyond <paramref name="max"/>.</summary>
        public static string Clip(string s, int max)
        {
            s = Regex.Replace((s ?? "").Trim(), @"\s+", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>时长格式：1h05m / 3m07s / 9s。/ Duration format: 1h05m / 3m07s / 9s.</summary>
        public static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" :
            t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{t.Seconds}s";
    }
}
