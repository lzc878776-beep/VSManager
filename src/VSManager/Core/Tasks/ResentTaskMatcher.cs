using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 界面隐藏标记：失败任务 <see cref="Id"/> 已被重新排队的任务 <see cref="ReplacedBy"/> 取代，只在任务清单界面隐藏（保存在 settings.json）。
    /// UI hide mark: failed task <see cref="Id"/> was superseded by the requeued task <see cref="ReplacedBy"/>; it is only
    /// hidden in the task list UI (stored in settings.json).
    /// </summary>
    [DataContract]
    public sealed class HiddenTaskMark
    {
        [DataMember] public int Id;
        [DataMember] public int ReplacedBy;
        [DataMember] public DateTime At;
    }

    /// <summary>一条未被隐藏的同内容失败任务及保留原因。/ A same-content failed task that was kept, with the reason.</summary>
    public sealed class KeptResend
    {
        public QueuedTask Task;
        public string Reason;
    }

    /// <summary>重新发布判定结果。/ Result of the resend check.</summary>
    public sealed class ResendMatch
    {
        /// <summary>Old failed entries reliably identified for replacement and removal from the queue.</summary>
        public readonly List<QueuedTask> Hide = new List<QueuedTask>();
        /// <summary>内容相近但无法可靠判定、保留不动的条目。/ Similar entries that cannot be identified reliably and are kept.</summary>
        public readonly List<KeptResend> Kept = new List<KeptResend>();
        /// <summary>新任务正文中引用的原任务编号（如「重发 #26」），没有时为 null。/ Original task id referenced in the new text (e.g. "resend #26"), or null.</summary>
        public int? ReferencedId;
    }

    /// <summary>
    /// 判定「失败任务被重新发布」：按规范化正文的指纹（SHA-256）比对，并要求目标 VS 相同；只找状态为失败、编号更早的条目。
    /// Detects "a failed task was published again": compares fingerprints (SHA-256) of the normalized text and requires the
    /// same target VS; only failed entries with a smaller id are considered.
    /// </summary>
    public static class ResentTaskMatcher
    {
        /// <summary>
        /// 规范化后短于该长度的正文（如「继续」）过于常见，不能仅凭内容判定为同一任务（显式引用原编号时除外）。
        /// Normalized texts shorter than this (such as "continue") are too common to identify a task by content alone
        /// (unless the original id is referenced explicitly).
        /// </summary>
        public const int MinReliableLength = 12;

        private static readonly Regex Spaces = new Regex(@"[ \t\u00A0\u3000]+", RegexOptions.Compiled);

        /// <summary>
        /// 重发标记（开头或结尾），如「重发 #26：」「(resend @26)」「【重试#26】」。
        /// Resend marker at the start or the end, e.g. "重发 #26:", "(resend @26)", "【重试#26】".
        /// </summary>
        private static readonly Regex Marker = new Regex(
            @"^\s*[\(（【\[]?\s*(?:重发|重新发布|重新发送|重试|resend|retry)\s*(?:任务\s*|task\s*)?[#@]\s*(\d+)\s*[\)）】\]]?\s*[:：,，]?\s*" +
            @"|\s*[\(（【\[]\s*(?:重发|重新发布|重新发送|重试|resend|retry)\s*(?:任务\s*|task\s*)?[#@]\s*(\d+)\s*[\)）】\]]\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 规范化：统一换行，去掉每行首尾空白与空行，行内连续空白合并为一个空格，去掉零宽 / 控制字符；大小写与标点保持不变。
        /// Normalization: unified line breaks, per-line trimming, blank lines dropped, runs of spaces collapsed to one, zero-width
        /// / control characters removed; case and punctuation are kept.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t') { sb.Append(c); continue; }
                var cat = char.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.Format || cat == System.Globalization.UnicodeCategory.Control) continue;
                sb.Append(c);
            }
            var lines = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(l => Spaces.Replace(l, " ").Trim()).Where(l => l.Length > 0);
            return string.Join("\n", lines);
        }

        /// <summary>规范化正文的指纹（SHA-256 前 16 字节的十六进制）；空正文返回空串。/ Fingerprint of the normalized text (hex of the first 16 bytes of SHA-256); "" for empty text.</summary>
        public static string Fingerprint(string text) => FingerprintOfNormalized(Normalize(text));

        private static string FingerprintOfNormalized(string n)
        {
            if (n.Length == 0) return "";
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(n));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>去掉重发标记，返回被引用的原任务编号。/ Strips a resend marker and returns the referenced original id.</summary>
        public static string StripMarker(string text, out int? referencedId)
        {
            referencedId = null;
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var m = Marker.Match(text);
            if (!m.Success) return text;
            string digits = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (int.TryParse(digits, out int id) && id > 0) referencedId = id;
            return text.Remove(m.Index, m.Length);
        }

        /// <summary>
        /// 两个任务的目标是否为同一 VS：键相同（忽略大小写）、解决方案路径相同，或都记录了相同的登记别名。
        /// Whether two tasks target the same VS: equal keys (case-insensitive), the same solution path, or the same registry alias.
        /// </summary>
        public static bool SameTarget(QueuedTask a, QueuedTask b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.VsKey) && string.Equals(a.VsKey, b.VsKey, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsPath(a.VsKey) && IsPath(b.VsKey) && SolutionMatcher.SamePath(a.VsKey, b.VsKey)) return true;
            return !string.IsNullOrWhiteSpace(a.Target) && string.Equals(a.Target.Trim(), (b.Target ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPath(string key) => !string.IsNullOrEmpty(key) && !key.StartsWith("title:", StringComparison.Ordinal);

        /// <summary>
        /// Finds failed entries for replacement: matching content and target, or an explicit resend id for the same target.
        /// An explicit id permits corrected instructions and short text, and replaces only that id.
        /// Ambiguous content-only matches and different targets are kept with an explanation.
        /// </summary>
        public static ResendMatch Find(IEnumerable<QueuedTask> items, QueuedTask resent)
        {
            var res = new ResendMatch();
            if (items == null || resent == null) return res;
            string body = StripMarker(resent.Text, out int? refId);
            res.ReferencedId = refId;
            string norm = Normalize(body);
            string fp = FingerprintOfNormalized(norm);
            if (fp.Length == 0) return res;

            foreach (var t in items)
            {
                if (t == null || t == resent || t.Id == resent.Id || t.Id > resent.Id || t.Status != QueueStatus.Failed) continue;
                string oldNorm = Normalize(StripMarker(t.Text, out _));
                bool referenced = refId.HasValue && refId.Value == t.Id;
                if (refId.HasValue ? !referenced : FingerprintOfNormalized(oldNorm) != fp) continue;
                if (!SameTarget(t, resent))
                    res.Kept.Add(new KeptResend { Task = t, Reason = "内容相同但目标 VS 不同 / same content but a different target VS" });
                else if (!referenced && norm.Length < MinReliableLength)
                    res.Kept.Add(new KeptResend { Task = t, Reason = $"正文过短（{norm.Length} 字符 < {MinReliableLength}），无法可靠判定为同一任务 / text too short ({norm.Length} < {MinReliableLength} chars) to identify the task reliably" });
                else
                    res.Hide.Add(t);
            }
            return res;
        }
    }

    /// <summary>界面隐藏标记列表的操作（纯逻辑，便于测试）。/ Operations on the list of UI hide marks (pure logic, testable).</summary>
    public static class TaskHideList
    {
        /// <summary>最多保留的标记条数，超出时丢弃最旧的。/ Maximum number of marks kept; the oldest are dropped beyond it.</summary>
        public const int MaxMarks = 2000;

        /// <summary>该任务当前是否因「已重新排队」被隐藏：只对仍为失败状态的条目生效。/ Whether the task is hidden as "requeued"; only applies while it is still failed.</summary>
        public static bool IsHidden(IList<HiddenTaskMark> marks, QueuedTask t) =>
            marks != null && t != null && t.Status == QueueStatus.Failed && marks.Any(m => m != null && m.Id == t.Id);

        /// <summary>取代该失败任务的新任务编号。/ Id of the task that superseded this failed one.</summary>
        public static int? ReplacedBy(IList<HiddenTaskMark> marks, int id)
        {
            var m = marks?.LastOrDefault(x => x != null && x.Id == id);
            return m == null ? (int?)null : m.ReplacedBy;
        }

        /// <summary>被该任务取代的失败任务编号。/ Ids of the failed tasks superseded by this task.</summary>
        public static List<int> Replaced(IList<HiddenTaskMark> marks, int replacedBy) =>
            marks == null ? new List<int>() : marks.Where(x => x != null && x.ReplacedBy == replacedBy).Select(x => x.Id).Distinct().OrderBy(x => x).ToList();

        /// <summary>添加或更新标记，并裁剪到 <see cref="MaxMarks"/>。/ Adds or updates a mark and trims to <see cref="MaxMarks"/>.</summary>
        public static void Add(IList<HiddenTaskMark> marks, int id, int replacedBy, DateTime at)
        {
            if (marks == null) return;
            for (int i = marks.Count - 1; i >= 0; i--) if (marks[i] == null || marks[i].Id == id) marks.RemoveAt(i);
            marks.Add(new HiddenTaskMark { Id = id, ReplacedBy = replacedBy, At = at });
            while (marks.Count > MaxMarks) marks.RemoveAt(0);
        }

        /// <summary>移除标记（撤销隐藏）；返回是否有变化。/ Removes the mark (undo hide); returns whether anything changed.</summary>
        public static bool Remove(IList<HiddenTaskMark> marks, int id)
        {
            if (marks == null) return false;
            bool changed = false;
            for (int i = marks.Count - 1; i >= 0; i--) if (marks[i] == null || marks[i].Id == id) { marks.RemoveAt(i); changed = true; }
            return changed;
        }
    }
}
