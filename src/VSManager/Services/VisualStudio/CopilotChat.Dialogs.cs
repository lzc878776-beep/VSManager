using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace VSManager
{
    public partial class CopilotChat
    {
        private readonly Dictionary<int, IntPtr> _handledDialogs = new Dictionary<int, IntPtr>();

        internal static bool IsLineEndingDialog(string title, string body, string format, string yes, string no)
        {
            bool chinese = title == "不一致的行尾" && body != null
                && body.Contains("以下文件中的行尾不一致") && body.Contains("是否将行尾标准化");
            bool english = title == "Inconsistent Line Endings" && body != null
                && body.IndexOf("line endings", StringComparison.OrdinalIgnoreCase) >= 0
                && body.IndexOf("normalize", StringComparison.OrdinalIgnoreCase) >= 0;
            return (chinese || english) && format == "Windows (CR LF)"
                && (yes == "是(Y)" || yes == "是(&Y)" || yes == "Yes" || yes == "&Yes")
                && (no == "否(N)" || no == "否(&N)" || no == "No" || no == "&No");
        }

        private string TryResolveDialog(VsInstance vs, IntPtr popup, string title)
        {
            var settings = _getSettings();
            if (_trace == null || settings == null
                || (!settings.SendAutoNormalizeLineEndings && !settings.SendAutoDismissNotices)) return null;
            lock (_lock)
                if (_handledDialogs.TryGetValue(vs.Pid, out var previous) && previous == popup)
                    return "已尝试自动处理；若弹窗未关闭，请手动确认 / Already attempted; confirm manually if still open";
            try
            {
                if (Native.GetWindow(popup, Native.GW_OWNER) != vs.MainHwnd) return null;
                var dialog = AutomationElement.FromHandle(popup);
                if (dialog.Current.ProcessId != vs.Pid || dialog.Current.Name != title
                    || dialog.Current.IsOffscreen || !dialog.Current.IsEnabled) return null;
                var elements = dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                    .Cast<AutomationElement>().Where(e => IsDialogContent(e, dialog)).ToList();
                var combos = elements.Where(e => e.Current.ControlType == ControlType.ComboBox).ToList();
                string body = string.Join(" ", elements.Where(e => e.Current.ControlType == ControlType.Text)
                    .Select(e => e.Current.Name));
                var buttons = elements.Where(e => e.Current.ControlType == ControlType.Button).ToList();
                AutomationElement selected = null;
                string action = null;
                if (settings.SendAutoNormalizeLineEndings && combos.Count == 1
                    && combos[0].TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                {
                    string format = ((ValuePattern)value).Current.Value;
                    var yes = buttons.Where(e => IsLineEndingDialog(title, body, format, e.Current.Name, "No")).ToList();
                    var no = buttons.Where(e => IsLineEndingDialog(title, body, format, "Yes", e.Current.Name)).ToList();
                    if (yes.Count == 1 && no.Count == 1)
                    {
                        selected = yes[0];
                        action = "行尾标准化 / Normalize line endings: Windows (CR LF), Yes";
                    }
                }
                if (selected == null && settings.SendAutoDismissNotices)
                {
                    bool hasInput = elements.Any(e => !IsNoticeControl(e.Current.ControlType));
                    if (DialogPolicy.CanAcknowledge(title, body, buttons.Select(e => e.Current.Name).ToList(), hasInput))
                    {
                        selected = buttons[0];
                        action = "确认已知通知 / Acknowledge known notice: " + body;
                    }
                }
                if (selected == null)
                    return "未匹配安全自动处理规则，请手动确认 / No safe automatic rule matched; please confirm manually";
                if (!selected.Current.IsEnabled || selected.Current.IsOffscreen
                    || !selected.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return null;
                if (!Native.IsWindow(popup) || Native.IsWindowEnabled(vs.MainHwnd)
                    || Native.GetLastActivePopup(vs.MainHwnd) != popup) return null;

                // Invoke this exact button once; never send Enter or change an unknown dialog's selection.
                lock (_lock) _handledDialogs[vs.Pid] = popup;
                T("自动处理弹窗 / Auto-resolving dialog: " + title + " -> " + action + " -> " + selected.Current.Name);
                ((InvokePattern)invoke).Invoke();
                return "已自动处理：" + action + "；等待弹窗关闭后继续 / Waiting for the dialog to close";
            }
            catch (Exception ex) when (ex is ElementNotAvailableException || ex is InvalidOperationException
                || ex is COMException || ex is ArgumentException)
            {
                T("自动处理弹窗失败 / Automatic dialog handling failed: " + ex.Message);
                return "自动处理未成功，请手动确认 / Automatic handling failed; please confirm manually";
            }
        }

        private static bool IsNoticeControl(ControlType type) =>
            type == ControlType.Text || type == ControlType.Button || type == ControlType.Image
            || type == ControlType.Pane || type == ControlType.Group || type == ControlType.Separator;

        private static bool IsDialogContent(AutomationElement element, AutomationElement dialog)
        {
            var walker = TreeWalker.ControlViewWalker;
            for (var current = element; current != null; current = walker.GetParent(current))
            {
                if (Automation.Compare(current, dialog)) return true;
                if (current.Current.ControlType == ControlType.TitleBar) return false;
            }
            return false;
        }
    }
}
