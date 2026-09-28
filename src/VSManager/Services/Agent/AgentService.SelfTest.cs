using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>预编译结果。/ Pre-build result.</summary>
    public sealed class SelfRestartBuild
    {
        public bool Ok;
        public string Summary;
    }

    /// <summary>
    /// 可选宿主能力：自测重启（VSManager 在 VS 调试器中运行时，让该 VS 重新启动调试以加载新程序）与测试项勾选。
    /// Optional host capability: self-test restart (when VSManager runs under a VS debugger, have that VS restart debugging to load
    /// the new build) and checking test items.
    /// </summary>
    public interface IAgentSelfRestartHost
    {
        /// <summary>检查能否自测重启；可以时返回 null，否则返回原因。/ Checks whether a self-test restart is possible; null when it is, else the reason.</summary>
        Task<string> CheckSelfRestart();

        /// <summary>把 VSManager 项目生成到临时目录，确认能编译通过（不覆盖正在运行的程序）。/ Builds the VSManager project into a temp folder to confirm it compiles (never overwrites the running exe).</summary>
        Task<SelfRestartBuild> PrebuildSelf(CancellationToken cancellationToken);

        /// <summary>写入交接单并安排在本轮对话结束后重启。/ Writes the handoff and schedules the restart after this round ends.</summary>
        Task<string> ScheduleSelfRestart(string testPlan, int taskId, string scope, string buildSummary);

        /// <summary>勾选 / 取消勾选待验证任务的测试项（item 从 1 开始）。/ Checks / unchecks a test item of a task awaiting verification (item is 1-based).</summary>
        Task<string> SetTestItem(int taskId, int item, bool passed, string evidence);
    }

    /// <summary>
    /// 可选宿主能力：返回正在调试 VSManager 的 VS 名称（自迭代时作为 send_task 目标）；找不到时返回 null 与原因。
    /// Optional host capability: the name of the VS debugging VSManager (the send_task target during self-iteration); null plus the reason when none.
    /// </summary>
    public interface IAgentSelfIterationHost
    {
        Task<Tuple<string, string>> SelfDebuggerName();
    }

    /// <summary>
    /// 可选宿主能力：为补 skill 闭环登记目标测试项（校验任务与测试项，记下要补的 skill）；返回 (测试项文字, null) 或 (null, 拒绝原因)。
    /// Optional host capability: registers the target item of a skill-gap loop (validates the task and item and records the skill); returns (item text, null) or (null, reason).
    /// </summary>
    public interface IAgentSkillGapHost
    {
        Task<Tuple<string, string>> ClaimSkillGap(int taskId, int item, string skill);
        /// <summary>闭环未能开始时撤销登记。/ Releases the registration when the loop could not start.</summary>
        Task ReleaseSkillGap(int taskId, int item);
    }

    /// <summary>
    /// 可选宿主能力：列出全部待验证任务的完整测试清单（taskId &gt; 0 时只列该任务）。
    /// Optional host capability: lists the full checklist of every task awaiting verification (only taskId when &gt; 0).
    /// </summary>
    public interface IAgentChecklistHost
    {
        Task<string> ListTestChecklists(int taskId);
    }

    public sealed partial class AgentService
    {
        [Description("自测重启技能：VSManager 自身的编程任务完成（常见为「待验证」）后，需要重启 VSManager 加载新程序再测试时调用。" +
            "仅当 VSManager 正在某个 VS 的调试器中运行时可用：先把 VSManager 项目生成到临时目录确认能编译通过，再保存任务清单、对话与本会话的启动授权，" +
            "在本轮回复结束后让该 VS 重新启动调试（Debug.Restart）。其他 VS 中执行的任务不必等待、不受影响，重启后继续跟踪，期间的通知重启后补发；对话自动接续，" +
            "新程序启动后你会收到「[重启完成通知]」，届时按测试计划逐项执行能用工具完成的测试。调用成功后本轮只需简短告诉用户即将重启，不要再调用其他工具。" +
            " Self-test restart skill: after a VSManager coding task, pre-builds to a temp folder, saves tasks, conversation and grants, then restarts the VS debug session after this round without waiting for tasks in other VS instances (they keep running and are tracked again; notices meanwhile are re-delivered); you receive a restart-completed notice to continue the test plan.")]
        internal async Task<string> RestartVsManagerForTesting(
            [Description("重启后要执行的测试计划，逐项一行，写清操作与预期结果；最多 4000 字")] string testPlan,
            [Description("要验证的任务编号（如 12），没有则填 0")] int taskId = 0,
            CancellationToken cancellationToken = default)
        {
            if (!(_host is IAgentSelfRestartHost host)) return "当前宿主不支持自测重启 / Self-test restart is unavailable.";
            string planError = SelfRestart.ValidatePlan(testPlan);
            if (planError != null) return planError;
            var iteration = SelfIteration.Load(DateTime.UtcNow);
            if (iteration != null && iteration.Round >= iteration.MaxRounds)
                return $"自迭代已达轮次上限（{iteration.Round}/{iteration.MaxRounds}），不再重启：请调用 stop_self_iteration，把未通过项、已做调整和建议交给用户 / The self-iteration reached its round limit; call stop_self_iteration and hand over to the user.";
            string blocked = await host.CheckSelfRestart().ConfigureAwait(false);
            if (blocked != null) return blocked;
            if (_settings().AgentConfirm && !await ConfirmAsync("重启 VSManager 以测试新程序",
                    "将先把 VSManager 生成到临时目录确认可编译，然后在本轮结束后让调试它的 VS 重新启动调试；任务清单、对话与启动授权会保存并在重启后恢复。\r\n" +
                    "VSManager will be pre-built to a temp folder, then the debugging VS restarts the session after this round; tasks, conversation and grants are saved and restored.\r\n\r\n" +
                    "测试计划 / Test plan:\r\n" + TextUtil.Clip(testPlan, 1500)))
                return "用户拒绝了该操作。/ The user declined.";

            var build = host.PrebuildSelf(cancellationToken);
            while (await Task.WhenAny(build, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false) != build)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Touch();
            }
            var result = await build.ConfigureAwait(false);
            Touch();
            if (result == null || !result.Ok)
                return "预编译未通过，已取消重启（正在运行的 VSManager 未受影响）/ Pre-build failed; restart cancelled (the running VSManager is untouched):\n" +
                    Truncate(result?.Summary ?? "", MaxToolText);
            iteration = SelfIteration.Load(DateTime.UtcNow);
            if (iteration != null)
            {
                // 预编译通过即计一轮，失败的安排也占用轮次，防止无限循环。/ A round counts once the pre-build passes, even if scheduling fails, so the loop cannot run forever.
                iteration.Round++;
                SelfIteration.Save(iteration);
            }
            string scheduled = await host.ScheduleSelfRestart(testPlan.Trim(), Math.Max(0, taskId), CurrentScope, result.Summary).ConfigureAwait(false);
            return iteration == null ? scheduled
                : scheduled + "\n[自迭代 / Self-iteration] 本次为第 " + iteration.Round + "/" + iteration.MaxRounds + " 轮 / round " + iteration.Round + "/" + iteration.MaxRounds;
        }

        [Description("开始自迭代：仅在用户明确要求 AI 自己循环改进 / 迭代 VSManager 时调用。围绕一个目标循环：你用 send_task 向调试 VSManager 的 VS 发布开发任务 → 收到「[任务完成通知]」后调用 restart_vsmanager_for_testing 重启并测试 → 有未通过项时发布修复任务再重启，直到全部通过或达到轮次上限（默认 3，最多 10；每次重启测试算一轮，由工具强制）。" +
            "需要 VSManager 在 VS 调试器中运行，并开启「任务完成自动跟进」。在自迭代期间，围绕该目标向该 VS 发布开发与修复任务视为已获用户授权；超出目标的需求仍需用户同意。结束时调用 stop_self_iteration。 " +
            "Starts a self-iteration, only when the user explicitly asks the AI to iterate on VSManager by itself: publish a task to the VS debugging VSManager, restart and test after its completion notice, publish fixes for failures and repeat until everything passes or the round limit (default 3, max 10; one round per test restart, enforced by the tools). Requires VSManager under a VS debugger and task auto follow-up. End with stop_self_iteration.")]
        internal async Task<string> StartSelfIteration(
            [Description("迭代目标与验收标准，说明要做成什么、怎样算完成；最多 2000 字 / Goal and acceptance criteria")] string goal,
            [Description("最多重启测试几轮，默认 3，上限 10 / Maximum test rounds, default 3, max 10")] int maxRounds = 0)
        {
            if (!(_host is IAgentSelfRestartHost host)) return "当前宿主不支持自迭代 / Self-iteration is unavailable.";
            goal = (goal ?? "").Trim();
            if (goal.Length == 0) return "请写明迭代目标与验收标准 / Describe the goal and acceptance criteria.";
            if (goal.Length > SelfIteration.MaxGoalChars) return $"目标过长（上限 {SelfIteration.MaxGoalChars} 字）/ Goal too long (max {SelfIteration.MaxGoalChars}).";
            var active = SelfIteration.Load(DateTime.UtcNow);
            if (active != null)
                return $"已有进行中的自迭代（第 {active.Round}/{active.MaxRounds} 轮，目标：{OneLine(active.Goal, 80)}）；如要重新开始，先调用 stop_self_iteration / A self-iteration is already active; call stop_self_iteration first.";
            if (!_settings().AgentAutoFollowUp)
                return "自迭代需要开启「属性 → AI 助手 → 任务完成自动跟进」，否则任务完成后无法自动进入测试；请用户开启后再试 / Self-iteration needs \"task auto follow-up\" enabled in Properties so completions trigger the next step.";
            string blocked = await host.CheckSelfRestart().ConfigureAwait(false);
            if (blocked != null) return "无法开始自迭代 / Cannot start self-iteration: " + blocked;
            string vsName = null;
            if (_host is IAgentSelfIterationHost ih)
            {
                var found = await ih.SelfDebuggerName().ConfigureAwait(false);
                if (found?.Item1 == null) return "无法开始自迭代 / Cannot start self-iteration: " + (found?.Item2 ?? "找不到调试 VSManager 的 VS / No VS debugs VSManager");
                vsName = found.Item1;
            }
            vsName = vsName ?? "调试 VSManager 的 VS / the VS debugging VSManager";
            int rounds = SelfIteration.ClampRounds(maxRounds);
            if (_settings().AgentConfirm && !await ConfirmAsync("开始自迭代（最多 " + rounds + " 轮）",
                    "目标 / Goal:\r\n" + TextUtil.Clip(goal, 1500) + "\r\n\r\n助手将自行向「" + vsName + "」发布开发与修复任务，完成后重启 VSManager 测试，循环直到通过或达到轮次上限。\r\n" +
                    "The assistant will publish development and fix tasks to that VS itself, restart VSManager to test after each, and loop until passing or the round limit."))
                return "用户拒绝了该操作。/ The user declined.";
            var state = new SelfIterationState { Goal = goal, MaxRounds = rounds, Round = 0, StartedUtc = DateTime.UtcNow, VsName = vsName, Scope = CurrentScope };
            string error = SelfIteration.Save(state);
            if (error != null) return "自迭代状态保存失败 / Failed to save the self-iteration state: " + error;
            Log("自迭代开始 / Self-iteration started: rounds=" + rounds + " vs=" + vsName);
            return "已开始自迭代（最多 " + rounds + " 轮）。下一步：用 send_task 向「" + vsName + "」发布第一项开发任务（写清目标与验收标准，要求返回测试清单）；收到它的「[任务完成通知]」后，用 list_tasks 读取测试清单写成测试计划，调用 restart_vsmanager_for_testing。用户说停止时调用 stop_self_iteration。/ " +
                "Self-iteration started (max " + rounds + " rounds). Next: send_task the first development task to that VS (goal, acceptance criteria, ask for a checklist); after its completion notice, build a test plan from list_tasks and call restart_vsmanager_for_testing. Call stop_self_iteration when the user says stop.";
        }

        [Description("补 skill 闭环（无需用户提醒，由你自动发起）：某个待验证测试项用现有工具无法验证、但理论上新增一个 VSManager skill（读取窗口 / 控件 / 托盘 / 文件 / 日志 / 进程等机器可读状态的工具）就能自动验证时调用。" +
            "它开始一轮专用自迭代（默认 3 轮，最多 10）：你向调试 VSManager 的 VS 发布实现该 skill 的开发任务 → 收到「[任务完成通知]」后 restart_vsmanager_for_testing 重启加载 → 用新 skill 验证该项，通过即 mark_test_item（该项即使标为人工也允许勾选），skill 不工作就带证据发修复任务再重启，直到通过或达到上限。" +
            "必须人眼 / 人手的项（观感、动画、颜色、点击按钮、人为制造故障）会被拒绝；每个测试项只发起一次；已有自迭代进行中时不能开始。需要 VSManager 在 VS 调试器中运行并开启「任务完成自动跟进」。" +
            " / Skill-gap loop (start it yourself, no user reminder needed): when an awaiting-verification item cannot be verified with existing tools but a new VSManager skill (a tool that reads machine-readable window / control / tray / file / log / process state) could verify it automatically. " +
            "Starts a dedicated self-iteration (default 3 rounds, max 10): send_task the skill implementation to the VS debugging VSManager → after its completion notice restart_vsmanager_for_testing → verify the item with the new skill and mark_test_item on success (allowed even if tagged manual), or send a fix with evidence and restart again, until it passes or the limit. " +
            "Items needing human eyes or hands are refused; one loop per item; not while another self-iteration is active. Requires VSManager under a VS debugger and task auto follow-up.")]
        internal async Task<string> StartSkillGapLoop(
            [Description("待验证任务编号 / Task id awaiting verification")] int taskId,
            [Description("测试项序号（从 1 开始，同 list_test_checklists）/ 1-based item number")] int item,
            [Description("需要的新 skill：工具名、参数、返回内容，以及它读取什么状态、怎样据此判定该项 / The skill needed: tool name, parameters, output, what state it reads and how it decides the item")] string skill,
            [Description("为什么现有工具验证不了 / Why existing tools cannot verify it")] string reason,
            [Description("最多重启测试几轮，默认 3，上限 10 / Maximum test rounds, default 3, max 10")] int maxRounds = 0)
        {
            if (!(_host is IAgentSelfRestartHost host) || !(_host is IAgentSkillGapHost gap)) return "当前宿主不支持补 skill 闭环 / The skill-gap loop is unavailable.";
            skill = (skill ?? "").Trim();
            reason = (reason ?? "").Trim();
            if (skill.Length == 0 || reason.Length == 0) return "请写明需要的 skill 与现有工具验证不了的原因 / Describe the skill needed and why existing tools cannot verify the item.";
            if (skill.Length > 1200 || reason.Length > 600) return "skill 说明不超过 1200 字、原因不超过 600 字 / Keep skill within 1200 and reason within 600 characters.";
            var active = SelfIteration.Load(DateTime.UtcNow);
            if (active != null)
                return $"已有进行中的自迭代（第 {active.Round}/{active.MaxRounds} 轮，目标：{OneLine(active.Goal, 80)}）；先完成或 stop_self_iteration 后再发起补 skill 闭环 / A self-iteration is already active; finish it or call stop_self_iteration first.";
            if (!_settings().AgentAutoFollowUp)
                return "补 skill 闭环需要开启「属性 → AI 助手 → 任务完成自动跟进」；请把需要的 skill 与原因告诉用户 / The skill-gap loop needs task auto follow-up; tell the user which skill is needed and why.";
            string blocked = await host.CheckSelfRestart().ConfigureAwait(false);
            if (blocked != null) return "无法发起补 skill 闭环（请把需要的 skill 与原因告诉用户）/ Cannot start the skill-gap loop (tell the user which skill is needed): " + blocked;
            string vsName = null;
            if (_host is IAgentSelfIterationHost ih)
            {
                var found = await ih.SelfDebuggerName().ConfigureAwait(false);
                if (found?.Item1 == null) return "无法发起补 skill 闭环 / Cannot start the skill-gap loop: " + (found?.Item2 ?? "找不到调试 VSManager 的 VS / No VS debugs VSManager");
                vsName = found.Item1;
            }
            vsName = vsName ?? "调试 VSManager 的 VS / the VS debugging VSManager";
            var claim = await gap.ClaimSkillGap(taskId, item, OneLine(skill, 200)).ConfigureAwait(false);
            if (claim?.Item1 == null) return claim?.Item2 ?? "无法登记该测试项 / The item could not be registered.";
            int rounds = maxRounds <= 0 ? SelfIteration.SkillGapRounds : SelfIteration.ClampRounds(maxRounds);
            if (_settings().AgentConfirm && !await ConfirmAsync("补 skill 闭环：任务 #" + taskId + " 第 " + item + " 项（最多 " + rounds + " 轮）",
                    "测试项 / Item: " + TextUtil.Clip(claim.Item1, 300) + "\r\n需要的 skill / Skill: " + TextUtil.Clip(skill, 800) + "\r\n原因 / Reason: " + TextUtil.Clip(reason, 400) + "\r\n\r\n" +
                    "助手将向「" + vsName + "」发布实现该 skill 的任务，完成后重启 VSManager 并用它验证该项。/ The assistant will publish the skill to that VS, restart VSManager and verify the item with it."))
            {
                await gap.ReleaseSkillGap(taskId, item).ConfigureAwait(false);
                return "用户拒绝了该操作。/ The user declined.";
            }
            string goal = TextUtil.Clip($"补齐验证 skill 并验证任务 #{taskId} 第 {item} 项「{OneLine(claim.Item1, 200)}」。需要的 skill / Skill: {skill}。原因 / Reason: {reason}", SelfIteration.MaxGoalChars);
            var state = new SelfIterationState
            {
                Goal = goal, MaxRounds = rounds, Round = 0, StartedUtc = DateTime.UtcNow, VsName = vsName, Scope = CurrentScope,
                Skill = OneLine(skill, 200), TargetTaskId = taskId, TargetItem = item
            };
            string error = SelfIteration.Save(state);
            if (error != null)
            {
                await gap.ReleaseSkillGap(taskId, item).ConfigureAwait(false);
                return "自迭代状态保存失败 / Failed to save the self-iteration state: " + error;
            }
            Log("补 skill 闭环开始 / Skill-gap loop started: task #" + taskId + " item " + item + " rounds=" + rounds);
            return $"已开始补 skill 闭环（最多 {rounds} 轮）。下一步：用 send_task 向「{vsName}」发布开发任务——在 AI 总控助手中新增该 skill（写清工具名、参数、返回内容与读取的状态，注册工具、写进提示词、补单元测试，文档与注释中英双语），并要求返回测试清单；" +
                $"收到它的「[任务完成通知]」后，用 list_tasks 写测试计划（包含「用新 skill 验证任务 #{taskId} 第 {item} 项」）并调用 restart_vsmanager_for_testing（taskId 填该开发任务）。/ " +
                $"Skill-gap loop started (max {rounds} rounds). Next: send_task a development task to that VS that adds the skill to the AI assistant (tool name, parameters, output, the state it reads; register it, document it in the prompt, add unit tests, bilingual docs and comments) and asks for a checklist; " +
                $"after its completion notice write a test plan from list_tasks (including \"verify item {item} of task #{taskId} with the new skill\") and call restart_vsmanager_for_testing with that development task's id.";
        }

        [Description("结束自迭代（目标达成、达到轮次上限、需要用户决定，或用户要求停止时调用），并附上结论摘要。已发布的任务不受影响。/ Ends the self-iteration (goal met, round limit reached, user decision needed or the user asked to stop) with a summary. Published tasks are unaffected.")]
        internal string StopSelfIteration(
            [Description("结论摘要：已通过 / 未通过 / 需用户测试的项与原因 / Summary: passed, failed and user-test items with reasons")] string summary = null)
        {
            var state = SelfIteration.Load(DateTime.UtcNow);
            SelfIteration.Clear();
            if (state == null) return "当前没有进行中的自迭代 / No self-iteration is active.";
            Log("自迭代结束 / Self-iteration stopped: round " + state.Round + "/" + state.MaxRounds + " " + OneLine(summary ?? "", 200));
            return "已结束自迭代：共进行 " + state.Round + "/" + state.MaxRounds + " 轮。请向用户汇报结论与需用户测试的项。/ Self-iteration ended after " + state.Round + "/" + state.MaxRounds + " rounds; report the conclusion and the user-test items.";
        }

        [Description("查看测试清单：列出全部「待验证」任务（不限最近几条）的完整测试清单，每项带序号（与 mark_test_item 的 item 一致）、勾选状态与 [AI] / [人工] 标注；taskId 大于 0 时只看该任务。" +
            "list_tasks 只含进行中与最近的任务，可能只显示部分清单；汇报、验证或勾选测试项前用本工具查看全部。这是只读查询，不入队、不发布任务。" +
            " / Lists the full checklist of every task awaiting verification (not just recent ones), each item numbered as mark_test_item expects, with its check state and [AI] / [manual] tag; pass taskId > 0 for one task. " +
            "list_tasks only covers active and recent tasks and may show just some checklists; use this before reporting, verifying or checking items. Read-only: it never enqueues or publishes a task.")]
        internal async Task<string> ListTestChecklists(
            [Description("任务编号；0 表示全部待验证任务 / Task id; 0 = every task awaiting verification")] int taskId = 0)
        {
            if (!(_host is IAgentChecklistHost host)) return "当前宿主不支持查看测试清单 / Listing checklists is unavailable.";
            return Truncate(await host.ListTestChecklists(taskId).ConfigureAwait(false), MaxToolText);
        }

        [Description("勾选或取消勾选「待验证」任务测试清单中的一项（item 从 1 开始，与 list_test_checklists 显示的序号一致）。" +
            "只在本轮已用工具真实验证通过时传 passed=true，并在 evidence 中写明依据（调用了哪个工具、返回了什么）；list_tasks 中标为「[人工]」的项必须人工验证，工具会拒绝勾选。" +
            "全部勾选后任务自动标记为已完成。/ Checks or unchecks one checklist item of a task awaiting verification (1-based). Pass passed=true only when a tool in this round really verified it, with the evidence; the task completes once every item is checked.")]
        internal async Task<string> MarkTestItem(
            [Description("任务编号，如 12")] int taskId,
            [Description("测试项序号，从 1 开始")] int item,
            [Description("是否通过：true 勾选，false 取消勾选")] bool passed,
            [Description("判定依据：调用的工具与关键返回内容，必填")] string evidence)
        {
            if (!(_host is IAgentSelfRestartHost host)) return "当前宿主不支持勾选测试项 / Checking test items is unavailable.";
            evidence = OneLine(evidence, 400);
            if (evidence.Length == 0) return "请在 evidence 中写明判定依据 / Provide the evidence.";
            if (item < 1) return "测试项序号从 1 开始 / Items are numbered from 1.";
            if (_settings().AgentConfirm && !await ConfirmAsync((passed ? "勾选" : "取消勾选") + "任务 #" + taskId + " 的测试项 " + item, "依据 / Evidence: " + evidence))
                return "用户拒绝了该操作。/ The user declined.";
            return await host.SetTestItem(taskId, item, passed, evidence).ConfigureAwait(false);
        }
    }
}
