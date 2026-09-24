using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace VSManager
{
    public partial class CopilotChat
    {
        [ThreadStatic] private static Func<bool> _queueGuard;
        [ThreadStatic] private static bool _queueTouched;

        /// <summary>只读活动探测，独立于监听及归档开关；不得写入日志中的草稿。/ Read-only activity probe independent of monitoring and archives; never logs drafts.</summary>
        public ManualChatObservation ObserveManualChat(VsInstance target)
        {
            try
            {
                if (target == null || !Native.IsWindow(target.MainHwnd)) return ManualChatObservation.Unknown;
                var pane = FindPane(target, strict: true);
                if (pane == null) return ManualChatObservation.PaneMissing;
                return ObserveManualInput(target, pane, null);
            }
            catch { return ManualChatObservation.Unknown; }
        }

        private ManualChatObservation ObserveManualInput(VsInstance target, AutomationElement pane, AutomationElement edit)
        {
            try
            {
                if (pane == null || pane.Current.ProcessId != target.Pid) return ManualChatObservation.Unknown;
                var ids = (_getSettings()?.BusyButtonIds ?? "CancelButton").Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string id in ids.Concat(new[] { "CancelButton" }).Distinct())
                    foreach (var button in RawFindAll(pane, TreeScope.Descendants, new AndCondition(IdCond(id.Trim()),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))))
                        if (!button.Current.IsOffscreen) return ManualChatObservation.Generating;
                edit = edit ?? LocateEdit(pane, target.Pid).Edit;
                if (edit == null || edit.Current.ProcessId != target.Pid) return ManualChatObservation.Unknown;
                string text = GetEditText(edit);
                bool focused = HasFocus(edit);
                if (focused && InputComposition(target) != false) return ManualChatObservation.Unknown;
                bool draft = text != null && text.Trim('\r', '\n').Length > 0;
                return ManualChatProtection.Classify(false, text != null, draft || AttachmentIds(pane).Count > 0, focused);
            }
            catch { return ManualChatObservation.Unknown; }
        }

        [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
        [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionStringW(IntPtr context, uint index, IntPtr buffer, uint length);

        // 仅读取合成长度，不读取按键或合成文本；不安装全局钩子。/ Reads composition length only, never keys or composition text; no global hooks.
        private static bool? InputComposition(VsInstance target)
        {
            IntPtr window = FocusHwnd(target.MainHwnd);
            Native.GetWindowThreadProcessId(window, out uint pid);
            if (pid != (uint)target.Pid) return null;
            IntPtr context = ImmGetContext(window);
            if (context == IntPtr.Zero) return false;
            try
            {
                int length = ImmGetCompositionStringW(context, 8, IntPtr.Zero, 0);
                return length == -2 ? (bool?)null : length > 0;
            }
            finally { ImmReleaseContext(window, context); }
        }

        /// <summary>仅队列使用；任何写入后的不确定结果均交用户核实，不能自动重发。/ Queue only; uncertain results after writing require manual verification, never automatic resend.</summary>
        public string SendQueued(VsInstance target, string text, IntPtr returnTo, bool background, IReadOnlyList<ChatImage> images, Func<bool> valid)
        {
            _queueGuard = valid ?? (() => false);
            _queueTouched = false;
            try
            {
                string result = Send(target, text, returnTo, background, images);
                return _queueTouched && !SendRetryPolicy.IsDelivered(result) ? ManualChatProtection.UncertainPrefix + result : result;
            }
            finally { _queueGuard = null; _queueTouched = false; }
        }

        private string GuardQueueSubmit(VsInstance target, AutomationElement pane, AutomationElement edit, string expected)
        {
            if (_queueGuard == null) return null;
            if (!_queueGuard() || HasCancel(pane) || InputComposition(target) != false
                || !PasteVerifier.IsConfirmed(PasteVerifier.Classify(expected, null, GetEditText(edit))))
                return ManualChatProtection.UncertainPrefix + "目标或输入在写入后发生变化，请检查草稿 / Target or input changed after writing; inspect the draft";
            return null;
        }

        private string GuardQueueInput(VsInstance target, AutomationElement pane, AutomationElement edit, bool writing = false)
        {
            if (_queueGuard == null) return null;
            if (!_queueGuard()) return ManualChatProtection.WaitPrefix + "目标或任务已变化 / Target or task changed";
            if (_queueTouched) return ManualChatProtection.UncertainPrefix + "输入已写入，请核实 / Input already written; verify before retry";
            if (_getSettings()?.WaitForManualChat != false)
            {
                var observation = ObserveManualInput(target, pane, edit);
                if (observation != ManualChatObservation.Idle) return ManualChatProtection.WaitPrefix + ManualChatProtection.Reason(observation);
            }
            if (writing) _queueTouched = true;
            return null;
        }
    }
}
