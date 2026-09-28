using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VSManager
{
    /// <summary>
    /// 笔记卡片：Markdown 中 ```card 围栏代码块，阅读视图渲染为与任务清单一致的卡片（状态胶囊、编号、时间、标题、正文、附注）。
    /// 每行一个「键: 值」，不带键的行接在上一个键后面（换行）。
    /// Note card: a ```card fenced block in Markdown, rendered in the reading view as a card like the task list
    /// (status pill, meta, time, title, text, note). One "key: value" per line; lines without a key continue the previous key.
    /// </summary>
    public sealed class NoteCard
    {
        /// <summary>围栏代码块的语言标记。/ Info string of the fenced block.</summary>
        public const string FenceInfo = "card";

        /// <summary>支持的状态。/ Supported statuses.</summary>
        public static readonly string[] Statuses = { "done", "needs_user", "unverified", "running", "waiting", "failed", "cancelled", "info" };

        /// <summary>状态列表文字（用于提示词与工具说明）。/ Status list text (for prompts and tool descriptions).</summary>
        public static string StatusList => string.Join(" / ", Statuses);

        /// <summary>支持的卡片样式（standard 为默认，不写入 Markdown）。/ Supported
        public static readonly string[] Styles = { "standard", "compact", "numbered", "noted", "accent" };

        /// <summary>样式列表文字（用于提示词与工具说明）。/ Style list
        public static string StyleList => string.Join(" / ", Styles);

        private static readonly string[] Keys = { "status", "label", "duration", "meta", "time", "title", "text", "note", "style" };

        public string Status = "info";
        public string Label;
        public string Duration;
        public string Meta;
        public string Time;
        public string Title;
        public string Text;
        public string Note;
        public string Style = "standard";

        /// <summary>规范化样式（支持中文别名），无法识别时为 standard。/ Normalizes
        public static string NormalizeStyle(string style)
        {
            string s = (style ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            switch (s)
            {
                case "compact": case "dense": case "紧凑": case "紧凑型": return "compact";
                case "numbered": case "number": case "number_left": case "编号": case "编号左列": case "编号左列型": return "numbered";
                case "noted": case "note": case "callout": case "附注": case "带附注": case "带附注型": return "noted";
                case "accent": case "bar": case "色条": case "左色条": case "左色条型": return "accent";
                default: return "standard";
            }
        }

        /// <summary>样式的中文名。/ Chinese name
        public static string StyleName(string style)
        {
            switch (NormalizeStyle(style))
            {
                case "compact": return "紧凑型";
                case "numbered": return "编号左列型";
                case "noted": return "带附注型";
                case "accent": return "左色条型";
                default: return "标准型";
            }
        }

        /// <summary>规范化状态（支持中文别名），无法识别时为 info。/ Normalizes a status (Chinese aliases accepted); unknown values become info.</summary>
        public static string NormalizeStatus(string status)
        {
            string s = (status ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            switch (s)
            {
                case "done": case "success": case "completed": case "已完成": case "完成": case "成功": return "done";
                case "needs_user": case "awaiting": case "待验证": case "待确认": return "needs_user";
                case "unverified": case "未验证": return "unverified";
                case "running": case "busy": case "执行中": case "进行中": return "running";
                case "waiting": case "queued": case "排队中": case "等待": return "waiting";
                case "failed": case "error": case "失败": return "failed";
                case "cancelled": case "canceled": case "已取消": case "取消": return "cancelled";
                default: return "info";
            }
        }

        /// <summary>状态胶囊文字：自定义 label 优先，否则按状态生成，再附加用时。/ Pill text: a custom label wins, otherwise derived from the status, then the duration.</summary>
        public string PillText
        {
            get
            {
                string text = !string.IsNullOrWhiteSpace(Label) ? Label.Trim() : DefaultLabel(NormalizeStatus(Status));
                return string.IsNullOrWhiteSpace(Duration) ? text : text + " · " + Duration.Trim();
            }
        }

        private static string DefaultLabel(string status)
        {
            switch (status)
            {
                case "done": return "✓ 已完成";
                case "needs_user": return "✓ 待验证";
                case "unverified": return "◐ 未验证";
                case "running": return "执行中";
                case "waiting": return "排队中";
                case "failed": return "失败";
                case "cancelled": return "已取消";
                default: return "记录";
            }
        }

        /// <summary>解析卡片代码块内容。/ Parses the body of a card block.</summary>
        public static NoteCard Parse(string body)
        {
            var values = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            string current = null;
            foreach (string raw in NotebookStore.NormalizeNewLines(body ?? "", "\n").Split('\n'))
            {
                int colon = raw.IndexOf(':');
                string key = colon > 0 ? raw.Substring(0, colon).Trim().ToLowerInvariant() : null;
                if (key != null && Keys.Contains(key))
                {
                    current = key;
                    values[key] = new StringBuilder(raw.Substring(colon + 1).Trim());
                }
                else if (current != null && raw.Trim().Length > 0)
                    values[current].Append('\n').Append(raw.Trim());
            }
            string Get(string k) => values.TryGetValue(k, out var v) && v.Length > 0 ? v.ToString() : null;
            return new NoteCard
            {
                Status = NormalizeStatus(Get("status")),
                Label = Get("label"),
                Duration = Get("duration"),
                Meta = Get("meta"),
                Time = Get("time"),
                Title = Get("title"),
                Text = Get("text"),
                Note = Get("note"),
                Style = NormalizeStyle(Get("style")),
            };
        }

        /// <summary>生成卡片的 Markdown（围栏长度自动避开内容中的反引号）。/ Formats the card as Markdown (the fence avoids backtick runs in the content).</summary>
        public string ToMarkdown()
        {
            var lines = new List<string> { "status: " + NormalizeStatus(Status) };
            string style = NormalizeStyle(Style);
            if (style != "standard") lines.Add("style: " + style);
            void Add(string key, string value, bool multiLine)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var parts = NotebookStore.NormalizeNewLines(value.Trim(), "\n").Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (!multiLine) { lines.Add(key + ": " + string.Join(" ", parts)); return; }
                lines.Add(key + ": " + parts[0]);
                // 续行不能被误认成键 / Continuation lines must not look like a key
                foreach (string p in parts.Skip(1)) lines.Add(LooksLikeKey(p) ? "  " + p.Replace(":", "：") : p);
            }
            Add("label", Label, false);
            Add("duration", Duration, false);
            Add("meta", Meta, false);
            Add("time", Time, false);
            Add("title", Title, false);
            Add("text", Text, true);
            Add("note", Note, true);
            int run = 0, longest = 0;
            foreach (char c in string.Join("\n", lines)) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
            string fence = new string('`', Math.Max(3, longest + 1));
            return fence + FenceInfo + "\n" + string.Join("\n", lines) + "\n" + fence;
        }

        private static bool LooksLikeKey(string line)
        {
            int colon = line.IndexOf(':');
            return colon > 0 && Keys.Contains(line.Substring(0, colon).Trim().ToLowerInvariant());
        }
    }
}
