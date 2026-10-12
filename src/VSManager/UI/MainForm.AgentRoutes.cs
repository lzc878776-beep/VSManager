using System;
using System.Linq;

namespace VSManager
{
    /// <summary>
    /// 为「AI 助手」标题提供消息发布到 / 结果来自哪个 VS 的信息：按任务编号查目标 VS，并在任务通知中带上来源 VS。
    /// Supplies which VS an AI assistant message published to / got results from: looks up a task's VS by id and passes the source VS with task notices.
    /// </summary>
    public partial class MainForm : IAgentTaskVsHost, ISourcedAgentNotifyHost
    {
        string IAgentTaskVsHost.TaskVsName(int id)
        {
            // 可能在后台线程调用：取快照，列表恰好在变化时放弃 / May run on a background thread: take a snapshot and give up if the list is changing
            try { return _tasks.Items.ToList().FirstOrDefault(t => t != null && t.Id == id)?.VsName; }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
        }

        void ISourcedAgentNotifyHost.NotifyAgent(string scope, string source, string title, string body) => _agent.Notify(title, body, scope ?? "", source);
    }
}
