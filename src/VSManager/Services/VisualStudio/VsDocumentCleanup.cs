using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.CSharp.RuntimeBinder;

namespace VSManager
{
    public sealed class VsDocumentCleanupResult
    {
        public int InitialTabCount { get; internal set; }
        public int Eligible { get; internal set; }
        public int Attempted { get; internal set; }
        public int Closed { get; internal set; }
        public int SkippedUnsaved { get; internal set; }
        public int Failed { get; internal set; }
        public int Unknown { get; internal set; }
        public bool ThresholdExceeded { get; internal set; }
        public bool DebuggingOrUnknown { get; internal set; }
        public List<string> ClosedNames { get; } = new List<string>();
        public List<string> UnsavedNames { get; } = new List<string>();
        public List<string> Diagnostics { get; } = new List<string>();
        public string SummaryZh => $"文档清理：初始标签 {InitialTabCount}，已关闭 {Closed}，未保存跳过 {SkippedUnsaved}，失败 {Failed}，未知 {Unknown}";
        public string SummaryEn => $"Document cleanup: initial tabs {InitialTabCount}, closed {Closed}, unsaved skipped {SkippedUnsaved}, failed {Failed}, unknown {Unknown}";
    }

    internal sealed class DocumentTabTarget
    {
        internal object Document;
        internal object Window;
        internal string Title;
        internal string FullName;
        internal IntPtr Hwnd;
    }

    internal interface IDocumentTabFallback
    {
        bool TryClose(VsInstance vs, DocumentTabTarget target, Func<bool> mayInvoke, out string reason);
    }

    /// <summary>
    /// 仅在调用方的 DTE STA 线程上串行清理；不启动超时后仍会关闭窗口的后台工作。
    /// Runs serially on the caller's DTE STA; never starts background work that could close windows after a timeout.
    /// </summary>
    public static class VsDocumentCleanup
    {
        public const int DefaultThreshold = 10;
        internal const int MaximumTabs = 512;
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

        public static VsDocumentCleanupResult Run(VsInstance vs, int threshold, Action<string> log) =>
            Run(vs, threshold, log, new DocumentTabUiaFallback(), DocumentTabUiaFallback.OwnerReady);

        internal static VsDocumentCleanupResult Run(VsInstance vs, int threshold, Action<string> log,
            IDocumentTabFallback fallback, Func<VsInstance, bool> ownerReady)
        {
            var result = new VsDocumentCleanupResult();
            var clock = Stopwatch.StartNew();
            Action<string> report = message =>
            {
                string line = $"初始标签 / Initial tabs={result.InitialTabCount}; {message}";
                result.Diagnostics.Add(line);
                try { log?.Invoke(line); }
                catch (Exception ex) when (Expected(ex)) { }
            };
            if (vs?.Dte == null)
            {
                result.Unknown++;
                report("拒绝清理：DTE 不可用，保存状态未知 / Refused: DTE unavailable; saved state unknown");
                return result;
            }
            try
            {
                if (!DesignMode(vs))
                {
                    result.DebuggingOrUnknown = true;
                    report("跳过：正在调试或调试状态未知 / Skipped: debugging or unknown debugger state");
                    return result;
                }
                var targets = Snapshot(vs, clock, result, report);
                if (targets == null) return result;
                result.InitialTabCount = targets.Count;
                result.ThresholdExceeded = targets.Count > Math.Max(0, threshold);
                report("检查阈值 / Threshold=" + Math.Max(0, threshold));
                if (!result.ThresholdExceeded) return result;
                foreach (var target in targets)
                {
                    if (clock.Elapsed >= Budget)
                    {
                        result.Unknown++;
                        report("停止：达到时间预算 / Stopped: time budget exhausted");
                        break;
                    }
                    if (!DesignMode(vs))
                    {
                        result.DebuggingOrUnknown = true;
                        report("停止：调试模式已改变或未知 / Stopped: debugger mode changed or unknown");
                        break;
                    }
                    bool? saved = Saved(target.Document);
                    if (saved != true)
                    {
                        if (saved == false) { result.SkippedUnsaved++; result.UnsavedNames.Add(target.Title); }
                        else result.Unknown++;
                        report(Describe(target, saved) + "跳过 / Skipped");
                        continue;
                    }
                    result.Eligible++;
                    if (!Ready(vs, target, clock, ownerReady))
                    {
                        RecordNotReady(target, result, report);
                        continue;
                    }
                    result.Attempted++;
                    string method = "DTE Window.Close(Prompt=0)";
                    Exception closeError = null;
                    try { ((dynamic)target.Window).Close(0); }
                    catch (Exception ex) when (Expected(ex)) { closeError = ex; }
                    bool? absent = Absent(vs, target, clock);
                    if (absent == true)
                    {
                        Closed(target, result, report, method);
                        continue;
                    }
                    if (Saved(target.Document) == false)
                    {
                        RecordNotReady(target, result, report);
                        continue;
                    }
                    if (absent == false)
                    {
                        report(Describe(target, Saved(target.Document)) + method + "; 失败 / Failed: " +
                            (closeError == null ? "关闭未生效 / Close had no effect" : Failure(closeError)));
                        if (UniqueTitle(targets, target) && FallbackReady(vs, target, clock, ownerReady))
                        {
                            string reason;
                            method = "UIA 文档标签关闭 / UIA document-tab close";
                            try
                            {
                                fallback.TryClose(vs, target,
                                    () => FallbackReady(vs, target, clock, ownerReady), out reason);
                                report(Describe(target, Saved(target.Document)) + method + "; " + reason);
                            }
                            catch (Exception ex) when (Expected(ex))
                            {
                                report(Describe(target, Saved(target.Document)) + method + "; 失败 / Failed: " + ex.GetType().Name);
                            }
                            if (Absent(vs, target, clock) == true)
                            {
                                Closed(target, result, report, method);
                                continue;
                            }
                        }
                        else report(Describe(target, Saved(target.Document)) + "拒绝 UIA：身份歧义或状态不安全 / UIA refused: ambiguous identity or unsafe state");
                    }
                    result.Failed++;
                    report(Describe(target, Saved(target.Document)) + method + "; 未确认关闭 / Closure not confirmed");
                }
            }
            catch (Exception ex) when (Expected(ex))
            {
                result.Failed++;
                report("清理失败，继续发送任务 / Cleanup failed; continue task sending: " + ex.GetType().Name);
            }
            return result;
        }

