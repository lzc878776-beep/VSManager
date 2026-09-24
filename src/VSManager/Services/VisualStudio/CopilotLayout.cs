using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace VSManager
{
    /// <summary>
    /// 一键布局：把各 VS 的 Copilot 对话窗格切换为浮动窗口，在指定屏幕上均布排列，并可最小化 VS 主窗口；
    /// 执行前记录原布局（主窗口位置 / 最大化状态、窗格停靠状态），可一键还原。所有方法必须在 DteWorker 线程调用。
    /// 说明：浮动工具窗口归 VS 主窗口所有，主窗口最小化时 Windows 会一并隐藏它们，因此最小化后再以不激活的方式重新显示窗格。
    /// One-click layout: floats the Copilot chat pane of each VS, spreads the panes evenly over a chosen screen and can
    /// minimize the VS main windows; the original layout (main window position / maximized state, pane docking state) is
    /// recorded first so it can be restored. All methods must run on the DteWorker thread.
    /// Note: floating tool windows are owned by the VS main window and Windows hides them when the owner is minimized, so
    /// the panes are shown again (without activation) after minimizing.
    /// </summary>
    public static class CopilotLayout
    {
        private sealed class Saved
        {
            public int Pid;
            public long StartTicks;
            public string Name;
            public IntPtr MainHwnd;
            public Native.WINDOWPLACEMENT Main;
            public bool HasMain;
            public bool WasFloating, WasLinkable, WasAutoHides;
            public IntPtr PaneHwnd;
            public Native.WINDOWPLACEMENT Pane;
            public bool HasPane;
        }

        private sealed class Prepared
        {
            public VsInstance Vs;
            public string Name;
            public IntPtr Pane;
        }

        private static readonly object Lock = new object();
        private static readonly List<Saved> Snapshot = new List<Saved>();

        /// <summary>已记录、可还原的 VS 数。/ Number of VS instances whose layout can be restored.</summary>
        public static int SavedCount { get { lock (Lock) return Snapshot.Count; } }

        /// <summary>
        /// 排列各 VS 的 Copilot 对话窗格，返回给用户 / 模型的结果说明。无法处理的 VS（未连接 DTE、没有窗格等）会跳过并说明原因，不影响其他 VS。
        /// Arranges the Copilot chat panes and returns a report. A VS that cannot be handled (no DTE, no pane…) is skipped
        /// with the reason; the others are not affected.
        /// </summary>
        public static string Arrange(IList<VsInstance> list, IList<string> names, Rectangle area, PaneArrangement arrangement,
            bool minimize, string keyword, int minWidth, int minHeight)
        {
            var lines = new List<string>();
            var ready = new List<Prepared>();
            for (int i = 0; i < list.Count; i++)
            {
                string name = i < names.Count ? names[i] : "#" + (i + 1);
                string err;
                IntPtr pane = IntPtr.Zero;
                try { err = Prepare(list[i], name, keyword, out pane); }
                catch (Exception ex) { err = "失败 / failed：" + ex.Message; }
                if (err != null) lines.Add("· " + name + "：" + err + "（已跳过 / skipped）");
                else ready.Add(new Prepared { Vs = list[i], Name = name, Pane = pane });
            }
            if (ready.Count == 0)
                return "没有可排列的 Copilot 对话窗格 / No Copilot chat pane could be arranged。\n" + string.Join("\n", lines);

            var grid = PaneGrid.Compute(area, ready.Count, minWidth, minHeight, arrangement);
            if (minimize)
                foreach (var p in ready)
                    if (Native.IsWindow(p.Vs.MainHwnd) && !Native.IsIconic(p.Vs.MainHwnd))
                        Native.ShowWindow(p.Vs.MainHwnd, Native.SW_SHOWMINNOACTIVE);
            Thread.Sleep(minimize ? 400 : 100);
            Place(ready, grid);
            // 跨 DPI 屏幕移动时窗口会按新 DPI 自行调整一次尺寸，稍后再摆放一次 / Windows resize themselves once after a DPI change; place again
            Thread.Sleep(350);
            Place(ready, grid);

            int shown = 0;
            var done = new List<string>();
            foreach (var p in ready)
            {
                bool visible = Native.IsWindowVisible(p.Pane);
                if (visible) shown++;
                done.Add("· " + p.Name + "：" + (visible ? "✓" : "⚠ 窗格未能显示 / pane not visible"));
            }
            lines.InsertRange(0, done);
            string head = $"已把 {shown} 个 Copilot 对话窗格{(arrangement == PaneArrangement.Grid ? "按网格" : "横向均布")}排列（{grid.Columns} 列 × {grid.Rows} 行）" +
                          (minimize ? "，并最小化了对应的 VS 主窗口" : "") +
                          $" / Arranged {shown} pane(s) in {grid.Columns} x {grid.Rows}" + (minimize ? ", VS main windows minimized" : "") + "。";
            if (grid.Rows > 1 && arrangement == PaneArrangement.Horizontal)
                head += $"\n一行放不下（每格至少 {minWidth} 像素宽），已自动换行 / Wrapped to more rows (min width {minWidth}px)。";
            if (grid.Cramped) head += "\n⚠ 窗格较多，每格高度偏小 / Many panes, cells are short。";
            head += "\n可用「还原 Copilot 布局」恢复原来的窗口布局 / Use restore to bring back the previous layout。";
            return head + "\n" + string.Join("\n", lines);
        }

        /// <summary>
        /// 还原最近一次排列之前的布局：先恢复主窗口位置与最大化状态，再把原本停靠的窗格放回停靠位置。
        /// Restores the layout recorded before arranging: main window position / maximized state first, then the panes
        /// that were docked go back to their dock positions.
        /// </summary>
        public static string Restore(IList<VsInstance> live, string keyword)
        {
            List<Saved> items;
            lock (Lock) { items = Snapshot.ToList(); Snapshot.Clear(); }
            if (items.Count == 0) return "没有可还原的布局（尚未执行一键布局，或已经还原）/ Nothing to restore";

            var lines = new List<string>();
            var pending = new List<Tuple<Saved, VsInstance>>();
            foreach (var s in items)
            {
                var vs = live.FirstOrDefault(v => v.Pid == s.Pid && (s.StartTicks == 0 || v.StartTicks == s.StartTicks));
                if (vs == null || !Native.IsWindow(s.MainHwnd)) { lines.Add("· " + s.Name + "：VS 已关闭，跳过 / closed, skipped"); continue; }
                if (s.HasMain)
                {
                    var wp = s.Main;
                    wp.length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
                    if (wp.showCmd == Native.SW_SHOWMINIMIZED) wp.showCmd = Native.SW_SHOWMINNOACTIVE;
                    Native.SetWindowPlacement(s.MainHwnd, ref wp);
                }
                else if (Native.IsIconic(s.MainHwnd)) Native.ShowWindow(s.MainHwnd, Native.SW_RESTORE);
                pending.Add(Tuple.Create(s, vs));
            }
            if (pending.Count > 0) Thread.Sleep(300);

            foreach (var t in pending)
            {
                var s = t.Item1;
                string r;
                try
                {
                    if (s.WasFloating)
                    {
                        if (s.HasPane && Native.IsWindow(s.PaneHwnd))
                        {
                            var wp = s.Pane;
                            wp.length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
                            if (wp.showCmd != Native.SW_MAXIMIZE) wp.showCmd = Native.SW_SHOWNOACTIVATE;
                            Native.SetWindowPlacement(s.PaneHwnd, ref wp);
                        }
                        r = "✓";
                    }
                    else
                    {
                        object win = t.Item2.Dte == null ? null : VsService.FindCopilotWindow(t.Item2.Dte, keyword);
                        if (win == null) r = "主窗口已还原，未找到 Copilot 窗格 / main window restored, pane not found";
                        else { RestorePaneState(win, false, s.WasLinkable, s.WasAutoHides); r = "✓"; }
                    }
                }
                catch (Exception ex) { r = "窗格还原失败 / pane restore failed：" + ex.Message; }
                lines.Add("· " + s.Name + "：" + r);
            }
            return $"已还原 {pending.Count} 个 VS 的窗口布局 / Restored {pending.Count} VS layout(s)。\n" + string.Join("\n", lines);
        }

        /// <summary>打开并浮动 Copilot 窗格，返回其窗口句柄；失败返回原因（并撤销已做的修改）。/ Opens and floats the pane; returns the reason on failure (changes are undone).</summary>
        private static string Prepare(VsInstance vs, string name, string keyword, out IntPtr pane)
        {
            pane = IntPtr.Zero;
            if (vs == null || !Native.IsWindow(vs.MainHwnd)) return "VS 已关闭 / VS closed";
            if (vs.Dte == null) return "无法连接到该 VS 的自动化接口 (DTE) / DTE unavailable";

            Saved saved;
            lock (Lock) saved = Snapshot.FirstOrDefault(s => s.Pid == vs.Pid && s.StartTicks == vs.StartTicks);
            bool isNew = saved == null;
            if (isNew)
            {
                saved = new Saved { Pid = vs.Pid, StartTicks = vs.StartTicks, Name = name, MainHwnd = vs.MainHwnd };
                saved.HasMain = TryPlacement(vs.MainHwnd, out saved.Main);
            }

            dynamic dte = vs.Dte;
            dynamic win = VsService.FindCopilotWindow(dte, keyword);
            if (win == null)
            {
                try { dte.ExecuteCommand(VsService.CopilotChatCommand); }
                catch (Exception ex) { return "无法打开 Copilot 对话窗格 / cannot open the Copilot pane：" + ex.Message; }
                Thread.Sleep(600);
                win = VsService.FindCopilotWindow(dte, keyword);
            }
            if (win == null) return "未找到 Copilot 对话窗格 / Copilot pane not found";

            bool floating = false, linkable = true, autoHides = false;
            try { floating = win.IsFloating; } catch { }
            try { linkable = win.Linkable; } catch { }
            try { autoHides = win.AutoHides; } catch { }
            if (isNew) { saved.WasFloating = floating; saved.WasLinkable = linkable; saved.WasAutoHides = autoHides; }
            try { win.Visible = true; } catch { }

            if (!floating)
            {
                try { if (autoHides) win.AutoHides = false; } catch { }
                try { win.IsFloating = true; }
                catch
                {
                    // 选项卡式文档不能直接浮动：先切换为工具窗口 / A tabbed document cannot float directly: make it a tool window first
                    try { if (!linkable) win.Linkable = true; win.IsFloating = true; }
                    catch (Exception ex)
                    {
                        if (isNew) RestorePaneState(win, false, linkable, autoHides);
                        return "无法切换为浮动窗口 / cannot float the pane：" + ex.Message;
                    }
                }
            }

            string kind = null;
            try { kind = win.ObjectKind; } catch { }
            pane = FindPaneHost(vs, kind, keyword);
            if (pane == IntPtr.Zero)
            {
                if (isNew && !floating) RestorePaneState(win, false, linkable, autoHides);
                return "未找到浮动窗格的窗口 / floating pane window not found";
            }

            if (isNew)
            {
                saved.PaneHwnd = pane;
                if (floating) saved.HasPane = TryPlacement(pane, out saved.Pane);
                lock (Lock) Snapshot.Add(saved);
            }
            return null;
        }

        private static bool TryPlacement(IntPtr h, out Native.WINDOWPLACEMENT wp)
        {
            wp = new Native.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT)) };
            return h != IntPtr.Zero && Native.GetWindowPlacement(h, ref wp);
        }

        /// <summary>把窗格恢复为原来的浮动 / 停靠 / 自动隐藏状态。/ Puts the pane back to its original floating / docking / auto-hide state.</summary>
        private static void RestorePaneState(dynamic win, bool wasFloating, bool wasLinkable, bool wasAutoHides)
        {
            if (!wasFloating) try { if ((bool)win.IsFloating) win.IsFloating = false; } catch { }
            try { if ((bool)win.Linkable != wasLinkable) win.Linkable = wasLinkable; } catch { }
            try { if (wasAutoHides && !(bool)win.AutoHides) win.AutoHides = true; } catch { }
        }

        private static readonly PropertyCondition ViewPresenterCond = new PropertyCondition(AutomationElement.ClassNameProperty, "ViewPresenter");
        private static readonly PropertyCondition GenericPaneCond = new PropertyCondition(AutomationElement.ClassNameProperty, "GenericPane");

        /// <summary>
        /// 查找承载 Copilot 窗格的浮动窗口：优先按 ViewPresenter 的 AutomationId（含窗口类型 GUID），其次按窗格名称关键字。
        /// Finds the floating window hosting the pane: by the ViewPresenter AutomationId (contains the kind GUID) first, then by
        /// the pane name keyword.
        /// </summary>
        private static IntPtr FindPaneHost(VsInstance vs, string kind, string keyword)
        {
            string guid = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(keyword)) keyword = "Copilot";
            for (int i = 0; i < 20; i++)
            {
                foreach (var h in Native.GetProcessWindows(vs.Pid))
                {
                    if (h == vs.MainHwnd) continue;
                    try
                    {
                        var root = AutomationElement.FromHandle(h);
                        if (guid != null)
                            foreach (AutomationElement p in root.FindAll(TreeScope.Descendants, ViewPresenterCond))
                                if ((p.Current.AutomationId ?? "").ToLowerInvariant().Contains(guid)) return h;
                        foreach (AutomationElement p in root.FindAll(TreeScope.Descendants, GenericPaneCond))
                            if ((p.Current.Name ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return h;
                    }
                    catch { }
                }
                Thread.Sleep(150);
            }
            return IntPtr.Zero;
        }

        private static void Place(List<Prepared> ready, PaneGridResult grid)
        {
            for (int i = 0; i < ready.Count && i < grid.Cells.Count; i++)
            {
                IntPtr h = ready[i].Pane;
                if (!Native.IsWindow(h)) continue;
                var r = grid.Cells[i];
                if (Native.IsIconic(h) || Native.IsZoomed(h) || !Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(h, Native.HWND_TOP, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
        }
    }
}
