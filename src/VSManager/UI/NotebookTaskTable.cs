using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 将旧版和新写入的每日任务索引投影为表格，不重写用户笔记或详情。
    /// Projects existing and newly written daily task indexes into tables without rewriting notes or details.
    /// </summary>
    internal static class NotebookTaskTable
    {
        private static readonly Regex EntryLine = new Regex(@"^- \*\*(?<time>(?:[01]\d|2[0-3]):[0-5]\d)\*\* \[(?<title>(?:\\.|[^\]\\])*)\]\(page:(?<id>[0-9a-f]{32})\)(?: · (?<project>.*?))?(?: · 手动对话 / Manual chat)?\r?$", RegexOptions.Multiline);
        private static readonly Regex TaskId = new Regex(@"^# 任务 #(?<id>\d+)\b");

        internal static bool IsJournal(string title) => DateTime.TryParseExact(title,
            "yyyy.M.d '任务记录'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        private sealed class Entry
        {
            public string Link, Title, Project, Status, Duration, Number;
            public DateTime Finished;
        }

        internal static string Render(string pageId, string title, string text, IReadOnlyList<NotebookDocument> details, Func<string, string> imageData = null)
        {
            var byId = details.ToDictionary(d => d.Path, StringComparer.Ordinal);
            DateTime day = DateTime.ParseExact(title, "yyyy.M.d '任务记录'", CultureInfo.InvariantCulture);
            var entries = new List<Entry>();
            foreach (Match match in EntryLine.Matches(text ?? ""))
            {
                byId.TryGetValue(match.Groups["id"].Value, out var detail);
                string header = detail?.Text ?? "";
                string finished = Field(header, "完成 / Finished");
                if (!DateTime.TryParseExact(finished, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    time = day.Add(TimeSpan.ParseExact(match.Groups["time"].Value, @"hh\:mm", CultureInfo.InvariantCulture));
                string project = Field(header, "VS") ?? Unescape(match.Groups["project"].Value);
                entries.Add(new Entry
                {
                    Link = NotebookStore.LinkPrefix + match.Groups["id"].Value,
                    Title = Unescape(match.Groups["title"].Value),
                    Project = string.IsNullOrWhiteSpace(project) ? "未命名项目 / Unnamed project" : project,
                    Status = Status(Field(header, "状态 / Status")),
                    Duration = Field(header, "用时 / Duration") ?? "—",
                    Number = TaskId.Match(header).Groups["id"].Value,
                    Finished = time
                });
            }

            var sb = new StringBuilder("<div class=\"task-journal\" data-journal=\"").Append(E(pageId)).Append("\">");
            sb.Append("<div class=\"tj-filters\" role=\"group\" aria-label=\"状态筛选 / Filter by status\">");
            foreach (var filter in new[] { ("all", "全部 / All"), ("done", "已完成 / Done"), ("unverified", "待验证 / Awaiting verification"), ("failed", "失败 / Failed") })
                sb.Append("<button type=\"button\" data-status-filter=\"").Append(filter.Item1).Append("\" aria-pressed=\"")
                    .Append(filter.Item1 == "all" ? "true" : "false").Append("\">").Append(E(filter.Item2)).Append("</button>");
            sb.Append("</div><p class=\"tj-summary\">共 ").Append(entries.Count).Append(" 条 · 完成 ").Append(entries.Count(e => e.Status == "done"))
                .Append(" · 待验证 ").Append(entries.Count(e => e.Status == "unverified")).Append(" · 失败 ").Append(entries.Count(e => e.Status == "failed"))
                .Append(" <span>/ Total · Done · Awaiting verification · Failed</span></p>");
            sb.Append("<p class=\"tj-empty\" role=\"status\" hidden>没有符合条件的记录 / No matching records</p>");
            foreach (var date in entries.GroupBy(e => e.Finished.Date).OrderByDescending(g => g.Key))
            {
                sb.Append("<section class=\"tj-day\"><h1>").Append(E(NotebookTaskJournal.FolderName(date.Key))).Append("</h1>");
                foreach (var group in date.GroupBy(e => e.Project, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    sb.Append("<section class=\"tj-project\"><h2>").Append(E(group.Key)).Append("（<span class=\"tj-count\">").Append(group.Count()).Append("</span>）</h2>")
                        .Append("<div class=\"tj-scroll\"><table class=\"tj-table\"><colgroup><col class=\"tj-time\"><col><col class=\"tj-status\"><col class=\"tj-duration\"></colgroup>")
                        .Append("<thead><tr><th scope=\"col\">时间 / Time</th><th scope=\"col\">任务 / Task</th><th scope=\"col\">状态 / Status</th><th scope=\"col\">用时 / Duration</th></tr></thead><tbody>");
                    foreach (var entry in group.OrderByDescending(e => e.Finished))
                    {
                        string label = Label(entry.Status);
                        string caption = (entry.Number.Length > 0 ? "#" + entry.Number + " · " : "") + entry.Title;
                        sb.Append("<tr data-task-status=\"").Append(entry.Status).Append("\" class=\"nc-").Append(entry.Status).Append("\">")
                            .Append("<td>").Append(entry.Finished.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("</td><td class=\"tj-task\"><a class=\"note-link\" href=\"#\" data-note=\"")
                            .Append(E(entry.Link)).Append("\" title=\"").Append(E(caption)).Append("\">").Append(E(caption)).Append("</a></td>")
                            .Append("<td><span class=\"nc-pill\" title=\"").Append(E(label)).Append("\"><i></i>").Append(E(label)).Append("</span></td><td>")
                            .Append(E(entry.Duration)).Append("</td></tr>");
                    }
                    sb.Append("</tbody></table></div></section>");
                }
                sb.Append("</section>");
            }
            if (entries.Count == 0) sb.Append("<h1>").Append(E(title)).Append("</h1>");
            sb.Append("</div>");
            // 未识别的正文保留原样渲染，避免隐藏用户追加的笔记或旧链接。
            // Keep unrecognized content visible, including user annotations and legacy links.
            string remainder = EntryLine.Replace(text ?? "", "");
            remainder = Regex.Replace(remainder, @"^# " + Regex.Escape(title) + @"\r?$", "", RegexOptions.Multiline)
                .Replace("点击条目查看任务详情。/ Click an entry to open its details.", "");
            return sb.Append(NotebookMarkdown.Render(remainder, imageData)).ToString();
        }

        private static string Field(string text, string field)
        {
            var match = Regex.Match(text, @"^\| " + Regex.Escape(field) + @" \| (?<value>.*?) \|\r?$", RegexOptions.Multiline);
            return match.Success ? Unescape(match.Groups["value"].Value) : null;
        }

        private static string Status(string value)
        {
            if (value == null) return "info";
            if (value.Contains("待验证") || value.Contains("未验证") || value.Contains("待用户验证") || value.Contains("Unverified") || value.Contains("Awaiting")) return "unverified";
            if (value.Contains("失败") || value.Contains("Failed")) return "failed";
            if (value.Contains("已完成") || value.Contains("Done")) return "done";
            return "info";
        }

        internal static string Label(string status) => status == "done" ? "已完成 / Done" : status == "unverified" ? "待验证 / Awaiting verification"
            : status == "failed" ? "失败 / Failed" : "未知 / Unknown";
        private static string Unescape(string text) => Regex.Replace(text ?? "", @"\\([\\`*_\[\]<>|])", "$1");
        private static string E(string text) => WebUtility.HtmlEncode(text ?? "");
    }
}
