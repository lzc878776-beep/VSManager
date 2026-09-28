using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 自测重启的场景补齐，以及启动时对已有测试清单的可验证性重判。
    /// Scenario setup for self-test restarts, plus re-judging the verifiability of existing checklists at startup.
    /// </summary>
    public partial class MainForm : IAgentScenarioHost
    {
        private RestartScenario _restartScenario;
        private DateTime _restartScenarioAt;
        private const string ScenarioSource = "重启测试场景 / restart scenario";

        Task<string> IAgentScenarioHost.PrepareRestartScenario(RestartScenario s) => OnUi(() =>
        {
            if (s.Draft.Length > 0)
            {
                string current = _agentPanel.DraftText ?? "";
                if (current.Trim().Length > 0 && current != s.Draft)
                    return "AI 输入框已有用户草稿，不覆盖；请去掉 draft 参数，或请用户先清空输入框 / The AI input already holds a user draft and is not overwritten; drop draft or ask the user to clear it.";
            }
            if (s.Vs.Length > 0 && FindScenarioVs(s.Vs) == null)
                return "没有找到名为「" + s.Vs + "」的 VS，可先 list_vs 查看 / No VS named \"" + s.Vs + "\"; see list_vs.";
            int screens = ScreenHelper.Ordered().Count;
            if (s.Screen > screens) return $"只有 {screens} 块屏幕，没有第 {s.Screen} 块 / Only {screens} screen(s); no screen {s.Screen}.";
            if (s.Page != null && !_settings.AgentEnabled && s.Page == RestartScenario.PageAgent)
                return "AI 总控页未启用 / The AI assistant page is disabled.";
            // 多次调用合并：本次未填的项沿用仍有效的上一次场景（例如先设草稿、再设页面），重启前整体再应用一次
            // Repeated calls merge: items not given now keep the still-valid previous scenario (e.g. draft first, page later); the whole scenario is re-applied before the restart
            var previous = _restartScenario != null && DateTime.UtcNow - _restartScenarioAt <= RestartScenario.Lifetime ? _restartScenario : null;
            s = s.MergeOnto(previous);
            _restartScenario = s;
            _restartScenarioAt = DateTime.UtcNow;
            string notes = ApplyRestartScenario(s);
            AppLog.Write(ProcessWatchdog.LogFile, "AI 助手准备重启测试场景 / Assistant prepared a restart scenario: " + s.Describe() + notes);
            return "已应用场景 / Scenario applied: " + s.Describe() + notes + "。" +
                "场景保留 " + (int)RestartScenario.Lifetime.TotalMinutes + " 分钟；多次调用会合并（未填的项沿用上一次）；调用 restart_vsmanager_for_testing 后，重启前会再应用一次再保存界面状态，并写进「[重启完成通知]」。" +
                "用户在 AI 输入框发送消息会清空草稿，重启前会重新写入。可先用 get_window_state / get_foreground_window 确认。/ Kept for " + (int)RestartScenario.Lifetime.TotalMinutes + " minutes; repeated calls merge (items not given keep the previous values); restart_vsmanager_for_testing re-applies it " +
                "before saving the UI state and records it in the restart notice. A user send from the AI input clears the draft; it is written again before the restart. Confirm with get_window_state / get_foreground_window if needed.";
        });

        /// <summary>取出仍有效的场景（取出即清除）/ Takes the prepared scenario if still valid (and clears it).</summary>
        private RestartScenario TakeRestartScenario()
        {
            var s = _restartScenario;
            _restartScenario = null;
            return s != null && DateTime.UtcNow - _restartScenarioAt <= RestartScenario.Lifetime ? s : null;
        }

        private VsInstance FindScenarioVs(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var all = _instances.ToList();
            return all.FirstOrDefault(v => string.Equals(NameOf(v), name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(v => string.Equals(v.DisplaySolution, name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(v => (NameOf(v) ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? all.FirstOrDefault(v => (v.DisplaySolution ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 应用场景，返回未能做到的部分说明（空串表示全部做到）。顺序：页面 → 草稿 → 屏幕 → 窗口状态 → 前台。
        /// Applies the scenario and returns notes on what could not be done (empty when all succeeded). Order: page → draft → screen → window state → foreground.
        /// </summary>
        private string ApplyRestartScenario(RestartScenario s)
        {
            var notes = new List<string>();
            if (s.Page == RestartScenario.PageAgent)
            {
                if (!ShowAgent(true, ScenarioSource)) notes.Add("未能切到 AI 总控页 / could not switch to the AI page");
            }
            else if (s.Page == RestartScenario.PageVs)
            {
                var v = s.Vs.Length > 0 ? FindScenarioVs(s.Vs) : null;
                if (s.Vs.Length > 0 && v == null) notes.Add("VS「" + s.Vs + "」已不在 / VS \"" + s.Vs + "\" is gone");
                // 先同步切到 VS 页再选中：选中事件是异步处理的，只靠选中会误报「仍停在 AI 总控页」
                // Switch to the VS page synchronously before selecting: selection is handled asynchronously, so selecting alone misreports "still on the AI page"
                if (_agentMode && !ShowAgent(false, ScenarioSource)) notes.Add("未能切到 VS 对话页 / could not switch to the VS page");
                if (v != null && Selected?.Pid != v.Pid)
                {
                    int index = _list.Items.IndexOf(v);
                    if (index >= 0) _list.SelectedIndex = index;
                    else notes.Add("VS「" + s.Vs + "」不在左侧列表中 / VS \"" + s.Vs + "\" is not in the list");
                }
                if (_agentMode) notes.Add("仍停在 AI 总控页 / still on the AI page");
            }
            if (s.Draft.Length > 0)
            {
                // 输入框已有其他文字（用户新输入的草稿）时不覆盖；输入框被发送清空后重新写入
                // Never overwrite other text the user typed; write it again when a send has cleared the input
                string current = _agentPanel.DraftText ?? "";
                if (current.Trim().Length == 0 || current == s.Draft) _agentPanel.DraftText = s.Draft;
                else notes.Add("输入框已有用户草稿，未写入场景草稿 / the input holds a user draft; the scenario draft was not written");
            }
            if (s.Screen > 0)
            {
                var screens = ScreenHelper.Ordered();
                if (s.Screen <= screens.Count)
                {
                    if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                    var wa = screens[s.Screen - 1].WorkingArea;
                    int w = Math.Min(Width, wa.Width), h = Math.Min(Height, wa.Height);
                    Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
                }
                else notes.Add("屏幕 " + s.Screen + " 已不存在 / screen " + s.Screen + " is gone");
            }
            if (s.Window == RestartScenario.WindowMaximize) { if (!Visible) Show(); WindowState = FormWindowState.Maximized; }
            else if (s.Window == RestartScenario.WindowNormal) { if (!Visible) Show(); WindowState = FormWindowState.Normal; }
            else if (s.Window == RestartScenario.WindowMinimize) WindowState = FormWindowState.Minimized;
            if (s.Foreground == RestartScenario.ForeVsManager)
            {
                if (!Visible) Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Native.ForceForeground(Handle, TopMost);
            }
            else if (s.Foreground == RestartScenario.ForeOther)
            {
                var other = OtherTopWindow();
                if (other == IntPtr.Zero) notes.Add("没有可切换的其他窗口 / no other window to switch to");
                else Native.ForceForeground(other, false);
            }
            return notes.Count == 0 ? "" : "（未完成 / not done: " + string.Join("；", notes) + "）";
        }

        /// <summary>找一个可切到前台的其他应用窗口：优先已知 VS 主窗口，其次其他可见顶层窗口。/ Finds another app window for the foreground: known VS main windows first, then other visible top-level windows.</summary>
        private IntPtr OtherTopWindow()
        {
            foreach (var v in _instances.ToList())
                if (v.MainHwnd != IntPtr.Zero && IsWindowVisible(v.MainHwnd) && !IsIconic(v.MainHwnd)) return v.MainHwnd;
            uint me = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h) || GetWindowTextLength(h) == 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == me) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Shell_TrayWnd" || c == "Progman" || c == "WorkerW" || c == "Windows.UI.Core.CoreWindow") return true;
                if ((GetWindowLong(h, -20) & 0x80) != 0) return true; // WS_EX_TOOLWINDOW
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// 启动时按测试项文字重判已有待验证清单的 [AI] / [人工]（只改未勾选项），有改动时保存。
        /// At startup re-judges the [AI] / [manual] tags of existing pending checklists (unchecked items only) and saves when anything changed.
        /// </summary>
        private void RejudgeChecklists()
        {
            // 以前只在结论改变时记日志，而清单在进入待验证时已重判过，启动时通常无改变，所以从不写记录；现在只要重判过就记录项数
            // Previously this logged only when a verdict changed; checklists are already re-judged on entering verification, so startup
            // rarely changed anything and nothing was logged. Now the count is logged whenever items were re-judged.
            int judged = 0, changed = 0;
            bool filled = false;
            foreach (var t in _tasks.Items.Where(TaskTestChecklist.Pending).ToList())
            {
                if (t.TestItems == null || t.TestItems.Length == 0 || t.TestItems.Any(i => i == null))
                {
                    t.TestItems = TaskTestChecklist.Parse(t.FullResult ?? t.Result, out int j, out int c);
                    judged += j;
                    changed += c;
                    filled = true;
                    continue;
                }
                changed += TaskTestChecklist.Reclassify(t, out int n);
                judged += n;
            }
            if (changed > 0 || filled) _tasks.Save();
            if (judged > 0) AppLog.Write(AppLog.TasksFile, "启动时 / At startup：" + TaskTestChecklist.RejudgeLogText(judged, changed));
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    }
}
