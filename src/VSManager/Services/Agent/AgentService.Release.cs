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
        /// <summary>
        /// 插入补充信息后重试任务（<paramref name="fromUser"/>：信息来自用户，不受 AI 自主重试限制；<paramref name="replace"/>：info 为整合后的完整说明，替换此前的补充、前次反馈与接续说明；
        /// <paramref name="freshContext"/>：Copilot 对话已清空），返回结果文字。
        /// Retries a task with supplementary info (<paramref name="fromUser"/>: the info comes from the user and bypasses the AI self-retry limits;
        /// <paramref name="replace"/>: the info is a consolidated brief replacing earlier supplements, feedback and continuation note;
        /// <paramref name="freshContext"/>: the Copilot conversation was cleared); returns result text.
        /// </summary>
        Task<string> RetryTaskWithInfo(int id, string info, bool fromUser, bool replace, bool freshContext);
        /// <summary>非内容类失败后重试任务（<paramref name="note"/>：可选的接续说明；<paramref name="freshContext"/>：Copilot 对话已清空），返回结果文字。/ Retries a task after a non-content failure (<paramref name="note"/>: optional continuation note; <paramref name="freshContext"/>: the Copilot conversation was cleared); returns result text.</summary>
        Task<string> RetryTask(int id, string note, bool freshContext);
    }

    /// <summary>
    /// 可选宿主能力：分页读取失败任务保存的本轮 Copilot 完整回复（界面线程之外可调用）。
    /// Optional host capability: reads the whole Copilot turn stored for a failed task, page by page (callable from background threads).
    /// </summary>
    public interface IAgentTaskReplyHost
    {
        Task<string> ReadTaskReply(int id, int page);
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

    /// <summary>
    /// 可选宿主能力：为待验证或部分完成的任务向同一 VS 发布接续任务（界面线程之外可调用）。
    /// Optional host capability: publishes a continuation to the same VS for a task awaiting verification or partially done (callable from background threads).
    /// </summary>
    public interface IAgentContinuationHost
    {
        /// <summary>发布接续任务并返回结果文字（以「✅」「⏳」「❌」开头，与 send_task 相同）。/ Publishes the continuation and returns result text (starting with ✅ / ⏳ / ❌ like send_task).</summary>
        Task<string> ContinueTask(int id, string remaining, string title);
    }

    public sealed partial class AgentService
    {
        [Description("任务自动接续：任务返回待验证、或已完成但回复提到还有剩余步骤时，为它向同一 VS 发布接续任务，只做剩余步骤（原任务摘要与上一轮结果会自动附上）。" +
            "收到带「[自动接续]」的通知时，判断剩余工作是目标 VS 自己能完成的（未完成部分、后续步骤、需补的代码 / 编译 / 单元测试 / 文档）才调用，不要停下来等用户；" +
            "[AI] 项验证不通过、属于本次改动本身的问题用 retry_task_with_info；只剩 [人工] 项或需要用户提供信息 / 授权时不要调用，告诉用户。" +
            "每个任务只能接续一次，每条接续链最多 " + TaskContinuation.MaxDepthText + " 次；原任务阻塞同一 VS 时会自动放行（测试清单保持待验证），接续任务按编号排在该 VS 已排队任务之后。" +
            "/ Task auto-continue: for a task awaiting verification, or done but whose reply mentions remaining steps, publishes a continuation to the same VS covering only the remaining steps (the original task summary and previous result are attached). " +
            "On a notice with \"[自动接续 / Auto-continue]\", call it when the remaining work is something the target VS can do itself, instead of waiting for the user; use retry_task_with_info when an [AI] item fails because of the change itself; " +
            "do not call it when only [人工] items or user-only information / authorization remain. One continuation per task, at most " + TaskContinuation.MaxDepthText + " per chain; a blocking original is released (its checklist stays pending) and the continuation queues after the tasks already queued on that VS.")]
        internal async Task<string> ContinueTask(
            [Description("要接续的原任务编号，如 3")] int id,
            [Description("本轮要完成的具体剩余步骤：做什么、改哪里、如何验收；不要只写「继续」/ The concrete remaining steps: what, where and how to accept; never just \"continue\"")] string remaining,
            [Description("可选：接续任务的中文题目，不超过 20 字；留空时沿用原题目加「接续」/ Optional short Chinese title; defaults to the original title plus \"接续\"")] string title = null)
        {
            if (!(_host is IAgentContinuationHost host)) return "当前宿主不支持任务接续 / Task continuation is unavailable.";
            if (!_settings().AgentAutoContinue)
                return "任务自动接续未开启（属性 → AI 助手 → 任务自动接续），请把剩余步骤告诉用户 / Task auto-continue is off (Properties > AI assistant); tell the user the remaining steps.";
            if (string.IsNullOrWhiteSpace(remaining)) return "remaining 不能为空 / remaining is empty.";
            if (_settings().AgentConfirm && !await ConfirmAsync("发布任务 #" + id + " 的接续任务", remaining))
                return "用户拒绝了该操作。";
            return await host.ContinueTask(id, remaining.Trim(), title).ConfigureAwait(false);
        }

        /// <summary>发给 VSManager 项目的任务附加开源约束（按界面语言）。/ Open-source constraint appended to tasks for the VSManager project (by UI language).</summary>
        internal static string OpenSourceSuffixFor(VsInstance v, bool english) =>
            v != null && IsVsManager(v) ? (english ? OpenSourceTaskSuffixEn : OpenSourceTaskSuffix) : "";

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
            "投递、读取失败或 Copilot 本轮中断时不必补充信息，改用 retry_task。" +
            "不要把用户的话、前次反馈和此前的补充原样拼接转发：先理解用户的意图，用自己的话写出这次需要 Copilot 做什么；" +
            "此前已累积多段补充或反馈、或用户改变了做法时，设 replace_previous=true，并把仍然有效的要点整合进 info（未整合的前次反馈与补充将不再发送）；" +
            "用户说已清空 / 新建了 Copilot 对话（或你刚调用 new_copilot_thread）时设 fresh_context=true，Copilot 会先重新阅读相关代码与文档了解进度。")]
        internal async Task<string> RetryTaskWithInfo(
            [Description("任务编号，如 3")] int id,
            [Description("补充信息：要告诉 Copilot 的新信息、澄清或修正做法，简洁具体；用你自己的话整合用户意图，不要原样堆叠此前的反馈")] string info,
            [Description("info 是否来自用户本轮消息（用户给出的补充、修正或验证反馈）；为 true 时不受 AI 自主重试次数限制，但会请用户确认。不要把你自己推断的内容标为 true。")] bool from_user = false,
            [Description("true：info 是整合后的完整重试说明，替换此前累积的补充信息、前次尝试反馈与接续说明（仍有效的要点须写进 info）；false：追加到已有补充之后")] bool replace_previous = false,
            [Description("true：Copilot 对话已被清空或换成新线程，提示 Copilot 不要依赖之前的对话，先重新阅读相关代码、文档与 Git 状态再继续")] bool fresh_context = false)
        {
            if (!(_host is IAgentReleaseHost host)) return ReleaseUnsupported;
            if (string.IsNullOrWhiteSpace(info)) return "补充信息不能为空 / Supplementary info is empty.";
            // 用户补充绕过 AI 自主限制，因此总是请用户确认 / User supplements bypass the AI limits, so always confirm
            string mode = (replace_previous ? "\n（替换此前的补充与反馈 / replaces earlier supplements and feedback）" : "")
                + (fresh_context ? "\n（对话已重置，Copilot 将重新阅读相关内容 / conversation reset, Copilot re-reads the relevant content）" : "");
            if ((from_user || _settings().AgentConfirm) && !await ConfirmAsync("补充信息后重试任务 #" + id + (from_user ? "（来自用户 / from the user）" : ""), info + mode))
                return "用户拒绝了该操作。";
            return await host.RetryTaskWithInfo(id, info, from_user, replace_previous, fresh_context).ConfigureAwait(false);
        }

        [Description("重试因非任务内容原因失败的任务：投递失败、目标 VS 关闭、读取回复失败，或 Copilot 本轮没执行完（返回中断 / 未预期的 EOF、返回体过大、达到单轮迭代上限、网络 / 服务错误、没有回复）。" +
            "本轮中断的任务重试时会自动提示 Copilot 在已有进度上继续；可在 note 中针对中断原因补充做法（如分步完成、缩小范围、减少输出、从哪一步接着做）。" +
            "这类失败不计入需求的执行次数；每个任务最多直接重试 " + TaskFailureAnalyzer.MaxRecoveryRetriesText + " 次，超过后交给用户检查。" +
            "Copilot 执行完但任务失败（缺少回执、回报失败）时会被拒绝，应用 retry_task_with_info 写明新信息或交给用户。" +
            "Copilot 对话已被清空或换成新线程时设 fresh_context=true。")]
        internal async Task<string> RetryTask(
            [Description("任务编号，如 3")] int id,
            [Description("可选：给 Copilot 的接续说明，针对中断原因调整做法；没有时留空")] string note = null,
            [Description("true：Copilot 对话已被清空或换成新线程，提示 Copilot 先重新阅读相关代码、文档与 Git 状态再继续")] bool fresh_context = false)
        {
            if (!(_host is IAgentReleaseHost host)) return ReleaseUnsupported;
            if (_settings().AgentConfirm && !await ConfirmAsync("重试任务 #" + id, "任务 #" + id + " 因投递、读取或 Copilot 本轮中断而失败，将重新排队并在已有进度上继续。" + (string.IsNullOrWhiteSpace(note) ? "" : "\n接续说明：" + note) + (fresh_context ? "\n（对话已重置，Copilot 将重新阅读相关内容 / conversation reset）" : "")))
                return "用户拒绝了该操作。";
            return await host.RetryTask(id, note, fresh_context).ConfigureAwait(false);
        }

        [Description("读取失败任务保存的本轮 Copilot 完整回复（最后一条任务消息之后的全部回答与过程步骤，不只是最后一行状态），分页返回。" +
            "失败通知中的回复被省略、或需要确认中断原因（返回中断 / EOF、返回体过大、迭代上限等）与已完成进度时调用，据此决定补充什么内容后用 retry_task / retry_task_with_info 继续或重试。")]
        internal async Task<string> ReadTaskReply(
            [Description("任务编号，如 3")] int id,
            [Description("页码，从 1 开始")] int page = 1)
        {
            if (!(_host is IAgentTaskReplyHost host)) return "当前宿主不支持读取任务回复 / Reading task replies is unavailable.";
            return await host.ReadTaskReply(id, page).ConfigureAwait(false);
        }
    }
}
