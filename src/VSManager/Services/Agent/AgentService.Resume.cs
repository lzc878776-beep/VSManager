using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
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

        /// <summary>接续时恢复到模型上下文的最多记录条数。/ Max records restored into the model context when resuming.</summary>
        internal const int RestoredContextRecords = 20;

        /// <summary>恢复的上下文开头的说明。/ Note at the start of the restored context.</summary>
        internal const string RestoredContextNote =
            "[VSManager] 以下是重开 VSManager 前最近几条对话的摘录，只供了解上下文。其中的任务编号、入队与推送结果都是当时的，不代表现在已执行；" +
            "本轮需要发布任务、查询清单或执行任何操作时，必须实际调用对应工具，只汇报工具返回的结果。" +
            " / Excerpt of the latest conversation before VSManager reopened, for context only. Task IDs, queue and push results in it are historical and do not mean anything was done now; " +
            "to publish tasks, check the list or take any action you must actually call the tool and report only what it returns.";

        /// <summary>
        /// 重开 VSManager 时接续上次对话：从本机对话记录（agent-chat.jsonl）读取最近一次「新对话」之后的记录，恢复界面对话与模型上下文。
        /// 记录了的真实工具调用按结构化调用 / 结果恢复，被核查判定虚报的回复换成作废说明；仅本机展示的通知不进入上下文；模型上下文只恢复最近 RestoredContextRecords 条。必须在界面线程、助手未运行时调用；返回恢复的记录条数。
        /// Resumes the previous conversation when VSManager reopens: reads the records after the latest "new conversation" marker
        /// from the local chat history (agent-chat.jsonl) and restores the transcript and model context. Recorded tool calls are restored
        /// as structured calls / results, replies flagged as fabricated become a discard note, and local-only notices stay out of it; the model context gets only the latest RestoredContextRecords records.
        /// Call on the UI thread while idle; returns how many records were restored.
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

            // 界面恢复全部记录；模型上下文只恢复最近一小段，并先说明这是恢复的旧对话：
            // 大量只剩文字的旧回复会让模型以为「写出结果」就够了，从而不再调用工具
            // The transcript gets every record; the model context gets only a short recent tail, preceded by a note that it is restored:
            // a long run of text-only old replies teaches the model that writing results is enough, and it stops calling tools
            int tailStart = Math.Max(0, current.Count - RestoredContextRecords);
            while (tailStart < current.Count && current[tailStart].Role == AgentChatLog.RoleAssistant) tailStart++;
            if (tailStart < current.Count) _history.Add(new AIMessage(AIRole.User, RestoredContextNote));
            for (int i = 0; i < current.Count; i++)
            {
                AddToTranscript(current[i]);
                if (i >= tailStart) AddToHistory(current[i]);
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
                if (!string.IsNullOrWhiteSpace(r.Text)) AddScoped(new AIMessage(AIRole.User, r.Text), r.Scope);
                return;
            }
            if (r.Role == AgentChatLog.RoleNotice)
            {
                // 只有曾交给模型的通知（带完整内容）才进入上下文 / Only notices that went to the model (with full content) enter the context
                if (!r.Local && !string.IsNullOrWhiteSpace(r.Detail)) AddScoped(new AIMessage(AIRole.User, r.Detail), r.Scope);
                return;
            }
            string text = ToolClaimCheck.HistoryText(r.Text, r.Steps, r.Calls != null && r.Calls.Count > 0);
            bool fabricated = ToolClaimCheck.IsFabricated(r.Steps);
            // 真实工具调用按结构化的调用 / 结果消息恢复，模型看到的是「调用过工具」而不是可以照抄的文字
            // Real tool calls are restored as structured call / result messages, so the model sees tool use rather than text to copy
            if (!fabricated && r.Calls != null && r.Calls.Count > 0)
            {
                var callMsg = new AIMessage { Role = AIRole.Assistant };
                var resultMsg = new AIMessage { Role = AIRole.Tool };
                foreach (var c in r.Calls)
                {
                    string id = "restored_" + (++_restoredCallSeq);
                    callMsg.Contents.Add(new FunctionCallContent(id, c.Name, ParseArgs(c.Args)));
                    resultMsg.Contents.Add(new FunctionResultContent(id, c.Result ?? ""));
                }
                _history.Add(callMsg);
                _history.Add(resultMsg);
            }
            if (text.Length == 0 && string.IsNullOrWhiteSpace(r.Error)) return;
            if (!string.IsNullOrWhiteSpace(r.Error)) text += "\n（" + r.Error + "）";
            _history.Add(new AIMessage(AIRole.Assistant, text.Trim()));
        }

        /// <summary>恢复时带上记录的项目归属（会话隔离）。/ Restores with the recorded project scope (session isolation).</summary>
        private void AddScoped(AIMessage m, string scope)
        {
            SetScope(m, SessionIsolation ? scope : null);
            _history.Add(m);
        }

        private int _restoredCallSeq;

        private static IDictionary<string, object> ParseArgs(string json)
        {
            var d = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(json)) return d;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
            }
            catch { }
            return d;
        }
    }
}
