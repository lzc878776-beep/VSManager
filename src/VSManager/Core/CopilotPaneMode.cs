using System;
using System.Collections.Generic;

namespace VSManager
{
    /// <summary>Copilot 对话窗格的显示状态。/ Display state of the Copilot chat pane.</summary>
    public enum CopilotPaneMode
    {
        /// <summary>UIA 树中找不到窗格（已关闭或从未打开）。/ The pane is not in the UIA tree (closed or never opened).</summary>
        NotFound,
        /// <summary>窗格存在但不可见（自动隐藏、被其他标签页覆盖或最小化）。/ The pane exists but is offscreen (auto-hidden, behind another tab or minimized).</summary>
        Hidden,
        /// <summary>窗格显示的是聊天历史列表：「返回」可见，对话列表与输入框不可见。/ The pane shows the chat history list: "Back" visible, conversation and input offscreen.</summary>
        History,
        /// <summary>当前会话已显示且输入框可见。/ The current conversation and its input box are shown.</summary>
        Conversation,
        /// <summary>窗格可见但未找到可见的输入框（例如尚未登录或正在加载）。/ The pane is visible but has no visible input (e.g. signed out or loading).</summary>
        NoInput
    }

    /// <summary>
    /// 窗格状态判定的纯逻辑（输入为 UIA 观测结果），便于单元测试。
    /// Pure pane-state classification over UIA observations, so it is unit-testable.
    /// </summary>
    public static class CopilotPaneModes
    {
        /// <summary>「返回」按钮（从历史列表回到当前会话）。/ The "Back" button (from the history list back to the conversation).</summary>
        public const string BackToChatId = "backToChat";
        /// <summary>「查看聊天历史记录」按钮。/ The "View chat history" button.</summary>
        public const string ChatHistoryId = "chatHistory";

        /// <param name="paneFound">是否找到窗格。/ Whether the pane was found.</param>
        /// <param name="paneOffscreen">窗格本身是否不可见。/ Whether the pane itself is offscreen.</param>
        /// <param name="backVisible">「返回」按钮是否可见（null 表示不存在）。/ Whether "Back" is visible (null = absent).</param>
        /// <param name="listVisible">对话列表是否可见（null 表示不存在）。/ Whether the conversation list is visible (null = absent).</param>
        /// <param name="inputVisible">输入框是否可见（null 表示不存在）。/ Whether the input box is visible (null = absent).</param>
        public static CopilotPaneMode Classify(bool paneFound, bool paneOffscreen, bool? backVisible, bool? listVisible, bool? inputVisible)
        {
            if (!paneFound) return CopilotPaneMode.NotFound;
            if (paneOffscreen) return CopilotPaneMode.Hidden;
            if (backVisible == true && (listVisible != true || inputVisible != true)) return CopilotPaneMode.History;
            if (inputVisible == true) return CopilotPaneMode.Conversation;
            return CopilotPaneMode.NoInput;
        }

        /// <summary>
        /// 自动修复失败后的原因诊断与处理建议（中文在前、英文在后），用于发送失败提示，避免只报「无法确认输入安全」。
        /// Diagnosis and remedy after the automatic repair failed (Chinese first, English second), used for send failures instead of a vague "cannot confirm input".
        /// </summary>
        /// <param name="inputMissing">窗格处于会话状态但定位不到可编辑输入框。/ The pane shows a conversation but no editable input was located.</param>
        public static string Diagnose(CopilotPaneMode m, bool inputMissing = false)
        {
            switch (m)
            {
                case CopilotPaneMode.NotFound:
                    return "未找到 Copilot 对话窗格（窗格已关闭或 Copilot 扩展尚未加载），已执行「视图 → GitHub Copilot 对话」仍未出现；请在该 VS 中手动打开对话窗格"
                        + " / Copilot chat pane not found (closed or extension not loaded yet); View → GitHub Copilot Chat did not bring it up. Open the chat pane in that VS manually";
                case CopilotPaneMode.Hidden:
                    return "对话窗格不可见（自动隐藏、被其他标签页覆盖或 VS 最小化），显示命令未能让它可见；请在该 VS 中把对话窗格固定显示"
                        + " / Chat pane is offscreen (auto-hidden, behind another tab or VS minimized) and the show command did not reveal it. Pin the chat pane visible in that VS";
                case CopilotPaneMode.History:
                    return "对话窗格停留在聊天历史列表，自动点击「返回」未生效；请在该 VS 的对话窗格中点「← 返回」或打开一个会话"
                        + " / Chat pane is stuck on the chat history list and pressing Back automatically did not work. Press \"← Back\" or open a conversation in that VS";
                case CopilotPaneMode.NoInput:
                    return "对话窗格可见但没有输入框（可能未登录 GitHub、正在加载或 Copilot 被禁用）；请在该 VS 中确认 Copilot 已登录并可输入"
                        + " / Chat pane is visible but has no input (signed out, still loading or Copilot disabled). Make sure Copilot is signed in and accepts input in that VS";
                default:
                    return inputMissing
                        ? "对话窗格已显示当前会话，但定位不到可编辑的输入框（可能正在加载或界面结构变化）；请在该 VS 中点击一次输入框确认可输入"
                            + " / The conversation is shown but no editable input could be located (loading or UI changed). Click the input once in that VS to confirm it accepts text"
                        : "对话窗格已就绪 / Chat pane is ready";
            }
        }

