using System;
using System.IO;
using System.Linq;

namespace VSManager
{
    /// <summary>
    /// 笔记本根目录下的「AI 助手补充提示词」页面：用户在其中写给 AI 总控助手的补充要求，每次开始新对话时读取并加入系统提示词。
    /// The root notebook page "AI 助手补充提示词": extra instructions the user writes for the AI assistant, read at the start of
    /// every new conversation and added to the system prompt.
    /// </summary>
    internal static class NotebookAgentPrompt
    {
        internal const string PageTitle = "AI 助手补充提示词";
        internal const int MaxLength = 8000;
        internal const string NoteZh = "> 在下方写给 AI 总控助手的补充要求；每次开始新对话时读取，修改后点「新对话」生效。本段说明不会发送给 AI。";
        internal const string NoteEn = "> Write extra instructions for the AI assistant below; they are read at the start of each new conversation (click \"New chat\" to apply edits). This note is not sent to the AI.";

        internal static string Template => "# " + PageTitle + "\n\n" + NoteZh + "\n" + NoteEn + "\n\n";

        /// <summary>确保页面存在并返回其编号。/ Ensures the page exists and returns its id.</summary>
        internal static string Ensure(NotebookStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            string id = store.FindChild("", PageTitle);
            if (id != null) return id;
            try { return store.CreatePage("", PageTitle, Template); }
            catch (IOException) when (store.FindChild("", PageTitle) != null) { return store.FindChild("", PageTitle); }
        }

        /// <summary>读取用户写的补充提示词（去掉页面标题与说明）；没有内容时返回 null。/ Reads the user's extra instructions (without the heading and note); null when empty.</summary>
        internal static string Load(NotebookStore store)
        {
            return Extract(store.Read(Ensure(store)).Text);
        }

        internal static string Extract(string text)
        {
            var lines = NotebookStore.NormalizeNewLines(text ?? "", "\n").Split('\n').ToList();
            int first = lines.FindIndex(l => l.Trim().Length > 0);
            if (first >= 0 && lines[first].Trim() == "# " + PageTitle) lines.RemoveAt(first);
            lines.RemoveAll(l => l.Trim() == NoteZh || l.Trim() == NoteEn);
            string body = string.Join("\n", lines).Trim();
            if (body.Length == 0) return null;
            if (body.Length > MaxLength) body = body.Substring(0, MaxLength) + "\n…（已截断 / truncated）";
            return body;
        }
    }
}