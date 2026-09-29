using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using Microsoft.CSharp.RuntimeBinder;

namespace VSManager
{
    /// <summary>物理屏幕坐标中的工作区目标。/ Workspace targets in physical screen coordinates.</summary>
    public sealed class VsWorkspacePlacement
    {
        public VsInstance Vs;
        public string Name;
        public Rectangle MainBounds, CopilotBounds, OutputBounds, ErrorListBounds, SolutionExplorerBounds;
    }

    /// <summary>
    /// 仅在串行 DteWorker STA 上调用；快照仅保留于当前进程。未知状态不会修改。
    /// Call only on the serialized DteWorker STA; snapshots are process-local. Unknown states are not modified.
    /// </summary>
    public static class VsWorkspaceLayout
    {
        private static readonly object Gate = new object();
        private static readonly WorkspaceLayoutEngine Engine = new WorkspaceLayoutEngine(new WindowsWorkspaceBackend());
        public static int SavedCount { get { lock (Gate) return Engine.SavedCount; } }

        public static string Arrange(IList<VsWorkspacePlacement> placements, bool includeOutput, bool includeErrorList, string keyword, bool includeSolutionExplorer = false)
        {
            RequireSta();
            lock (Gate) return Engine.Arrange(placements, includeOutput, includeErrorList, keyword, includeSolutionExplorer);
        }

        public static string Restore(IList<VsInstance> live)
        {
            RequireSta();
            lock (Gate) return Engine.Restore(live);
        }

