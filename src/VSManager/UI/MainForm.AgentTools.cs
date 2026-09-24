using System;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    public partial class MainForm : IAgentDesktopHost, IAgentCopilotPaneHost
    {
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
                cancellationToken.ThrowIfCancellationRequested();
                if (!Native.IsWindow(vs.MainHwnd)) throw new InvalidOperationException("目标 VS 已关闭 / Target VS is closed.");
                var window = Native.IsWindowEnabled(vs.MainHwnd) ? vs.MainHwnd : Native.GetLastActivePopup(vs.MainHwnd);
                Native.GetWindowThreadProcessId(window, out uint pid);
                if (pid != (uint)vs.Pid) throw new InvalidOperationException("目标窗口已改变 / Target window changed.");
                Native.Activate(window);
                await Task.Delay(200, cancellationToken);
                byte[] png = AgentScreenshot.Capture(vs);
                cancellationToken.ThrowIfCancellationRequested();
                ShowMe();
                bool approved = AgentToolApproval.Show(this, "截图共享审批 / Approve screenshot sharing",
                    "目标 / Target: " + NameOf(vs) + "\r\n" + destination
                    + "\r\n\r\n批准后，此图片将发送给上述模型服务；不保存截图文件。请取消包含代码、密钥、个人信息或其他敏感内容的截图。\r\n"
                    + "Approval sends this image to the model service shown above. No screenshot file is saved. Cancel if it contains sensitive content.",
                    png, cancellationToken);
                return approved ? png : null;
            });

        Task<bool> IAgentDesktopHost.ApprovePowerShell(string detail, CancellationToken cancellationToken) =>
            OnUi(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShowMe();
                return AgentToolApproval.Show(this, "PowerShell 脚本审批 / Approve PowerShell script", detail, null, cancellationToken);
            });
    }
}
