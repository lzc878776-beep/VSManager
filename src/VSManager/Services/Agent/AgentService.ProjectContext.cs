using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 可选宿主能力：返回目标项目摘要（项目名、职责描述、该 VS 最近 3 条任务结果），附在 send_task 的返回结果中；未实现时不附加。
    /// Optional host capability: returns the target project's summary (name, responsibility, latest 3 task outcomes of that VS),
    /// appended to send_task results; nothing is appended when not implemented.
    /// </summary>
    public interface IAgentProjectContextHost
    {
        Task<string> ProjectContext(string vsKey, string vsName);
    }

    public sealed partial class AgentService
    {
        /// <summary>在任务发布结果后附上项目摘要；失败时原样返回。/ Appends the project summary to a submission result; returns it unchanged on failure.</summary>
        private async Task<string> WithProjectContext(string result, string vsKey, string vsName)
        {
            if (!(_host is IAgentProjectContextHost host) || string.IsNullOrEmpty(result)) return result;
            try
            {
                string ctx = await host.ProjectContext(vsKey, vsName).ConfigureAwait(false);
                return string.IsNullOrEmpty(ctx) ? result : result + "\n" + ctx;
            }
            catch (System.Exception ex)
            {
                AppLog.Write(LogFile, "生成项目摘要失败 / Project summary failed: " + ex.Message);
                return result;
            }
        }
    }
}