        private static void RequireSta()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("必须使用 DteWorker STA / Use DteWorker STA");
        }
    }

    internal enum WorkspacePane { Copilot, Output, ErrorList, SolutionExplorer }

    internal sealed class WorkspacePaneSnapshot
    {
        internal WorkspacePane Pane;
        internal string Kind, Keyword;
        internal bool Floating, Linkable, AutoHides, Visible;
        internal Native.WINDOWPLACEMENT Placement;
    }

    /// <summary>测试只替换此边界，不访问用户的 VS。/ Tests replace this boundary without accessing the user's VS.</summary>
    internal interface IWorkspaceLayoutBackend
    {
        void Validate(VsInstance vs);
        Native.WINDOWPLACEMENT CaptureMain(VsInstance vs);
        WorkspacePaneSnapshot CapturePane(VsInstance vs, WorkspacePane pane, string keyword);
        void PlaceMain(VsInstance vs, Rectangle bounds);
        void PlacePane(VsInstance vs, WorkspacePaneSnapshot pane, Rectangle bounds);
        void VerifyMain(VsInstance vs, Rectangle bounds);
        void VerifyPane(VsInstance vs, WorkspacePaneSnapshot pane, Rectangle bounds);
        void RestoreMain(VsInstance vs, Native.WINDOWPLACEMENT placement);
        void RestorePane(VsInstance vs, WorkspacePaneSnapshot pane);
    }

    internal sealed class WorkspaceLayoutEngine
    {
        private sealed class Saved
        {
            internal int Pid;
            internal long StartTicks;
            internal IntPtr Hwnd;
            internal string Name;
            internal Native.WINDOWPLACEMENT Main;
            internal bool MainPending;
            internal readonly Dictionary<WorkspacePane, WorkspacePaneSnapshot> Panes = new Dictionary<WorkspacePane, WorkspacePaneSnapshot>();
        }

        private readonly IWorkspaceLayoutBackend backend;
        private readonly List<Saved> saved = new List<Saved>();
        internal WorkspaceLayoutEngine(IWorkspaceLayoutBackend backend) { this.backend = backend; }
        internal int SavedCount => saved.Count;

        internal static bool Expected(Exception ex) => ex is COMException || ex is Win32Exception ||
            ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException ||
            ex is UnauthorizedAccessException || ex is RuntimeBinderException || ex is ElementNotAvailableException ||
            ex is InvalidCastException || ex is InvalidComObjectException;

        // VS 的 DTE 调用在接口 QueryInterface 失败（E_NOINTERFACE，如文档尚未完全加载时的 IVsPersistDocData）时抛 InvalidCastException；按单项失败报告，不让整个布局中断。
        // VS DTE calls throw InvalidCastException when a QueryInterface fails (E_NOINTERFACE, e.g. IVsPersistDocData on a document not fully loaded); report it per item instead of aborting the whole layout.
        internal static string Reason(Exception ex) => ex is InvalidCastException
            ? "VS 自动化接口不可用（E_NOINTERFACE，常见于尚未完全加载的文档窗口）/ VS automation interface unavailable (E_NOINTERFACE, typically a document not fully loaded): " + ex.Message
            : ex.Message;

        internal string Arrange(IList<VsWorkspacePlacement> placements, bool output, bool errors, string keyword, bool solution = false)
        {
            var lines = new List<string>();
            var seen = new HashSet<int>();
            foreach (var target in placements ?? new List<VsWorkspacePlacement>())
            {
                string name = target?.Name ?? "VS";
                var requested = new List<Tuple<WorkspacePane, Rectangle>>();
                if (target != null)
                {
                    // 空矩形表示该窗格不移动（AI 自定义布局可只安排部分窗格）/ An empty rectangle leaves that pane alone (custom AI layouts may place only some panes)
                    if (!target.CopilotBounds.IsEmpty) requested.Add(Tuple.Create(WorkspacePane.Copilot, target.CopilotBounds));
                    if (output && !target.OutputBounds.IsEmpty) requested.Add(Tuple.Create(WorkspacePane.Output, target.OutputBounds));
                    if (errors && !target.ErrorListBounds.IsEmpty) requested.Add(Tuple.Create(WorkspacePane.ErrorList, target.ErrorListBounds));
                    if (solution && !target.SolutionExplorerBounds.IsEmpty) requested.Add(Tuple.Create(WorkspacePane.SolutionExplorer, target.SolutionExplorerBounds));
                }
                try
                {
                    if (target?.Vs == null) throw new InvalidOperationException("目标为空 / missing target");
                    if (!seen.Add(target.Vs.Pid)) throw new InvalidOperationException("重复目标 / duplicate target");
                    CheckBounds(target.MainBounds);
                    backend.Validate(target.Vs);
                    var state = saved.FirstOrDefault(s => s.Pid == target.Vs.Pid && s.StartTicks == target.Vs.StartTicks);
                    if (state != null && state.Hwnd != target.Vs.MainHwnd)
                        throw new InvalidOperationException("主窗口身份已改变 / main window identity changed");
                    if (state == null)
                    {
                        state = new Saved { Pid = target.Vs.Pid, StartTicks = target.Vs.StartTicks, Hwnd = target.Vs.MainHwnd,
                            Name = name, Main = backend.CaptureMain(target.Vs) };
                        saved.Add(state);
                    }

                    // 在主窗口移动影响浮动窗格之前，先读取所有窗格。/ Capture all panes before moving their owner.
                    var ready = new List<Tuple<WorkspacePaneSnapshot, Rectangle>>();
                    foreach (var request in requested)
                    {
                        try
                        {
                            CheckBounds(request.Item2);
                            backend.Validate(target.Vs);
                            var current = backend.CapturePane(target.Vs, request.Item1, keyword);
                            if (state.Panes.TryGetValue(request.Item1, out var original))
                            {
                                if (Guid.Parse(original.Kind) != Guid.Parse(current.Kind))
                                    throw new InvalidOperationException("窗格身份不同；请先还原 / pane identity changed; restore first");
                            }
                            else state.Panes.Add(request.Item1, current);
                            ready.Add(Tuple.Create(current, request.Item2));
                        }
                        catch (Exception ex) when (Expected(ex)) { lines.Add(Result(name, request.Item1.ToString(), "跳过 / skipped: " + Reason(ex))); }
                    }

                    state.MainPending = true;
                    bool mainPlaced = false;
                    var placed = new List<Tuple<WorkspacePaneSnapshot, Rectangle>>();
                    try
                    {
                        backend.Validate(target.Vs);
                        backend.PlaceMain(target.Vs, target.MainBounds);
                        mainPlaced = true;
                    }
                    catch (Exception ex) when (Expected(ex)) { lines.Add(Result(name, "Main", "失败，保留快照 / failed, snapshot retained: " + Reason(ex))); }
                    foreach (var pane in ready)
                    {
                        try
                        {
                            backend.Validate(target.Vs);
                            backend.PlacePane(target.Vs, pane.Item1, pane.Item2);
                            placed.Add(pane);
                        }
                        catch (Exception ex) when (Expected(ex)) { lines.Add(Result(name, pane.Item1.Pane.ToString(), "失败，保留快照 / failed, snapshot retained: " + Reason(ex))); }
                    }
                    // 后续窗格可能共享宿主或触发缩放，最终再次验证全部目标。/ Later panes may share hosts or trigger resizing; verify all final targets again.
                    if (mainPlaced)
                    {
                        try
                        {
                            backend.VerifyMain(target.Vs, target.MainBounds);
                            lines.Add(Result(name, "Main", "已验证 / verified"));
                        }
                        catch (Exception ex) when (Expected(ex)) { lines.Add(Result(name, "Main", "最终验证失败，保留快照 / final verification failed, snapshot retained: " + Reason(ex))); }
                    }
                    foreach (var pane in placed)
                    {
                        try
                        {
                            backend.VerifyPane(target.Vs, pane.Item1, pane.Item2);
                            lines.Add(Result(name, pane.Item1.Pane.ToString(), "已验证 / verified"));
                        }
                        catch (Exception ex) when (Expected(ex)) { lines.Add(Result(name, pane.Item1.Pane.ToString(), "最终验证失败，保留快照 / final verification failed, snapshot retained: " + Reason(ex))); }
                    }
                }
                catch (Exception ex) when (Expected(ex))
                {
                    lines.Add(Result(name, "Main", "跳过 / skipped: " + Reason(ex)));
                    foreach (var pane in requested) lines.Add(Result(name, pane.Item1.ToString(), "跳过：目标不安全 / skipped: target unsafe"));
                }
            }
            return (lines.Count == 0 ? "没有目标 / No targets" : string.Join("\n", lines)) +
                "\n待还原实例 / Saved instances: " + SavedCount;
        }

        internal string Restore(IList<VsInstance> live)
        {
            var lines = new List<string>();
            foreach (var state in saved.ToArray())
            {
                var vs = live?.FirstOrDefault(v => v != null && v.Pid == state.Pid && v.StartTicks == state.StartTicks && v.MainHwnd == state.Hwnd);
                try
                {
                    if (vs == null) throw new InvalidOperationException("没有身份匹配的实例 / no identity-matching instance");
                    backend.Validate(vs);
                }
                catch (Exception ex) when (Expected(ex))
                {
                    lines.Add(Result(state.Name, "Main", "保留待重试 / retained for retry: " + Reason(ex)));
                    foreach (var pane in state.Panes.Keys) lines.Add(Result(state.Name, pane.ToString(), "保留待重试 / retained for retry"));
                    continue;
                }
                // 先还原主窗口，避免主窗口的跨屏缩放再次移动已还原的浮动窗格。/ Restore the owner first so its DPI transition cannot move restored floating panes.
                if (state.MainPending)
                {
                    try
                    {
                        backend.Validate(vs);
                        backend.RestoreMain(vs, state.Main);
                        state.MainPending = false;
                        lines.Add(Result(state.Name, "Main", "已还原并验证 / restored and verified"));
                    }
                    catch (Exception ex) when (Expected(ex)) { lines.Add(Result(state.Name, "Main", "还原失败，保留待重试 / restore failed, retained for retry: " + Reason(ex))); }
                }
                foreach (var pane in state.Panes.Values.ToArray())
                {
                    try
                    {
                        backend.Validate(vs);
                        backend.RestorePane(vs, pane);
                        if (!state.MainPending) state.Panes.Remove(pane.Pane);
                        lines.Add(Result(state.Name, pane.Pane.ToString(), "已还原并验证 / restored and verified" +
                            (state.MainPending ? "；主窗口待重试，仍保留窗格快照 / pane snapshot retained until owner restoration succeeds" : "") +
                            (pane.Floating ? "" : "；停靠组位置由 VS 决定 / docking group position is managed by VS")));
                    }
                    catch (Exception ex) when (Expected(ex)) { lines.Add(Result(state.Name, pane.Pane.ToString(), "还原失败，保留待重试 / restore failed, retained for retry: " + Reason(ex))); }
                }
                if (state.Panes.Count == 0 && !state.MainPending) saved.Remove(state);
            }
            return (lines.Count == 0 ? "没有可还原布局 / Nothing to restore" : string.Join("\n", lines)) +
                "\n待还原实例 / Saved instances: " + SavedCount;
        }

        private static string Result(string name, string pane, string text) => "· " + name + " / " + pane + ": " + text;
        private static void CheckBounds(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.X + bounds.Width > int.MaxValue || (long)bounds.Y + bounds.Height > int.MaxValue)
                throw new ArgumentException("无效物理矩形 / invalid physical bounds");
        }
    }

    internal sealed class WindowsWorkspaceBackend : IWorkspaceLayoutBackend
    {
        internal const string ErrorListKind = VsService.ErrorListKind;
        private const uint SwpAsyncWindowPos = 0x4000;
        private const int WpfAsyncWindowPlacement = 0x0004;
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        public void Validate(VsInstance vs)
        {
            if (vs == null || vs.Pid <= 0 || vs.StartTicks <= 0 || vs.Dte == null)
                throw new InvalidOperationException("缺少进程身份或 DTE / process identity or DTE unavailable");
            using (var process = Process.GetProcessById(vs.Pid))
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != vs.StartTicks)
                    throw new InvalidOperationException("进程身份已改变 / process identity changed");
            ValidateHwnd(vs, vs.MainHwnd);
            if (DteMainHandle(vs) != vs.MainHwnd || GetAncestor(vs.MainHwnd, 2) != vs.MainHwnd ||
                Native.GetWindow(vs.MainHwnd, Native.GW_OWNER) != IntPtr.Zero)
                throw new InvalidOperationException("DTE 主窗口不匹配 / DTE main window mismatch");
            if (!Native.IsWindowEnabled(vs.MainHwnd))
                throw new InvalidOperationException("主窗口存在模态阻塞 / main window is modal-blocked");
            var popup = Native.GetLastActivePopup(vs.MainHwnd);
            if (popup != vs.MainHwnd && popup != IntPtr.Zero && Native.IsWindowVisible(popup) && Native.GetClass(popup) == "#32770")
                throw new InvalidOperationException("存在模态对话框 / modal dialog present");
        }

        /// <summary>
        /// DTE.MainWindow 的句柄；该调用因 E_NOINTERFACE 失败时，退回到已按进程、顶层与无所有者校验过的发现句柄。
        /// Handle of DTE.MainWindow; when that call fails with E_NOINTERFACE, fall back to the discovered handle, which is still checked for process, top level and no owner.
        /// </summary>
        private static IntPtr DteMainHandle(VsInstance vs)
        {
            try { return ToHandle(((dynamic)vs.Dte).MainWindow.HWnd); }
            catch (InvalidCastException) { return vs.MainHwnd; }
        }

        public Native.WINDOWPLACEMENT CaptureMain(VsInstance vs)
        {
            Validate(vs);
            if (!Native.IsWindowVisible(vs.MainHwnd))
                throw new InvalidOperationException("不修改隐藏的主窗口 / hidden main window left unchanged");
            using (new PhysicalCoordinates()) return Placement(vs.MainHwnd);
        }

        public WorkspacePaneSnapshot CapturePane(VsInstance vs, WorkspacePane pane, string keyword)
        {
            Validate(vs);
            dynamic window = Find(vs, pane, keyword, null);
            if (window == null && pane != WorkspacePane.Copilot)
            {
                // 内置窗格（输出 / 错误列表 / 解决方案资源管理器）尚未创建时按 GUID 创建；快照记录创建后的可见状态，还原时恢复为隐藏。
                // Built-in panes (Output / Error List / Solution Explorer) not yet created are created by GUID; the snapshot records the post-creation visibility so restore hides them again.
                ((dynamic)vs.Dte).Windows.Item(KindOf(pane));
                window = Find(vs, pane, keyword, null);
            }
            if (window == null)
                throw new InvalidOperationException("未找到窗格，原状态未知，未执行打开命令 / pane missing; original state unknown, no open command executed");
            var snapshot = new WorkspacePaneSnapshot { Pane = pane, Keyword = keyword, Kind = (string)window.ObjectKind,
                Floating = (bool)window.IsFloating, Linkable = (bool)window.Linkable,
                AutoHides = (bool)window.AutoHides, Visible = (bool)window.Visible };
            CheckTool(window, snapshot.Kind);
            if (snapshot.Floating)
            {
                using (new PhysicalCoordinates()) snapshot.Placement = Placement(FindHost(vs, window, snapshot.Kind));
                if (IsMinimized(snapshot.Placement.showCmd))
                    throw new InvalidOperationException("浮动窗格已最小化，跳过 / minimized floating pane skipped");
            }
            return snapshot;
        }

        public void PlaceMain(VsInstance vs, Rectangle bounds) => Move(vs, vs.MainHwnd, bounds, null, null);

        public void PlacePane(VsInstance vs, WorkspacePaneSnapshot pane, Rectangle bounds)
        {
            dynamic window = RequirePane(vs, pane);
            Set(vs, window, pane.Kind, "AutoHides", false);
            Set(vs, window, pane.Kind, "Linkable", true);
            Set(vs, window, pane.Kind, "IsFloating", true);
            Set(vs, window, pane.Kind, "Visible", true);
            var host = FindHost(vs, window, pane.Kind);
            Move(vs, host, bounds, window, pane.Kind);
            if (!(bool)window.Visible || !(bool)window.IsFloating)
                throw new InvalidOperationException("窗格可见或浮动状态未生效 / pane visibility or floating state not honored");
        }

        public void VerifyMain(VsInstance vs, Rectangle bounds)
        {
            Validate(vs);
            VerifyBounds(vs.MainHwnd, bounds);
        }

        public void VerifyPane(VsInstance vs, WorkspacePaneSnapshot pane, Rectangle bounds)
        {
            dynamic window = RequirePane(vs, pane);
            if (!(bool)window.Visible || !(bool)window.IsFloating)
                throw new InvalidOperationException("窗格不可见或未浮动 / pane not visible or not floating");
            VerifyBounds(FindHost(vs, window, pane.Kind), bounds);
        }

        private static void VerifyBounds(IntPtr hwnd, Rectangle bounds)
        {
            using (new PhysicalCoordinates())
            {
                if (!Native.GetWindowRect(hwnd, out var actual) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) ||
                    !BoundsMatch(Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom), bounds))
                    throw new InvalidOperationException("物理边界或可见状态未生效（DPI/最小尺寸限制）/ physical bounds or visibility not honored (DPI/minimum size): "
                        + $"requested={bounds}; actual={Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom)}");
            }
        }

        public void RestoreMain(VsInstance vs, Native.WINDOWPLACEMENT placement)
        {
            RestorePlacement(vs, vs.MainHwnd, placement, null, null);
            if (!Native.IsWindowVisible(vs.MainHwnd) || Native.IsIconic(vs.MainHwnd) != IsMinimized(placement.showCmd))
                throw new InvalidOperationException("主窗口可见状态未恢复 / main visibility not restored");
        }

        public void RestorePane(VsInstance vs, WorkspacePaneSnapshot pane)
        {
            dynamic window = RequirePane(vs, pane);
            Set(vs, window, pane.Kind, "AutoHides", false);
            Set(vs, window, pane.Kind, "Linkable", true);
            Set(vs, window, pane.Kind, "IsFloating", pane.Floating);
            if (pane.Floating)
            {
                Set(vs, window, pane.Kind, "Visible", true);
                RestorePlacement(vs, FindHost(vs, window, pane.Kind), pane.Placement, window, pane.Kind);
            }
            Set(vs, window, pane.Kind, "Linkable", pane.Linkable);
            Set(vs, window, pane.Kind, "AutoHides", pane.AutoHides);
            Set(vs, window, pane.Kind, "Visible", pane.Visible);
            Thread.Sleep(100);
            Validate(vs);
            if ((bool)window.IsFloating != pane.Floating || (bool)window.Linkable != pane.Linkable ||
                (bool)window.AutoHides != pane.AutoHides || (bool)window.Visible != pane.Visible)
                throw new InvalidOperationException("窗格原状态未完全恢复 / original pane state not fully restored");
        }

        private object RequirePane(VsInstance vs, WorkspacePaneSnapshot pane)
        {
            Validate(vs);
            var window = Find(vs, pane.Pane, pane.Keyword, pane.Kind);
            if (window == null) throw new InvalidOperationException("原窗格不可用 / original pane unavailable");
            return window;
        }

        private static object Find(VsInstance vs, WorkspacePane pane, string keyword, string capturedKind)
        {
            dynamic dte = vs.Dte;
            if (pane == WorkspacePane.Copilot && capturedKind == null)
            {
                object found = VsService.FindCopilotWindow(vs.Dte, keyword);
                if (found != null) CheckTool(found, (string)((dynamic)found).ObjectKind);
                return found;
            }
            string kind = capturedKind ?? KindOf(pane);
            object match = null;
            foreach (dynamic window in dte.Windows)
            {
                // 读取不了 ObjectKind 的窗口（多为尚未加载完成的文档）无法是目标窗格，直接跳过。/ Windows whose ObjectKind cannot be read (mostly documents not fully loaded) cannot be the target pane; skip them.
                string objectKind;
                try { objectKind = (string)window.ObjectKind; }
                catch (Exception ex) when (ex is InvalidCastException || ex is COMException) { continue; }
                if (!SameGuid(objectKind, kind)) continue;
                CheckTool(window, kind);
                if (match != null) throw new InvalidOperationException("窗格 GUID 不唯一 / ambiguous pane GUID");
                match = window;
            }
            return match;
        }

        internal static string KindOf(WorkspacePane pane) =>
            pane == WorkspacePane.Output ? VsService.OutputKind
            : pane == WorkspacePane.SolutionExplorer ? VsService.SolutionExplorerKind
            : ErrorListKind;

        private static bool SameGuid(string a, string b) => Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb) && ga == gb;

        private static void CheckTool(dynamic window, string kind)
        {
            if (!Guid.TryParse(kind, out _) || !SameGuid((string)window.ObjectKind, kind) ||
                !string.Equals((string)window.Kind, "Tool", StringComparison.OrdinalIgnoreCase) || HasDocument(window))
                throw new InvalidOperationException("无法证明是目标工具窗格 / target tool-pane identity unproven");
        }

        // 工具窗格没有可持久化的文档数据时，Window.Document 可能因 IVsPersistDocData 的 E_NOINTERFACE 抛出，等同于没有文档。
        // For a tool pane without persistable document data, Window.Document may throw E_NOINTERFACE for IVsPersistDocData, which means there is no document.
        private static bool HasDocument(dynamic window)
        {
            try { return window.Document != null; }
            catch (InvalidCastException) { return false; }
        }

        private void Set(VsInstance vs, dynamic window, string kind, string property, bool value)
        {
            CheckTool(window, kind);
            Validate(vs);
            switch (property)
            {
                case "AutoHides": if ((bool)window.AutoHides != value) { Validate(vs); window.AutoHides = value; } break;
                case "Linkable": if ((bool)window.Linkable != value) { Validate(vs); window.Linkable = value; } break;
                case "IsFloating": if ((bool)window.IsFloating != value) { Validate(vs); window.IsFloating = value; } break;
                case "Visible": if ((bool)window.Visible != value) { Validate(vs); window.Visible = value; } break;
                default: throw new ArgumentException("不允许的属性 / property not allowed");
            }
        }

        private static void ValidateHwnd(VsInstance vs, IntPtr hwnd)
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || pid != vs.Pid)
                throw new InvalidOperationException("窗口句柄或进程不匹配 / window handle or process mismatch");
        }

        internal void ValidateHost(VsInstance vs, IntPtr host)
        {
            Validate(vs);
            ValidateHwnd(vs, host);
            if (host == vs.MainHwnd || GetAncestor(host, 2) != host || !Native.IsWindowEnabled(host) || Native.GetClass(host) == "#32770")
                throw new InvalidOperationException("不是安全的浮动窗口 / not a safe floating host");
            IntPtr owner = Native.GetWindow(host, Native.GW_OWNER);
            for (int i = 0; i < 16 && owner != IntPtr.Zero; i++)
            {
                ValidateHwnd(vs, owner);
                if (!Native.IsWindowEnabled(owner)) break;
                if (owner == vs.MainHwnd) return;
                owner = Native.GetWindow(owner, Native.GW_OWNER);
            }
            throw new InvalidOperationException("浮动窗口不属于目标主窗口 / floating host is not owned by target main window");
        }

        private IntPtr FindHost(VsInstance vs, dynamic window, string kind)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Validate(vs);
                CheckTool(window, kind);
                if (!(bool)window.IsFloating) throw new InvalidOperationException("窗格不是浮动状态 / pane is not floating");
                var handle = ToHandle(window.HWnd);
                if (handle != IntPtr.Zero)
                {
                    ValidateHwnd(vs, handle);
                    var root = GetAncestor(handle, 2);
                    if (root != vs.MainHwnd) { ValidateHost(vs, root); return root; }
                }
                IntPtr match = IntPtr.Zero;
                foreach (var candidate in Native.GetProcessWindows(vs.Pid, false).Take(32))
                {
                    if (candidate == vs.MainHwnd || Native.GetWindow(candidate, Native.GW_OWNER) == IntPtr.Zero) continue;
                    var root = AutomationElement.FromHandle(candidate);
                    if (root == null) continue;
                    var presenters = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "ViewPresenter"));
                    foreach (AutomationElement presenter in presenters.Cast<AutomationElement>().Take(128))
                    {
                        if (!MatchesAutomationId(presenter.Current.AutomationId, kind)) continue;
                        ValidateHost(vs, candidate);
                        if (match != IntPtr.Zero && match != candidate) throw new InvalidOperationException("浮动宿主不唯一 / ambiguous floating host");
                        match = candidate;
                    }
                }
                if (match != IntPtr.Zero) return match;
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("无法确定浮动宿主 / floating host could not be proven");
        }

        internal static bool MatchesAutomationId(string id, string kind)
        {
            if (SameGuid(id, kind)) return true;
            const string prefix = "ViewPresenter_";
            if (id != null && id.StartsWith(prefix, StringComparison.Ordinal) && SameGuid(id.Substring(prefix.Length), kind)) return true;
            var parts = (id ?? "").Split(':');
            return parts.Length == 4 && parts[0] == "ST" && parts[1].Length > 0 && parts[2].Length > 0
                && parts[1].All(c => c >= '0' && c <= '9') && parts[2].All(c => c >= '0' && c <= '9') && SameGuid(parts[3], kind);
        }

        private void GuardMove(VsInstance vs, IntPtr hwnd, object window, string kind)
        {
            Validate(vs);
            if (window == null)
            {
                if (hwnd != vs.MainHwnd) throw new InvalidOperationException("主窗口已改变 / main window changed");
                return;
            }
            CheckTool(window, kind);
            ValidateHost(vs, hwnd);
            if (FindHost(vs, window, kind) != hwnd) throw new InvalidOperationException("窗格宿主已改变 / pane host changed");
        }

        private void Move(VsInstance vs, IntPtr hwnd, Rectangle bounds, object window, string kind)
        {
            using (new PhysicalCoordinates())
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    GuardMove(vs, hwnd, window, kind);
                    if (Native.IsIconic(hwnd) || Native.IsZoomed(hwnd))
                    {
                        GuardMove(vs, hwnd, window, kind);
                        if (!ShowWindowAsync(hwnd, Native.SW_SHOWNOACTIVATE))
                            throw new InvalidOperationException("无法恢复正常显示 / cannot restore normal show state");
                    }
                    GuardMove(vs, hwnd, window, kind);
                    if (!Native.SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | SwpAsyncWindowPos))
                        throw new InvalidOperationException("SetWindowPos 失败 / SetWindowPos failed");
                    // 跨 DPI 后等待应用完成二次缩放，再复位并验证。/ Let cross-DPI resizing settle before repositioning and verification.
                    Thread.Sleep(150);
                }
                GuardMove(vs, hwnd, window, kind);
                VerifyBounds(hwnd, bounds);
            }
        }

        private void RestorePlacement(VsInstance vs, IntPtr hwnd, Native.WINDOWPLACEMENT original, object window, string kind)
        {
            using (new PhysicalCoordinates())
            {
                for (int i = 0; i < 2; i++)
                {
                    var placement = original;
                    placement.length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
                    placement.flags |= WpfAsyncWindowPlacement;
                    GuardMove(vs, hwnd, window, kind);
                    if (!Native.SetWindowPlacement(hwnd, ref placement)) throw new InvalidOperationException("SetWindowPlacement 失败 / SetWindowPlacement failed");
                    Thread.Sleep(150);
                }
                GuardMove(vs, hwnd, window, kind);
                var actual = Placement(hwnd);
                if (actual.showCmd != original.showCmd || !SameRect(actual.rcNormalPosition, original.rcNormalPosition))
                    throw new InvalidOperationException("原窗口位置或显示状态未恢复 / original placement or show state not restored");
            }
        }

        // DPI 虚拟化往返可能产生少量舍入；不容许实际最小尺寸或位置偏移。/ DPI virtualization may round slightly; real minimum-size or position deviations remain failures.
        internal static bool BoundsMatch(Rectangle a, Rectangle b) => Math.Abs((long)a.Left - b.Left) <= 2 && Math.Abs((long)a.Top - b.Top) <= 2
            && Math.Abs((long)a.Right - b.Right) <= 2 && Math.Abs((long)a.Bottom - b.Bottom) <= 2;
        private static bool SameRect(Native.RECT a, Native.RECT b) => BoundsMatch(Rectangle.FromLTRB(a.Left, a.Top, a.Right, a.Bottom), Rectangle.FromLTRB(b.Left, b.Top, b.Right, b.Bottom));
        private static bool IsMinimized(int command) => command == 2 || command == 6 || command == 7 || command == 11;
        private static IntPtr ToHandle(object value)
        {
            if (value is IntPtr pointer) return pointer;
            long number = Convert.ToInt64(value);
            return new IntPtr(number < 0 && number >= int.MinValue ? (long)unchecked((uint)(int)number) : number);
        }
        private static Native.WINDOWPLACEMENT Placement(IntPtr hwnd)
        {
            var result = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT)) };
            if (!Native.GetWindowPlacement(hwnd, ref result)) throw new InvalidOperationException("无法读取原窗口位置 / original window placement unavailable");
            return result;
        }

        private sealed class PhysicalCoordinates : IDisposable
        {
            private readonly IntPtr previous;
            internal PhysicalCoordinates()
            {
                try { previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException ex) { throw new NotSupportedException("系统不支持物理 DPI 上下文 / physical DPI context unavailable", ex); }
                if (previous == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置物理 DPI 上下文 / cannot set physical DPI context");
            }
            public void Dispose()
            {
                if (SetThreadDpiAwarenessContext(previous) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复 DPI 上下文 / cannot restore DPI context");
            }
        }
    }
}
