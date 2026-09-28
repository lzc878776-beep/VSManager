using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 可选宿主能力：启动任务流程（与任务清单的「开始流程 / Start」按钮等效，可在后台线程调用）。
    /// Optional host capability: start the task workflow (same as the task list's Start button; callable from background threads).
    /// </summary>
    public interface IAgentWorkflowHost
    {
        /// <summary>本次会话的任务流程是否已启动。/ Whether the task workflow has been started in this session.</summary>
        bool WorkflowStarted { get; }
        /// <summary>启动任务流程并同步界面按钮，返回结果文字。/ Starts the workflow, syncs the UI button and returns result text.</summary>
        Task<string> StartWorkflow();
    }

    public sealed partial class AgentService
    {
        [Description("启动任务流程：与任务清单顶栏的「开始流程 / Start」按钮等效，界面按钮会同步显示「已启动」；本次会话内有效，之后所有排队任务（含手动与恢复的任务）按编号自动调度，不会插队。" +
            "list_tasks 显示任务「等待手动授权」而用户希望它们执行时调用。/ Start the task workflow: same as the Start button in the task list header, which then shows \"Started\"; valid for this session, " +
            "after which every queued task (including manual and restored ones) dispatches in ID order without jumping the queue. Call it when list_tasks shows tasks waiting for manual start and the user wants them to run.")]
        internal async Task<string> StartTaskWorkflow()
        {
            if (!(_host is IAgentWorkflowHost host)) return "当前宿主不支持启动任务流程 / Starting the task workflow is unavailable.";
            if (host.WorkflowStarted) return "任务流程已启动，无需重复启动 / The task workflow is already started.";
            if (_settings().AgentConfirm && !await ConfirmAsync("启动任务流程", "等同点击任务清单的「开始流程 / Start」：本次会话内所有排队任务（含手动与恢复的任务）将按编号自动调度。"))
                return "用户拒绝了该操作。/ The user declined.";
            return await host.StartWorkflow().ConfigureAwait(false);
        }
    }
}
