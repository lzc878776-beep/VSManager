using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>可选的 Agent 自循环反馈能力（侧边栏「Agent 自循环」区），可从后台线程调用。/ Optional agent self-loop feedback capability (the sidebar "Agent loop" section); accepts background-thread calls.</summary>
    public interface IAgentEvalHost
    {
        /// <summary>读取代码查找评测运行目录；apply 时自动选择最合适的目录。/ Reads code to find evaluation run folders; with apply, selects the best one.</summary>
        Task<string> FindAgentEvalFolder(string path, bool apply);

        /// <summary>当前评测目录中的全部问题（不截断）。/ Every issue in the current evaluation folder (not truncated).</summary>
        Task<string> DescribeAgentEvalIssues(string state);
    }

    public sealed partial class AgentService
    {
        [Description("Agent 自循环反馈技能：读取代码自动选择评测目录。在已打开 / 已登记解决方案所在仓库（或 path 指定的文件夹）中查找写出 result.json / cases.json 的评测脚本与代码，"
            + "从中解析由环境变量与字面路径拼出的运行目录（只保留实际存在且含运行子目录的文件夹），按最近运行排序；apply=true（默认）时把最合适的文件夹设为侧边栏「Agent 自循环」的评测目录并刷新。只读代码，不执行脚本。"
            + " / Agent self-loop feedback skill: reads code to select the evaluation folder automatically. Searches the repositories of open / registered solutions (or the folder given by path) "
            + "for evaluation scripts and code that write result.json / cases.json, resolves the run folders built from environment variables and literal paths (keeping only existing folders that hold runs), "
            + "orders them by the latest run, and with apply=true (default) sets the best folder as the evaluation folder of the sidebar \"Agent loop\" and refreshes it. Reads code only; never runs scripts.")]
        private async Task<string> FindAgentEvalFolder(
            [Description("要读取的代码文件夹，留空为已打开与已登记的解决方案所在仓库 / Code folder to read; empty means the repositories of open and registered solutions")] string path = "",
            [Description("是否自动选择找到的最佳文件夹，默认 true；false 只列出候选 / Select the best folder found, default true; false only lists candidates")] bool apply = true)
        {
            if (!(_host is IAgentEvalHost host)) return "宿主不支持 Agent 自循环反馈 / Host does not support agent self-loop feedback";
            return Truncate(await host.FindAgentEvalFolder(path ?? "", apply), MaxToolText);
        }

        [Description("列出 Agent 自循环评测的全部问题（按「会话名 + 轮次」归并所有运行的失败：仍失败 / 未再运行 / 已恢复，含失败次数、最近失败运行与原因），不省略任何一条；内容较长时用 offset 分段继续读取。未设置评测目录时先调用 find_agent_eval_folder。"
            + " / Lists every agent self-loop evaluation issue (failures of all runs merged by conversation and turn: failing / not rerun / resolved, with failure count, latest failing run and reason), omitting none; "
            + "read long output in parts with offset. Call find_agent_eval_folder first when no evaluation folder is set.")]
        private async Task<string> ListAgentEvalIssues(
            [Description("状态筛选：留空或 all 为全部，failing 仍失败，notrerun 未再运行，resolved 已恢复 / State filter: empty or all, failing, notrerun, resolved")] string state = "",
            [Description("从第几个字符继续读取，默认 0 / Character offset to continue from, default 0")] int offset = 0)
        {
            if (!(_host is IAgentEvalHost host)) return "宿主不支持 Agent 自循环反馈 / Host does not support agent self-loop feedback";
            return Page(await host.DescribeAgentEvalIssues(state ?? ""), offset, MaxToolText);
        }

        /// <summary>按字符分段返回长文本，并提示下一段的 offset（尽量在换行处断开）。/ Returns long text in parts with the next offset (breaking at a newline when possible).</summary>
        internal static string Page(string text, int offset, int max)
        {
            text = text ?? "";
            offset = Math.Max(0, Math.Min(offset, text.Length));
            max = Math.Max(500, max);
            if (text.Length - offset <= max) return offset == 0 ? text : text.Substring(offset);
            int end = text.LastIndexOf('\n', offset + max - 1, max);
            if (end <= offset) end = offset + max;
            return text.Substring(offset, end - offset).TrimEnd()
                + $"\n……还有 {text.Length - end} 个字符，请用 offset={end} 继续读取 / {text.Length - end} more characters; continue with offset={end}";
        }
    }
}
