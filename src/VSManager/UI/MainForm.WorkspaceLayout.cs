using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    public partial class MainForm : IAgentWorkspaceLayoutHost
    {
        Task<WorkspaceDisplaySnapshot> IAgentWorkspaceLayoutHost.GetDisplays() => OnUi(ReadWorkspaceDisplays);

        private WorkspaceDisplaySnapshot ReadWorkspaceDisplays()
        {
            var screens = ScreenHelper.Ordered();
            int Number(IntPtr hwnd)
            {
                if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return 0;
                string device = Screen.FromHandle(hwnd).DeviceName;
                return screens.FindIndex(s => s.DeviceName == device) + 1;
            }
            var result = CaptureDisplayGeometry(screens);
            result.CurrentScreen = Number(Native.GetForegroundWindow());
            result.ManagerScreen = Number(Handle);
            result.VsLocations.AddRange(_instances.Select((v, i) =>
            {
                string rect = Native.IsWindow(v.MainHwnd) && Native.GetWindowRect(v.MainHwnd, out var r)
                    ? $"; 窗口 / Window=({r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top})" + (Native.IsIconic(v.MainHwnd) ? " 最小化 / minimized" : "") : "";
                return $"VS #{i + 1} {NameOf(v)}: 屏幕 / Screen {Number(v.MainHwnd)}{rect}";
            }));
            return result;
        }

        private static WorkspaceDisplaySnapshot CaptureDisplayGeometry(System.Collections.Generic.IList<Screen> screens)
        {
            var result = new WorkspaceDisplaySnapshot();
            result.Displays.AddRange(screens.Select((s, i) => new WorkspaceDisplay { Number = i + 1, Bounds = s.Bounds, WorkingArea = s.WorkingArea, Primary = s.Primary }));
            return result;
        }

        Task<string> IAgentWorkspaceLayoutHost.ArrangeWorkspace(WorkspaceLayoutPlan plan, string displaySignature) => OnUiAsync(async () =>
        {
            if (_arranging || _docking) return "正在调整窗格布局，请稍候 / Layout change in progress";
            if (CopilotLayout.SavedCount > 0) return "请先还原旧 Copilot 布局 / First restore the existing Copilot layout with restore_copilot_layout";
            if (ReadWorkspaceDisplays().Signature != displaySignature) return "显示器配置已改变，未移动窗口；请重新读取并布局 / Displays changed; no windows moved. Read displays and arrange again.";
            if (!plan.TargetsUnchanged || plan.Placements.Any(p => !_instances.Contains(p.Vs))) return "目标 VS 已改变，未移动窗口 / Target VS changed; no windows moved";
            _arranging = true;
            string keyword = _settings.CopilotPaneKeyword;
            var foreground = Native.GetForegroundWindow();
            SetStatus("正在布局 VS 主窗口与工具窗格 / Arranging VS main windows and tool panes");
            try
            {
                var result = await DteWorker.Run(() => CaptureDisplayGeometry(ScreenHelper.Ordered()).Signature != displaySignature
                    ? "显示器配置已改变，未移动窗口 / Displays changed; no windows moved"
                    : !plan.TargetsUnchanged ? "目标 VS 已改变，未移动窗口 / Target VS changed; no windows moved"
                    : VsWorkspaceLayout.Arrange(plan.Placements, plan.IncludeOutput, plan.IncludeErrorList, keyword, plan.IncludeSolutionExplorer));
                foreach (var p in plan.Placements) _chatSvc.PaneRestored(p.Vs.Pid);
                SetStatus(result.Split('\n')[0]);
                return plan.Describe() + "\n" + result;
            }
            finally
            {
                _arranging = false;
                if (foreground != IntPtr.Zero && Native.IsWindow(foreground) && !Native.IsIconic(foreground) && Native.GetForegroundWindow() != foreground) Native.Activate(foreground);
            }
        });

        Task<string> IAgentWorkspaceLayoutHost.RestoreWorkspaceLayout() => OnUiAsync(async () =>
        {
            if (_arranging || _docking) return "正在调整窗格布局，请稍候 / Layout change in progress";
            var live = _instances.ToList();
            _arranging = true;
            var foreground = Native.GetForegroundWindow();
            try
            {
                var result = await DteWorker.Run(() => VsWorkspaceLayout.Restore(live));
                foreach (var v in live) _chatSvc.PaneRestored(v.Pid);
                SetStatus(result.Split('\n')[0]);
                return result;
            }
            finally
            {
                _arranging = false;
                if (foreground != IntPtr.Zero && Native.IsWindow(foreground) && !Native.IsIconic(foreground) && Native.GetForegroundWindow() != foreground) Native.Activate(foreground);
            }
        });
    }
}
