using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace VSManager
{
    /// <summary>助手类型：总控助手管理所有 VS；笔记助手只处理笔记本内容。/ Assistant profile: the manager handles every VS; the note assistant only works with notebook content.</summary>
    public enum AgentProfile { Manager, Notes }

    /// <summary>笔记本中当前打开页面的快照（含尚未保存的编辑）。/ Snapshot of the page currently open in the notebook (including unsaved edits).</summary>
    public sealed class NoteSnapshot
    {
        /// <summary>页面编号。/ Page id.</summary>
        public string Id;
        /// <summary>层级标题路径，如「项目 / 周报」。/ Hierarchical title path, e.g. "Project / Weekly".</summary>
        public string TitlePath;
        /// <summary>Markdown 正文。/ Markdown body.</summary>
        public string Text;
    }

    public sealed partial class AgentService
    {
        /// <summary>笔记助手在对话中显示的名称。/ Name shown for the note assistant in the transcript.</summary>
        public const string NoteAgentTitle = "笔记助手";

        /// <summary>当前助手类型。/ The assistant profile.</summary>
        public AgentProfile Profile { get; }

        /// <summary>
        /// 读取笔记本当前打开的页面；返回 null 表示没有打开页面。实现方负责切换到界面线程。
        /// Reads the page currently open in the notebook; null means none is open. The implementation marshals to the UI thread itself.
        /// </summary>
        public Func<NoteSnapshot> CurrentNoteSource { get; set; }

        private List<AITool> NoteTools() => new List<AITool>
        {
            AIFunctionFactory.Create((Func<string>)ReadCurrentNote, "read_current_note"),
            AIFunctionFactory.Create((Func<string, string>)ListNotes, "list_notes"),
            AIFunctionFactory.Create((Func<string, string>)ReadNote, "read_note"),
        };

        private NoteSnapshot CurrentNote()
        {
            try { return CurrentNoteSource?.Invoke(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                Log("读取当前笔记失败 / Failed to read the current note: " + ex.Message);
                return null;
            }
        }

        /// <summary>写入系统提示词的当前笔记说明（只含标题与编号，正文由工具读取）。/ Current-note line for the system prompt (title and id only; the body is read by the tool).</summary>
        private string DescribeCurrentNote()
        {
            var note = CurrentNote();
            if (note == null) return null;
            return (string.IsNullOrWhiteSpace(note.TitlePath) ? "（无标题 / Untitled）" : note.TitlePath) + "（id: " + note.Id + "，" + (note.Text ?? "").Length + " 字 / chars）";
        }

        [Description("读取用户在笔记本中当前打开的笔记（标题路径与 Markdown 正文，含尚未保存的编辑）；只读。用户说「这篇 / 当前笔记」时先调用。/ Read the note currently open in the notebook (title path and Markdown body, including unsaved edits); read-only. Call it first when the user refers to \"this / the current note\".")]
        private string ReadCurrentNote()
        {
            var note = CurrentNote();
            if (note == null) return "当前没有打开的笔记 / No note is open";
            string body = string.IsNullOrWhiteSpace(note.Text) ? "（正文为空 / Empty body）" : note.Text;
            return Truncate("标题 / Title: " + note.TitlePath + "\nid: " + note.Id + "\n\n" + body, MaxToolText);
        }
    }
}
