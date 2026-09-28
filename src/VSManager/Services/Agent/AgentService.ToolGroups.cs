using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace VSManager
{
    public sealed partial class AgentService
    {
        private readonly Dictionary<string, int> _groupRound = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _toolRound;

        /// <summary>是否按需加载工具组（笔记助手始终提供全部工具）。/ Whether tool groups load on demand (the note assistant always gets all tools).</summary>
        private bool ToolGrouping => Profile != AgentProfile.Notes && _settings().AgentToolGrouping;

        /// <summary>
        /// 开始新一轮：轮次加一，并激活本轮文字触发的工具组。
        /// Starts a new round: bumps the round counter and activates the groups triggered by this round's text.
        /// </summary>
        internal void BeginToolRound(string text)
        {
            lock (_groupRound)
            {
                _toolRound++;
                foreach (var key in AgentToolGroups.Triggered(text)) _groupRound[key] = _toolRound;
            }
        }

        /// <summary>模型调用了某工具：其所在组在后续几轮继续提供。/ The model called a tool: its group stays offered for the next rounds.</summary>
        private void NoteToolUsed(string name)
        {
            string key = AgentToolGroups.GroupOf(name);
            if (key == null) return;
            lock (_groupRound) _groupRound[key] = _toolRound;
        }

        /// <summary>当前处于激活期的工具组。/ Tool groups currently active.</summary>
        internal IReadOnlyCollection<string> ActiveToolGroups()
        {
            lock (_groupRound)
                return _groupRound.Where(p => _toolRound - p.Value < AgentToolGroups.StickyRounds).Select(p => p.Key).ToList();
        }

        /// <summary>清空工具组激活状态（新对话）。/ Clears group activation (new conversation).</summary>
        private void ResetToolGroups()
        {
            lock (_groupRound) _groupRound.Clear();
        }

        /// <summary>
        /// 本轮向模型公布的内置工具：关闭分组时为全部；开启时为核心工具 + 激活中的分组。未公布的工具仍通过 AdditionalTools 可调用。
        /// Built-in tools advertised this round: all of them when grouping is off; otherwise core tools plus the active groups.
        /// Unadvertised tools remain invocable through AdditionalTools.
        /// </summary>
        private IList<AITool> AdvertisedBuiltIns()
        {
            if (!ToolGrouping) return _tools;
            var active = new HashSet<string>(ActiveToolGroups(), StringComparer.OrdinalIgnoreCase);
            return _tools.Where(t =>
            {
                string key = AgentToolGroups.GroupOf(t.Name);
                return key == null || active.Contains(key);
            }).ToList();
        }

        /// <summary>load_tools 的说明（含分组目录）。/ Description of load_tools, including the group catalog.</summary>
        internal static string LoadToolsDescription() =>
            "加载按需提供的工具组，返回组内工具的说明与参数（JSON Schema）；加载后本轮即可按名调用，后续几轮也会出现在工具列表中。需要的工具不在工具列表中时先调用本工具，不要说做不到。"
            + " / Loads on-demand tool groups and returns each tool's description and parameters (JSON Schema); once loaded they can be called by name in this round and appear in the tool list for the next rounds. Call this first when a tool you need is not in your tool list instead of saying it is unavailable."
            + " 分组 / Groups — " + AgentToolGroups.Catalog();

        private string LoadTools(
            [Description("要加载的组键，多个用逗号分隔，all 表示全部 / Group keys, comma-separated, or all")] string groups)
        {
            var keys = (groups ?? "").Split(new[] { ',', '，', ';', '；', ' ', '、' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var picked = keys.Any(k => k.Equals("all", StringComparison.OrdinalIgnoreCase))
                ? AgentToolGroups.All.ToList()
                : keys.Select(AgentToolGroups.Find).Where(g => g != null).Distinct().ToList();
            if (picked.Count == 0)
                return "未识别的组 / Unknown group: " + (groups ?? "") + "\n可用组 / Available groups: " + AgentToolGroups.Catalog();
            var byName = _tools.OfType<AIFunction>().ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            lock (_groupRound)
                foreach (var g in picked) _groupRound[g.Key] = _toolRound;
            foreach (var g in picked)
            {
                sb.AppendLine("## " + g.Key + " — " + g.Title);
                foreach (string name in g.Tools)
                {
                    if (!byName.TryGetValue(name, out var f)) continue;
                    sb.AppendLine("- " + f.Name + "：" + (f.Description ?? "").Trim());
                    sb.AppendLine("  parameters: " + f.JsonSchema.ToString());
                }
            }
            sb.Append("已加载，可直接按名调用 / Loaded; call them by name now.");
            return sb.ToString();
        }
    }
}
