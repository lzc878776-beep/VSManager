using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>可选宿主能力：管理 VS 中已打开的文档标签页。/ Optional host capability: manage the open document tabs of a VS.</summary>
    public interface IAgentDocumentHost
    {
        /// <summary>关闭目标 VS 中所有已打开的 .cs 文件标签页（有未保存修改的保留），返回结果文字（中文在前，英文在后）。/ Closes all open .cs tabs in the target VS (unsaved ones are kept); returns bilingual result text.</summary>
        Task<string> CloseCsDocuments(VsInstance vs);
    }

    public sealed partial class AgentService
    {
        [Description("关闭指定 VS 中所有已打开的 .cs 文件标签页（仅 .cs，不含 .cshtml / .csproj 等）。有未保存修改的文件不会关闭也不会保存，会在结果中列出；不影响其他类型的文件与工具窗口。用户要求关闭 / 清理 .cs 标签页时调用。")]
        internal async Task<string> CloseCsTabs([Description("VS 编号（如 \"1\"）或名称")] string vs)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            if (!(_host is IAgentDocumentHost host)) return "当前宿主不支持关闭文件标签页 / Closing document tabs is unavailable.";
            if (_settings().AgentConfirm && !await ConfirmAsync("在「" + _host.NameOf(v) + "」中关闭 .cs 标签页",
                    "关闭所有已打开的 .cs 文件标签页（有未保存修改的保留）。/ Close all open .cs tabs (unsaved ones are kept)."))
                return "用户拒绝了该操作。/ The user declined.";
            return await host.CloseCsDocuments(v).ConfigureAwait(false);
        }
    }
}
