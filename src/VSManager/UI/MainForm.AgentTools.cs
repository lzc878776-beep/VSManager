using System;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    public partial class MainForm : IAgentDesktopHost, IAgentCopilotPaneHost, IAgentDocumentHost, IAgentScreenshotHost
    {
        Task<string> IAgentDocumentHost.CloseCsDocuments(VsInstance vs) => OnUiAsync(() => CloseCsTabsAsync(vs));

        /// <summary>关闭目标 VS 中所有 .cs 标签页并在状态栏显示结果。/ Closes all .cs tabs in the target VS and shows the result in the status bar.</summary>
        private async Task<string> CloseCsTabsAsync(VsInstance vs)
        {
            string r;
            try { r = await DteWorker.Run(() => _vsOps.CloseCsDocuments(vs)); }
            catch (Exception ex) { r = "关闭 .cs 文件标签页失败 / Failed to close .cs tabs：" + ex.Message; }
            SetStatus($"「{NameOf(vs)}」{r}");
            return r;
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
