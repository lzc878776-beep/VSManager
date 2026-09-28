using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 总控助手工具分组（按需加载）：核心工具每轮都提供给模型，其余按领域分组，只在本轮文字相关、近几轮用过或模型调用 load_tools 时才出现在工具列表中，
    /// 以减少每次请求携带的工具定义、降低上下文占用并让模型更专注；未列出的工具仍可按名调用。
    /// Manager-assistant tool groups (loaded on demand): core tools are offered every round, the rest are grouped by domain and appear in the
    /// tool list only when the round's text is related, they were used recently, or the model calls load_tools. This trims the tool
    /// definitions sent with every request, saving context and keeping the model focused; unlisted tools can still be invoked by name.
    /// </summary>
    public static class AgentToolGroups
    {
        /// <summary>加载工具组的元工具名。/ Name of the meta tool that loads tool groups.</summary>
        public const string LoaderName = "load_tools";

        /// <summary>组被激活后保持提供的轮数。/ Rounds a group stays offered after it was activated.</summary>
        public const int StickyRounds = 8;

        /// <summary>一个工具组。/ A tool group.</summary>
        public sealed class Group
        {
            public string Key;
            public string Title;
            public string[] Tools;
            public Regex Trigger;
        }

        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>全部可按需加载的分组（未在任何组中的工具属于核心）。/ All on-demand groups (tools not in any group are core).</summary>
        public static readonly IReadOnlyList<Group> All = new[]
        {
            new Group
            {
                Key = "layout", Title = "屏幕布局与 Copilot 窗格 / Screen layout and Copilot panes",
                Tools = new[] { "close_cs_tabs", "dock_copilot_panes", "get_displays", "arrange_workspace_layout", "place_workspace_windows", "restore_workspace_layout", "arrange_copilot_panes", "restore_copilot_layout", "open_copilot" },
                Trigger = new Regex(@"布局|屏幕|显示器|多屏|窗口|排列|停靠|摆放|标签页|窗格|layout|screen|monitor|display|arrange|dock|window|\btabs?\b|pane", Opt),
            },
            new Group
            {
                Key = "selftest", Title = "VSManager 自测、重启与界面探针 / VSManager self-test, restart and UI probes",
                Tools = new[] { "restart_vsmanager_for_testing", "get_window_state", "get_foreground_window", "list_tray_icons", "read_restart_handoff", "prepare_restart_scenario", "start_self_iteration", "start_skill_gap_loop", "stop_self_iteration" },
                Trigger = new Regex(@"重启|自测|自验证|自迭代|自我迭代|补\s*skill|前台|托盘|窗口状态|交接|草稿|VSManager|restart|self[- ]?(test|verify|iteration)|foreground|tray|handoff|skill[- ]gap|draft", Opt),
            },
            new Group
            {
                Key = "inspect", Title = "代码 / 文件检查与截图 / Code and file inspection, screenshots",
                Tools = new[] { "scan_vs_code", "read_vs_file", "find_files", "search_file_contents", "read_file", "list_directory", "capture_vs_screenshot", "read_vs_screenshot" },
                Trigger = new Regex(@"文件|代码|源码|目录|文件夹|搜索|查找|截图|界面|画面|扫描|职责|file|code|folder|director|search|\bfind\b|grep|screenshot|\bscan\b|\.(cs|xaml|json|md|csproj|slnx?|config|xml|txt|log|ps1|py|js|ts)\b|%\w+%", Opt),
            },
            new Group
            {
                Key = "worktree", Title = "Git worktree",
                Tools = new[] { "create_worktree", "list_worktrees" },
                Trigger = new Regex(@"worktree|工作树|分支|并入|合并|branch|merge", Opt),
            },
            new Group
            {
                Key = "notes", Title = "笔记与 Notion 计划 / Notes and Notion plans",
                Tools = new[] { "list_notes", "read_note", "create_note", "append_to_note", "update_note", "format_note_card", "add_note_card", "add_note_card_styles", "preview_notion_plan", "dispatch_notion_plan" },
                Trigger = new Regex(@"笔记|记事|卡片|计划|notion|notebook|\bnotes?\b|\bcards?\b|\bplans?\b", Opt),
            },
            new Group
            {
                Key = "cad", Title = "CAD 调试与验证接口 / CAD debugging and verification",
                Tools = new[] { "set_cad_debug_drawing", "get_cad_debug_drawing", "run_cad_actions", "list_cad_adapters", "list_verify_checks", "run_verify_check" },
                Trigger = new Regex(@"CAD|图纸|\.dwg|\.dxf|dwg|dxf|drawing|ai\.exe|ai\.agent|验证接口|验证检查|verify[_ ]check|适配包|adapter|钢筋", Opt),
            },
            new Group
            {
                Key = "mcp", Title = "MCP 服务器管理 / MCP server management",
                Tools = new[] { "list_mcp_servers", "reconnect_mcp_servers" },
                Trigger = new Regex(@"MCP|外部工具|external tool", Opt),
            },
        };

        private static readonly Dictionary<string, Group> ByTool = All.SelectMany(g => g.Tools.Select(t => new { t, g }))
            .ToDictionary(x => x.t, x => x.g, StringComparer.OrdinalIgnoreCase);

        /// <summary>工具所属分组键；核心工具返回 null。/ Group key of a tool; null for core tools.</summary>
        public static string GroupOf(string tool) => tool != null && ByTool.TryGetValue(tool, out var g) ? g.Key : null;

        /// <summary>按键查找分组（不区分大小写）。/ Finds a group by key (case-insensitive).</summary>
        public static Group Find(string key) =>
            All.FirstOrDefault(g => string.Equals(g.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 本轮文字触发的分组：命中分组关键词，或直接提到组内工具名。
        /// Groups triggered by the round's text: a group keyword matches, or a tool name of the group is mentioned.
        /// </summary>
        public static IEnumerable<string> Triggered(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (var g in All)
                if (g.Trigger.IsMatch(text) || g.Tools.Any(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                    yield return g.Key;
        }

        /// <summary>load_tools 说明中的分组目录。/ Group catalog for the load_tools description.</summary>
        public static string Catalog() =>
            string.Join("; ", All.Select(g => g.Key + " (" + g.Title + "): " + string.Join(", ", g.Tools)));
    }
}
