using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>可选的显示器感知布局能力，所有方法可从后台线程调用。/ Optional monitor-aware layout capability; all methods accept background-thread calls.</summary>
    public interface IAgentWorkspaceLayoutHost
    {
        Task<WorkspaceDisplaySnapshot> GetDisplays();
        Task<string> ArrangeWorkspace(WorkspaceLayoutPlan plan, string displaySignature);
        Task<string> RestoreWorkspaceLayout();
    }

    public sealed partial class AgentService
    {
        [Description("读取显示器数量、编号、分辨率、桌面位置、工作区、主屏、当前前台窗口及各 VS 所在屏幕；只读，不改变布局。/ Reads numbered display geometry, work areas, primary/current screens and VS locations without changing layout.")]
        private async Task<string> GetDisplays()
        {
            if (!(_host is IAgentWorkspaceLayoutHost host)) return "宿主不支持显示器查询 / Host does not support display discovery";
            return (await host.GetDisplays()).Describe();
        }

        [Description("自动布局 VS 主窗口及工具窗格（默认全部包含，可逐项关闭）。按各屏尺寸与主屏属性（不是编号）分配：主窗口集中在主屏（与 VSManager 同屏），资源管理器同屏相邻；Copilot 集中在尺寸足够大的非主屏中最大的一块，过小的屏幕不放对话；自动模式下输出与错误列表优先剩余屏幕，否则与 Copilot 上下分布；没有合适副屏时与主窗口左右分区。空间不足时请用 place_workspace_windows 自行调整。不保存或关闭文件，不最小化 VS，可还原。/ Arranges main VS windows and tool panes (all included by default, each optional), assigning screens by size and the primary flag rather than numbers: main windows share the primary screen (with VSManager) with their adjacent Solution Explorers; Copilot chats use the largest sufficiently large non-primary screen and never a too-small one; auto mode prefers a remaining screen for Output and Error List, otherwise stacked below Copilot; without a suitable secondary screen the chats split the main screen. Adapt with place_workspace_windows if space is insufficient. Never saves/closes files or minimizes VS; restorable.")]
        private async Task<string> ArrangeWorkspace(
            [Description("VS 编号或名称，多个用逗号分隔，留空为全部 / Comma-separated VS numbers or names; empty means all")] string vs = "",
            [Description("主窗口与解决方案资源管理器屏幕编号，0 自动 / Main-window and Solution Explorer screen number; 0 selects automatically")] int mainScreen = 0,
            [Description("Copilot、输出与错误列表屏幕编号；0 自动按角色分屏 / Copilot, Output and Error List screen number; 0 automatically separates screen roles")] int paneScreen = 0,
            [Description("同时布局输出窗格，默认 true / Include Output, default true")] bool includeOutput = true,
            [Description("同时布局错误列表，默认 true / Include Error List, default true")] bool includeErrorList = true,
            [Description("同时布局解决方案资源管理器，默认 true / Include Solution Explorer, default true")] bool includeSolutionExplorer = true)
        {
            if (!(_host is IAgentWorkspaceLayoutHost host)) return "宿主不支持工作区布局 / Host does not support workspace layout";
            var targets = new List<VsInstance>();
            var parts = (vs ?? "").Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            foreach (var part in parts)
            {
                if (!Resolve(part, out var target, out var error)) return error;
                if (!targets.Contains(target)) targets.Add(target);
            }
            if (parts.Count == 0) targets.AddRange(_host.Instances.ToArray());
            if (targets.Count == 0) return "当前没有正在运行的 Visual Studio / No running Visual Studio instances";
            var snapshot = await host.GetDisplays();
            WorkspaceLayoutPlan plan;
            try { plan = WorkspaceLayoutPlan.Create(snapshot, targets, targets.Select(_host.NameOf).ToList(), mainScreen, paneScreen, includeOutput, includeErrorList, includeSolutionExplorer); }
            catch (ArgumentException ex) { return ex.Message; }
            if (_settings().AgentConfirm && !await ConfirmAsync("自动布局 VS 工作区 / Arrange VS workspace",
                plan.Describe() + "\n将浮动所选工具窗格；不保存或关闭文件，可还原 / Selected tool panes will float; files remain untouched; layout can be restored."))
                return "用户拒绝了该操作 / User cancelled the operation";
            return Truncate(await host.ArrangeWorkspace(plan, snapshot.Signature), MaxToolText);
        }

        [Description("按你自己设计的位置摆放 VS 主窗口与 Copilot / 输出 / 错误列表 / 解决方案资源管理器窗格。先 get_displays 了解各屏尺寸、方向与相对位置，再为每个 VS 选择屏幕和位置。"
            + WorkspaceLayoutPlan.ScreenPolicyZh + " / " + WorkspaceLayoutPlan.ScreenPolicyEn + " "
            + "layout 为 JSON 数组，每项 {\"vs\":\"1\",\"main\":{\"screen\":1,\"x\":0,\"y\":0,\"w\":65,\"h\":100},\"copilot\":{...},\"output\":{...},\"errorList\":{...},\"solutionExplorer\":{...}}；"
            + "x/y/w/h 为该屏工作区百分比（0–100），main 必填，其余窗格省略则不移动；尚未创建的内置窗格会被创建，还原时恢复隐藏。重叠或过小只警告不拒绝。不保存或关闭文件，不最小化 VS，可用 restore_workspace_layout 还原。"
            + " / Places VS main windows and Copilot/Output/Error List/Solution Explorer panes where you decide. Call get_displays first (sizes, orientation, relative positions), then choose a screen and position per VS. "
            + "layout is a JSON array of {\"vs\":\"1\",\"main\":{\"screen\":1,\"x\":0,\"y\":0,\"w\":65,\"h\":100},\"copilot\":{...},\"output\":{...},\"errorList\":{...},\"solutionExplorer\":{...}}; "
            + "x/y/w/h are percentages (0–100) of that screen's work area; main is required, omitted panes are not moved; built-in panes not yet created are created and hidden again on restore. Overlaps and small sizes only warn. Never saves/closes files or minimizes VS; restorable.")]
        private async Task<string> PlaceWorkspace(
            [Description("布局 JSON，见工具说明 / Layout JSON, see the tool description")] string layout)
        {
            if (!(_host is IAgentWorkspaceLayoutHost host)) return "宿主不支持工作区布局 / Host does not support workspace layout";
            return await ApplyCustomLayout(host, layout, "按 AI 设计布局 VS 工作区 / Apply AI-designed VS layout");
        }

        /// <summary>屏幕状态 + VS 个数组成的记忆键。/ Memory key made of the display state and the VS count.</summary>
        internal static string LayoutMemoryKey(string displaySignature, int vsCount) => displaySignature + "|vs=" + vsCount;

        [Description("窗口布局记忆：把布局记在当前屏幕状态（各屏分辨率、位置、主屏）与当前 VS 个数下，例如家里屏幕一种、公司屏幕另一种；相同屏幕状态与 VS 个数已有记录时直接替换。用户说「存储 / 记住当前布局」时直接调用本工具且 layout 留空，按窗口当前实际位置记录；此时不要先调用 arrange_workspace_layout / place_workspace_windows 等会移动窗口的工具。"
            + " / Window layout memory: stores a layout under the current display state (resolutions, positions, primary screen) and VS count, e.g. one for home screens and another for office screens; an existing record for the same state and VS count is replaced. When the user asks to save / remember the current layout, call this tool directly with an empty layout to record the actual current window positions; do not call arrange_workspace_layout / place_workspace_windows or other window-moving tools first.")]
        private async Task<string> SaveLayoutMemory(
            [Description("布局 JSON，格式同 place_workspace_windows；留空表示直接记录各 VS 主窗口当前的实际位置（不移动任何窗口）/ Layout JSON in place_workspace_windows format; empty records the current actual position of each VS main window (moves nothing)")] string layout = "")
        {
            if (!(_host is IAgentWorkspaceLayoutHost host)) return "宿主不支持工作区布局 / Host does not support workspace layout";
            var snapshot = await host.GetDisplays();
            if (string.IsNullOrWhiteSpace(layout))
            {
                layout = CaptureCurrentLayout(snapshot, out string captureError);
                if (layout == null) return captureError;
            }
            try { WorkspaceCustomLayoutParser.Parse(layout); }
            catch (ArgumentException ex) { return ex.Message; }
            int count = _host.Instances.Count;
            string key = LayoutMemoryKey(snapshot.Signature, count);
            var settings = _settings();
            bool replaced;
            var list = settings.LayoutMemories ?? (settings.LayoutMemories = new List<AliasEntry>());
            lock (list)
            {
                replaced = list.RemoveAll(a => a.Key == key) > 0;
                list.Add(new AliasEntry { Key = key, Alias = layout.Trim() });
            }
            if (!settings.Save()) return "布局记忆保存失败 / Failed to save the layout memory";
            return (replaced ? "已替换" : "已记录") + $"布局记忆（{snapshot.Displays.Count} 块屏幕，{count} 个 VS）/ Layout memory "
                + (replaced ? "replaced" : "saved") + $" ({snapshot.Displays.Count} screens, {count} VS)";
        }

        /// <summary>
        /// 把各 VS 主窗口当前位置换算为所在屏工作区百分比的布局 JSON，只读不移动窗口。
        /// Converts each VS main window's current position into layout JSON as percentages of its screen's work area; read-only.
        /// </summary>
        private string CaptureCurrentLayout(WorkspaceDisplaySnapshot snapshot, out string error)
        {
            error = null;
            var items = new List<string>();
            var instances = _host.Instances.ToArray();
            for (int i = 0; i < instances.Length; i++)
            {
                var hwnd = instances[i].MainHwnd;
                if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || !Native.GetWindowRect(hwnd, out var r)) continue;
                var rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                var center = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                var d = snapshot.Displays.FirstOrDefault(x => x.Bounds.Contains(center)) ?? snapshot.Displays.FirstOrDefault(x => x.Primary);
                if (d == null || d.WorkingArea.Width <= 0 || d.WorkingArea.Height <= 0) continue;
                var wa = d.WorkingArea;
                int Pct(int value, int total) => Math.Max(0, Math.Min(100, (int)Math.Round(value * 100.0 / total)));
                int x = Pct(rect.Left - wa.Left, wa.Width), y = Pct(rect.Top - wa.Top, wa.Height);
                int w = Math.Max(1, Math.Min(100 - x, Pct(rect.Width, wa.Width))), h = Math.Max(1, Math.Min(100 - y, Pct(rect.Height, wa.Height)));
                items.Add($"{{\"vs\":\"{i + 1}\",\"main\":{{\"screen\":{d.Number},\"x\":{x},\"y\":{y},\"w\":{w},\"h\":{h}}}}}");
            }
            if (items.Count == 0)
            {
                error = "没有可记录的 VS 主窗口（未运行或已最小化）/ No VS main window to record (none running or all minimized)";
                return null;
            }
            return "[" + string.Join(",", items) + "]";
        }

        [Description("按窗口布局记忆一键布局：检测当前屏幕状态与 VS 个数，找到匹配的记录后直接应用（可用 restore_workspace_layout 还原）。没有匹配时返回屏幕信息，请按屏幕尺寸设计布局并用 save_layout_memory 保存。"
            + " / One-click layout from memory: detects the current display state and VS count and applies the matching record (restorable with restore_workspace_layout). Without a match it returns display info; design a layout for the screens and store it with save_layout_memory.")]
        private async Task<string> ApplyLayoutMemory()
        {
            if (!(_host is IAgentWorkspaceLayoutHost host)) return "宿主不支持工作区布局 / Host does not support workspace layout";
            var snapshot = await host.GetDisplays();
            int count = _host.Instances.Count;
            if (count == 0) return "当前没有正在运行的 Visual Studio / No running Visual Studio instances";
            string key = LayoutMemoryKey(snapshot.Signature, count);
            var list = _settings().LayoutMemories;
            string layout = null;
            if (list != null) lock (list) layout = list.LastOrDefault(a => a.Key == key)?.Alias;
            if (layout == null)
                return $"当前屏幕状态与 {count} 个 VS 没有布局记忆，请按屏幕尺寸设计后用 save_layout_memory 保存 / No layout memory for this display state and {count} VS; design one and save it with save_layout_memory.\n" + snapshot.Describe();
            return await ApplyCustomLayout(host, layout, "按布局记忆布局 VS 工作区 / Apply remembered VS layout");
        }

        [Description("列出全部窗口布局记忆（屏幕状态、VS 个数、布局 JSON），并标出与当前状态匹配的一条；只读。/ Lists all window layout memories (display state, VS count, layout JSON) and marks the one matching the current state; read-only.")]
        private async Task<string> ListLayoutMemories()
        {
            if (!(_host is IAgentWorkspaceLayoutHost host)) return "宿主不支持工作区布局 / Host does not support workspace layout";
            string current = LayoutMemoryKey((await host.GetDisplays()).Signature, _host.Instances.Count);
            var list = _settings().LayoutMemories;
            List<AliasEntry> items;
            if (list == null) items = new List<AliasEntry>();
            else lock (list) items = list.ToList();
            if (items.Count == 0) return "暂无布局记忆 / No layout memories";
            return Truncate(string.Join("\n", items.Select((a, i) => $"#{i + 1}{(a.Key == current ? " [当前 / current]" : "")} {a.Key}\n{a.Alias}")), MaxToolText);
        }

        [Description("删除一条窗口布局记忆，编号来自 list_layout_memories。/ Deletes one window layout memory by its list_layout_memories number.")]
        private Task<string> DeleteLayoutMemory(
            [Description("记录编号（从 1 开始）/ Record number (1-based)")] int number)
        {
            var settings = _settings();
            var list = settings.LayoutMemories;
            if (list == null) return Task.FromResult("暂无布局记忆 / No layout memories");
            lock (list)
            {
                if (number < 1 || number > list.Count) return Task.FromResult("编号无效 / Invalid number");
                list.RemoveAt(number - 1);
            }
            return Task.FromResult(settings.Save() ? "已删除布局记忆 / Layout memory deleted" : "布局记忆保存失败 / Failed to save the layout memory");
        }

        private async Task<string> ApplyCustomLayout(IAgentWorkspaceLayoutHost host, string layout, string title)
        {
            List<WorkspaceCustomPlacement> items;
            try { items = WorkspaceCustomLayoutParser.Parse(layout); }
            catch (ArgumentException ex) { return ex.Message; }
            foreach (var item in items)
            {
                if (!Resolve(item.VsRef, out var target, out var error)) return error;
                item.Vs = target;
                item.Name = _host.NameOf(target);
            }
            var snapshot = await host.GetDisplays();
            WorkspaceLayoutPlan plan;
            try { plan = WorkspaceLayoutPlan.CreateCustom(snapshot, items); }
            catch (ArgumentException ex) { return ex.Message; }
            if (_settings().AgentConfirm && !await ConfirmAsync(title,
                plan.Describe() + "\n将浮动所选工具窗格；不保存或关闭文件，可还原 / Selected tool panes will float; files remain untouched; layout can be restored."))
                return "用户拒绝了该操作 / User cancelled the operation";
            return Truncate(await host.ArrangeWorkspace(plan, snapshot.Signature), MaxToolText);
        }

        [Description("还原 arrange_workspace_layout / place_workspace_windows 之前的 VS 主窗口与 Copilot、输出、错误列表、解决方案资源管理器窗格状态。/ Restores main VS windows and Copilot/Output/Error List/Solution Explorer states saved by arrange_workspace_layout / place_workspace_windows.")]
        private async Task<string> RestoreWorkspaceLayout()
        {
            if (!(_host is IAgentWorkspaceLayoutHost host)) return "宿主不支持工作区布局 / Host does not support workspace layout";
            if (_settings().AgentConfirm && !await ConfirmAsync("还原 VS 工作区布局 / Restore VS workspace layout",
                "恢复自动布局前的主窗口位置与窗格状态，不保存或关闭文件 / Restore previous main-window and pane states without saving or closing files."))
                return "用户拒绝了该操作 / User cancelled the operation";
            return Truncate(await host.RestoreWorkspaceLayout(), MaxToolText);
        }
    }
}
