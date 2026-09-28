using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 可选宿主能力：取消 / 删除前读取任务摘要，以及从任务清单中删除任务（可在后台线程调用）。
    /// Optional host capability: reads a task summary before cancelling / deleting, and deletes a task from the list (callable from background threads).
    /// </summary>
    public interface IAgentTaskControlHost
    {
        /// <summary>任务摘要（编号、状态、目标与内容）；任务不存在时返回 null。/ Task summary (id, status, target, text); null when the task does not exist.</summary>
        Task<string> DescribeTask(int id);
        /// <summary>从任务清单删除任务（归档保留），返回结果文字。/ Deletes the task from the list (the archive is kept); returns result text.</summary>
        Task<string> DeleteTask(int id);
    }

    public sealed partial class AgentService
    {
        [Description("取消任务清单中的任务：排队中、等待目标 VS、执行中（只停止跟踪，不会停止 Copilot；需要停止请用 stop_copilot），" +
            "以及已结束的失败、待验证或已完成任务（保留结果与失败记录，不再暂停同一 VS 的后续任务）。发送中的任务不能取消。" +
            "每次调用都会先弹窗请用户确认（不受「操作前确认」设置影响），用户拒绝时不要重复调用。")]
        internal async Task<string> CancelTask([Description("任务编号，如 3")] int id)
        {
            string summary = await TaskSummary(id).ConfigureAwait(false);
            if (summary == null) return "没有任务 #" + id + " / No task #" + id;
            // 取消总是需要用户确认 / Cancelling always requires the user's confirmation
            if (!await ConfirmAsync("取消任务 #" + id + " / Cancel task",
                    "AI 助手请求取消以下任务（记录保留，可在任务清单中手动重新排队）：\r\nThe assistant asks to cancel this task (the record is kept and can be requeued manually):\r\n\r\n" + summary))
                return "用户拒绝取消任务 #" + id + "，请勿重复请求 / The user declined to cancel task #" + id + "; do not ask again.";
            return await _host.CancelTask(id).ConfigureAwait(false);
        }

        [Description("从任务清单中删除任务（tasks.json 中移除，归档记录保留；发送中的任务与 Worktree 记录不能删除）。" +
            "仅在用户明确要求删除时调用；每次调用都会先弹窗请用户确认（不受「操作前确认」设置影响），用户拒绝时不要重复调用。只想停止或忽略任务时改用 cancel_task。")]
        internal async Task<string> DeleteTask([Description("任务编号，如 3")] int id)
        {
            if (!(_host is IAgentTaskControlHost host)) return "当前宿主不支持删除任务 / Deleting tasks is unavailable.";
            string summary = await TaskSummary(id).ConfigureAwait(false);
            if (summary == null) return "没有任务 #" + id + " / No task #" + id;
            // 删除总是需要用户确认 / Deleting always requires the user's confirmation
            if (!await ConfirmAsync("删除任务 #" + id + " / Delete task",
                    "AI 助手请求从任务清单删除以下任务（归档记录保留，删除后不能在清单中恢复）：\r\nThe assistant asks to delete this task from the list (the archive is kept; it cannot be restored in the list):\r\n\r\n" + summary))
                return "用户拒绝删除任务 #" + id + "，请勿重复请求 / The user declined to delete task #" + id + "; do not ask again.";
            return await host.DeleteTask(id).ConfigureAwait(false);
        }

        private async Task<string> TaskSummary(int id)
        {
            if (_host is IAgentTaskControlHost host) return await host.DescribeTask(id).ConfigureAwait(false);
            return "任务 #" + id + " / Task #" + id;
        }
    }
}
