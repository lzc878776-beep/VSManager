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
            if (_settings().AgentConfirm && !await ConfirmAsync("按 AI 设计布局 VS 工作区 / Apply AI-designed VS layout",
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
