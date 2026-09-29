using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 界面状态探测：让 AI 总控助手用工具读取主窗口状态与重启交接的实际结果，自动验证原本需要人工观察的测试项。
    /// UI state probes: let the AI assistant read the main window state and the actual restart handoff results through tools, so
    /// items that used to need a human eye can be verified automatically.
    /// </summary>
    public partial class MainForm : IAgentUiProbeHost
    {
        private Rectangle? _restoredBounds;
        private bool _coverSignaled;
        private bool _coverReleaseRequested;
        private bool _restoreFramePrepared;
        private SelfRestartHandoff _resumedHandoff;
        private SelfRestartOutcome _resumeOutcome;
        private string _resumeError;
        private bool _resumeExpired;

        Task<UiSelfInfo> IAgentUiProbeHost.SelfInfo() => OnUi(() => new UiSelfInfo { MainWindow = Handle, TrayText = _tray.Text, TrayVisible = _tray.Visible });

        Task<string> IAgentUiProbeHost.GetWindowState() => OnUi(() =>
        {
            var sb = new StringBuilder();
            int me = Process.GetCurrentProcess().Id;
            DateTime started;
            using (var p = Process.GetCurrentProcess()) started = p.StartTime;
            var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var screens = Screen.AllScreens;
            var screen = Screen.FromControl(this);
            int screenNo = Array.FindIndex(screens, s => s.DeviceName == screen.DeviceName) + 1;
            var fg = Native.GetForegroundWindow();
            uint fgPid = 0;
            if (fg != IntPtr.Zero) Native.GetWindowThreadProcessId(fg, out fgPid);
            bool foreground = fg == Handle;
            var sel = _agentMode ? null : Selected;
            string draft = _agentPanel.DraftText ?? "";

            sb.Append("VSManager 主窗口 / Main window（PID ").Append(me).Append("，启动于 / started ").Append(started.ToString("HH:mm:ss")).AppendLine("）");
            sb.Append("可见 / Visible: ").Append(YesNo(Visible)).Append(" | 状态 / State: ").Append(StateText(WindowState)).Append(" | 置顶 / TopMost: ").Append(YesNo(TopMost)).AppendLine();
            sb.Append("位置大小 / Bounds: ").Append(Rect(Bounds)).Append(" | 常规位置 / Normal bounds: ").Append(Rect(normal)).AppendLine();
            sb.Append("所在屏幕 / Screen: #").Append(screenNo).Append('/').Append(screens.Length).Append(screen.Primary ? "（主屏 / primary）" : "").Append(" 工作区 / working area ").Append(Rect(screen.WorkingArea)).AppendLine();
            sb.Append("前台 / Foreground: ").Append(foreground ? "是，主窗口在前台 / yes, the main window"
                : fgPid == (uint)me ? "本进程的其他窗口在前台 / another window of this process"
                : "否，前台为 / no, foreground is " + UiProbe.ProcessName((int)fgPid) + " (PID " + fgPid + ")").AppendLine();
            sb.Append("当前页面 / Page: ").Append(_agentMode ? "AI 总控助手 / AI assistant" : "VS 对话 / VS chat").AppendLine();
            sb.Append("选中的 VS / Selected VS: ").Append(sel == null ? "无 / none" : NameOf(sel) + "（PID " + sel.Pid + "，" + sel.DisplaySolution + "）").AppendLine();
            sb.Append("AI 输入框草稿 / AI draft: ").Append(draft.Length).Append(" 字 / chars");
            if (draft.Length > 0) sb.Append("：「").Append(TextUtil.Clip(draft, 500)).Append("」");
            sb.AppendLine();
            sb.Append("托盘图标 / Tray icon: ").Append(_tray.Visible ? "显示 / shown" : "隐藏 / hidden").Append("，提示文字 / tooltip「").Append(_tray.Text).AppendLine("」");

            var ui = RestoredUi;
            if (ui == null)
            {
                sb.Append("重启恢复 / Restart restore: 本进程启动时没有读取到界面状态（不是由重启启动，或文件已过期 / 损坏）/ No saved UI state was consumed at startup (not started by a restart, or the file was expired / corrupt).");
                return sb.ToString();
            }
            sb.Append("重启恢复 / Restart restore: 启动时读取了重启前保存的界面状态（保存于 / saved ").Append(ui.CreatedUtc.ToLocalTime().ToString("HH:mm:ss"))
              .Append("，旧 PID / old PID ").Append(ui.OldPid).AppendLine("），对照 / comparison:");
            var saved = new Rectangle(ui.X, ui.Y, ui.Width, ui.Height);
            if (_restoredBounds == null)
                sb.AppendLine("- 位置大小 / Bounds: 保存值 / saved " + Rect(saved) + "，原屏幕不可用，已退回默认位置 / its screen is gone, default placement used");
            else
                sb.Append("- 位置大小 / Bounds: 保存值 / saved ").Append(Rect(saved)).Append("，当前常规位置 / now ").Append(Rect(normal)).Append(" → ").AppendLine(Same(normal == _restoredBounds.Value));
            sb.Append("- 最大化 / Maximized: 保存值 / saved ").Append(YesNo(ui.Maximized)).Append("，当前 / now ").Append(YesNo(WindowState == FormWindowState.Maximized)).Append(" → ")
              .AppendLine(Same(ui.Hidden || ui.Maximized == (WindowState == FormWindowState.Maximized)));
            bool hiddenNow = !Visible || WindowState == FormWindowState.Minimized;
            sb.Append("- 最小化或隐藏 / Minimized or hidden: 保存值 / saved ").Append(YesNo(ui.Hidden)).Append("，当前 / now ").Append(YesNo(hiddenNow)).Append(" → ").AppendLine(Same(ui.Hidden == hiddenNow));
            bool wantAgent = RestartUi.WantsAgentPage(ui, _settings.AgentEnabled);
            sb.Append("- 页面 / Page: ").Append(ui.SelfTest ? "自测恢复目标 / self-test restore target " : "恢复目标 / restore target ")
              .Append(wantAgent ? "AI 总控 / AI assistant" : "VS 对话 / VS chat").Append(" → ").AppendLine(Same(wantAgent == _agentMode));
            if (_pageChange != null) sb.Append("  最近一次页面切换 / Last page switch: ").AppendLine(_pageChange);
            if (_userNavigatedAt >= _restoreStartedAt && _restoreStartedAt > DateTime.MinValue)
                sb.Append("  重启后用户手动切换过 / The user switched manually after the restart: ").Append(_userNavigatedAt.ToString("HH:mm:ss")).Append("（").Append(_userNavigation).AppendLine("）");
            if (!wantAgent && ui.SelectedVsPid > 0)
                sb.Append("- 选中的 VS / Selected VS: 保存值 / saved PID ").Append(ui.SelectedVsPid).Append("，当前 / now ").Append(sel?.Pid.ToString() ?? "无 / none").Append(" → ")
                  .AppendLine(sel?.Pid == ui.SelectedVsPid ? Same(true)
                      : _instances.Any(v => v.Pid == ui.SelectedVsPid) ? Same(false) : "该 VS 已不在实例列表中 / that VS is no longer listed");
            sb.Append("- 草稿 / Draft: 保存值 / saved ").Append((ui.AgentDraft ?? "").Length).Append(" 字 / chars → ").AppendLine(Same((ui.AgentDraft ?? "") == draft || string.IsNullOrEmpty(ui.AgentDraft) && draft.Length == 0));
            sb.Append("- 前台 / Foreground: 重启前 / before ").Append(YesNo(ui.Foreground)).Append("，当前 / now ").Append(YesNo(foreground))
              .AppendLine("；恢复策略是不主动激活；单次采样无法证明期间未抢焦点 / restore never requests activation; a single sample cannot prove no focus steal during the transition");
            sb.Append("- 过渡画面 / Cover: ").Append(string.IsNullOrEmpty(ui.CoverEvent) ? "未使用（无需截图或准备失败）/ not used (unneeded or preparation failed)"
                : _coverSignaled ? "关闭事件已触发（不代表已退出）/ close event signaled (not proof of exit)"
                : _coverReleaseRequested ? "已请求释放，事件已不存在或不可访问 / release requested; event absent or inaccessible" : "尚未请求关闭 / close not yet requested");
            sb.Append("；旧进程确认就绪用时 / readiness acknowledged after: ").Append(ui.CoverReadyMilliseconds).Append(" ms")
              .Append("；恢复控件已同步绘制 / restored controls synchronously painted: ").Append(YesNo(_restoreFramePrepared));
            return sb.ToString();
        });

        Task<string> IAgentUiProbeHost.RestartConsumption() => OnUi(() =>
        {
            var sb = new StringBuilder();
            int me = Process.GetCurrentProcess().Id;
            var ui = RestoredUi;
            sb.Append("【本进程 PID ").Append(me).AppendLine(" 启动时的消费情况 / What this process consumed at startup】");
            sb.Append("界面状态 / UI state: ").AppendLine(ui == null
                ? "未读取到（文件不存在、已过期、损坏或由本进程写入）/ none (absent, expired, corrupt or written by this process)"
                : "已读取并删除（已消费），保存于 / consumed; saved " + ui.CreatedUtc.ToLocalTime().ToString("HH:mm:ss") + "，旧 PID / old PID " + ui.OldPid +
                  "，位置 / bounds " + (_restoredBounds != null ? "已按原值恢复 / restored" : "原屏幕不可用，退回默认 / screen gone, default used"));
            if (_resumeError != null) sb.Append("交接单读取出错 / Handoff read error: ").AppendLine(_resumeError);
            if (_resumedHandoff == null) sb.AppendLine("自测重启交接单 / Self-test handoff: 本进程启动时没有 / none at startup");
            else
            {
                var h = _resumedHandoff;
                sb.Append("自测重启交接单 / Self-test handoff: 已读取并删除（已消费），创建于 / consumed; created ").Append(h.CreatedUtc.ToLocalTime().ToString("HH:mm:ss"))
                  .Append("，PID ").Append(h.OldPid).Append(" → ").Append(me);
                if (_resumeExpired) sb.AppendLine("；已过期，未恢复授权也未续跑测试 / expired: grants were not restored and tests were not resumed");
                else
                {
                    sb.Append("；加载新程序 / new build loaded: ").Append(_resumeOutcome != null && SelfRestart.LoadedNewBuild(h, _resumeOutcome) ? "是 / yes" : "否或无法判断 / no or unknown")
                      .Append("；恢复启动授权 / grants restored: ").Append(_resumeOutcome?.RestoredGrants ?? 0)
                      .Append("；任务 / task #").Append(h.TaskId).AppendLine();
                    if (!string.IsNullOrEmpty(h.TestPlan)) sb.Append("测试计划 / Test plan: ").AppendLine(TextUtil.Clip(h.TestPlan, 600));
                }
            }
            sb.Append("本进程待执行的自测重启 / Pending self-test restart in this process: ").Append(_selfRestart == null ? "无 / none" : "有，等待本轮结束后执行 / yes, runs after this round");
            return sb.ToString();
        });

        private static string YesNo(bool b) => b ? "是 / yes" : "否 / no";
        private static string Same(bool b) => b ? "一致 / match" : "不一致 / differs";
        private static string Rect(Rectangle r) => r.X + "," + r.Y + " " + r.Width + "×" + r.Height;

        private static string StateText(FormWindowState s) =>
            s == FormWindowState.Maximized ? "最大化 / maximized" : s == FormWindowState.Minimized ? "最小化 / minimized" : "常规 / normal";
    }
}