        private static List<DocumentTabTarget> Snapshot(VsInstance vs, Stopwatch clock, VsDocumentCleanupResult result, Action<string> report)
        {
            var targets = new List<DocumentTabTarget>();
            int inspected = 0;
            foreach (object document in (IEnumerable)((dynamic)vs.Dte).Documents)
            {
                if (++inspected > MaximumTabs || clock.Elapsed >= Budget) return Incomplete(result, report);
                try
                {
                    foreach (object window in (IEnumerable)((dynamic)document).Windows)
                    {
                        if (++inspected > MaximumTabs * 2 || clock.Elapsed >= Budget) return Incomplete(result, report);
                        if (!IsDocumentWindow(document, window))
                        {
                            result.Unknown++;
                            report("跳过非文档或身份未知窗口 / Skipped non-document or unknown-identity window");
                            continue;
                        }
                        if (targets.Any(t => Same(t.Window, window))) continue;
                        dynamic w = window;
                        dynamic d = document;
                        var target = new DocumentTabTarget { Document = document, Window = window };
                        target.Title = (string)w.Caption;
                        try { target.FullName = (string)d.FullName; }
                        catch (Exception ex) when (Expected(ex)) { report(Describe(target, Saved(document)) + "路径读取失败，禁用 UIA 后备 / Path unavailable; UIA fallback disabled: " + Failure(ex)); }
                        try { target.Hwnd = new IntPtr((long)w.HWnd); }
                        catch (Exception ex) when (Expected(ex)) { report(Describe(target, Saved(document)) + "句柄读取失败，禁用 UIA 后备 / HWND unavailable; UIA fallback disabled: " + Failure(ex)); }
                        targets.Add(target);
                        if (targets.Count > MaximumTabs) return Incomplete(result, report);
                    }
                }
                catch (Exception ex) when (Expected(ex))
                {
                    report("读取文档窗口失败 / Cannot read document windows: " + Failure(ex));
                    return Incomplete(result, report);
                }
            }
            return targets;
        }

        private static List<DocumentTabTarget> Incomplete(VsDocumentCleanupResult result, Action<string> report)
        {
            result.Unknown++;
            report("拒绝清理：标签枚举不完整 / Refused: incomplete document-tab enumeration");
            return null;
        }

