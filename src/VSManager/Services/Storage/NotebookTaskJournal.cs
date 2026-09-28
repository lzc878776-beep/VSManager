using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 把已结束任务与手动对话按天写入笔记本，每条记录一个详情子页面；阅读视图将索引展示为按项目分组的表格。
    /// Records finished tasks and manual chats in daily notebook pages with one detail subpage per record;
    /// the reading view displays the index as project-grouped tables.
    /// </summary>
    internal sealed class NotebookTaskJournal
    {
        /// <summary>旧版在日期页下单独建立的清单页名称，写入时自动并入日期页。/ Legacy list page under each day; merged into the day page on write.</summary>
        internal const string LegacyIndexName = "已完成任务";
        private const int MaxReplyChars = 200000;
        private static readonly Regex DayPattern = new Regex(@"^\d{4}\.\d{1,2}\.\d{1,2} 任务记录$", RegexOptions.Compiled);
        private readonly NotebookStore _store;

        public NotebookTaskJournal(NotebookStore store) { _store = store ?? throw new ArgumentNullException(nameof(store)); }

        internal static string FolderName(DateTime day) => day.Year + "." + day.Month + "." + day.Day + " 任务记录";

        private static string DayHeader(string title) => "# " + title + "\n\n点击条目查看任务详情。/ Click an entry to open its details.\n\n";

        /// <summary>写入一条已结束任务，返回详情页面编号。/ Writes one finished task and returns the detail page id.</summary>
        public string Record(QueuedTask task, string fullReply = null)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            DateTime finished = task.Finished ?? DateTime.Now;
            string day = Day(finished);
            string summary = TaskTitle.Normalize(task.Title) ?? Summary(task.Text);
            string detail = CreateDetail(day, "任务 " + task.Id + " - " + TitleSafe(summary), TaskDetail(task, summary, fullReply, day));
            Append(day, Line(finished, summary, detail, task.VsName, false));
            return detail;
        }

        /// <summary>写入一条检测到的已完成手动对话，返回详情页面编号。/ Writes one detected, completed manual chat and returns the detail page id.</summary>
        public string RecordManual(ExternalChat chat)
        {
            if (chat == null) throw new ArgumentNullException(nameof(chat));
            DateTime finished = chat.Finished ?? DateTime.Now;
            string day = Day(finished);
            string summary = Summary(chat.Question);
            string detail = CreateDetail(day, "手动 - " + TitleSafe(summary), ManualDetail(chat, summary, day));
            Append(day, Line(finished, summary, detail, chat.VsName, true));
            return detail;
        }

        private string Day(DateTime finished)
        {
            MergeLegacyIndexes();
            string title = FolderName(finished);
            return _store.FindChild("", title) ?? _store.CreatePage("", title, DayHeader(title));
        }

        private string CreateDetail(string day, string baseName, string text)
        {
            string name = baseName;
            for (int i = 2; _store.FindChild(day, name) != null; i++) name = baseName + " (" + i + ")";
            return _store.CreatePage(day, name, text);
        }

        private static string Line(DateTime finished, string summary, string detail, string vsName, bool manual) =>
            "- **" + finished.ToString("HH:mm", CultureInfo.InvariantCulture) + "** [" + LinkText(summary) + "](" + NotebookStore.LinkPrefix + detail + ")"
            + (string.IsNullOrWhiteSpace(vsName) ? "" : " · " + LinkText(vsName.Trim()))
            + (manual ? " · 手动对话 / Manual chat" : "");

        private void Append(string day, string line)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var document = _store.Read(day);
                    string text = document.Text;
                    if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
                    _store.Save(document, text + line + "\n");
                    return;
                }
                catch (NotebookConflictException) when (attempt < 3) { }
            }
        }

        /// <summary>
        /// 把旧版「已完成任务」清单页并入日期页：清单条目移到日期页正文，详情页的返回链接改指日期页，旧清单页移入废纸篓。
        /// Merges legacy "已完成任务" list pages into their day page: entries move into the day body, detail back links point to
        /// the day page, and the old list page goes to the trash.
        /// </summary>
        private void MergeLegacyIndexes()
        {
            foreach (var day in _store.LoadTree().Where(e => DayPattern.IsMatch(e.Name)))
            {
                var index = day.Children.FirstOrDefault(c => c.Name == LegacyIndexName);
                if (index == null) continue;
                var entries = NotebookStore.NormalizeNewLines(_store.Read(index.Path).Text, "\n").Split('\n')
                    .Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
                var document = _store.Read(day.Path);
                _store.Save(document, DayHeader(day.Name) + string.Concat(entries.Select(l => l + "\n")));
                foreach (var child in day.Children.Where(c => c != index))
                {
                    var page = _store.Read(child.Path);
                    string text = page.Text.Replace("[← 返回已完成任务清单 / Back to completed tasks](" + NotebookStore.LinkPrefix + index.Path + ")", BackLink(day.Path))
                        .Replace(NotebookStore.LinkPrefix + index.Path, NotebookStore.LinkPrefix + day.Path);
                    if (text != page.Text) _store.Save(page, text);
                }
                _store.Trash(index.Path);
            }
        }

        private static string BackLink(string day) => "[← 返回任务记录 / Back to task records](" + NotebookStore.LinkPrefix + day + ")";

        /// <summary>没有 AI 题目时，取首行前 20 字作为题目。/ Without an AI title, uses the first 20 characters of the first line.</summary>
        internal static string Summary(string text)
        {
            string plain = Regex.Replace(text ?? "", @"@\[[^\]\r\n]*\]", " ");
            string first = plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            first = Regex.Replace(first, @"\s+", " ");
            if (first.Length == 0) return "（无内容）/ (empty)";
            return first.Length > TaskTitle.MaxLength ? first.Substring(0, TaskTitle.MaxLength) + "…" : first;
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

        private static string Reply(string reply)
        {
            reply = reply ?? "";
            if (reply.Length > MaxReplyChars) reply = reply.Substring(0, MaxReplyChars) + "\r\n…（已截断 / truncated）";
            return reply.Trim().Length == 0 ? "（无回复内容）/ (no reply)" : reply.Trim();
        }

        private static void Body(StringBuilder sb, string heading, string body)
        {
            body = body ?? "";
            string fence = Fence(body);
            sb.Append("\r\n## ").Append(heading).Append("\r\n\r\n").Append(fence).Append("text\r\n").Append(body.TrimEnd()).Append("\r\n").Append(fence).Append("\r\n");
        }

        private static string TaskDetail(QueuedTask task, string summary, string fullReply, string day)
        {
            string duration = task.Started.HasValue && task.Finished.HasValue ? TextUtil.FormatDuration(task.Finished.Value - task.Started.Value) : "—";
            var sb = new StringBuilder();
            sb.Append("# 任务 #").Append(task.Id).Append(" · ").Append(LinkText(summary)).Append("\r\n\r\n");
            sb.Append(BackLink(day)).Append("\r\n\r\n");
            sb.Append("| 项目 / Field | 内容 / Value |\r\n| --- | --- |\r\n");
            string status = task.Status == QueueStatus.Failed ? "失败 / Failed" : task.Status == QueueStatus.Unverified ? "待验证 / Awaiting verification" : "已完成 / Done";
            sb.Append("| 状态 / Status | ").Append(status).Append(" |\r\n");
            sb.Append("| VS | ").Append(Cell(task.VsName)).Append(" |\r\n");
            sb.Append("| 创建 / Created | ").Append(Time(task.Created)).Append(" |\r\n");
            sb.Append("| 开始 / Started | ").Append(Time(task.Started)).Append(" |\r\n");
            sb.Append("| 完成 / Finished | ").Append(Time(task.Finished)).Append(" |\r\n");
            sb.Append("| 用时 / Duration | ").Append(Cell(duration)).Append(" |\r\n");
            sb.Append("| 尝试次数 / Attempts | ").Append(Math.Max(1, task.Attempts)).Append(" |\r\n");
            if (task.HasAttachments) sb.Append("| 附件 / Attachments | ").Append(Cell(string.Join("、", task.Attachments.Select(a => a?.Name ?? "")))).Append(" |\r\n");
            Body(sb, "任务内容 / Task", task.Text);
            if (task.Status == QueueStatus.Failed) Body(sb, "失败原因 / Failure reason", task.FailureReason ?? task.Error);
            sb.Append("\r\n## Copilot 回复 / Reply\r\n\r\n").Append(Reply(string.IsNullOrWhiteSpace(fullReply) ? task.Result : fullReply)).Append("\r\n");
            return sb.ToString();
        }

        private static string ManualDetail(ExternalChat chat, string summary, string day)
        {
            string duration = chat.Finished.HasValue ? TextUtil.FormatDuration(chat.Finished.Value - chat.Started) : "—";
            var sb = new StringBuilder();
            sb.Append("# 手动对话 · ").Append(LinkText(summary)).Append("\r\n\r\n");
            sb.Append(BackLink(day)).Append("\r\n\r\n");
            sb.Append("| 项目 / Field | 内容 / Value |\r\n| --- | --- |\r\n");
            sb.Append("| 类型 / Type | 检测到的手动对话 / Detected manual chat |\r\n");
            sb.Append("| 状态 / Status | 已完成 / Done |\r\n");
            sb.Append("| VS | ").Append(Cell(chat.VsName)).Append(" |\r\n");
            sb.Append("| 开始 / Started | ").Append(Time(chat.Started)).Append(" |\r\n");
            sb.Append("| 完成 / Finished | ").Append(Time(chat.Finished)).Append(" |\r\n");
            sb.Append("| 用时 / Duration | ").Append(Cell(duration)).Append(" |\r\n");
            Body(sb, "提问 / Question", chat.Question);
            sb.Append("\r\n## Copilot 回复 / Reply\r\n\r\n").Append(Reply(chat.Answer)).Append("\r\n");
            return sb.ToString();
        }
    }
}