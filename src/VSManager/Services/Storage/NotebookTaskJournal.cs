using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 把已完成任务写入笔记本：按天建立「yyyy.M.d 任务记录」页面，其下维护一页已完成任务清单，每个任务一个详情子页面。
    /// Records completed tasks in the notebook: one "yyyy.M.d 任务记录" page per day with a completed-task list subpage and one detail subpage per task.
    /// </summary>
    internal sealed class NotebookTaskJournal
    {
        internal const string IndexName = "已完成任务";
        private const int MaxReplyChars = 200000;
        private readonly NotebookStore _store;

        public NotebookTaskJournal(NotebookStore store) { _store = store ?? throw new ArgumentNullException(nameof(store)); }

        internal static string FolderName(DateTime day) => day.Year + "." + day.Month + "." + day.Day + " 任务记录";

        /// <summary>写入一条已完成任务，返回详情页面编号。/ Writes one completed task and returns the detail page id.</summary>
        public string Record(QueuedTask task, string fullReply = null)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            DateTime finished = task.Finished ?? DateTime.Now;
            string title = FolderName(finished);
            string day = _store.FindChild("", title) ?? _store.CreatePage("", title,
                "# " + title + "\n\n当天完成的任务记录在子页面中。/ Tasks completed on this day are recorded in the subpages.\n\n[已完成任务清单 / Completed tasks](page:{index})\n");
            string index = _store.FindChild(day, IndexName) ?? _store.CreatePage(day, IndexName,
                "# " + title + " · 已完成任务 / Completed tasks\n\n点击条目查看任务详情。/ Click an entry to open its details.\n\n");
            FillDayLink(day, index);

            string summary = Summary(task.Text);
            string baseName = "任务 " + task.Id + " - " + TitleSafe(summary);
            string name = baseName;
            for (int i = 2; _store.FindChild(day, name) != null; i++) name = baseName + " (" + i + ")";
            string detail = _store.CreatePage(day, name, Detail(task, summary, fullReply, index));

            string line = "- **" + finished.ToString("HH:mm", CultureInfo.InvariantCulture) + "** [" + LinkText(summary) + "](" + NotebookStore.LinkPrefix + detail + ")"
                + (string.IsNullOrWhiteSpace(task.VsName) ? "" : " · " + LinkText(task.VsName.Trim()));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var document = _store.Read(index);
                    string text = document.Text;
                    if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
                    _store.Save(document, text + line + "\n");
                    break;
                }
                catch (NotebookConflictException) when (attempt < 3) { }
            }
            return detail;
        }

        private void FillDayLink(string day, string index)
        {
            var document = _store.Read(day);
            if (document.Text.Contains("page:{index}")) _store.Save(document, document.Text.Replace("page:{index}", NotebookStore.LinkPrefix + index));
        }
        internal static string Summary(string text)
        {
            string plain = Regex.Replace(text ?? "", @"@\[[^\]\r\n]*\]", " ");
            string first = plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            first = Regex.Replace(first, @"\s+", " ");
            if (first.Length == 0) return "（无内容）/ (empty)";
            return first.Length > 40 ? first.Substring(0, 40) + "…" : first;
        }

        private static string TitleSafe(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text) sb.Append(char.IsControl(c) ? ' ' : c);
            string result = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', '…').Trim();
            if (result.Length > 30) result = result.Substring(0, 30).Trim();
            return result.Length == 0 ? "任务" : result;
        }

        private static string LinkText(string text) => Regex.Replace(text, @"([\\`*_\[\]<>|])", @"\$1");
        private static string Cell(string text) => string.IsNullOrEmpty(text) ? "—" : LinkText(Regex.Replace(text, @"\s+", " ").Trim());
        private static string Time(DateTime? time) => time?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";

        private static string Fence(string text)
        {
            int longest = 0, run = 0;
            foreach (char c in text ?? "") { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
            return new string('`', Math.Max(3, longest + 1));
        }

        private static string Detail(QueuedTask task, string summary, string fullReply, string index)
        {
            string reply = string.IsNullOrWhiteSpace(fullReply) ? task.Result ?? "" : fullReply;
            if (reply.Length > MaxReplyChars) reply = reply.Substring(0, MaxReplyChars) + "\r\n…（已截断 / truncated）";
            string duration = task.Started.HasValue && task.Finished.HasValue ? TextUtil.FormatDuration(task.Finished.Value - task.Started.Value) : "—";
            string body = task.Text ?? "";
            string fence = Fence(body);
            var sb = new StringBuilder();
            sb.Append("# 任务 #").Append(task.Id).Append(" · ").Append(LinkText(summary)).Append("\r\n\r\n");
            sb.Append("[← 返回已完成任务清单 / Back to completed tasks](").Append(NotebookStore.LinkPrefix).Append(index).Append(")\r\n\r\n");
            sb.Append("| 项目 / Field | 内容 / Value |\r\n| --- | --- |\r\n");
            sb.Append(task.Status == QueueStatus.Unverified ? "| 状态 / Status | 未验证 / Unverified |\r\n" : "| 状态 / Status | 已完成 / Done |\r\n");
            sb.Append("| VS | ").Append(Cell(task.VsName)).Append(" |\r\n");
            sb.Append("| 创建 / Created | ").Append(Time(task.Created)).Append(" |\r\n");
            sb.Append("| 开始 / Started | ").Append(Time(task.Started)).Append(" |\r\n");
            sb.Append("| 完成 / Finished | ").Append(Time(task.Finished)).Append(" |\r\n");
            sb.Append("| 用时 / Duration | ").Append(Cell(duration)).Append(" |\r\n");
            sb.Append("| 尝试次数 / Attempts | ").Append(Math.Max(1, task.Attempts)).Append(" |\r\n");
            if (task.HasAttachments) sb.Append("| 附件 / Attachments | ").Append(Cell(string.Join("、", task.Attachments.Select(a => a?.Name ?? "")))).Append(" |\r\n");
            sb.Append("\r\n## 任务内容 / Task\r\n\r\n").Append(fence).Append("text\r\n").Append(body.TrimEnd()).Append("\r\n").Append(fence).Append("\r\n");
            sb.Append("\r\n## Copilot 回复 / Reply\r\n\r\n").Append(reply.Trim().Length == 0 ? "（无回复内容）/ (no reply)" : reply.Trim()).Append("\r\n");
            return sb.ToString();
        }
    }
}
