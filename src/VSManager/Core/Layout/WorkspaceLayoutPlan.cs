using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace VSManager
{
    public sealed class WorkspaceDisplay
    {
        public int Number { get; set; }
        public Rectangle Bounds { get; set; }
        public Rectangle WorkingArea { get; set; }
        public bool Primary { get; set; }
    }

    public sealed class WorkspaceDisplaySnapshot
    {
        public List<WorkspaceDisplay> Displays { get; } = new List<WorkspaceDisplay>();
        public int CurrentScreen { get; set; }
        public int ManagerScreen { get; set; }
        public List<string> VsLocations { get; } = new List<string>();
        public string Signature => string.Join(";", Displays.Select(d => $"{d.Number}:{Rect(d.Bounds)}:{Rect(d.WorkingArea)}:{d.Primary}"));

        internal static string Rect(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

        public string Describe() => $"屏幕数量 / Screen count: {Displays.Count}\n"
            + $"当前前台窗口屏幕 / Foreground screen: {CurrentScreen}; VSManager: {ManagerScreen}\n"
            + "编号与属性一致；坐标为桌面像素，工作区排除任务栏 / Numbers match Settings; desktop pixels; work areas exclude taskbars.\n"
            + string.Join("\n", Displays.Select(d => $"#{d.Number}: {d.Bounds.Width}x{d.Bounds.Height}; "
                + $"位置 / Position=({d.Bounds.X},{d.Bounds.Y}); 工作区 / Work area=({Rect(d.WorkingArea)}); 主屏 / Primary={d.Primary}"))
            + "\n" + string.Join("\n", VsLocations);
    }

    /// <summary>只计算布局，不操作窗口；审批前后使用同一份计划。/ Pure layout math; the same plan is used before and after approval.</summary>
    public sealed class WorkspaceLayoutPlan
    {
        private readonly List<(VsInstance Target, int Pid, long Started, IntPtr Window)> _targets = new List<(VsInstance, int, long, IntPtr)>();
        internal bool TargetsUnchanged => _targets.All(t => t.Target.Pid == t.Pid && t.Target.StartTicks == t.Started && t.Target.MainHwnd == t.Window);
        public List<VsWorkspacePlacement> Placements { get; } = new List<VsWorkspacePlacement>();
        public bool IncludeOutput { get; private set; }
        public bool IncludeErrorList { get; private set; }
        public int PaneScreen { get; private set; }
        public List<int> MainScreens { get; } = new List<int>();
        public bool Cramped { get; private set; }

        public string Describe() => $"VS 主窗口屏幕 / Main screens: {string.Join(",", MainScreens)}; 窗格屏幕 / Pane screen: {PaneScreen}\n"
            + $"输出 / Output: {IncludeOutput}; 错误列表 / Error List: {IncludeErrorList}\n"
            + string.Join("\n", Placements.Select(p => $"{p.Name}: VS=({WorkspaceDisplaySnapshot.Rect(p.MainBounds)}); Copilot=({WorkspaceDisplaySnapshot.Rect(p.CopilotBounds)})"
                + (IncludeOutput ? $"; Output=({WorkspaceDisplaySnapshot.Rect(p.OutputBounds)})" : "")
                + (IncludeErrorList ? $"; Error List=({WorkspaceDisplaySnapshot.Rect(p.ErrorListBounds)})" : "")))
            + (Cramped ? "\n空间较小，窗口最小尺寸可能限制布局 / Limited space; window minimum sizes may constrain placement." : "");

        public static WorkspaceLayoutPlan Create(WorkspaceDisplaySnapshot snapshot, IList<VsInstance> targets,
            IList<string> names, int mainScreen, int paneScreen, bool includeOutput, bool includeErrorList)
        {
            if (snapshot == null || snapshot.Displays.Count == 0)
                throw new ArgumentException("没有可用显示器 / No displays available");
            var displays = snapshot.Displays;
            if (displays.Any(d => d.Number <= 0 || d.WorkingArea.Width < 4 || d.WorkingArea.Height < 4)
                || displays.Select(d => d.Number).Distinct().Count() != displays.Count)
                throw new ArgumentException("显示器工作区无效 / Invalid display work areas");
            if (targets == null || targets.Count == 0 || names == null || names.Count != targets.Count)
                throw new ArgumentException("没有有效的 VS 目标 / No valid VS targets");
            if (mainScreen < 0 || paneScreen < 0
                || (mainScreen > 0 && !displays.Any(d => d.Number == mainScreen))
                || (paneScreen > 0 && !displays.Any(d => d.Number == paneScreen)))
                throw new ArgumentException("屏幕编号无效，请重新读取显示器 / Invalid screen number; read displays again");

            var main = displays.FirstOrDefault(d => d.Number == (mainScreen == 0 ? snapshot.CurrentScreen : mainScreen))
                ?? displays.FirstOrDefault(d => d.Primary) ?? displays[0];
            var panes = paneScreen > 0 ? displays.Single(d => d.Number == paneScreen)
                : displays.Where(d => d.Number != main.Number).OrderByDescending(d => (long)d.WorkingArea.Width * d.WorkingArea.Height).FirstOrDefault() ?? main;
            var mains = new List<WorkspaceDisplay> { main };
            if (mainScreen == 0 && main.Number != panes.Number)
                mains.AddRange(displays.Where(d => d.Number != main.Number && d.Number != panes.Number)
                    .OrderByDescending(d => (long)d.WorkingArea.Width * d.WorkingArea.Height));
            var plan = new WorkspaceLayoutPlan { IncludeOutput = includeOutput, IncludeErrorList = includeErrorList, PaneScreen = panes.Number };
            var mainCells = new Dictionary<int, Queue<Rectangle>>();
            Rectangle paneArea = panes.WorkingArea;
            for (int i = 0; i < mains.Count && i < targets.Count; i++)
            {
                var area = mains[i].WorkingArea;
                if (mains[i].Number == panes.Number)
                {
                    int width = (int)((long)area.Width * 65 / 100);
                    paneArea = new Rectangle(area.X + width, area.Y, area.Width - width, area.Height);
                    area.Width = width;
                }
                int count = (targets.Count + mains.Count - 1 - i) / mains.Count;
                var cells = PaneGrid.Compute(area, count, 800, 500, PaneArrangement.Grid);
                mainCells.Add(i, new Queue<Rectangle>(cells.Cells));
                plan.MainScreens.Add(mains[i].Number);
            }
            var paneCells = PaneGrid.Compute(paneArea, targets.Count, 360, 300, PaneArrangement.Grid).Cells;
            for (int i = 0; i < targets.Count; i++)
            {
                var p = new VsWorkspacePlacement { Vs = targets[i], Name = names[i], MainBounds = mainCells[i % mains.Count].Dequeue(), CopilotBounds = paneCells[i] };
                int extras = (includeOutput ? 1 : 0) + (includeErrorList ? 1 : 0);
                if (extras > 0)
                {
                    var area = paneCells[i];
                    int chatHeight = (int)((long)area.Height * 65 / 100);
                    p.CopilotBounds = new Rectangle(area.X, area.Y, area.Width, chatHeight);
                    var bottom = new Rectangle(area.X, area.Y + chatHeight, area.Width, area.Height - chatHeight);
                    if (extras == 1)
                    {
                        if (includeOutput) p.OutputBounds = bottom;
                        else p.ErrorListBounds = bottom;
                    }
                    else
                    {
                        int height = bottom.Height / 2;
                        p.OutputBounds = new Rectangle(bottom.X, bottom.Y, bottom.Width, height);
                        p.ErrorListBounds = new Rectangle(bottom.X, bottom.Y + height, bottom.Width, bottom.Height - height);
                    }
                }
                if (p.MainBounds.Width < 800 || p.MainBounds.Height < 500 || p.CopilotBounds.Width < 360 || p.CopilotBounds.Height < 300
                    || (includeOutput && p.OutputBounds.Height < 150) || (includeErrorList && p.ErrorListBounds.Height < 150)) plan.Cramped = true;
                if (new[] { p.MainBounds, p.CopilotBounds }.Concat(includeOutput ? new[] { p.OutputBounds } : new Rectangle[0])
                    .Concat(includeErrorList ? new[] { p.ErrorListBounds } : new Rectangle[0]).Any(r => r.Width <= 0 || r.Height <= 0))
                    throw new ArgumentException("窗口过多，显示器空间不足 / Too many windows for available display space");
                plan.Placements.Add(p);
                plan._targets.Add((p.Vs, p.Vs.Pid, p.Vs.StartTicks, p.Vs.MainHwnd));
            }
            return plan;
        }
    }
}
