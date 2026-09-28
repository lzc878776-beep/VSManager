using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 待处理 / 失败说明：从 Copilot 回复中提取「需要用户处理什么」或「为什么失败」，供任务清单与 AI 助手展示。
    /// Hold notes: extracts "what the user must handle" or "why it failed" from a Copilot reply, for the task list and the AI assistant.
    /// </summary>
    public static class TaskHoldNote
    {
        /// <summary>回执规则要求 Copilot 使用的段落标记。/ Paragraph tags the receipt rules ask Copilot to use.</summary>
        public const string PendingTag = "待处理：", ReasonTag = "失败原因：";

        /// <summary>单条说明的最大长度。/ Maximum length of a note.</summary>
        public const int MaxLength = 800;

        private static readonly string[] PendingTags = { "待处理", "需要用户验证", "需要验证", "待验证", "待确认", "Pending", "Needs user", "To verify" };
        private static readonly string[] ReasonTags = { "失败原因", "原因", "Failure reason", "Reason" };

        // 允许标记前带 Markdown 列表、标题或加粗符号 / Allows Markdown list, heading or bold markers before the tag
        private static readonly Regex Decoration = new Regex(@"^[\s>#*_\-+`]*(\d+[.)、]\s*)?", RegexOptions.Compiled);

        /// <summary>提取待处理内容；找不到标记时取回复最后一段。/ Extracts the pending items; falls back to the reply's last paragraph.</summary>
        public static string Pending(string reply) =>
            Extract(reply, PendingTags) ?? "改动已完成，待用户验证 / Changes done; awaiting user verification";

        /// <summary>提取失败原因；找不到标记时取回复最后一段。/ Extracts the failure reason; falls back to the reply's last paragraph.</summary>
        public static string Reason(string reply) => Extract(reply, ReasonTags);

        /// <summary>
        /// 失败说明：Copilot 回报失败时用回复中的原因，其余按失败类别加错误信息。
        /// Failure note: the reply's reason when Copilot reported the failure, otherwise the failure category plus the error.
        /// </summary>
        public static string ForFailure(string kind, string error, string reply)
        {
            string reason = kind == FailureKind.Reported ? Reason(reply) : null;
            if (reason != null) return reason;
            string label = kind == null ? null : FailureKind.Label(kind);
            string text = Clip((error ?? "").Trim(), MaxLength);
            return label == null ? (text.Length == 0 ? null : text) : "【" + label + "】" + text;
        }

        /// <summary>详情区标题；不是待确认或失败的任务时为 null。/ Detail title; null unless the task awaits confirmation or failed.</summary>
        public static string DetailTitle(QueuedTask t)
        {
            if (t == null) return null;
            string released = t.Released ? "（已放行 / released）" : "";
            if (t.Status == QueueStatus.Failed) return $"#{t.Id} 失败原因 / Failure reason{released}";
            if (t.Status == QueueStatus.Done && t.NeedsUser) return $"#{t.Id} 待处理 / Pending{released}";
            return null;
        }

        /// <summary>详情区正文：待处理内容或失败原因，附处理方式。/ Detail body: pending items or failure reason, plus how to proceed.</summary>
        public static string DetailText(QueuedTask t)
        {
            if (DetailTitle(t) == null) return null;
            if (t.Status == QueueStatus.Failed)
            {
                string reason = !string.IsNullOrEmpty(t.FailureReason) ? t.FailureReason : ForFailure(t.FailureKind, t.Error, t.Result) ?? "（未记录原因 / no reason recorded）";
                return reason
                    + "\n\n类别 / Kind：" + FailureKind.Label(t.FailureKind)
                    + "\n处理 / Next：右键「补充信息后重试…」「重新排队」或「放行后续任务」/ right-click for \"Retry with info…\", \"Requeue\" or \"Release successors\"";
            }
            string pending = !string.IsNullOrEmpty(t.PendingNote) ? t.PendingNote : Pending(t.Result);
            return pending
                + "\n\n处理 / Next：验证通过后右键「放行后续任务」；有问题时右键「补充信息后重试…」/ once verified, right-click \"Release successors\"; if not, \"Retry with info…\"";
        }

        private static string Extract(string reply, string[] tags)
        {
            if (string.IsNullOrWhiteSpace(reply)) return null;
            var lines = reply.Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = Decoration.Replace(lines[i], "");
                string rest = StripTag(line, tags);
                if (rest == null) continue;
                // 标记行之后到空行为止的内容一并收录（如列表）/ Include following lines up to a blank line (e.g. a list)
                var parts = new System.Collections.Generic.List<string>();
                if (rest.Length > 0) parts.Add(rest);
                for (int j = i + 1; j < lines.Length && lines[j].Trim().Length > 0; j++) parts.Add(lines[j].TrimEnd());
                if (parts.Count == 0)
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        if (lines[j].Trim().Length == 0) { if (parts.Count > 0) break; continue; }
                        parts.Add(lines[j].TrimEnd());
                    }
                string note = string.Join("\n", parts).Trim();
                if (note.Length > 0) return Clip(note, MaxLength);
            }
            string last = reply.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).LastOrDefault(p => p.Length > 0);
            return last == null ? null : Clip(last, MaxLength);
        }

        // 保留换行（列表需要），只截断长度 / Keeps line breaks (lists need them); only limits the length
        private static string Clip(string s, int max) =>
            s == null || s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "…";

        private static string StripTag(string line, string[] tags)
        {
            foreach (var tag in tags)
            {
                if (!line.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = line.Substring(tag.Length).TrimStart('*', '_', ' ');
                if (rest.Length == 0 || !(rest[0] == '：' || rest[0] == ':')) continue;
                return rest.Substring(1).Trim().Trim('*', '_').Trim();
            }
            return null;
        }
    }
}