        public static string Describe(CopilotPaneMode m)
        {
            switch (m)
            {
                case CopilotPaneMode.NotFound: return "未找到窗格 / pane not found";
                case CopilotPaneMode.Hidden: return "窗格不可见 / pane offscreen";
                case CopilotPaneMode.History: return "历史记录模式 / history mode";
                case CopilotPaneMode.Conversation: return "当前会话 / conversation";
                default: return "未见输入框 / no visible input";
            }
        }
    }

    /// <summary>「打开对话助手」的结果与诊断。/ Result and diagnostics of "open the Copilot chat".</summary>
    public sealed class CopilotPaneOpenResult
    {
        public bool Ok;
        /// <summary>开始时的窗格状态。/ Pane state at the start.</summary>
        public CopilotPaneMode Initial = CopilotPaneMode.NotFound;
        /// <summary>结束时的窗格状态。/ Pane state at the end.</summary>
        public CopilotPaneMode Final = CopilotPaneMode.NotFound;
        /// <summary>是否执行了 DTE 显示命令。/ Whether the DTE show command ran.</summary>
        public bool UsedDte;
        /// <summary>是否点击了「返回」退出历史记录模式。/ Whether "Back" was pressed to leave history mode.</summary>
        public bool LeftHistory;
        /// <summary>工具窗口原本是否自动隐藏（null 表示未知）。/ Whether the tool window was auto-hidden (null = unknown).</summary>
        public bool? WasAutoHide;
        /// <summary>输入框是否已获得焦点。/ Whether the input got focus.</summary>
        public bool Focused;
        /// <summary>找到的窗格候选数量。/ Number of pane candidates.</summary>
        public int Candidates;
        public long ElapsedMs;
        /// <summary>拦截弹窗等阻止原因。/ Blocking reason such as a modal dialog.</summary>
        public string Blocked;
        public readonly List<string> Steps = new List<string>();

        public void Step(string s) => Steps.Add(s);

        /// <summary>中文结果文字（用于通知与语音）。/ Chinese result text (for notifications and voice).</summary>
        public string MessageZh(string vsName)
        {
            string who = string.IsNullOrEmpty(vsName) ? "" : "「" + vsName + "」的";
            if (Blocked != null) return "未能打开" + who + "对话助手：" + Blocked;
            if (Ok) return "已打开" + who + "对话助手" + (LeftHistory ? "（已从历史记录切回当前会话）" : "");
            return "未能打开" + who + "对话助手，请手动打开：在该 VS 中选择「视图 → GitHub Copilot 对话」，若显示历史记录请点「返回」";
        }

        /// <summary>英文结果文字。/ English result text.</summary>
        public string MessageEn(string vsName)
        {
            string who = string.IsNullOrEmpty(vsName) ? "" : " of \"" + vsName + "\"";
            if (Blocked != null) return "Could not open the Copilot chat" + who + ": a dialog is blocking VS";
            if (Ok) return "Copilot chat" + who + " opened" + (LeftHistory ? " (switched back from the history list)" : "");
            return "Could not open the Copilot chat" + who + "; please open it manually: View → GitHub Copilot Chat in that VS, and press Back if it shows the history";
        }

        /// <summary>给用户与模型看的结果文字（中文在前，英文在后）。/ Result text for the user and the model (Chinese first, English second).</summary>
        public string Message(string vsName) =>
            MessageZh(vsName) + " / " + MessageEn(vsName) + (Ok ? "" : "（状态 / state：" + CopilotPaneModes.Describe(Final) + "）");

        /// <summary>日志用的诊断摘要。/ Diagnostic summary for logs.</summary>
        public string Diagnostics() =>
            $"初始 / initial={Initial} 结束 / final={Final} DTE={UsedDte} 退出历史 / leftHistory={LeftHistory} 自动隐藏 / autoHide={(WasAutoHide.HasValue ? WasAutoHide.Value.ToString() : "?")} " +
            $"候选 / candidates={Candidates} 焦点 / focused={Focused} 耗时 / elapsed={ElapsedMs}ms" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", Steps);
    }
}
