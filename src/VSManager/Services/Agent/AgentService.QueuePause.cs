using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 可选宿主能力：暂停 / 继续任务队列（可在后台线程调用）。
    /// Optional host capability: pauses / resumes the task queue (callable from background threads).
    /// </summary>
    public interface IAgentQueuePauseHost
    {
        /// <summary>队列是否已暂停。/ Whether the queue is paused.</summary>
        bool QueuePaused { get; }
        /// <summary>暂停（可同时中断执行中的任务）或继续，返回结果文字。/ Pauses (optionally interrupting running tasks) or resumes; returns result text.</summary>
        Task<string> SetQueuePaused(bool paused, bool interruptRunning);
    }

    public sealed partial class AgentService
    {
        [Description("暂停或继续任务队列（与任务清单顶栏的「暂停 / 继续」按钮同步，重开 VSManager 后保持）。暂停后不再发布新任务，排队任务全部保留；" +
            "interrupt_running=true 时同时停止执行中任务的 Copilot 并把任务重新排队，继续后会重新发送并提示 Copilot 在已有改动上接着做（需用户确认）。" +
            "仅在用户要求暂停、中断或继续任务队列时调用。")]
        internal async Task<string> PauseTaskQueue(
            [Description("true = 暂停，false = 继续")] bool paused,
            [Description("暂停时是否中断执行中的任务（停止 Copilot 并重新排队）；默认 false：让执行中的任务做完")] bool interrupt_running = false)
        {
            if (!(_host is IAgentQueuePauseHost host)) return "当前宿主不支持暂停任务队列 / Pausing the task queue is unavailable.";
            if (paused && interrupt_running && !await ConfirmAsync("暂停任务队列并中断执行中的任务 / Pause and interrupt running tasks",
                    "执行中任务的 Copilot 会被停止，任务重新排队；点「继续」后重新发送并接着执行。/ Running Copilot work is stopped and requeued; it is re-sent and continued after Resume."))
                return "用户拒绝了该操作。";
            return await host.SetQueuePaused(paused, paused && interrupt_running).ConfigureAwait(false);
        }
    }
}
