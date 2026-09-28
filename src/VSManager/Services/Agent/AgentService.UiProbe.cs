using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>探测所需的本进程信息。/ Information about this process needed by the probes.</summary>
    public sealed class UiSelfInfo
    {
        public IntPtr MainWindow;
        public string TrayText;
        public bool TrayVisible;
    }

    /// <summary>
    /// 可选宿主能力：界面状态探测（主窗口状态、本进程启动时消费的重启交接信息、托盘与主窗口句柄）。
    /// Optional host capability: UI state probes (main window state, the restart handoff consumed at startup, tray and main window handle).
    /// </summary>
    public interface IAgentUiProbeHost
    {
        /// <summary>主窗口位置、大小、状态、页面、选中的 VS、AI 草稿，以及与重启前保存值的对照。/ Main window bounds, state, page, selected VS, AI draft and a comparison with the saved pre-restart values.</summary>
        Task<string> GetWindowState();
        /// <summary>本进程启动时读取（消费）了哪些重启交接信息。/ Which restart handoff data this process consumed at startup.</summary>
        Task<string> RestartConsumption();
        Task<UiSelfInfo> SelfInfo();
    }

    /// <summary>
    /// 可选宿主能力：造出自测重启前的界面场景，并在重启前再应用一次。
    /// Optional host capability: sets up the UI scenario for a self-test restart and re-applies it right before restarting.
    /// </summary>
    public interface IAgentScenarioHost
    {
        Task<string> PrepareRestartScenario(RestartScenario scenario);
    }

    public sealed partial class AgentService
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

        [Description("界面探测：返回 VSManager 主窗口的位置、大小、最大化 / 最小化状态、所在屏幕、是否前台、当前页面（AI 总控 / VS 对话）、选中的 VS、AI 输入框草稿与托盘图标状态；" +
            "若本进程由重启启动，还逐项对照重启前保存的值（一致 / 不一致）。用于自动验证「重启后状态恢复」类测试项。只读。" +
            " / UI probe: returns the VSManager main window's position, size, maximized / minimized state, screen, foreground state, current page (AI assistant / VS chat), " +
            "selected VS, AI input draft and tray icon state; after a restart it also compares each value with the saved pre-restart value. For verifying restore-after-restart items. Read-only.")]
        internal async Task<string> ProbeWindowState()
        {
            if (!(_host is IAgentUiProbeHost host)) return "当前宿主不支持界面探测 / UI probes are unavailable.";
            return Truncate(await host.GetWindowState().ConfigureAwait(false), MaxToolText);
        }

        [Description("界面探测：返回当前前台窗口的标题、窗口类、进程名与 PID，并说明是否属于本 VSManager（主窗口或其对话框）。用于验证「是否抢焦点」类测试项。只读。" +
            " / UI probe: returns the foreground window's title, class, process name and PID, and whether it belongs to this VSManager (main window or a dialog). For verifying focus-stealing items. Read-only.")]
        internal async Task<string> ProbeForeground()
        {
            var self = _host is IAgentUiProbeHost host ? await host.SelfInfo().ConfigureAwait(false) : null;
            int pid = Process.GetCurrentProcess().Id;
            return await Task.Run(() => UiProbe.ForegroundText(pid, self?.MainWindow ?? IntPtr.Zero)).ConfigureAwait(false);
        }

        [Description("界面探测：列出托盘区图标（提示文字；Windows 10 及更早还有所属进程与「所属窗口已不存在」的残影标记），并给出 VSManager 是否留有托盘残影的结论。" +
            "Windows 11 上 openOverflow=true（默认）会短暂展开「显示隐藏的图标」读取溢出区，读完收起并把前台还给原窗口——要验证前台 / 焦点时先调用 get_foreground_window 再调用本工具。只读。" +
            " / UI probe: lists tray icons (tooltip; on Windows 10 and earlier also the owner process and a ghost flag when the owner window is gone) and concludes whether VSManager left a ghost icon. " +
            "On Windows 11 openOverflow=true (default) briefly opens the hidden-icons flyout, closes it and hands the foreground back — call get_foreground_window first when checking focus. Read-only.")]
        internal async Task<string> ProbeTrayIcons(
            [Description("是否展开溢出区读取隐藏的图标（Windows 11），默认 true / Open the overflow flyout to read hidden icons (Windows 11); default true")] bool openOverflow = true)
        {
            var self = _host is IAgentUiProbeHost host ? await host.SelfInfo().ConfigureAwait(false) : null;
            int pid = Process.GetCurrentProcess().Id;
            var work = Task.Run(() =>
            {
                var icons = UiProbe.TrayIcons(openOverflow, out string source, out bool overflowRead);
                int live;
                using (var me = Process.GetCurrentProcess())
                {
                    var all = Process.GetProcessesByName(me.ProcessName);
                    live = all.Length;
                    foreach (var p in all) p.Dispose();
                }
                return UiProbe.TrayText(icons, source, overflowRead, self?.TrayText, self?.TrayVisible ?? false, pid, live);
            });
            if (await Task.WhenAny(work, Task.Delay(ProbeTimeout)).ConfigureAwait(false) != work)
                return "读取托盘超时（" + (int)ProbeTimeout.TotalSeconds + " 秒）/ Reading the tray timed out.";
            try { return Truncate(await work.ConfigureAwait(false), MaxToolText); }
            catch (Exception ex) { return "读取托盘失败 / Reading the tray failed: " + ex.Message; }
        }

        [Description("界面探测：读取 restart-ui.json（界面状态）与 restart-handoff.json（自测重启交接单）的内容与消费状态——文件仍在表示尚未被新进程读取，不存在通常表示已消费；" +
            "并说明本进程启动时实际读取、恢复了什么（旧 / 新 PID、是否加载新程序、恢复的授权、位置是否按原值恢复），以及常驻的 agent-plans.json（进行中的执行计划，重启不消费）。用于验证重启交接。只读。" +
            " / UI probe: reads restart-ui.json (UI state) and restart-handoff.json (self-test handoff) with their consumption state — present means not yet read by a new process, absent usually means consumed; " +
            "also reports what this process actually consumed and restored at startup (old / new PID, new build loaded, grants restored, bounds restored), plus the resident agent-plans.json (active execution plans, not consumed by a restart). For verifying the restart handoff. Read-only.")]
        internal async Task<string> ProbeRestartHandoff()
        {
            string consumed = _host is IAgentUiProbeHost host ? await host.RestartConsumption().ConfigureAwait(false) : null;
            string files = RestartProbe.DescribeFiles(DateTime.UtcNow);
            return Truncate((consumed == null ? "" : consumed + Environment.NewLine + Environment.NewLine) + files, MaxToolText);
        }

        [Description("场景补齐：为自测重启造出测试项需要的前置场景并立即应用——切到 AI 总控页或某个 VS 对话页、在 AI 输入框写入草稿（已有用户草稿时不覆盖）、最大化 / 还原 / 最小化窗口、移到第 N 块屏幕、" +
            "把前台切给其他应用或 VSManager；场景保留 10 分钟，随后调用 restart_vsmanager_for_testing 时会在保存界面状态前再应用一次，并写进重启完成通知以便对照。只填需要的参数。" +
            " / Scenario setup: creates the precondition a test item needs before a self-test restart and applies it now — switch to the AI assistant page or a VS chat page, put a draft in the AI input " +
            "(an existing user draft is never overwritten), maximize / restore / minimize the window, move it to screen N, hand the foreground to another app or to VSManager. The scenario is kept for 10 minutes; " +
            "restart_vsmanager_for_testing re-applies it before saving the UI state and records it in the restart notice. Fill only what you need.")]
        internal async Task<string> PrepareRestartScenario(
            [Description("页面：agent（AI 总控）或 vs（VS 对话）/ Page: agent or vs")] string page = "",
            [Description("要选中的 VS 名称（模糊匹配，隐含 page=vs）/ VS to select (fuzzy, implies page=vs)")] string vs = "",
            [Description("写入 AI 输入框的草稿 / Draft for the AI input")] string draft = "",
            [Description("窗口：maximize、normal 或 minimize / Window: maximize, normal or minimize")] string window = "",
            [Description("移到第几块屏幕（从 1 开始，编号同 get_displays），0 不移动 / 1-based screen (as in get_displays), 0 keeps it")] int screen = 0,
            [Description("前台：other（切给其他应用，用于验证不抢焦点）或 vsmanager / Foreground: other (hand to another app, for focus-stealing checks) or vsmanager")] string foreground = "")
        {
            if (!(_host is IAgentScenarioHost host)) return "当前宿主不支持场景补齐 / Scenario setup is unavailable.";
            var s = RestartScenario.Create(page, vs, draft, window, screen, foreground, out string error);
            if (s == null) return "场景无效 / Invalid scenario: " + error;
            return Truncate(await host.PrepareRestartScenario(s).ConfigureAwait(false), MaxToolText);
        }
    }
}