        private static void RecordNotReady(DocumentTabTarget target, VsDocumentCleanupResult result, Action<string> report)
        {
            bool? saved = Saved(target.Document);
            if (saved == false) { result.SkippedUnsaved++; result.UnsavedNames.Add(target.Title); }
            else result.Unknown++;
            report(Describe(target, saved) + "跳过：状态改变、窗口身份未知或模态窗口 / Skipped: changed state, unknown window identity or modal window");
        }

        private static string Describe(DocumentTabTarget target, bool? saved) =>
            "标题 / Title=" + target.Title + "; 已保存 / Saved=" + (saved.HasValue ? saved.Value.ToString() : "未知 / Unknown") + "; ";

        private static void Closed(DocumentTabTarget target, VsDocumentCleanupResult result, Action<string> report, string method)
        {
            result.Closed++;
            result.ClosedNames.Add(target.Title);
            report(Describe(target, true) + method + "; 已确认关闭 / Confirmed closed");
        }

        private static bool UniqueTitle(List<DocumentTabTarget> targets, DocumentTabTarget target) =>
            !string.IsNullOrEmpty(target.Title) && targets.Count(t => string.Equals(t.Title, target.Title, StringComparison.OrdinalIgnoreCase)) == 1;

        private static bool Ready(VsInstance vs, DocumentTabTarget target, Stopwatch clock, Func<VsInstance, bool> ownerReady)
        {
            try
            {
                return clock.Elapsed < Budget && ownerReady(vs) && IsDocumentWindow(target.Document, target.Window)
                    && Absent(vs, target, clock) == false && Saved(target.Document) == true
                    && DesignMode(vs) && ownerReady(vs) && clock.Elapsed < Budget;
            }
            catch (Exception ex) when (Expected(ex)) { return false; }
        }

