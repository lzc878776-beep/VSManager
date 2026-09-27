using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VSManager
{
    public sealed partial class AgentService
    {
        private const int MaxNotebookEntries = 200;

        /// <summary>笔记本技能使用的存储；可在测试中替换。/ Store used by the notebook skill; replaceable in tests.</summary>
        internal Func<NotebookStore> NotebookStoreFactory { get; set; } = () => new NotebookStore();

        [Description("笔记本技能：列出或搜索笔记本页面（按标题与正文匹配），返回页面编号、层级标题路径；只读。/ Notebook skill: list or search notebook pages (title and body match), returning page ids and title paths; read-only.")]
        private string ListNotes(
            [Description("可选搜索词；为空时列出全部页面 / Optional search text; empty lists every page")] string query = null)
        {
            try
            {
                var tree = NotebookStoreFactory().LoadTree(query ?? "");
                var sb = new StringBuilder();
                int count = 0;
                void Walk(IEnumerable<NotebookEntry> entries, string prefix)
                {
                    foreach (var e in entries)
                    {
                        if (count >= MaxNotebookEntries) return;
                        string path = prefix.Length == 0 ? e.Name : prefix + " / " + e.Name;
                        sb.Append(e.Path).Append("  ").AppendLine(path);
                        count++;
                        Walk(e.Children, path);
                    }
                }
                Walk(tree, "");
                if (count == 0) return "没有匹配的笔记 / No matching notes";
                if (count >= MaxNotebookEntries) sb.AppendLine($"（仅显示前 {MaxNotebookEntries} 项，请缩小搜索范围 / Showing the first {MaxNotebookEntries}; narrow the search）");

                return Truncate(sb.ToString(), MaxToolText);
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is UnauthorizedAccessException)
            {
                return "读取笔记本失败 / Failed to read notebooks: " + ex.Message;
            }
        }

        [Description("笔记本技能：按页面编号读取笔记标题路径与正文（Markdown）；只读。若要据此发布任务，须先把拟发布的任务清单（目标 VS 与任务文本）呈现给用户确认，再逐条调用 send_task；归属或粒度不明确的内容向用户询问，不得猜测补全。/ Notebook skill: read a note's title path and Markdown body by page id; read-only. To publish tasks from it, first present the proposed list (target VS and text) for user confirmation, then call send_task per item; ask about unclear ownership or granularity, never guess.")]
        private string ReadNote(
            [Description("list_notes 返回的 32 位页面编号 / 32-character page id returned by list_notes")] string page)
        {
            try
            {
                var store = NotebookStoreFactory();
                string id = (page ?? "").Trim().ToLowerInvariant();
                var doc = store.Read(id);
                string path = string.Join(" / ", store.TitlePath(id));
                string body = string.IsNullOrWhiteSpace(doc.Text) ? "（正文为空 / Empty body）" : doc.Text;

                return Truncate("标题 / Title: " + (path.Length > 0 ? path : doc.Title) + "\n\n" + body, MaxToolText);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is SqliteException || ex is UnauthorizedAccessException)
            {
                return "读取笔记失败 / Failed to read the note: " + ex.Message;
            }
        }
    }
}
