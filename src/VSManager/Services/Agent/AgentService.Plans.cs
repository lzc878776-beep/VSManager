using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>主窗体提供的 Notion 计划编排入口（可选能力）。/ Optional Notion plan orchestration provided by the main form.</summary>
    internal interface IAgentPlanHost
    {
        NotionPlanService Plans { get; }
    }

    public sealed partial class AgentService
    {
        [Description("读取 Notion 计划数据库（每条记录一项任务，页面正文为详情）并解析为待确认清单；只读，不派发、不回写。必须把清单与所有 ⚠ 问题原样呈现给用户，模糊项请用户在 Notion 中澄清，不得自行猜测补全。/ Read a Notion plan database (one record per task, page body as details) into a list for confirmation; read-only. Present the list and every ⚠ issue to the user verbatim; ask the user to clarify ambiguous items in Notion, never guess.")]
        private async Task<string> PreviewNotionPlan(
            [Description("Notion 数据库链接或 32 位 ID / Notion database URL or 32-character id")] string database,
            CancellationToken cancellationToken = default)
        {
            if (!(_host is IAgentPlanHost h) || h.Plans == null) return "当前环境不支持 Notion 计划 / Notion plans are not available here";
            try { return Truncate(await h.Plans.PreviewAsync(database, cancellationToken), MaxToolText); }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException) { return "读取 Notion 计划失败 / Failed to read the Notion plan: " + ex.Message; }
        }

        [Description("仅在用户明确确认预览清单后调用：把选中的条目加入任务清单。selection 为 \"all\"（全部无问题的新条目）或用户确认的序号（逗号分隔）；已派发且内容未变的条目自动跳过，内容变更的条目须用户按序号确认。跨项目依赖由编排层暂缓，结果只回写 Notion 专用状态字段。/ Call only after the user explicitly confirms the preview: enqueue the selected items. selection is \"all\" (every clear new item) or user-confirmed numbers; unchanged dispatched items are skipped, changed items need explicit numbers. Cross-project dependencies are held; results are written to the dedicated status property only.")]
        private async Task<string> DispatchNotionPlan(
            [Description("与预览相同的数据库链接或 ID / Same database URL or id as the preview")] string database,
            [Description("\"all\" 或用户确认的序号，如 \"1,3\" / \"all\" or confirmed numbers such as \"1,3\"")] string selection,
            CancellationToken cancellationToken = default)
        {
            if (!(_host is IAgentPlanHost h) || h.Plans == null) return "当前环境不支持 Notion 计划 / Notion plans are not available here";
            if (_settings().AgentConfirm && !await ConfirmAsync("派发 Notion 计划 / Dispatch Notion plan", database + "\n" + selection))
                return "用户拒绝了该操作 / Denied";
            try { return Truncate(await h.Plans.DispatchAsync(database, selection, cancellationToken), MaxToolText); }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException) { return "派发 Notion 计划失败 / Failed to dispatch the Notion plan: " + ex.Message; }
        }
    }
}
