using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 可选宿主能力：任务队列放行等级、放行与补充信息重试（均可在后台线程调用）。
    /// Optional host capability: task queue release level, release and retry with supplementary info (callable from background threads).
    /// </summary>
    public interface IAgentReleaseHost
    {
        /// <summary>当前放行等级。/ The current release level.</summary>
        ReleaseLevel ReleaseLevel { get; }
        /// <summary>设置并保存放行等级（同步顶栏滑块），返回结果文字。/ Sets and saves the release level (syncs the header slider); returns result text.</summary>
        Task<string> SetReleaseLevel(ReleaseLevel level);
        /// <summary>放行失败 / 待验证的任务，返回结果文字。/ Releases a failed / awaiting-verification task; returns result text.</summary>
        Task<string> ReleaseTask(int id);
        /// <summary>插入补充信息后重试任务，返回结果文字。/ Retries a task with supplementary info; returns result text.</summary>
        Task<string> RetryTaskWithInfo(int id, string info);
    }

    public sealed partial class AgentService
    {
        private const string ReleaseUnsupported = "当前宿主不支持放行等级 / Release levels are unavailable.";

        [Description("设置任务队列的放行等级（与侧边栏顶栏三刻度滑块同步并保存）：completed=已完成（只有成功才自动执行同一 VS 的下一项，待验证或失败都暂停后续）；" +
            "needs_user=待验证（已完成与待验证自动放行，失败暂停后续）；failed=失败（无论结果如何都自动执行下一项，默认）。仅在用户要求调整放行策略时调用。")]
        internal async Task<string> SetReleaseLevel([Description("放行等级：completed / needs_user / failed（也可用 已完成 / 待验证 / 失败）")] string level)
        {
            if (!(_host is IAgentReleaseHost host)) return ReleaseUnsupported;
            var parsed = ReleaseLevels.TryParse(level);
            if (parsed == null) return "无法识别的放行等级「" + level + "」，可选 completed / needs_user / failed / Unknown release level.";
            if (parsed.Value == host.ReleaseLevel) return "放行等级已是「" + ReleaseLevels.ShortName(parsed.Value) + "」，无需修改 / Already at this level.\n" + ReleaseLevels.Describe(parsed.Value);
            if (_settings().AgentConfirm && !await ConfirmAsync("修改放行等级为「" + ReleaseLevels.ShortName(parsed.Value) + "」", ReleaseLevels.Describe(parsed.Value)))
                return "用户拒绝了该操作。";
            return await host.SetReleaseLevel(parsed.Value).ConfigureAwait(false);
        }

        [Description("放行失败或待验证的任务：该任务的结果与记录保持不变，但不再暂停同一 VS 的后续任务，后续会自动继续发布。" +
            "用户确认忽略失败继续执行、或确认待验证内容已验证通过时调用。")]
        internal async Task<string> ReleaseTask([Description("任务编号，如 3")] int id)
        {
            if (!(_host is IAgentReleaseHost host)) return ReleaseUnsupported;
            if (_settings().AgentConfirm && !await ConfirmAsync("放行任务 #" + id, "任务 #" + id + " 的结果保持不变，同一 VS 的后续排队任务将继续执行。"))
                return "用户拒绝了该操作。";
            return await host.ReleaseTask(id).ConfigureAwait(false);
        }

        [Description("为失败（或待验证但验证未通过）的任务插入补充信息后重新排队重试：补充信息与前次失败反馈一起发给原 VS 的 Copilot。" +
            "放行等级使失败阻塞后续时，若你能根据 VS 返回的信息补齐缺失内容（例如指明文件、澄清要求、给出已知参数），可自行调用；" +
            "需要用户决定或只有用户知道的信息时，先询问用户再把用户的答复作为 info 传入。每个任务最多补充 3 次。")]
        internal async Task<string> RetryTaskWithInfo(
            [Description("任务编号，如 3")] int id,
            [Description("补充信息：要告诉 Copilot 的新信息、澄清或修正做法，简洁具体")] string info)
        {
            if (!(_host is IAgentReleaseHost host)) return ReleaseUnsupported;
            if (string.IsNullOrWhiteSpace(info)) return "补充信息不能为空 / Supplementary info is empty.";
            if (_settings().AgentConfirm && !await ConfirmAsync("补充信息后重试任务 #" + id, info))
                return "用户拒绝了该操作。";
            return await host.RetryTaskWithInfo(id, info).ConfigureAwait(false);
        }
    }
}
