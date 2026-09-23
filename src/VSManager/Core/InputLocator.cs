using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    /// <summary>定位 Copilot 输入框的结论。/ Outcome of locating the Copilot input box.</summary>
    public enum LocateOutcome
    {
        /// <summary>已找到可编辑的输入框。/ An editable input box was found.</summary>
        Found,
        /// <summary>未找到 Copilot 对话窗格。/ The Copilot chat pane was not found.</summary>
        PaneNotFound,
        /// <summary>窗格已找到，但其中没有输入框候选。/ The pane was found but holds no input candidate.</summary>
        InputNotFound,
        /// <summary>找到了对话列表之外的输入框候选，但都只读或已禁用。/ Candidates outside the conversation list exist but are all read-only or disabled.</summary>
        InputReadOnly
    }

    /// <summary>
    /// 输入框候选元素的快照（与 UI Automation 解耦，便于单元测试）。
    /// Snapshot of an input box candidate (decoupled from UI Automation so it can be unit-tested).
    /// </summary>
    public sealed class InputCandidate
    {
        public string Name, AutomationId, ClassName, Error;
        public bool Enabled, KeyboardFocusable, Offscreen, HasTextPattern, InConversation;
        /// <summary>ValuePattern.IsReadOnly；null 表示元素不支持 ValuePattern（WpfTextView 即如此）。/ ValuePattern.IsReadOnly; null when unsupported (as for WpfTextView).</summary>
        public bool? ReadOnly;
        /// <summary>元素底边的屏幕坐标；NaN 表示未知（折叠 / 无尺寸）。/ Screen bottom of the element; NaN when unknown (collapsed / no size).</summary>
        public double Bottom = double.NaN;
        /// <summary>文档顺序。/ Document order.</summary>
        public int Order;
        /// <summary>对应的 UI Automation 元素。/ The underlying UI Automation element.</summary>
        public object Tag;

        /// <summary>元素已失效（读取属性时出错）。/ The element is stale (reading its properties failed).</summary>
        public bool Stale => Error != null;

        /// <summary>
        /// 可编辑：可用、可获得键盘焦点、未声明只读、能读写文本，且不在对话列表内（对话历史中的代码块都是只读的）。
        /// Editable: enabled, keyboard focusable, not declared read-only, exposes text, and not inside the conversation list
        /// (code blocks in the history are read-only).
        /// </summary>
        public bool Editable => !Stale && Enabled && KeyboardFocusable && ReadOnly != true && HasTextPattern && !InConversation;

        public override string ToString()
        {
            if (Stale) return "「" + Name + "」(已失效 / stale: " + Error + ")";
            return "「" + Name + "」id=" + (string.IsNullOrEmpty(AutomationId) ? "-" : AutomationId) +
                   " cls=" + (string.IsNullOrEmpty(ClassName) ? "-" : ClassName) +
                   " 可编辑/editable=" + Editable +
                   " (en=" + Enabled + " kbf=" + KeyboardFocusable + " ro=" + (ReadOnly?.ToString() ?? "?") +
                   " text=" + HasTextPattern + " 对话内/inList=" + InConversation + " off=" + Offscreen +
                   (double.IsNaN(Bottom) ? " bottom=?" : " bottom=" + (int)Bottom) + ")";
        }
    }

    /// <summary>
    /// 输入框定位的纯逻辑：候选选择、失败分类、退避与提示文案。UI Automation 的采集在 CopilotChat.Locate.cs。
    /// Pure logic for locating the input box: candidate selection, failure classification, back-off and messages.
    /// The UI Automation collection lives in CopilotChat.Locate.cs.
    /// </summary>
    public static class InputLocator
    {
        /// <summary>默认定位超时（秒）。/ Default locate timeout in seconds.</summary>
        public const int DefaultTimeoutSeconds = 6;
        /// <summary>默认自动重试次数。/ Default number of automatic retries.</summary>
        public const int DefaultRetryCount = 1;

        /// <summary>第一个可编辑的候选（按文档顺序）。/ The first editable candidate in document order.</summary>
        public static InputCandidate PickFirst(IEnumerable<InputCandidate> items) =>
            (items ?? Enumerable.Empty<InputCandidate>()).Where(c => c != null && c.Editable).OrderBy(c => c.Order).FirstOrDefault();

        /// <summary>
        /// 最靠下的可编辑候选（输入框位于窗格底部）；位置未知的排在最后，同一高度取文档顺序靠后的。
        /// The lowest editable candidate (the input sits at the bottom of the pane); unknown positions come last, ties go to the later one.
        /// </summary>
        public static InputCandidate PickLowest(IEnumerable<InputCandidate> items) =>
            (items ?? Enumerable.Empty<InputCandidate>()).Where(c => c != null && c.Editable)
                .OrderByDescending(c => double.IsNaN(c.Bottom) ? double.MinValue : c.Bottom)
                .ThenByDescending(c => c.Order).FirstOrDefault();

        /// <summary>
        /// 失败分类：窗格缺失 → PaneNotFound；对话列表外有可见候选但都不可编辑 → InputReadOnly；否则 InputNotFound。
        /// 隐藏（offscreen）的候选不计入只读：窗格里常驻有隐藏的标题编辑框等。
        /// Failure classification: no pane → PaneNotFound; visible candidates outside the list exist but none is editable →
        /// InputReadOnly; otherwise InputNotFound. Hidden (offscreen) candidates do not count as read-only: the pane always holds
        /// hidden boxes such as the title editor.
        /// </summary>
        public static LocateOutcome Classify(bool paneFound, IEnumerable<InputCandidate> seen)
        {
            if (!paneFound) return LocateOutcome.PaneNotFound;
            var list = (seen ?? Enumerable.Empty<InputCandidate>()).Where(c => c != null).ToList();
            if (list.Any(c => c.Editable)) return LocateOutcome.Found;
            return list.Any(c => !c.Stale && !c.InConversation && !c.Offscreen && !IsKnownNonInput(c)) ? LocateOutcome.InputReadOnly : LocateOutcome.InputNotFound;
        }

        /// <summary>
        /// 窗格中已知的非输入框文本元素（实测：对话标题编辑框、模式 / 模型下拉框内的可编辑文本框）。
        /// Text elements in the pane known not to be the input (observed: the chat title editor and the editable boxes inside the mode / model pickers).
        /// </summary>
        public static bool IsKnownNonInput(InputCandidate c) =>
            c != null && (c.AutomationId == "chatTitleEditor" || c.AutomationId == "PART_EditableTextBox");

        /// <summary>轮询退避：150 → 300 → 600 → 1000 毫秒（封顶）。/ Polling back-off: 150 → 300 → 600 → 1000 ms (cap).</summary>
        public static int NextDelayMs(int step) => step <= 0 ? 150 : step == 1 ? 300 : step == 2 ? 600 : 1000;

        /// <summary>按失败原因给出可操作提示（中文在前，英文在后）。/ Actionable message per failure reason (Chinese first, then English).</summary>
        public static string FailureMessage(LocateOutcome outcome, int attempts)
        {
            string tried = $"已尝试 {attempts} 次 / tried {attempts} time(s)";
            const string tail = "。详见发送日志 / See the send log for details";
            switch (outcome)
            {
                case LocateOutcome.PaneNotFound:
                    return $"未找到 Copilot 对话窗格（{tried}），发送已取消。请在该 VS 中通过「视图 → GitHub Copilot 对话」打开一次对话窗格，并确认已登录 GitHub Copilot / " +
                           "The Copilot chat pane was not found; send cancelled. Open it once in that VS (View → GitHub Copilot Chat) and make sure you are signed in to GitHub Copilot" + tail;
                case LocateOutcome.InputReadOnly:
                    return $"Copilot 输入框当前不可编辑（只读或已禁用，{tried}），发送已取消。Copilot 可能在等待你确认操作（如「允许」「保留」按钮）或仍在处理，请在该 VS 中处理后重试 / " +
                           "The Copilot input box is read-only or disabled; send cancelled. Copilot may be waiting for your confirmation (e.g. Allow / Keep buttons) or still busy; handle it in that VS and retry" + tail;
                default:
                    return $"未找到 Copilot 输入框（对话窗格已找到，{tried}），发送已取消。请切换到该 VS 单击一次 Copilot 输入框，或关闭后重新打开对话窗格，然后重试 / " +
                           "The Copilot input box was not found (the chat pane was); send cancelled. Switch to that VS and click the Copilot input box once, or close and reopen the chat pane, then retry" + tail;
            }
        }
    }
}
