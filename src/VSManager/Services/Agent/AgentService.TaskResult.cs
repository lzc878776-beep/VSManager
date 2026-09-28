using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 可选宿主能力：修改任务清单中任务的结果文字（可在后台线程调用）。
    /// Optional host capability: edits the result text of a task in the task list (callable from background threads).
    /// </summary>
    public interface IAgentTaskResultHost
    {
        /// <summary>修改结果文字并保存、刷新界面，返回结果说明。/ Edits the result text, saves and refreshes the UI; returns a description.</summary>
        Task<string> EditTaskResult(int id, string text);
    }

    public sealed partial class AgentService
    {
        [Description("修改任务清单中某个已结束任务的结果文字（例如把 VS 返回的英文测试清单翻译为中文），保存到 tasks.json 并刷新界面；" +
            "不改变任务状态、编号、排队与调度。text 是完整的新结果文字（会整体替换原结果），测试清单每项单独一行、使用「- [ ] 具体操作与预期结果」格式；" +
            "待验证任务的测试清单会按新文字更新（项数不变时保留已勾选状态）。先用 list_tasks 查看原结果与测试清单，只在用户要求修改时调用。")]
        internal async Task<string> EditTaskResult(
            [Description("任务编号，如 3")] int id,
            [Description("新的完整结果文字，最多 4000 字")] string text)
        {
            if (!(_host is IAgentTaskResultHost host)) return "当前宿主不支持修改任务结果 / Editing task results is unavailable.";
            if (string.IsNullOrWhiteSpace(text)) return "结果文字不能为空 / The result text is empty.";
            if (text.Length > TaskQueue.MaxResultChars)
                return $"结果文字过长（{text.Length} 字，上限 {TaskQueue.MaxResultChars}）/ Result text too long ({text.Length}, max {TaskQueue.MaxResultChars}).";
            if (_settings().AgentConfirm && !await ConfirmAsync("修改任务 #" + id + " 的结果文字", TextUtil.Clip(text, 1500)))
                return "用户拒绝了该操作。/ The user declined.";
            return await host.EditTaskResult(id, text).ConfigureAwait(false);
        }
    }
}
