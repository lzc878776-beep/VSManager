using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>可选宿主能力：真正打开目标 VS 的 Copilot 对话。/ Optional host capability: really open the Copilot chat of a target VS.</summary>
    public interface IAgentCopilotPaneHost
    {
        /// <summary>打开对话助手到当前会话并聚焦输入框，返回结果文字（中文在前，英文在后）。/ Opens the chat on the current conversation and focuses the input; returns bilingual result text.</summary>
        Task<string> OpenCopilotPane(VsInstance vs);
    }

    public sealed partial class AgentService
    {
        [Description("真正打开指定 VS 的 Copilot 对话助手：显示工具窗口（取消自动隐藏）、从聊天历史列表切回当前会话，并校验输入框可编辑后聚焦（会把该 VS 切到前台）。窗格停留在历史记录、找不到输入框或用户要求打开对话助手时调用；不发送任何内容。")]
        internal async Task<string> OpenCopilot([Description("VS 编号（如 \"1\"）或名称")] string vs)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            if (!(_host is IAgentCopilotPaneHost host)) return "当前宿主不支持打开对话助手 / Opening the Copilot chat is unavailable.";
            return await host.OpenCopilotPane(v).ConfigureAwait(false);
        }
    }
}
