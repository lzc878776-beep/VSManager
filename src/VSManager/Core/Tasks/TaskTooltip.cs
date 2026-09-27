using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 任务清单悬停提示的内容：未执行时以任务内容为主；已完成时以完成情况为主；未验证 / 待验证时列出已完成的内容与未验证的项目；失败时显示失败原因。
    /// Content of the task list hover tip: the task text before it runs; the outcome once done; for unverified / awaiting-verification
    /// tasks what was done and what is still unverified; the failure reason when it failed.
    /// </summary>
    public static class TaskTooltip
    {
        public const int MaxSection = 600;

        public static string Build(QueuedTask t, string status, string extra = null)
        {
            if (t == null) return "";
            var sb = new StringBuilder();
            string title = string.IsNullOrWhiteSpace(t.Title) ? FirstLine(t.Text) : t.Title.Trim();
            sb.Append('#').Append(t.Id).Append(' ').Append(Clip(title, 60)).Append('\n');
            if (!string.IsNullOrWhiteSpace(status)) sb.Append(status.Trim()).Append('\n');

            if (TaskTestChecklist.Pending(t))
            {
                string done = Clip(TaskTestChecklist.StripChecklist(t.Result), MaxSection);
                Section(sb, "✅ 已完成的内容 / Done", done.Length > 0 ? done : "回复中未单独说明 / Not described separately in the reply");
                var items = t.TestItems != null && t.TestItems.Length > 0 ? t.TestItems : TaskTestChecklist.Parse(t.Result);
                int left = items.Count(i => i != null && !i.Checked);
                Section(sb, $"🧪 未验证的内容（剩 {left} 项）/ Not yet verified ({left} left)",
                    string.Join("\n", items.Where(i => i != null).Select(i => (i.Checked ? "☑ " : "☐ ") + i.Text)));
            }
            else if (t.Status == QueueStatus.Done)
            {
                string outcome = Clip(TaskTestChecklist.StripChecklist(t.Result), MaxSection);
                Section(sb, "✅ 完成情况 / Outcome", outcome.Length > 0 ? outcome : "已完成 / Done");
            }
            else if (t.Status == QueueStatus.Failed)
            {
                Section(sb, "❌ 失败原因 / Failure", Clip(string.IsNullOrWhiteSpace(t.Error) ? "未知 / Unknown" : t.Error, MaxSection));
                string reply = Clip(TaskTestChecklist.StripChecklist(t.Result), MaxSection / 2);
                if (reply.Length > 0) Section(sb, "💬 已完成的部分 / What was done", reply);
                Section(sb, "📝 任务内容 / Task", Clip(t.Text, MaxSection / 2));
            }
            else
            {
                Section(sb, "📝 任务内容 / Task", Clip(t.Text, MaxSection));
                if (!string.IsNullOrWhiteSpace(t.Supplement)) Section(sb, "➕ 补充信息 / Supplement", Clip(t.Supplement, MaxSection / 2));
            }
            if (!string.IsNullOrWhiteSpace(extra)) sb.Append('\n').Append(extra.Trim()).Append('\n');
            return sb.ToString().TrimEnd();
        }

        private static void Section(StringBuilder sb, string heading, string body)
        {
            sb.Append('\n').Append(heading).Append('\n').Append(body.Trim()).Append('\n');
        }

        private static string FirstLine(string text) =>
            (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        /// <summary>压缩空行并截断。/ Collapses blank lines and truncates.</summary>
        internal static string Clip(string text, int max)
        {
            string s = Regex.Replace((text ?? "").Replace("\r\n", "\n").Trim(), @"\n\s*\n+", "\n");
            return s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";
        }
    }
}
