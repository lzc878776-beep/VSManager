using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    public sealed partial class AgentService
    {
        private const string CardStatusHelp = "状态 / Status: done=已完成, needs_user=待验证, unverified=未验证, running=执行中, waiting=排队中, failed=失败, cancelled=已取消, info=记录（默认 / default）";

        private const string CardStyleHelp = "可选：卡片样式 / Optional card style: standard=标准型（默认 / default）, compact=紧凑型（标题并入首行，正文一行）/ compact (title in the head row, one-line text), " +
            "numbered=编号左列型（meta 作为左侧大号编号）/ numbered (meta as a large number in a left column), noted=带附注型（附注完整显示为引用块）/ noted (full note as a callout), " +
            "accent=左色条型（按状态着色的左边框）/ accent (status-colored left bar)";

        private static NoteCard BuildCard(string title, string text, string status, string label, string duration, string meta, string time, string note, string style = null) =>
            new NoteCard { Title = title, Text = text, Status = status, Label = label, Duration = duration, Meta = meta, Time = time, Note = note, Style = NoteCard.NormalizeStyle(style) };

        /// <summary>解析样式列表（逗号、空格或顿号分隔），为空或 all 时返回全部样式。/ Parses a style list (comma, space or 、 separated); empty or "all" returns every style.</summary>
        internal static List<string> ParseStyleList(string styles)
        {
            var parts = (styles ?? "").Split(new[] { ',', '，', '、', ';', '；', ' ', '/' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Any(p => p.Trim().ToLowerInvariant() == "all" || p.Trim() == "全部"))
                return NoteCard.Styles.ToList();
            return parts.Select(NoteCard.NormalizeStyle).Distinct().ToList();
        }

        /// <summary>生成样式对比段落：每种样式一行说明加一张卡片。/ Builds a style comparison section: one caption line and one card per style.</summary>
        internal static string StyleSamplesMarkdown(NoteCard sample, IEnumerable<string> styles, string heading)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(heading)) sb.Append("### ").Append(heading.Trim()).Append("\n\n");
            foreach (string style in styles)
            {
                var card = BuildCard(sample.Title, sample.Text, sample.Status, sample.Label, sample.Duration, sample.Meta, sample.Time, sample.Note, style);
                sb.Append("**").Append(NoteCard.StyleName(style)).Append("** `style: ").Append(style).Append("`\n\n").Append(card.ToMarkdown()).Append("\n\n");
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static bool CardEmpty(NoteCard c) => string.IsNullOrWhiteSpace(c.Title) && string.IsNullOrWhiteSpace(c.Text) && string.IsNullOrWhiteSpace(c.Note);

        [Description("笔记卡片：生成一张笔记卡片的 Markdown（```card 代码块），笔记阅读视图会把它渲染成与任务清单一致的卡片（状态胶囊、编号、时间、标题、正文、附注）；只生成文字，不写入笔记。" +
            "修改已有卡片时按原卡片字段重新生成后替换原代码块。/ Note card: returns the Markdown (a ```card block) of a card that the notebook reading view renders like a task-list card " +
            "(status pill, meta, time, title, text, note); it only produces text and writes nothing. To change an existing card, regenerate it from its fields and replace the old block.")]
        internal string FormatNoteCard(
            [Description("标题，如「→ 项目名」/ Title, e.g. \"→ Project\"")] string title,
            [Description("正文（可多行）/ Body text (may span lines)")] string text = null,
            [Description(CardStatusHelp)] string status = null,
            [Description("可选：自定义胶囊文字，替代状态默认文字 / Optional custom pill text replacing the status default")] string label = null,
            [Description("可选：用时，如 16m57s，显示在胶囊中 / Optional duration shown in the pill, e.g. 16m57s")] string duration = null,
            [Description("可选：胶囊右侧的说明，如「#108 · AI」/ Optional meta next to the pill, e.g. \"#108 · AI\"")] string meta = null,
            [Description("可选：右上角时间，如 09:48 / Optional time at the top right, e.g. 09:48")] string time = null,
            [Description("可选：底部附注（以 ↳ 开头显示），如结果摘要 / Optional footer note (shown after ↳), e.g. a result summary")] string note = null,
            [Description(CardStyleHelp)] string style = null)
        {
            var card = BuildCard(title, text, status, label, duration, meta, time, note, style);
            if (CardEmpty(card)) return "请至少提供标题、正文或附注 / Provide a title, text or note.";
            return card.ToMarkdown();
        }

        [Description("笔记卡片：在已有笔记末尾追加一张卡片（```card 代码块，渲染为与任务清单一致的卡片），不改动原有正文；遵守「操作前确认」。" +
            "把任务结果记到笔记时，先用 list_tasks 查看任务编号、状态、用时与结果再填写。/ Note card: appends a card (a ```card block rendered like a task-list card) to the end of an existing note " +
            "without touching the existing body; honors Confirm before acting. When recording a task, check its id, status, duration and result with list_tasks first.")]
        internal async Task<string> AddNoteCard(
            [Description("list_notes 返回的 32 位页面编号 / 32-character page id returned by list_notes")] string page,
            [Description("标题，如「→ 项目名」/ Title, e.g. \"→ Project\"")] string title,
            [Description("正文（可多行）/ Body text (may span lines)")] string text = null,
            [Description(CardStatusHelp)] string status = null,
            [Description("可选：自定义胶囊文字 / Optional custom pill text")] string label = null,
            [Description("可选：用时，如 16m57s / Optional duration, e.g. 16m57s")] string duration = null,
            [Description("可选：胶囊右侧的说明，如「#108 · AI」/ Optional meta, e.g. \"#108 · AI\"")] string meta = null,
            [Description("可选：右上角时间，如 09:48 / Optional time, e.g. 09:48")] string time = null,
            [Description("可选：底部附注 / Optional footer note")] string note = null,
            [Description(CardStyleHelp)] string style = null)
        {
            var card = BuildCard(title, text, status, label, duration, meta, time, note, style);
            if (CardEmpty(card)) return "请至少提供标题、正文或附注 / Provide a title, text or note.";
            string block = card.ToMarkdown();
            if (TooLong(block)) return NoteTooLongText;
            string id = (page ?? "").Trim().ToLowerInvariant();
            if (_settings().AgentConfirm && !await ConfirmAsync("在笔记「" + NotePathOrId(id) + "」末尾添加卡片", block))
                return "用户拒绝了该操作。/ The user declined.";
            return WriteNote(id, body => body.TrimEnd('\r', '\n') + (body.Trim().Length == 0 ? "" : "\n\n") + block + "\n",
                "已添加卡片 / Added card");
        }

        [Description("卡片样式技能：用同一组卡片内容按多种样式各生成一张卡片，作为对比段落追加到已有笔记末尾，便于用户比较后选定样式；遵守「操作前确认」。" +
            "用户选定后，用 format_note_card/add_note_card 的 style 参数按该样式生成卡片，或 read_note 后用 update_note 给已有卡片加上「style: 样式」行。" +
            "/ Card style skill: renders the same card content once per style and appends them to an existing note as a comparison section so the user can pick one; honors Confirm before acting. " +
            "After the user picks, pass that style to format_note_card/add_note_card, or read_note and use update_note to add a \"style: <name>\" line to existing cards.")]
        internal async Task<string> AddNoteCardStyleSamples(
            [Description("list_notes 返回的 32 位页面编号 / 32-character page id returned by list_notes")] string page,
            [Description("示例卡片标题 / Sample card title")] string title,
            [Description("示例正文（可多行）/ Sample body text (may span lines)")] string text = null,
            [Description(CardStatusHelp)] string status = null,
            [Description("可选：示例胶囊文字 / Optional sample pill text")] string label = null,
            [Description("可选：示例用时 / Optional sample duration")] string duration = null,
            [Description("可选：示例编号或说明，如 01 / Optional sample meta, e.g. 01")] string meta = null,
            [Description("可选：示例时间 / Optional sample time")] string time = null,
            [Description("可选：示例附注 / Optional sample note")] string note = null,
            [Description("要对比的样式，逗号分隔，留空或 all 为全部 / Styles to compare, comma separated; empty or all = every style: standard, compact, numbered, noted, accent")] string styles = null,
            [Description("可选：对比段落的小标题，默认「卡片样式对比」/ Optional section heading, default \"卡片样式对比\"")] string heading = null)
        {
            var sample = BuildCard(title, text, status, label, duration, meta, time, note);
            if (CardEmpty(sample)) return "请至少提供标题、正文或附注 / Provide a title, text or note.";
            var list = ParseStyleList(styles);
            string block = StyleSamplesMarkdown(sample, list, heading ?? "卡片样式对比");
            if (TooLong(block)) return NoteTooLongText;
            string id = (page ?? "").Trim().ToLowerInvariant();
            if (_settings().AgentConfirm && !await ConfirmAsync("在笔记「" + NotePathOrId(id) + "」末尾添加卡片样式对比", block))
                return "用户拒绝了该操作。/ The user declined.";
            return WriteNote(id, body => body.TrimEnd('\r', '\n') + (body.Trim().Length == 0 ? "" : "\n\n") + block + "\n",
                "已添加 " + list.Count + " 种卡片样式对比 / Added card style samples: " + string.Join(", ", list));
        }
    }
}
