using System;
using System.ComponentModel;
using System.Linq;

namespace VSManager
{
    public sealed partial class AgentService
    {
        /// <summary>执行计划文件路径（测试可替换，null 为默认 %APPDATA%\VSManager\agent-plans.json）。/ Plan file path (replaceable in tests; null = the default under %APPDATA%\VSManager).</summary>
        internal string PlanFilePath;

        [Description("创建执行计划并持久化到磁盘：只在流程复杂时使用（多步骤、跨 VSManager 重启、步骤间有依赖，例如补 skill 闭环、自迭代、多阶段验证），简单请求不要建计划。计划常驻直到 complete_plan 释放，重启后新进程自动读回并提示继续。" +
            " / Creates an execution plan persisted on disk: only for complex flows (multi-step, across VSManager restarts, dependent steps such as the skill-gap loop, self-iteration, multi-stage verification); never for simple requests. The plan stays until complete_plan releases it and is read back after a restart.")]
        internal string CreatePlan(
            [Description("计划标题 / Plan title")] string title,
            [Description("步骤列表，每项一步，按执行顺序，2–30 步 / Steps in execution order, one per item, 2–30")] string[] steps,
            [Description("目标与验收标准 / Goal and acceptance criteria")] string goal = null)
        {
            return AgentPlans.Create(title, goal, steps, CurrentScope ?? "", DateTime.UtcNow, PlanFilePath);
        }

        [Description("更新执行计划中某一步的状态与说明：开始一步时标 in_progress，完成后立即标 done（失败标 failed、不再需要标 skipped），note 写结果或依据。" +
            " / Updates a plan step's status and note: mark in_progress when starting, done right after finishing (failed on failure, skipped when no longer needed), with the result or evidence in note.")]
        internal string UpdatePlanStep(
            [Description("第几步，从 1 开始 / Step number, 1-based")] int step,
            [Description("pending / in_progress / done / failed / skipped")] string status,
            [Description("说明：结果、依据或受阻原因 / Note: result, evidence or blocker")] string note = null,
            [Description("计划编号；只有一个进行中的计划时可填 0 / Plan id; 0 when only one plan is active")] int planId = 0)
        {
            return AgentPlans.UpdateStep(planId, step, status, note, DateTime.UtcNow, PlanFilePath);
        }

        [Description("读回进行中的执行计划（步骤、每步状态与说明、下一步）；重启后或不确定进度时先调用。/ Reads back the active execution plans (steps, each state and note, next step); call it after a restart or whenever unsure of progress.")]
        internal string ReadPlan(
            [Description("计划编号，0 表示全部进行中的计划 / Plan id, 0 = all active plans")] int planId = 0)
        {
            var plans = AgentPlans.Active(out string error, PlanFilePath);
            if (error != null) return "⚠ " + error;
            if (planId > 0)
            {
                var plan = plans.FirstOrDefault(p => p.Id == planId);
                return plan == null ? "没有进行中的计划 #" + planId + "（可能已释放）/ No active plan #" + planId + " (maybe released)" : AgentPlans.Describe(plan);
            }
            return Truncate(AgentPlans.DescribeAll(plans), MaxToolText);
        }

        [Description("整个流程完成后释放执行计划（从磁盘删除该计划）。所有步骤须为 done 或 skipped；确需放弃（如用户取消）时 force=true 并在 summary 写明原因。/ Releases the plan once the whole flow is complete (removes it from disk). Every step must be done or skipped; to abandon (e.g. the user cancelled) set force=true and give the reason in summary.")]
        internal string CompletePlan(
            [Description("结果总结 / Summary of the outcome")] string summary,
            [Description("计划编号；只有一个进行中的计划时可填 0 / Plan id; 0 when only one plan is active")] int planId = 0,
            [Description("有未完成步骤时强制释放 / Release even with unfinished steps")] bool force = false)
        {
            return AgentPlans.Complete(planId, summary, force, PlanFilePath);
        }

        /// <summary>
        /// 系统提示词中的常驻计划摘要：会话隔离时只含全局计划与本项目计划。
        /// Resident plan summary for the system prompt: with session isolation only global plans and this project's plans.
        /// </summary>
        internal string PlanPromptBlock()
        {
            if (Profile == AgentProfile.Notes) return "";
            var plans = AgentPlans.Active(out _, PlanFilePath);
            string scope = CurrentScope;
            if (!string.IsNullOrEmpty(scope)) plans = plans.Where(p => string.IsNullOrEmpty(p.Scope) || p.Scope == scope).ToList();
            string summary = AgentPlans.PromptSummary(plans);
            if (summary == null) return "";
            bool en = _settings().IsEnglishVoice;
            return Environment.NewLine + Environment.NewLine + (en
                ? "Active execution plans (on disk, kept until complete_plan; follow them, update each step with update_plan_step and read details with read_plan):"
                : "进行中的执行计划（已持久化，complete_plan 前一直保留；请按计划推进，每完成一步用 update_plan_step 更新，详情用 read_plan 读取）：")
                + Environment.NewLine + summary;
        }
    }
}
