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
                + $"位置 / Position=({d.Bounds.X},{d.Bounds.Y}); 工作区 / Work area=({Rect(d.WorkingArea)}); 主屏 / Primary={d.Primary}; "
                + $"方向 / Orientation={(d.Bounds.Height > d.Bounds.Width ? "竖屏 / portrait" : "横屏 / landscape")}"
                + (WorkspaceLayoutPlan.RoomyForChat(d) ? "" : "; 尺寸过小，不宜放 AI 对话 / too small for AI chats")))
            + RelativeText()
            + SuggestedRoles()
            + "\n" + string.Join("\n", VsLocations);

        /// <summary>
        /// 按尺寸与主屏属性建议各屏用途（与默认布局一致），供 AI 设计布局参考。
        /// Suggests a role per screen from size and the primary flag (same as the default layout) for the AI to consider.
        /// </summary>
        internal string SuggestedRoles()
        {
            if (Displays.Count == 0) return "";
            var roles = WorkspaceLayoutPlan.PickScreens(this, null, null, true);
            return $"\n建议用途 / Suggested roles: VS 主窗口与解决方案资源管理器 / main windows + Solution Explorer=#{roles.Main.Number}"
                + $"{(roles.Main.Primary ? "（主屏，与 VSManager 同屏 / primary, shared with VSManager）" : "")}; "
                + $"AI 对话 / AI chats=#{roles.Chat.Number}{(roles.Chat.Number == roles.Main.Number ? "（无合适副屏，与主窗口分区共用 / no suitable secondary screen; split with main windows）" : "")}; "
                + $"输出与错误列表 / Output + Error List=#{roles.Output.Number}{(roles.Output.Number == roles.Chat.Number ? "（在 AI 对话下方 / below the AI chats）" : "")}";
        }

        /// <summary>
        /// 屏幕之间的相对位置（左 / 右 / 上 / 下），便于 AI 理解物理摆放。
        /// Relative positions between screens (left / right / above / below) so the AI understands the physical arrangement.
        /// </summary>
        internal string RelativeText()
        {
            if (Displays.Count < 2) return "";
            var lines = new List<string>();
            for (int i = 0; i < Displays.Count; i++)
                for (int j = i + 1; j < Displays.Count; j++)
                    {
                        var r = Relation(Displays[i].Bounds, Displays[j].Bounds);
                        lines.Add($"#{Displays[j].Number} 位于 #{Displays[i].Number} 的{r.Zh} / #{Displays[j].Number} is {r.En} #{Displays[i].Number}");
                    }
            return "\n相对位置 / Relative layout:\n" + string.Join("\n", lines);
        }

        internal static (string Zh, string En) Relation(Rectangle a, Rectangle b)
        {
            const int tolerance = 8;
            string zh = "", en = "";
            if (b.Top >= a.Bottom - tolerance) { zh = "下方"; en = "below"; }
            else if (b.Bottom <= a.Top + tolerance) { zh = "上方"; en = "above"; }
            if (b.Left >= a.Right - tolerance) { zh = zh.Length > 0 ? "右" + zh : "右侧"; en = en.Length > 0 ? en + " and right of" : "right of"; }
            else if (b.Right <= a.Left + tolerance) { zh = zh.Length > 0 ? "左" + zh : "左侧"; en = en.Length > 0 ? en + " and left of" : "left of"; }
            return zh.Length == 0 ? ("重叠区域", "overlapping") : (zh, en);
        }
    }

    /// <summary>
    /// AI 指定的一个矩形：屏幕编号 + 该屏工作区内的百分比坐标（0–100）。
    /// A rectangle chosen by the AI: screen number plus percentage coordinates (0–100) inside that screen's work area.
    /// </summary>
    public sealed class WorkspaceRectSpec
    {
        public int Screen;
        public double X, Y, W, H;
    }

    /// <summary>AI 为一个 VS 指定的各窗口位置；窗格为 null 时不移动。/ Positions chosen by the AI for one VS; null panes are left untouched.</summary>
    public sealed class WorkspaceCustomPlacement
    {
        public string VsRef;
        public VsInstance Vs;
        public string Name;
        public WorkspaceRectSpec Main, Copilot, Output, ErrorList, SolutionExplorer;
    }

    /// <summary>只计算布局，不操作窗口；审批前后使用同一份计划。/ Pure layout math; the same plan is used before and after approval.</summary>
    public sealed class WorkspaceLayoutPlan
    {
        internal const string ScreenPolicyZh = "布局优先级：按各屏实际尺寸、方向与主屏属性（不是屏幕编号）判断用途，参考 get_displays 的「建议用途」。主屏留给 VSManager，VS 主界面可以放入主屏；所有 AI 助手（Copilot 对话）尽量集中在一块尺寸足够大的非主屏，不要放到尺寸过小的屏幕（如高度不足约 900 像素的窄条屏）；解决方案资源管理器与所属主界面同屏相邻；输出与错误列表优先使用第三块屏幕（较小的屏幕也可用于输出）。屏幕不够时，AI 对话在上、输出与错误列表在下，共用一屏；只有一屏或空间仍不足时，由 AI 根据尺寸、方向和窗口数量自行决定分区，优先可读性与不重叠，并说明取舍。用户明确指定的位置优先。";
        internal const string ScreenPolicyEn = "Layout priorities: judge each screen's role by its actual size, orientation and primary flag (not by its number); see the suggested roles from get_displays. Keep the primary screen for VSManager; VS main windows may use it. Keep all AI assistants (Copilot chats) together on one sufficiently large non-primary screen and never on a screen that is too small (e.g. a strip shorter than about 900 pixels); keep Solution Explorer beside its own main window on the same screen; prefer a third screen for Output and Error List (a smaller screen is fine for output). With fewer screens, stack AI chats above Output and Error List on one shared screen; with only one screen or still insufficient space, let the AI choose regions based on size, orientation and window count, prioritizing readability and avoiding overlaps, and explain the tradeoffs. Explicit user placements take precedence.";

        private readonly List<(VsInstance Target, int Pid, long Started, IntPtr Window)> _targets = new List<(VsInstance, int, long, IntPtr)>();
        internal bool TargetsUnchanged => _targets.All(t => t.Target.Pid == t.Pid && t.Target.StartTicks == t.Started && t.Target.MainHwnd == t.Window);
        public List<VsWorkspacePlacement> Placements { get; } = new List<VsWorkspacePlacement>();
        public bool IncludeOutput { get; private set; }
        public bool IncludeErrorList { get; private set; }
        public bool IncludeSolutionExplorer { get; private set; }
        public int PaneScreen { get; private set; }
        public int OutputScreen { get; private set; }
        public List<int> MainScreens { get; } = new List<int>();
        public bool Cramped { get; private set; }
        /// <summary>AI 自定义布局（不是自动算法）。/ Custom layout chosen by the AI (not the automatic algorithm).</summary>
        public bool Custom { get; private set; }
        public List<string> Warnings { get; } = new List<string>();

        public string Describe() => (Custom ? "AI 自定义布局 / Custom AI layout; " : "")
            + $"VS 主窗口屏幕 / Main screens: {string.Join(",", MainScreens)}; AI 对话屏幕 / AI chat screen: {(Custom ? "—" : PaneScreen.ToString())}; 输出屏幕 / Output screen: {(Custom || OutputScreen == 0 ? "—" : OutputScreen.ToString())}\n"
            + $"输出 / Output: {IncludeOutput}; 错误列表 / Error List: {IncludeErrorList}; 解决方案资源管理器 / Solution Explorer: {IncludeSolutionExplorer}\n"
            + string.Join("\n", Placements.Select(p => $"{p.Name}: VS=({WorkspaceDisplaySnapshot.Rect(p.MainBounds)})"
                + (p.CopilotBounds.IsEmpty ? "" : $"; Copilot=({WorkspaceDisplaySnapshot.Rect(p.CopilotBounds)})")
                + (IncludeOutput && !p.OutputBounds.IsEmpty ? $"; Output=({WorkspaceDisplaySnapshot.Rect(p.OutputBounds)})" : "")
                + (IncludeErrorList && !p.ErrorListBounds.IsEmpty ? $"; Error List=({WorkspaceDisplaySnapshot.Rect(p.ErrorListBounds)})" : "")
                + (IncludeSolutionExplorer && !p.SolutionExplorerBounds.IsEmpty ? $"; Solution Explorer=({WorkspaceDisplaySnapshot.Rect(p.SolutionExplorerBounds)})" : "")))
            + (Cramped ? "\n空间较小，窗口最小尺寸可能限制布局 / Limited space; window minimum sizes may constrain placement." : "")
            + (Warnings.Count > 0 ? "\n" + string.Join("\n", Warnings.Select(w => "⚠ " + w)) : "");

        /// <summary>
        /// 按 AI 指定的屏幕与百分比坐标生成计划：校验屏幕编号、范围与目标，重叠和过小只给出警告，由 AI 自行决定是否调整。
        /// Builds a plan from the screens and percentage coordinates chosen by the AI: validates screen numbers, ranges and targets;
        /// overlaps and small sizes only produce warnings so the AI can decide whether to adjust.
        /// </summary>
        public static WorkspaceLayoutPlan CreateCustom(WorkspaceDisplaySnapshot snapshot, IList<WorkspaceCustomPlacement> items)
        {
            if (snapshot == null || snapshot.Displays.Count == 0)
                throw new ArgumentException("没有可用显示器 / No displays available");
            if (items == null || items.Count == 0 || items.Any(i => i?.Vs == null))
                throw new ArgumentException("没有有效的 VS 目标 / No valid VS targets");
            if (items.Select(i => i.Vs).Distinct().Count() != items.Count)
                throw new ArgumentException("同一个 VS 出现多次 / The same VS appears more than once");
            var plan = new WorkspaceLayoutPlan { Custom = true };
            var all = new List<(string Label, Rectangle Rect)>();
            foreach (var item in items)
            {
                string name = item.Name ?? "VS";
                if (item.Main == null) throw new ArgumentException(name + "：缺少 main（主窗口位置）/ missing main (main window position)");
                var p = new VsWorkspacePlacement { Vs = item.Vs, Name = name };
                p.MainBounds = ToPixels(snapshot, item.Main, name + " main");
                if (item.Copilot != null) p.CopilotBounds = ToPixels(snapshot, item.Copilot, name + " copilot");
                if (item.Output != null) { p.OutputBounds = ToPixels(snapshot, item.Output, name + " output"); plan.IncludeOutput = true; }
                if (item.ErrorList != null) { p.ErrorListBounds = ToPixels(snapshot, item.ErrorList, name + " errorList"); plan.IncludeErrorList = true; }
                if (item.SolutionExplorer != null) { p.SolutionExplorerBounds = ToPixels(snapshot, item.SolutionExplorer, name + " solutionExplorer"); plan.IncludeSolutionExplorer = true; }
                if (!plan.MainScreens.Contains(item.Main.Screen)) plan.MainScreens.Add(item.Main.Screen);
                if (p.MainBounds.Width < 800 || p.MainBounds.Height < 500) plan.Warnings.Add($"{name} 主窗口小于 800x500，VS 可能无法缩到该尺寸 / main window below 800x500; VS may not shrink that far");
                foreach (var pane in new[] { ("Copilot", p.CopilotBounds), ("Output", p.OutputBounds), ("Error List", p.ErrorListBounds), ("Solution Explorer", p.SolutionExplorerBounds) })
                {
                    if (pane.Item2.IsEmpty) continue;
                    if (pane.Item2.Width < 300 || pane.Item2.Height < 150) plan.Warnings.Add($"{name} {pane.Item1} 小于 300x150 / {pane.Item1} below 300x150");
                    all.Add((name + " " + pane.Item1, pane.Item2));
                }
                all.Add((name + " VS", p.MainBounds));
                plan.Placements.Add(p);
                plan._targets.Add((p.Vs, p.Vs.Pid, p.Vs.StartTicks, p.Vs.MainHwnd));
            }
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                    if (all[i].Rect.IntersectsWith(all[j].Rect))
                        plan.Warnings.Add($"{all[i].Label} 与 {all[j].Label} 重叠 / {all[i].Label} overlaps {all[j].Label}");
            plan.Cramped = plan.Warnings.Any(w => w.Contains("below"));
            return plan;
        }

        /// <summary>把屏幕工作区内的百分比坐标换算成桌面像素。/ Converts percentage coordinates inside a screen's work area to desktop pixels.</summary>
        internal static Rectangle ToPixels(WorkspaceDisplaySnapshot snapshot, WorkspaceRectSpec spec, string label)
        {
            var display = snapshot.Displays.FirstOrDefault(d => d.Number == spec.Screen);
            if (display == null) throw new ArgumentException($"{label}：屏幕 #{spec.Screen} 不存在，请先 get_displays / screen #{spec.Screen} does not exist; call get_displays first");
            const double slack = 0.5;
            if (double.IsNaN(spec.X) || double.IsNaN(spec.Y) || double.IsNaN(spec.W) || double.IsNaN(spec.H)
                || spec.X < 0 || spec.Y < 0 || spec.W <= 0 || spec.H <= 0 || spec.X + spec.W > 100 + slack || spec.Y + spec.H > 100 + slack)
                throw new ArgumentException($"{label}：x/y/w/h 为工作区百分比，须满足 x,y≥0、w,h>0、x+w≤100、y+h≤100 / x/y/w/h are work-area percentages: x,y≥0, w,h>0, x+w≤100, y+h≤100");
            var area = display.WorkingArea;
            int left = area.X + (int)Math.Round(area.Width * spec.X / 100);
            int top = area.Y + (int)Math.Round(area.Height * spec.Y / 100);
            int right = Math.Min(area.Right, area.X + (int)Math.Round(area.Width * Math.Min(100, spec.X + spec.W) / 100));
            int bottom = Math.Min(area.Bottom, area.Y + (int)Math.Round(area.Height * Math.Min(100, spec.Y + spec.H) / 100));
            if (right - left < 4 || bottom - top < 4) throw new ArgumentException($"{label}：区域过小 / area too small");
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        /// <summary>AI 对话所需的最小工作区（像素）；更小的屏幕（如 2560x734 的窄条屏）不放对话。/ Minimum work area (pixels) for AI chats; smaller screens (e.g. a 2560x734 strip) never host chats.</summary>
        internal const int ChatMinWidth = 600, ChatMinHeight = 900;
        /// <summary>输出与错误列表所需的最小工作区高度（像素）。/ Minimum work-area height (pixels) for Output and Error List.</summary>
        internal const int OutputMinHeight = 300;

        internal static bool RoomyForChat(WorkspaceDisplay d) => d.WorkingArea.Width >= ChatMinWidth && d.WorkingArea.Height >= ChatMinHeight;

        private static WorkspaceDisplay Largest(IEnumerable<WorkspaceDisplay> list) =>
            list.OrderByDescending(d => (long)d.WorkingArea.Width * d.WorkingArea.Height).ThenBy(d => d.Number).FirstOrDefault();

        /// <summary>
        /// 按尺寸与属性（而不是编号）选择各屏用途：VS 主窗口用主屏（与 VSManager 同屏；主屏被指定给对话时用当前屏或其余最大屏）；
        /// AI 对话用尺寸足够的非主窗口屏中最大的一块，没有合适的就与主窗口同屏分区；输出与错误列表用剩余屏中最大且高度够用的一块，否则放在对话下方。显式指定优先。
        /// Picks screen roles by size and properties rather than numbers: main windows use the primary screen (shared with VSManager; the current or
        /// largest other screen when the primary is pinned for chats); AI chats use the largest roomy screen other than the main one, or split the main
        /// screen when none is roomy; Output and Error List use the largest remaining screen tall enough, otherwise sit below the chats. Explicit choices win.
        /// </summary>
        internal static (WorkspaceDisplay Main, WorkspaceDisplay Chat, WorkspaceDisplay Output) PickScreens(
            WorkspaceDisplaySnapshot snapshot, WorkspaceDisplay main, WorkspaceDisplay chat, bool autoOutput)
        {
            var displays = snapshot.Displays;
            if (main == null)
                main = new[] { displays.FirstOrDefault(d => d.Primary), displays.FirstOrDefault(d => d.Number == snapshot.CurrentScreen) }
                    .FirstOrDefault(d => d != null && d != chat)
                    ?? Largest(displays.Where(d => d != chat)) ?? chat ?? displays[0];
            if (chat == null) chat = Largest(displays.Where(d => d != main && RoomyForChat(d))) ?? main;
            var output = autoOutput
                ? Largest(displays.Where(d => d != main && d != chat && d.WorkingArea.Height >= OutputMinHeight)) ?? chat
                : chat;
            return (main, chat, output);
        }

        public static WorkspaceLayoutPlan Create(WorkspaceDisplaySnapshot snapshot, IList<VsInstance> targets,
            IList<string> names, int mainScreen, int paneScreen, bool includeOutput, bool includeErrorList, bool includeSolutionExplorer = false)
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

            var roles = PickScreens(snapshot,
                mainScreen > 0 ? displays.Single(d => d.Number == mainScreen) : null,
                paneScreen > 0 ? displays.Single(d => d.Number == paneScreen) : null,
                paneScreen == 0);
            var main = roles.Main;
            var panes = roles.Chat;
            bool hasOutput = includeOutput || includeErrorList;
            // 显式 paneScreen 保留输出同屏约束；自动模式才为输出另选屏幕。
            // An explicit paneScreen keeps Output on that screen; only auto mode selects a separate output screen.
            var output = hasOutput ? roles.Output : panes;
            var plan = new WorkspaceLayoutPlan { IncludeOutput = includeOutput, IncludeErrorList = includeErrorList,
                IncludeSolutionExplorer = includeSolutionExplorer, PaneScreen = panes.Number, OutputScreen = hasOutput ? output.Number : 0 };
            plan.MainScreens.Add(main.Number);
            Rectangle mainArea = main.WorkingArea, chatArea = panes.WorkingArea;
            if (main.Number == panes.Number)
            {
                int width = (int)((long)mainArea.Width * 65 / 100);
                chatArea = new Rectangle(mainArea.X + width, mainArea.Y, mainArea.Width - width, mainArea.Height);
                mainArea.Width = width;
            }
            Rectangle outputArea = output.WorkingArea;
            if (hasOutput && output.Number == panes.Number)
            {
                int height = (int)((long)chatArea.Height * 65 / 100);
                outputArea = new Rectangle(chatArea.X, chatArea.Y + height, chatArea.Width, chatArea.Height - height);
                chatArea.Height = height;
            }
            var mainCells = PaneGrid.Compute(mainArea, targets.Count, includeSolutionExplorer ? 1100 : 800, 500, PaneArrangement.Grid).Cells;
            var chatCells = PaneGrid.Compute(chatArea, targets.Count, 360, 300, PaneArrangement.Grid).Cells;
            var outputCells = hasOutput ? PaneGrid.Compute(outputArea, targets.Count, 360, 300, PaneArrangement.Grid).Cells : null;
            if (mainCells.Count != targets.Count || chatCells.Count != targets.Count || (hasOutput && outputCells.Count != targets.Count))
                throw new ArgumentException("窗口过多，显示器空间不足 / Too many windows for available display space");
            for (int i = 0; i < targets.Count; i++)
            {
                var p = new VsWorkspacePlacement { Vs = targets[i], Name = names[i], MainBounds = mainCells[i], CopilotBounds = chatCells[i] };
                if (includeSolutionExplorer)
                {
                    var area = mainCells[i];
                    int width = (int)((long)area.Width * 22 / 100);
                    p.SolutionExplorerBounds = new Rectangle(area.X, area.Y, width, area.Height);
                    p.MainBounds = new Rectangle(area.X + width, area.Y, area.Width - width, area.Height);
                }
                if (hasOutput)
                {
                    var area = outputCells[i];
                    if (includeOutput && includeErrorList)
                    {
                        int height = area.Height / 2;
                        p.OutputBounds = new Rectangle(area.X, area.Y, area.Width, height);
                        p.ErrorListBounds = new Rectangle(area.X, area.Y + height, area.Width, area.Height - height);
                    }
                    else if (includeOutput) p.OutputBounds = area;
                    else p.ErrorListBounds = area;
                }
                if (p.MainBounds.Width < 800 || p.MainBounds.Height < 500 || p.CopilotBounds.Width < 360 || p.CopilotBounds.Height < 300
                    || (includeOutput && (p.OutputBounds.Width < 300 || p.OutputBounds.Height < 150))
                    || (includeErrorList && (p.ErrorListBounds.Width < 300 || p.ErrorListBounds.Height < 150))
                    || (includeSolutionExplorer && (p.SolutionExplorerBounds.Width < 300 || p.SolutionExplorerBounds.Height < 150))) plan.Cramped = true;
                if (new[] { p.MainBounds, p.CopilotBounds }.Concat(includeOutput ? new[] { p.OutputBounds } : new Rectangle[0])
                    .Concat(includeErrorList ? new[] { p.ErrorListBounds } : new Rectangle[0])
                    .Concat(includeSolutionExplorer ? new[] { p.SolutionExplorerBounds } : new Rectangle[0]).Any(r => r.Width <= 0 || r.Height <= 0))
                    throw new ArgumentException("窗口过多，显示器空间不足 / Too many windows for available display space");
                plan.Placements.Add(p);
                plan._targets.Add((p.Vs, p.Vs.Pid, p.Vs.StartTicks, p.Vs.MainHwnd));
            }
            return plan;
        }
    }
}
