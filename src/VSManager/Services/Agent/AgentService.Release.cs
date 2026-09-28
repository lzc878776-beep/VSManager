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
        /// <summary>插入补充信息后重试任务（<paramref name="fromUser"/>：信息来自用户，不受 AI 自主重试限制），返回结果文字。/ Retries a task with supplementary info (<paramref name="fromUser"/>: the info comes from the user and bypasses the AI self-retry limits); returns result text.</summary>
        Task<string> RetryTaskWithInfo(int id, string info, bool fromUser);
        /// <summary>非内容类失败后原样重试任务，返回结果文字。/ Retries a task unchanged after a non-content failure; returns result text.</summary>
        Task<string> RetryTask(int id);
    }

    /// <summary>
    /// 可选宿主能力：AI 发布新任务前检查目标 VS 是否正被失败 / 待验证任务阻塞（界面线程之外可调用）。
    /// Optional host capability: before the AI publishes a task, checks whether the target VS is blocked by a failed /
    /// awaiting-verification task (callable from background threads).
    /// </summary>
    public interface IAgentBlockedTaskHost
    {
        /// <summary>目标被阻塞时返回提示文字（应改为向阻塞任务补充信息），否则 null。/ Returns guidance when the target is blocked (supplement the blocker instead), otherwise null.</summary>
        Task<string> CheckBlockedTarget(VsInstance v, SolutionEntry parkFor, string text);
    }

    public sealed partial class AgentService
    {
        private const string ReleaseUnsupported = "当前宿主不支持接续等级 / Continuation levels are unavailable.";

        [Description("设置任务队列的接续等级（与任务清单顶栏的四档滑块同步并保存）：completed=已完成（只有成功才自动执行同一 VS 的下一项，待确认或失败都阻塞后续）；" +
            "needs_user=待确认（待确认阻塞后续，成功与失败放行）；failed=失败（失败阻塞后续，成功与待确认放行）；unlimited=不限（无论结果如何都自动执行下一项，默认）。仅在用户要求调整接续策略时调用。")]
        internal async Task<string> SetReleaseLevel([Description("接续等级：completed / needs_user / failed / unlimited（也可用 已完成 / 待确认 / 失败 / 不限）")] string level)
        {
            if (!(_host is IAgentReleaseHost host)) return ReleaseUnsupported;
            var parsed = ReleaseLevels.TryParse(level);
            if (parsed == null) return "无法识别的接续等级「" + level + "」，可选 completed / needs_user / failed / unlimited / Unknown continuation level.";
            if (parsed.Value == host.ReleaseLevel) return "接续等级已是「" + ReleaseLevels.ShortName(parsed.Value) + "」，无需修改 / Already at this level.\n" + ReleaseLevels.Describe(parsed.Value);
            if (_settings().AgentConfirm && !await ConfirmAsync("修改接续等级为「" + ReleaseLevels.ShortName(parsed.Value) + "」", ReleaseLevels.Describe(parsed.Value)))
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

        [Description("为失败或待验证的任务插入补充信息后在原条目重新排队重试：补充信息与前次反馈一起发给原 VS 的 Copilot，阻塞随之解除。" +
            "任务失败 / 待验证并阻塞同一 VS 的后续任务时，用户或你对该任务的补充、修正、追加要求或验证反馈都应通过本工具发给该任务，不要用 send_task 另起新任务（新任务只会排在阻塞任务后面）。" +
            "接续等级使失败阻塞后续时，若你能根据 VS 返回的信息补齐缺失内容（例如指明文件、澄清要求、给出已知参数），可自行调用；" +
            "需要用户决定或只有用户知道的信息时，先询问用户再把用户的答复作为 info 传入，并设 from_user=true。" +
            "你自主补充时每个任务有补充次数上限，同一需求由你自主触发的 Copilot 执行（含重发链）也有次数上限（见系统提示与失败通知），info 必须包含新的具体信息，只写「请重试」会被拒绝；" +
            "from_user=true 表示 info 来自用户本轮消息，不受这些限制，但需用户确认。" +
            "投递、读取失败或 Copilot 本轮中断时不必补充信息，改用 retry_task。")]
        internal async Task<string> RetryTaskWithInfo(
            [Description("任务编号，如 3")] int id,
            [Description("补充信息：要告诉 Copilot 的新信息、澄清或修正做法，简洁具体")] string info,
            [Description("info 是否来自用户本轮消息（用户给出的补充、修正或验证反馈）；为 true 时不受 AI 自主重试次数限制，但会请用户确认。不要把你自己推断的内容标为 true。")] bool from_user = false)
        {
            if (!(_host is IAgentReleaseHost host)) return ReleaseUnsupported;
            if (string.IsNullOrWhiteSpace(info)) return "补充信息不能为空 / Supplementary info is empty.";
            // 用户补充绕过 AI 自主限制，因此总是请用户确认 / User supplements bypass the AI limits, so always confirm
            if ((from_user || _settings().AgentConfirm) && !await ConfirmAsync("补充信息后重试任务 #" + id + (from_user ? "（来自用户 / from the user）" : ""), info))
                return "用户拒绝了该操作。";
            return await host.RetryTaskWithInfo(id, info, from_user).ConfigureAwait(false);
        }

        [Description("原样重试因非任务内容原因失败的任务：投递失败、目标 VS 关闭、读取回复失败，或 Copilot 本轮没执行完（网络 / 服务错误、被中断、没有回复）。" +
            "这类失败不计入需求的执行次数，也不需要新内容；每个任务最多直接重试 " + TaskFailureAnalyzer.MaxRecoveryRetriesText + " 次，超过后交给用户检查。" +
            "Copilot 执行完但任务失败（缺少回执、回报失败）时会被拒绝，应用 retry_task_with_info 写明新信息或交给用户。")]
        internal async Task<string> RetryTask([Description("任务编号，如 3")] int id)
        {
            if (!(_host is IAgentReleaseHost host)) return ReleaseUnsupported;
            if (_settings().AgentConfirm && !await ConfirmAsync("重试任务 #" + id, "任务 #" + id + " 因投递、读取或 Copilot 本轮中断而失败，将原样重新排队。"))
                return "用户拒绝了该操作。";
            return await host.RetryTask(id).ConfigureAwait(false);
        }
    }
}
