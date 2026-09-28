using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VSManager
{
    /// <summary>可选宿主能力：AI 写入笔记后刷新笔记本界面。/ Optional host capability: refresh the notebook UI after the AI writes a note.</summary>
    public interface IAgentNotebookHost
    {
        /// <summary>笔记已被 AI 修改（可在后台线程调用）。/ A note was changed by the AI (may be called from a background thread).</summary>
        void NotebookChanged(string pageId);
    }

    public sealed partial class AgentService
    {
        /// <summary>单次写入笔记的最大字符数。/ Maximum characters per note write.</summary>
        internal const int MaxNoteWriteChars = 100000;

        [Description("笔记本技能：新建笔记页面并写入 Markdown 正文，返回新页面编号。parent 为空时建在根目录；同级不能重名。用户要求「记到笔记里 / 新建笔记 / 整理成笔记」时调用。/ Notebook skill: create a page with a Markdown body and return its id. Empty parent = root; sibling titles must be unique.")]
        internal async Task<string> CreateNote(
            [Description("页面标题（单行，最多 120 字）/ Page title (one line, at most 120 characters)")] string title,
            [Description("Markdown 正文；为空时只写标题 / Markdown body; empty writes only a heading")] string content = null,
            [Description("可选父页面编号（list_notes 返回的 32 位编号）；为空表示根目录 / Optional parent page id; empty = root")] string parent = null)
        {
            title = (title ?? "").Trim();
            if (title.Length == 0) return "请提供页面标题 / A title is required.";
            if (TooLong(content)) return NoteTooLongText;
            string p = (parent ?? "").Trim().ToLowerInvariant();
            string where = p.Length == 0 ? "根目录 / root" : NotePathOrId(p);
            if (_settings().AgentConfirm && !await ConfirmAsync("新建笔记「" + title + "」",
                    "位置 / Location: " + where + "\r\n正文 / Body: " + (content ?? "").Length + " 字 / characters"))
                return "用户拒绝了该操作。/ The user declined.";
            try
            {
                var store = NotebookStoreFactory();
                string id = store.CreatePage(p, title, string.IsNullOrWhiteSpace(content) ? null : content);
                NotifyNotebook(id);
                return "已新建笔记 / Created note: " + id + "  " + string.Join(" / ", store.TitlePath(id));
            }
            catch (Exception ex) when (IsNoteError(ex)) { return "新建笔记失败 / Failed to create the note: " + ex.Message; }
        }

        [Description("笔记本技能：在已有笔记末尾追加 Markdown 内容（不改动原有正文），适合补充记录、待办、会议结论。/ Notebook skill: append Markdown to the end of an existing note without touching the existing body.")]
        internal async Task<string> AppendToNote(
            [Description("list_notes 返回的 32 位页面编号 / 32-character page id returned by list_notes")] string page,
            [Description("要追加的 Markdown 内容 / Markdown to append")] string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "追加内容为空 / Nothing to append.";
            if (TooLong(content)) return NoteTooLongText;
            string id = (page ?? "").Trim().ToLowerInvariant();
            if (_settings().AgentConfirm && !await ConfirmAsync("追加到笔记「" + NotePathOrId(id) + "」",
                    "在末尾追加 " + content.Length + " 字，不改动原有正文。/ Append " + content.Length + " characters at the end; the existing body is kept."))
                return "用户拒绝了该操作。/ The user declined.";
            return WriteNote(id, body => body.TrimEnd('\r', '\n') + (body.Trim().Length == 0 ? "" : "\n\n") + content.Trim('\r', '\n') + "\n",
                "已追加到笔记 / Appended to note");
        }

        [Description("笔记本技能：用新的 Markdown 全文替换笔记正文（原正文会被覆盖）。仅在用户明确要求改写 / 整理 / 替换整篇笔记时使用，并先用 read_note 读取原文；只想补充内容时用 append_to_note。/ Notebook skill: replace a note's whole Markdown body (overwrites it). Use only when the user explicitly asks to rewrite the note, after read_note; use append_to_note to add content.")]
        internal async Task<string> UpdateNote(
            [Description("list_notes 返回的 32 位页面编号 / 32-character page id returned by list_notes")] string page,
            [Description("新的完整 Markdown 正文 / The new full Markdown body")] string content)
        {
            if (content == null) return "请提供新的正文 / The new body is required.";
            if (TooLong(content)) return NoteTooLongText;
            string id = (page ?? "").Trim().ToLowerInvariant();
            if (_settings().AgentConfirm && !await ConfirmAsync("改写笔记「" + NotePathOrId(id) + "」",
                    "用 " + content.Length + " 字的新正文覆盖原正文。/ Overwrite the body with " + content.Length + " characters."))
                return "用户拒绝了该操作。/ The user declined.";
            return WriteNote(id, _ => content, "已改写笔记 / Rewrote note");
        }

        private string WriteNote(string id, Func<string, string> change, string done)
        {
            try
            {
                var store = NotebookStoreFactory();
                // 与界面并发保存时重读重试一次 / Re-read and retry once when the UI saved concurrently
                for (int attempt = 0; ; attempt++)
                {
                    var doc = store.Read(id);
                    try
                    {
                        store.Save(doc, change(doc.Text ?? ""));
                        break;
                    }
                    catch (NotebookConflictException) when (attempt == 0) { }
                }
                NotifyNotebook(id);
                return done + ": " + id + "  " + string.Join(" / ", store.TitlePath(id));
            }
            catch (Exception ex) when (IsNoteError(ex)) { return "写入笔记失败 / Failed to write the note: " + ex.Message; }
        }

        private const string NoteTooLongText = "内容过长（单次最多 100000 字），请拆分写入 / Content too long (at most 100000 characters per write); split it.";

        private static bool TooLong(string content) => content != null && content.Length > MaxNoteWriteChars;

        private static bool IsNoteError(Exception ex) =>
            ex is IOException || ex is ArgumentException || ex is SqliteException || ex is UnauthorizedAccessException;

        /// <summary>页面的标题路径（用于确认文字）；读取失败时返回编号。/ The page's title path (for confirmation text); the id when it cannot be read.</summary>
        private string NotePathOrId(string id)
        {
            try
            {
                string path = string.Join(" / ", NotebookStoreFactory().TitlePath(id));
                return path.Length > 0 ? path : id;
            }
            catch (Exception ex) when (IsNoteError(ex)) { return id; }
        }

        private void NotifyNotebook(string id)
        {
            Log("AI 写入笔记 / AI wrote note: " + id);
            (_host as IAgentNotebookHost)?.NotebookChanged(id);
        }
    }
}
