using System;
using System.Collections.Generic;
using System.Linq;
using AIMessage = Microsoft.Extensions.AI.ChatMessage;
using AIRole = Microsoft.Extensions.AI.ChatRole;

namespace VSManager
{
    public sealed partial class AgentService
    {
        /// <summary>「新对话」分隔标记的文字。/ Text of the "new conversation" marker.</summary>
        public const string NewConversationMarker = "＋ 新对话 / New conversation";

        /// <summary>接续后显示在对话末尾的提示（不写入记录）。/ Notice shown after resuming (never recorded).</summary>
        public const string ResumedNotice = "↺ 已接续上次对话；点「＋ 新对话」可重新开始 / Resumed the previous conversation; click \"＋ 新对话\" to start over";

        /// <summary>
        /// 重开 VSManager 时接续上次对话：从本机对话记录（agent-chat.jsonl）读取最近一次「新对话」之后的记录，恢复界面对话与模型上下文。
        /// 工具调用细节不进入上下文，只保留文字；仅本机展示的通知不进入上下文。必须在界面线程、助手未运行时调用；返回恢复的记录条数。
        /// Resumes the previous conversation when VSManager reopens: reads the records after the latest "new conversation" marker
        /// from the local chat history (agent-chat.jsonl) and restores the transcript and model context. Tool-call details are not
        /// put back into the context, only the text; local-only notices stay out of it. Call on the UI thread while idle; returns
        /// how many records were restored.
        /// </summary>
        public int RestoreConversation() => RestoreConversation(AgentChatLog.ReadAll());

        /// <summary>模型上下文（测试用）。/ Model context (for tests).</summary>
        internal IReadOnlyList<AIMessage> HistoryForTests => _history;

        internal int RestoreConversation(IList<AgentChatRecord> records)
        {
            if (Profile == AgentProfile.Notes || Running || records == null) return 0;
            if (_history.Count > 0 || Transcript.Messages.Count > 0) return 0;
            int reset = -1;
            for (int i = records.Count - 1; i >= 0; i--)
                if (records[i] != null && records[i].Reset) { reset = i; break; }
            var current = records.Skip(reset + 1).Where(r => r != null && !r.Reset).ToList();
            if (current.Count == 0) return 0;

            foreach (var r in current)
            {
                AddToTranscript(r);
                AddToHistory(r);
            }
            // 上次在一轮对话中途关闭：补一条说明，避免上下文以无回复的用户消息结尾 / Closed mid-round: add a note so the context does not end with an unanswered user message
            if (_history.Count > 0 && _history[_history.Count - 1].Role == AIRole.User)
                _history.Add(new AIMessage(AIRole.Assistant, "（上一轮因 VSManager 关闭而中断 / The previous round was interrupted because VSManager closed）"));
            TrimHistory();
            var notice = new ChatMessage { Role = ChatRole.Assistant };
            notice.Parts.Add(new ChatPart { IsStep = true, Text = ResumedNotice });
            Transcript.Messages.Add(notice);
            TrimTranscript();
            Changed?.Invoke();
            return current.Count;
        }

        private void AddToTranscript(AgentChatRecord r)
        {
            var m = new ChatMessage { Role = r.IsUser ? ChatRole.User : ChatRole.Assistant };
            if (!string.IsNullOrWhiteSpace(r.Text)) m.Parts.Add(new ChatPart { Text = r.Text });
            if (r.Role == AgentChatLog.RoleAssistant)
            {
                foreach (var step in r.Steps ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(step)) m.Parts.Add(new ChatPart { IsStep = true, Text = step });
                if (!string.IsNullOrWhiteSpace(r.Error) && !(r.Steps ?? new List<string>()).Any(s => s.Contains(r.Error)))
                    m.Parts.Add(new ChatPart { IsStep = true, Text = "⚠ " + r.Error });
            }
            if (m.Parts.Count == 0) return;
            Transcript.Messages.Add(m);
            TrimTranscript();
        }

        private void AddToHistory(AgentChatRecord r)
        {
            if (r.IsUser)
            {
                if (!string.IsNullOrWhiteSpace(r.Text)) _history.Add(new AIMessage(AIRole.User, r.Text));
                return;
            }
            if (r.Role == AgentChatLog.RoleNotice)
            {
                // 只有曾交给模型的通知（带完整内容）才进入上下文 / Only notices that went to the model (with full content) enter the context
                if (!r.Local && !string.IsNullOrWhiteSpace(r.Detail)) _history.Add(new AIMessage(AIRole.User, r.Detail));
                return;
            }
            string text = (r.Text ?? "").Trim();
            if (text.Length == 0) return;
            if (!string.IsNullOrWhiteSpace(r.Error)) text += "\n（" + r.Error + "）";
            _history.Add(new AIMessage(AIRole.Assistant, text));
        }
    }
}
