using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    public partial class MainForm : IAgentDesktopHost, IAgentCopilotPaneHost, IAgentDocumentHost, IAgentScreenshotHost, IAgentCadDebugHost, IAgentMcpHost, IAgentCadActionHost, IAgentVerifyHost
    {
        async Task<IList<int>> IAgentVerifyHost.DebuggedProcessIds(VsInstance v) =>
            v == null ? new List<int>() : await DteWorker.Run(() => VsService.DebuggedProcessIds(v)).ConfigureAwait(false);

        /// <summary>MCP 服务器连接中心（第一版不预装任何服务器）。/ MCP server hub (no server is preinstalled in this version).</summary>
        private readonly McpHub _mcp = new McpHub();

        /// <summary>
        /// CAD 代理连接文件已存在时随设置更新（Web 远程开关、端口或密钥变化）。
        /// Refreshes the CAD agent connection file with the settings when it already exists (Web remote switch, port or key changes).
        /// </summary>
        private void RefreshCadAgentConnection()
        {
            try
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(AppPaths.DataFolder, VSManager.CadAgent.CadAgentConnection.FileName)))
                    CadActionBroker.WriteConnection(_settings);
            }
            catch (Exception ex) { AppLog.Write("cad.log", "连接文件更新失败 / Connection file update failed: " + ex.Message); }
        }

        void IAgentCadActionHost.ShowCadArtifacts(string title, VSManager.CadAgent.CadSequenceResult result)
        {
            if (IsDisposed || result == null) return;
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                new CadArtifactsForm(title, result).Show(this);
            }));
        }

        IReadOnlyList<McpRegisteredTool> IAgentMcpHost.McpTools() => _mcp.Tools;

        string IAgentMcpHost.McpStatusText() => _mcp.StatusText();

        async Task<string> IAgentMcpHost.ReconnectMcp()
        {
            await _mcp.Reconnect().ConfigureAwait(false);
            return _mcp.StatusText();
        }

        string IAgentCadDebugHost.GetCadDrawing(VsInstance v) => _settings.GetCadDrawing(v?.SolutionPath);

        Task<string> IAgentCadDebugHost.SetCadDrawing(VsInstance v, string drawing) => OnUi(() =>
        {
            _settings.SetCadDrawing(v.SolutionPath, drawing);
            _settings.Save();
            string r = string.IsNullOrWhiteSpace(drawing)
                ? "已清除「" + NameOf(v) + "」的 CAD 调试图纸，调试时打开新图 / Cleared the CAD debug drawing; debugging opens a new drawing"
                : "已记录「" + NameOf(v) + "」的 CAD 调试图纸：" + drawing + "；下次 CAD 调试启动时自动打开 / Recorded the CAD debug drawing; it opens on the next CAD debug launch";
            SetStatus(r);
            return r;
        });

        Task<string> IAgentDocumentHost.CloseCsDocuments(VsInstance vs) => OnUiAsync(async () => (await CloseCsTabsAsync(vs)).Text);

        /// <summary>关闭目标 VS 中所有 .cs 标签页并在状态栏显示结果。/ Closes all .cs tabs in the target VS and shows the result in the status bar.</summary>
        private async Task<VsService.CsTabCloseResult> CloseCsTabsAsync(VsInstance vs)
        {
            VsService.CsTabCloseResult r;
            var foreground = ForegroundKeeper.Capture();
            try { r = await DteWorker.Run(() => _vsOps.CloseCsDocuments(vs)); }
            catch (Exception ex) { r = VsService.CsTabCloseResult.Fail("关闭 .cs 文件标签页失败 / Failed to close .cs tabs：" + ex.Message); }
            RestoreForeground(foreground, vs);
            SetStatus($"「{NameOf(vs)}」{r.Text}");
            return r;
        }

        /// <summary>
        /// 快捷按钮：依次关闭所有已打开 VS 中的 .cs 标签页（未保存的保留、不保存），返回汇总并弹出通知。
        /// Quick button: closes the .cs tabs in every open VS in turn (unsaved ones kept, not saved), returns the summary and shows a notice.
        /// </summary>
        private async Task<string> CloseCsTabsInAllVsAsync()
        {
            var list = _instances.ToList();
            var results = new List<(string Label, VsService.CsTabCloseResult Result)>();
            for (int i = 0; i < list.Count; i++)
                results.Add(("#" + (i + 1) + " " + NameOf(list[i]), await CloseCsTabsAsync(list[i])));
            string summary = VsService.CsTabCloseResult.Summarize(results);
            SetStatus(summary.Split('\n')[0]);
            try
            {
                const string title = "🗂 关闭 .cs 标签页 / Close .cs tabs";
                if (_settings.Popup) new ToastForm(title, summary, () => { ShowMe(); }).Show();
                else ShowBalloon(title, summary, results.Any(r => r.Result.Dirty.Count > 0 || r.Result.Failed.Count > 0 || r.Result.Error != null) ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            catch { }
            return summary;
        }

        async Task<string> IAgentCopilotPaneHost.OpenCopilotPane(VsInstance vs)
        {
            string name = await OnUi(() => NameOf(vs)).ConfigureAwait(false);
            CopilotPaneOpenResult r;
            try { r = await DteWorker.RunSta(() => _chatSvc.OpenChat(vs)).ConfigureAwait(false); }
            catch (Exception ex)
            {
                SendLog.Event(name, "打开对话助手异常 / open chat error：" + ex);
                r = new CopilotPaneOpenResult();
                r.Step("异常 / error：" + ex.Message);
            }
            SendLog.Event(name, "打开对话助手 / open Copilot chat：" + (r.Ok ? "成功 / ok" : "失败 / failed") + " " + r.Diagnostics());
            string zh = r.MessageZh(name), en = r.MessageEn(name);
            SafeInvoke(() =>
            {
                SetStatus(zh + " / " + en);
                NotifyWithVoice(r.Ok ? "💬 对话助手 / Copilot chat" : "⚠ 对话助手 / Copilot chat", zh, en);
                _chatSvc.PaneRestored(vs.Pid);
            });
            return r.Message(name) + $"（DTE={r.UsedDte}，退出历史 / left history={r.LeftHistory}，输入框焦点 / focused={r.Focused}，{r.ElapsedMs}ms；详见发送日志 / see the send log）";
        }

        Task<byte[]> IAgentDesktopHost.CaptureApprovedScreenshot(VsInstance vs, string destination, CancellationToken cancellationToken) =>
            OnUiAsync(async () =>
            {
                byte[] png = await CaptureVsAsync(vs, cancellationToken);
                ShowMe();
                bool approved = AgentToolApproval.Show(this, "截图共享审批 / Approve screenshot sharing",
                    "目标 / Target: " + NameOf(vs) + "\r\n" + destination
                    + "\r\n\r\n批准后，此图片将发送给上述模型服务；不保存截图文件。请取消包含代码、密钥、个人信息或其他敏感内容的截图。\r\n"
                    + "Approval sends this image to the model service shown above. No screenshot file is saved. Cancel if it contains sensitive content.",
                    png, cancellationToken);
                return approved ? png : null;
            });

        /// <summary>直接截图（不预览），截图后切回原前台窗口。/ Captures directly (no preview) and switches back to the previous foreground window.</summary>
        Task<byte[]> IAgentScreenshotHost.CaptureScreenshot(VsInstance vs, CancellationToken cancellationToken) =>
            OnUiAsync(async () =>
            {
                IntPtr previous = Native.GetForegroundWindow();
                try
                {
                    byte[] png = await CaptureVsAsync(vs, cancellationToken);
                    SetStatus("📷 AI 助手已读取「" + NameOf(vs) + "」的截图 / The AI assistant read a screenshot of this VS");
                    return png;
                }
                finally
                {
                    if (previous != IntPtr.Zero && previous != vs.MainHwnd && Native.IsWindow(previous)) Native.Activate(previous);
                }
            });

        /// <summary>AI Skill 截图：按设置直接截图或先预览批准。/ AI Skill screenshot: direct capture or preview approval per settings.</summary>
        Task<byte[]> IRemoteHost.CaptureScreenshot(VsInstance vs, bool requirePreview, CancellationToken cancellationToken) =>
            requirePreview
                ? ((IAgentDesktopHost)this).CaptureApprovedScreenshot(vs, "本机外部 AI 客户端（AI Skill）/ Local external AI client (AI Skill)", cancellationToken)
                : ((IAgentScreenshotHost)this).CaptureScreenshot(vs, cancellationToken);

        /// <summary>把目标 VS（或其前台弹窗）切到前台并截图，需在界面线程调用。/ Brings the target VS (or its popup) to the front and captures it; call on the UI thread.</summary>
        private async Task<byte[]> CaptureVsAsync(VsInstance vs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Native.IsWindow(vs.MainHwnd)) throw new InvalidOperationException("目标 VS 已关闭 / Target VS is closed.");
            var window = Native.IsWindowEnabled(vs.MainHwnd) ? vs.MainHwnd : Native.GetLastActivePopup(vs.MainHwnd);
            Native.GetWindowThreadProcessId(window, out uint pid);
            if (pid != (uint)vs.Pid) throw new InvalidOperationException("目标窗口已改变 / Target window changed.");
            Native.Activate(window);
            await Task.Delay(200, cancellationToken);
            byte[] png = AgentScreenshot.Capture(vs);
            cancellationToken.ThrowIfCancellationRequested();
            return png;
        }

        Task<bool> IAgentDesktopHost.ApprovePowerShell(string detail, CancellationToken cancellationToken) =>
            OnUi(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShowMe();
                return AgentToolApproval.Show(this, "PowerShell 脚本审批 / Approve PowerShell script", detail, null, cancellationToken);
            });
    }
}
