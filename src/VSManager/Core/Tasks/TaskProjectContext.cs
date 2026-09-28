using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VSManager
{
    /// <summary>
    /// 任务层项目隔离：为任务题目补全项目名，并生成交给 AI 总控助手的项目摘要（项目名、职责描述、该 VS 最近任务结果），
    /// 避免多个项目的结论在同一对话中混淆。不改变排队与调度规则。
    /// Task-level project isolation: prefixes task titles with the project name and builds the project summary handed to the
    /// AI assistant (project name, responsibility, recent task outcomes of that VS) so conclusions from several projects do not
    /// get mixed up in one conversation. Queueing and dispatch rules are unchanged.
    /// </summary>
    public static class TaskProjectContext
    {
        /// <summary>题目中项目名的最大长度。/ Maximum length of the project name inside a title.</summary>
        public const int MaxProjectLength = 24;

        /// <summary>摘要附带的最近任务条数。/ Number of recent tasks in the summary.</summary>
        public const int RecentCount = 3;

        public const string Separator = " · ";

        /// <summary>
        /// 项目名：优先取解决方案名（任务键为解决方案 / 项目路径或「title:名称」），否则取 VS 显示名去掉实例后缀。
        /// Project name: the solution name when the task key is a solution / project path or "title:name", otherwise the VS
        /// display name without its instance suffix.
        /// </summary>
        public static string ProjectName(string vsKey, string vsName)
        {
            string name = null;
            string key = (vsKey ?? "").Trim();
            if (key.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) name = key.Substring(6);
            else if (key.Length > 0 && !key.StartsWith("inst:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string ext = Path.GetExtension(key);
                    if (!string.IsNullOrEmpty(ext) && ext.Length <= 8) name = Path.GetFileNameWithoutExtension(key);
                }
                catch (ArgumentException) { }
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = vsName ?? "";
                int cut = name.IndexOf(Separator, StringComparison.Ordinal);
                if (cut > 0) name = name.Substring(0, cut);
                int hash = name.LastIndexOf(" #", StringComparison.Ordinal);
                if (hash > 0 && name.Substring(hash + 2).All(char.IsDigit)) name = name.Substring(0, hash);
            }
            name = TaskTitle.Normalize(name, MaxProjectLength);
            return name;
        }

        /// <summary>
        /// 题目补全为「项目名 · 事项」：已包含项目名（不区分大小写）时保持不变；未提供题目时取任务正文首句。
        /// Completes the title as "Project · item": unchanged when it already contains the project name (case-insensitive);
        /// without a title the first sentence of the task text is used.
        /// </summary>
        public static string TitleWithProject(string project, string title, string text = null)
        {
            string item = TaskTitle.Normalize(title) ?? TaskTitle.FromText(text);
            if (string.IsNullOrEmpty(project)) return item;
            if (item == null) return null;
            if (item.IndexOf(project, StringComparison.OrdinalIgnoreCase) >= 0) return item;
            return TaskTitle.Normalize(project + Separator + item, TaskTitle.MaxStoredLength);
        }

        /// <summary>
        /// 项目摘要：项目名、职责描述与该 VS 最近 <see cref="RecentCount"/> 条已结束任务的结果摘要（不含 <paramref name="excludeId"/>）。
        /// Project summary: project name, responsibility and the outcome of the latest <see cref="RecentCount"/> finished tasks
        /// of that VS (excluding <paramref name="excludeId"/>).
        /// </summary>
        public static string Summary(string project, string note, IEnumerable<QueuedTask> tasks, string vsKey, int excludeId = 0)
        {
            if (string.IsNullOrEmpty(project) && string.IsNullOrEmpty(vsKey)) return null;
            var sb = new StringBuilder("[项目上下文 / Project context] 项目 / Project：").Append(string.IsNullOrEmpty(project) ? "未知 / unknown" : project);
            sb.Append("；职责 / Role：").Append(string.IsNullOrWhiteSpace(note) ? "未设置 / not set" : TextUtil.Clip(OneLine(note), 160));
            var recent = (tasks ?? Enumerable.Empty<QueuedTask>())
                .Where(t => t != null && t.Id != excludeId && !QueueStatus.Active(t.Status)
                    && string.Equals(t.VsKey ?? "", vsKey ?? "", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Finished ?? t.Created).ThenByDescending(t => t.Id)
                .Take(RecentCount).ToList();
            if (recent.Count == 0) sb.Append("；最近任务 / Recent tasks：无 / none");
            else
            {
                sb.Append("；最近任务 / Recent tasks：");
                foreach (var t in recent)
                {
                    string title = TaskTitle.Normalize(t.Title, TaskTitle.MaxStoredLength) ?? TaskTitle.FromText(t.Text) ?? "";
                    string outcome = t.Status == QueueStatus.Failed ? t.FailureReason ?? t.Error : t.Result;
                    sb.Append("\n- #").Append(t.Id).Append(' ').Append(title).Append("｜").Append(StatusLabel(t.Status));
                    if (!string.IsNullOrWhiteSpace(outcome)) sb.Append("｜").Append(TextUtil.Clip(OneLine(outcome), 120));
                }
            }
            sb.Append("\n以上仅属于该项目；判断时不要套用其他项目的结论。/ This context belongs to this project only; do not apply other projects' conclusions.");
            return sb.ToString();
        }

        private static string StatusLabel(string status)
        {
            switch (status)
            {
                case QueueStatus.Done: return "已完成 / done";
                case QueueStatus.Unverified: return "待验证 / awaiting verification";
                case QueueStatus.Failed: return "失败 / failed";
                case QueueStatus.Cancelled: return "已取消 / cancelled";
                default: return status ?? "";
            }
        }

        private static string OneLine(string s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim(), @"\s+", " ");
    }
}