        private static bool FallbackReady(VsInstance vs, DocumentTabTarget target, Stopwatch clock, Func<VsInstance, bool> ownerReady)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(target.FullName) || target.Hwnd == IntPtr.Zero
                    || (string)((dynamic)target.Document).FullName != target.FullName
                    || (string)((dynamic)target.Window).Caption != target.Title
                    || new IntPtr((long)((dynamic)target.Window).HWnd) != target.Hwnd) return false;
                int inspected = 0, matches = 0;
                foreach (object document in (IEnumerable)((dynamic)vs.Dte).Documents)
                {
                    if (++inspected > MaximumTabs || clock.Elapsed >= Budget) return false;
                    foreach (object window in (IEnumerable)((dynamic)document).Windows)
                    {
                        if (++inspected > MaximumTabs * 2 || clock.Elapsed >= Budget) return false;
                        if (!IsDocumentWindow(document, window)) continue;
                        if (string.Equals((string)((dynamic)window).Caption, target.Title, StringComparison.OrdinalIgnoreCase)) matches++;
                    }
                }
                return matches == 1 && Ready(vs, target, clock, ownerReady);
            }
            catch (Exception ex) when (Expected(ex)) { return false; }
        }

        private static bool DesignMode(VsInstance vs)
        {
            try { return (int)((dynamic)vs.Dte).Debugger.CurrentMode == 1; }
            catch (Exception ex) when (Expected(ex)) { return false; }
        }

        private static bool? Saved(object document)
        {
            try { return (bool)((dynamic)document).Saved; }
            catch (Exception ex) when (Expected(ex)) { return null; }
        }

        private static bool IsDocumentWindow(object document, object window)
        {
            try
            {
                if (!Same(document, (object)((dynamic)window).Document)) return false;
                int type = (int)((dynamic)window).Type;
                // EnvDTE：代码窗口=0，设计器=1，文档=16；仍要求文档对象身份一致。/ EnvDTE: code=0, designer=1, document=16; matching document identity is still required.
                return type == 0 || type == 1 || type == 16;
            }
            catch (Exception ex) when (Expected(ex)) { return false; }
        }

        private static string Failure(Exception ex) => ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + ")";

        private static bool Same(object left, object right) => ReferenceEquals(left, right);

        private static bool? Absent(VsInstance vs, DocumentTabTarget target, Stopwatch clock)
        {
            try
            {
                int count = 0;
                foreach (object document in (IEnumerable)((dynamic)vs.Dte).Documents)
                {
                    if (++count > MaximumTabs || clock.Elapsed >= Budget) return null;
                    foreach (object window in (IEnumerable)((dynamic)document).Windows)
                    {
                        if (++count > MaximumTabs * 2 || clock.Elapsed >= Budget) return null;
                        if (Same(window, target.Window)) return false;
                    }
                }
                return true;
            }
            catch (Exception ex) when (Expected(ex)) { return null; }
        }

        internal static bool Expected(Exception ex) => ex is COMException || ex is InvalidOperationException
            || ex is ArgumentException || ex is RuntimeBinderException || ex is InvalidCastException
            || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception
            || ex is UnauthorizedAccessException || ex is System.Security.SecurityException
            || ex is TimeoutException || ex is NullReferenceException || ex is InvalidComObjectException
            || ex is ElementNotAvailableException || ex is ElementNotEnabledException;
    }

    internal sealed class DocumentTabUiaFallback : IDocumentTabFallback
    {
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        internal static bool OwnerReady(VsInstance vs)
        {
            if (vs == null || vs.Pid <= 0 || !IsWindow(vs.MainHwnd) || !IsWindowEnabled(vs.MainHwnd)) return false;
            GetWindowThreadProcessId(vs.MainHwnd, out uint pid);
            IntPtr popup = GetLastActivePopup(vs.MainHwnd);
            return pid == (uint)vs.Pid && (popup == vs.MainHwnd || popup == IntPtr.Zero || !IsWindowVisible(popup));
        }

        public bool TryClose(VsInstance vs, DocumentTabTarget target, Func<bool> mayInvoke, out string reason)
        {
            reason = "拒绝：无法证明文档标签与关闭按钮身份 / Refused: document tab and close-button identity not proven";
            try
            {
                if (!OwnerReady(vs) || target.Hwnd == IntPtr.Zero || target.Hwnd == vs.MainHwnd
                    || string.IsNullOrWhiteSpace(target.FullName)) return false;
                GetWindowThreadProcessId(target.Hwnd, out uint pid);
                if (pid != (uint)vs.Pid) return false;
                var clock = Stopwatch.StartNew();
                var walker = TreeWalker.ControlViewWalker;
                AutomationElement current = AutomationElement.FromHandle(target.Hwnd);
                AutomationElement tab = null;
                AutomationElement well = null;
                bool rootFound = false;
                for (int depth = 0; current != null && depth < 32 && clock.ElapsedMilliseconds < 1500; depth++)
                {
                    var info = current.Current;
                    if (info.ProcessId != vs.Pid) return false;
                    if (tab == null && info.ControlType == ControlType.TabItem) tab = current;
                    else if (tab != null && well == null)
                    {
                        if (info.ControlType != ControlType.Tab) return false;
                        well = current;
                    }
                    if (info.NativeWindowHandle == vs.MainHwnd.ToInt64()) { rootFound = true; break; }
                    current = walker.GetParent(current);
                }
                // DTE 文档 HWND 必须位于标签内部；不在主窗口中搜索任意“关闭”按钮。
                // The DTE document HWND must be inside the tab; never search the main window for arbitrary Close buttons.
                if (!rootFound || tab == null || well == null || tab.Current.Name != target.Title
                    || !string.Equals(tab.Current.HelpText, target.FullName, StringComparison.OrdinalIgnoreCase)) return false;
                int matches = 0, visited = 0;
                for (var child = walker.GetFirstChild(well); child != null; child = walker.GetNextSibling(child))
                {
                    if (++visited > 128 || clock.ElapsedMilliseconds >= 1500) return false;
                    if (child.Current.ControlType == ControlType.TabItem && string.Equals(child.Current.Name, target.Title, StringComparison.OrdinalIgnoreCase)) matches++;
                }
                if (matches != 1) return false;
                AutomationElement close = null;
                for (var child = walker.GetFirstChild(tab); child != null; child = walker.GetNextSibling(child))
                {
                    if (++visited > 160 || clock.ElapsedMilliseconds >= 1500) return false;
                    var info = child.Current;
                    if (info.ControlType != ControlType.Button || info.AutomationId != "CloseButton") continue;
                    if (close != null || info.ProcessId != vs.Pid || !info.IsEnabled || info.IsOffscreen) return false;
                    close = child;
                }
                if (close == null || !close.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern)) return false;
                if (clock.ElapsedMilliseconds >= 1500 || !OwnerReady(vs) || !mayInvoke()) return false;
                ((InvokePattern)pattern).Invoke();
                reason = "已请求关闭，需 DTE 确认 / Close requested; DTE confirmation required";
                return true;
            }
            catch (Exception ex) when (VsDocumentCleanup.Expected(ex) || ex is ElementNotAvailableException || ex is ElementNotEnabledException)
            {
                reason = "UIA 失败 / UIA failed: " + ex.GetType().Name;
                return false;
            }
        }
    }
}
