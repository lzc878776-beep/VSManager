using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace VSManager
{
    public partial class CopilotChat
    {
        [ThreadStatic] private static Func<bool> _queueGuard;
        [ThreadStatic] private static bool _queueTouched;
        private static readonly Dictionary<string, string> _inputDiagnostics = new Dictionary<string, string>();

        private static ManualChatObservation InputDiagnostic(VsInstance target, ManualChatObservation observation, string detail)
        {
            string key = target?.InstanceKey ?? "unknown";
            string summary = $"探测 / Probe: {observation}; {detail}";
            T(summary);
            lock (_inputDiagnostics)
            {
                if (_inputDiagnostics.TryGetValue(key, out var previous) && previous == summary) return observation;
                if (_inputDiagnostics.Count >= 128) _inputDiagnostics.Clear();
                _inputDiagnostics[key] = summary;
                using (var hash = SHA256.Create())
                {
                    string identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key)), 0, 6).Replace("-", "");
                    AppLog.Write("send-diagnostics.log", $"目标标识 / Target hash={identity}; {summary}");
                }
            }
            return observation;
        }

        /// <summary>只读活动探测，独立于监听及归档开关；不得写入日志中的草稿。/ Read-only activity probe independent of monitoring and archives; never logs drafts.</summary>
        public ManualChatObservation ObserveManualChat(VsInstance target)
        {
            try
            {
                if (target == null || !Native.IsWindow(target.MainHwnd)) return InputDiagnostic(target, ManualChatObservation.Unknown, "窗口无效 / Invalid window");
                var pane = FindPane(target, strict: true);
                if (pane == null) return InputDiagnostic(target, ManualChatObservation.PaneMissing, "窗格未找到 / Pane missing");
                return ObserveManualInput(target, pane, null);
            }
            catch (Exception ex) { return InputDiagnostic(target, ManualChatObservation.Unknown, "探测异常类别 / Probe error type=" + ex.GetType().Name); }
        }

        private ManualChatObservation ObserveManualInput(VsInstance target, AutomationElement pane, AutomationElement edit)
        {
            try
            {
                if (pane == null || pane.Current.ProcessId != target.Pid) return InputDiagnostic(target, ManualChatObservation.Unknown, "窗格身份不符 / Pane identity mismatch");
                var ids = (_getSettings()?.BusyButtonIds ?? "CancelButton").Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string id in ids.Concat(new[] { "CancelButton" }).Select(x => x.Trim()).Distinct())
                {
                    var buttonCond = new AndCondition(IdCond(id), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    var buttons = RawFindAll(pane, TreeScope.Children, buttonCond);
                    if (buttons.Count == 0) buttons = RawFindAll(pane, TreeScope.Descendants, buttonCond);
                    foreach (var button in buttons)
                        if (!button.Current.IsOffscreen) return InputDiagnostic(target, ManualChatObservation.Generating, "停止按钮可见 / Stop button visible");
                }
                edit = edit ?? LocateEdit(pane, target.Pid).Edit;
                if (edit == null || edit.Current.ProcessId != target.Pid) return InputDiagnostic(target, ManualChatObservation.Unknown, "输入不可定位 / Input unavailable");
                string text = GetEditText(edit);
                bool focused = HasFocus(edit);
                bool? composing = focused ? InputComposition(target) : false;
                bool draft = ManualChatProtection.HasDraft(text);
                bool attachmentsReadable = TryAttachmentIds(pane, out var attachments);
                var observation = composing != false ? ManualChatObservation.Unknown
                    : ManualChatProtection.Classify(false, text != null && attachmentsReadable,
                        draft || (attachmentsReadable && attachments.Count > 0), focused);
                return InputDiagnostic(target, observation,
                    $"原始长度 / RawLength={text?.Length ?? -1}; 规范化长度 / NormalizedLength={(text == null ? -1 : ManualChatProtection.NormalizeInput(text).Length)}; " +
                    $"可读取 / Readable={text != null}; 附件数 / Attachments={(attachmentsReadable ? attachments.Count : -1)}; 合成 / IME={composing?.ToString() ?? "Unknown"}; 焦点 / Focus={focused}");
            }
            catch (Exception ex) { return InputDiagnostic(target, ManualChatObservation.Unknown, "输入探测异常类别 / Input probe error type=" + ex.GetType().Name); }
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
                return ManualChatProtection.DeliveryResult(_queueTouched, result);
            }
            finally { _queueGuard = null; _queueTouched = false; }
        }

        private static bool TryAttachmentIds(AutomationElement pane, out HashSet<string> attachments)
        {
            attachments = null;
            try
            {
                if (pane == null || pane.Current.ProcessId <= 0) return false;
                // 原始树成功读取但无附件列表表示空；读取失败不能冒充空集合。/ A successful raw-tree read with no attachment list means empty; a failed read must not masquerade as empty.
                using (var cache = new CacheRequest { TreeFilter = Automation.RawViewCondition }.Activate())
                    attachments = AttachmentIds(pane);
                return true;
            }
            catch { return false; }
        }

        private string GuardQueueSubmit(VsInstance target, AutomationElement pane, AutomationElement edit,
            string expected, IEnumerable<string> expectedAttachments)
        {
            if (_queueGuard == null) return null;
            // 文字必须无附件，图片必须恰为本次确认加入的数量（附件列表重新渲染时 RuntimeId 会变，按数量比对）；未知状态也保留草稿。
            // Text requires no attachments; images require exactly the count this send confirmed (RuntimeIds change when the list re-renders, so compare counts); unknown state also preserves the draft.
            if (!_queueGuard() || !TryAttachmentIds(pane, out var attachments)
                || expectedAttachments == null || attachments.Count != expectedAttachments.Count()
                || HasCancel(pane) || InputComposition(target) != false
                || !PasteVerifier.IsConfirmed(PasteVerifier.Classify(expected, null, GetEditText(edit))))
                return ManualChatProtection.UncertainPrefix + "目标、输入或附件在写入后发生变化或无法确认，请检查草稿 / Target, input or attachments changed or cannot be confirmed after writing; inspect the draft";
            return null;
        }

        private string GuardQueueInput(VsInstance target, AutomationElement pane, AutomationElement edit, bool writing = false)
        {
            if (_queueGuard == null) return null;
            if (!_queueGuard()) return ManualChatProtection.WaitPrefix + "目标或任务已变化 / Target or task changed";
            if (_queueTouched) return ManualChatProtection.UncertainPrefix + "输入已写入，请核实 / Input already written; verify before retry";
            // 即使关闭提前让行，写入边界仍不能覆盖真实草稿。/ Disabling advance yielding never permits overwriting drafts at the write boundary.
            var observation = ObserveManualInput(target, pane, edit);
            if (observation != ManualChatObservation.Idle) return ManualChatProtection.WaitPrefix + ManualChatProtection.Reason(observation);
            if (writing) _queueTouched = true;
            return null;
        }
    }
}
