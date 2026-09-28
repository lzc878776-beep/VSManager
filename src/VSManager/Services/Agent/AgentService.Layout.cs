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

        [Description("自动布局 VS 主窗口、Copilot 与输出窗格，可选错误列表。先读取显示器，0 为自动：主窗口优先当前屏，窗格放到其他最大工作区，多余屏幕分担 VS；单屏分区不重叠。不保存或关闭文件，不最小化 VS，可用 restore_workspace_layout 还原。/ Arranges main VS windows, Copilot, Output and optional Error List; auto-selects displays, splits one screen, never saves/closes files or minimizes VS; supports restore.")]
        private async Task<string> ArrangeWorkspace(
            [Description("VS 编号或名称，多个用逗号分隔，留空为全部 / Comma-separated VS numbers or names; empty means all")] string vs = "",
            [Description("主窗口屏幕编号，0 自动 / Main-window screen number; 0 selects automatically")] int mainScreen = 0,
            [Description("窗格屏幕编号，0 自动 / Tool-pane screen number; 0 selects automatically")] int paneScreen = 0,
            [Description("同时布局输出窗格，默认 true / Include Output, default true")] bool includeOutput = true,
            [Description("同时布局错误列表，默认 false / Include Error List, default false")] bool includeErrorList = false)
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
            try { plan = WorkspaceLayoutPlan.Create(snapshot, targets, targets.Select(_host.NameOf).ToList(), mainScreen, paneScreen, includeOutput, includeErrorList); }
            catch (ArgumentException ex) { return ex.Message; }
            if (_settings().AgentConfirm && !await ConfirmAsync("自动布局 VS 工作区 / Arrange VS workspace",
                plan.Describe() + "\n将浮动所选工具窗格；不保存或关闭文件，可还原 / Selected tool panes will float; files remain untouched; layout can be restored."))
                return "用户拒绝了该操作 / User cancelled the operation";
            return Truncate(await host.ArrangeWorkspace(plan, snapshot.Signature), MaxToolText);
        }

        [Description("还原 arrange_workspace_layout 之前的 VS 主窗口与 Copilot、输出、错误列表窗格状态。/ Restores main VS windows and Copilot/Output/Error List states saved by arrange_workspace_layout.")]
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
