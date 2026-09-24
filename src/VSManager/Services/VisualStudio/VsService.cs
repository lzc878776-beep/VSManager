using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace VSManager
{
    public class VsInstance
    {
        public int Pid;
        public IntPtr MainHwnd;
        public object Dte;
        /// <summary>解决方案路径；直接打开项目（无 .sln）时为项目文件路径。</summary>
        public string SolutionPath = "";
        /// <summary>启动命令行中的解决方案 / 项目文件（null 表示尚未读取）。</summary>
        public string LaunchPath;
        public string Title = "";
        public string Key = "";
        /// <summary>进程启动时间（UTC Ticks），与 Pid 一起唯一标识一个 VS 实例（PID 可能被复用）。</summary>
        public long StartTicks;
        /// <summary>实例级配置键：同一解决方案被多个 VS 打开时用它区分各实例。</summary>
        public string InstanceKey => "inst:" + Pid + ":" + StartTicks;
        public volatile CopilotState Copilot = CopilotState.Unknown;
        public DateTime BusySince;
        public int IdleCount;
        /// <summary>最近一次 Copilot 任务完成的时间；再次开始运行时清空。</summary>
        public DateTime? CompletedAt;
        public TimeSpan LastDuration;
        /// <summary>完成后用户尚未查看该 VS。</summary>
        public bool CompletionUnseen;
        /// <summary>DTE dbgDebugMode：1 设计，2 中断，3 运行；0 未知。</summary>
        public int DebugMode;
        public bool Building;

        public string DisplaySolution =>
            !string.IsNullOrEmpty(SolutionPath) ? Path.GetFileNameWithoutExtension(SolutionPath) : VsService.TitleName(Title);
    }

    public static class VsService
    {
        public const string OutputKind = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}";
        public const string ErrorListKind = "{D78612C7-9962-4B83-95D9-268046DAD23A}";

        public static Dictionary<int, object> GetDtesByPid()
        {
            var result = new Dictionary<int, object>();
            try
            {
                if (Native.GetRunningObjectTable(0, out var rot) != 0) return result;
                Native.CreateBindCtx(0, out var ctx);
                rot.EnumRunning(out IEnumMoniker en);
                var m = new IMoniker[1];
                while (en.Next(1, m, IntPtr.Zero) == 0)
                {
                    try
                    {
                        m[0].GetDisplayName(ctx, null, out string name);
                        if (name == null || !name.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase)) continue;
                        int colon = name.LastIndexOf(':');
                        if (colon > 0 && int.TryParse(name.Substring(colon + 1), out int pid))
                        {
                            rot.GetObject(m[0], out object o);
                            if (o != null) result[pid] = o;
                        }
                    }
                    catch { }
                    finally
                    {
                        if (m[0] != null) { try { Marshal.ReleaseComObject(m[0]); } catch { } m[0] = null; }
                    }
                }
                try { Marshal.ReleaseComObject(en); Marshal.ReleaseComObject(ctx); Marshal.ReleaseComObject(rot); } catch { }
            }
            catch { }
            return result;
        }

        public static IntPtr FindMainWindow(Process p)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            foreach (var h in Native.GetProcessWindows(p.Id))
            {
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) continue;
                var t = Native.GetText(h);
                if (t.IndexOf("Visual Studio", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var r = Native.GetRect(h);
                long area = (long)r.Width * r.Height;
                if (area > bestArea) { best = h; bestArea = area; }
            }
            if (best == IntPtr.Zero)
            {
                try { best = p.MainWindowHandle; } catch { }
            }
            return best;
        }

        /// <summary>刷新 VS 实例列表，复用已存在的实例对象以保留状态。</summary>
        public static List<VsInstance> Enumerate(Dictionary<int, VsInstance> existing)
        {
            var dtes = GetDtesByPid();
            var list = new List<VsInstance>();
            var procs = Process.GetProcessesByName("devenv");
            try
            {
            foreach (var p in procs)
            {
                try
                {
                    var hwnd = FindMainWindow(p);
                    if (hwnd == IntPtr.Zero) continue;
                    if (!existing.TryGetValue(p.Id, out var vs))
                    {
                        vs = new VsInstance { Pid = p.Id };
                        try { vs.StartTicks = p.StartTime.ToUniversalTime().Ticks; } catch { }
                    }
                    vs.MainHwnd = hwnd;
                    vs.Title = Native.GetText(hwnd);
                    if (dtes.TryGetValue(p.Id, out var dte)) vs.Dte = dte;
                    if (vs.Dte != null)
                    {
                        try
                        {
                            dynamic d = vs.Dte;
                            string sln = d.Solution.FullName;
                            // 直接打开 .csproj 时解决方案是临时的（FullName 为空），改用第一个项目的路径
                            if (string.IsNullOrEmpty(sln))
                                try { if (d.Solution.Projects.Count > 0) sln = d.Solution.Projects.Item(1).FullName; } catch { }
                            vs.SolutionPath = sln ?? "";
                        }
                        catch { }
                        ReadDebugState(vs);
                    }
                    if (string.IsNullOrEmpty(vs.SolutionPath))
                    {
                        if (vs.LaunchPath == null) vs.LaunchPath = LaunchFile(p.Id) ?? "";
                        if (vs.LaunchPath.Length > 0) vs.SolutionPath = vs.LaunchPath;
                    }
                    vs.Key = !string.IsNullOrEmpty(vs.SolutionPath)
                        ? vs.SolutionPath
                        : "title:" + TitleName(vs.Title);
                    list.Add(vs);
                }
                catch { }
            }
            }
            finally
            {
                // Process 对象持有进程句柄与线程快照，每次刷新都要释放 / Process objects hold handles and thread snapshots; release them every refresh
                foreach (var p in procs) p.Dispose();
            }
            return list.OrderBy(v => v.Pid).ToList();
        }

        /// <summary>从 devenv 启动命令行中取出存在的 .sln / .slnx / 项目文件路径。</summary>
        private static string LaunchFile(int pid)
        {
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                    foreach (System.Management.ManagementObject o in q.Get())
                    {
                        string cmd = o["CommandLine"] as string ?? "";
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(cmd, "\"([^\"]+)\"|(\\S+)"))
                        {
                            string a = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                            if (System.Text.RegularExpressions.Regex.IsMatch(a, @"\.(sln|slnx|csproj|vbproj|fsproj|vcxproj)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && File.Exists(a))
                                return Path.GetFullPath(a);
                        }
                    }
            }
            catch { }
            return null;
        }

        public static string StripSuffix(string title)
        {
            int i = title.LastIndexOf(" - Microsoft Visual Studio", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? title.Substring(0, i) : title;
        }

        /// <summary>
        /// 从窗口标题取稳定的解决方案名：“VSManager (正在运行) - xxx.md [预览] - Microsoft Visual Studio” → “VSManager”。
        /// 标题随活动文档和调试状态变化，不能直接作为名称或配置键。
        /// </summary>
        public static string TitleName(string title)
        {
            string s = StripSuffix(title ?? "").Trim();
            int dash = s.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) s = s.Substring(0, dash);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(\s*[（(][^()（）]{1,20}[)）])+\s*$", "").Trim();
            return s.Length > 0 ? s : (title ?? "");
        }

        public static void ReadDebugState(VsInstance vs)
        {
            if (vs.Dte == null) { vs.DebugMode = 0; vs.Building = false; return; }
            try { dynamic d = vs.Dte; vs.DebugMode = Convert.ToInt32(d.Debugger.CurrentMode); } catch { vs.DebugMode = 0; }
            try { dynamic d = vs.Dte; vs.Building = Convert.ToInt32(d.Solution.SolutionBuild.BuildState) == 2; } catch { vs.Building = false; }
        }

        /// <summary>对指定 VS 执行调试 / 生成命令。</summary>
        public static string DebugAction(VsInstance vs, string action)
        {
            if (vs?.Dte == null) return "无法连接到该 VS 的自动化接口 (DTE)";
            try
            {
                dynamic dte = vs.Dte;
                // 通过 VS 命令路由执行，与 IDE 按钮使用相同的状态检查和命令处理。
                switch (action)
                {
                    case "go": dte.ExecuteCommand("Debug.Start"); return "已启动或继续调试";
                    case "run": dte.ExecuteCommand("Debug.StartWithoutDebugging"); return "已开始执行（不调试）";
                    case "break": dte.ExecuteCommand("Debug.BreakAll"); return "已全部中断";
                    case "stop": dte.ExecuteCommand("Debug.StopDebugging"); return "已停止调试";
                    case "restart": dte.ExecuteCommand("Debug.Restart"); return "已重新启动调试";
                    case "stepover": dte.ExecuteCommand("Debug.StepOver"); return "逐过程";
                    case "stepinto": dte.ExecuteCommand("Debug.StepInto"); return "逐语句";
                    case "stepout": dte.ExecuteCommand("Debug.StepOut"); return "跳出";
                    case "build": dte.ExecuteCommand("Build.BuildSolution"); return "已开始生成解决方案";
                    case "rebuild": dte.ExecuteCommand("Build.RebuildSolution"); return "已开始重新生成解决方案";
                    case "cancelbuild": dte.ExecuteCommand("Build.Cancel"); return "已取消生成";
                    default: return "未知操作";
                }
            }
            catch (Exception ex)
            {
                return "当前状态下无法执行：" + ex.Message;
            }
            finally
            {
                ReadDebugState(vs);
            }
        }

        /// <summary>读取错误列表（错误优先），用于 AI 助手汇报生成结果。</summary>
        public static string ReadErrorList(VsInstance vs, int max)
        {
            if (vs?.Dte == null) return "无法连接到该 VS 的自动化接口 (DTE)";
            try
            {
                dynamic dte = vs.Dte;
                dynamic items = dte.ToolWindows.ErrorList.ErrorItems;
                int count = Convert.ToInt32(items.Count);
                if (count == 0) return "错误列表为空（没有错误或警告）。";
                var list = new List<(int level, string text)>();
                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        dynamic it = items.Item(i);
                        int level = Convert.ToInt32(it.ErrorLevel);
                        string file = "";
                        try { file = Path.GetFileName((string)it.FileName ?? ""); } catch { }
                        int line = 0;
                        try { line = Convert.ToInt32(it.Line); } catch { }
                        string kind = level >= 4 ? "错误" : level >= 2 ? "警告" : "消息";
                        list.Add((level, $"[{kind}] {(file.Length > 0 ? file + (line > 0 ? "(" + line + ")" : "") + " " : "")}{(string)it.Description}"));
                    }
                    catch { }
                }
                int errors = list.Count(x => x.level >= 4), warnings = list.Count(x => x.level >= 2 && x.level < 4);
                var sb = new System.Text.StringBuilder($"共 {errors} 个错误、{warnings} 个警告、{list.Count - errors - warnings} 条消息：\n");
                foreach (var x in list.OrderByDescending(x => x.level).Take(max)) sb.AppendLine(x.text);
                if (list.Count > max) sb.Append("…其余 ").Append(list.Count - max).Append(" 条已省略");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "读取错误列表失败：" + ex.Message;
            }
        }

        #region 启动配置（launchSettings.json）

        public class LaunchProfiles
        {
            public string Project = "";
            public string ProjectPath = "";
            public List<string> Names = new List<string>();
            public string Active;
            /// <summary>项目系统是否支持通过 ActiveDebugProfile 属性切换。</summary>
            public bool CanSet;
        }

        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        private static object FindProject(dynamic projects, string uniqueName, int depth = 0)
        {
            if (depth > 8) return null;
            foreach (dynamic p in projects)
            {
                try
                {
                    string un = null;
                    try { un = p.UniqueName; } catch { }
                    if (string.Equals(un, uniqueName, StringComparison.OrdinalIgnoreCase)) return p;
                    string kind = null;
                    try { kind = p.Kind; } catch { }
                    if (string.Equals(kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
                    {
                        var subs = new List<object>();
                        foreach (dynamic it in p.ProjectItems)
                        {
                            object sp = null;
                            try { sp = it.SubProject; } catch { }
                            if (sp != null) subs.Add(sp);
                        }
                        var r = FindProject(subs, uniqueName, depth + 1);
                        if (r != null) return r;
                    }
                }
                catch { }
            }
            return null;
        }

        private static object StartupProject(dynamic dte)
        {
            object sp = dte.Solution.SolutionBuild.StartupProjects;
            string first = (sp as object[])?.OfType<string>().FirstOrDefault() ?? (sp as Array)?.OfType<string>().FirstOrDefault();
            if (string.IsNullOrEmpty(first)) return null;
            return FindProject(dte.Solution.Projects, first);
        }

        /// <summary>读取启动项目的调试启动配置。必须在 DteWorker 线程调用。</summary>
        public static LaunchProfiles GetLaunchProfiles(VsInstance vs)
        {
            var r = new LaunchProfiles();
            if (vs?.Dte == null) return r;
            try
            {
                dynamic dte = vs.Dte;
                dynamic proj = StartupProject(dte);
                if (proj == null) return r;
                try { r.Project = proj.Name; } catch { }
                try { r.ProjectPath = proj.FullName; } catch { }
                if (string.IsNullOrEmpty(r.ProjectPath)) return r;
                string dir = Path.GetDirectoryName(r.ProjectPath);
                foreach (var rel in new[] { @"Properties\launchSettings.json", @"My Project\launchSettings.json" })
                {
                    var f = Path.Combine(dir, rel);
                    if (!File.Exists(f)) continue;
                    var json = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                    if (json != null && json.TryGetValue("profiles", out var pr) && pr is Dictionary<string, object> profiles)
                        r.Names.AddRange(profiles.Keys);
                    break;
                }
                try { r.Active = Convert.ToString(proj.Properties.Item("ActiveDebugProfile").Value); r.CanSet = true; } catch { }
                if (string.IsNullOrEmpty(r.Active))
                {
                    var user = r.ProjectPath + ".user";
                    if (File.Exists(user))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(user), "<ActiveDebugProfile>(.*?)</ActiveDebugProfile>");
                        if (m.Success) r.Active = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                    }
                }
                if (string.IsNullOrEmpty(r.Active) || !r.Names.Contains(r.Active)) r.Active = r.Names.FirstOrDefault();
            }
            catch { }
            return r;
        }

        /// <summary>切换启动项目的调试启动配置。必须在 DteWorker 线程调用。</summary>
        public static string SetLaunchProfile(VsInstance vs, string name)
        {
            if (vs?.Dte == null) return "无法连接到该 VS 的自动化接口 (DTE)";
            try
            {
                dynamic dte = vs.Dte;
                dynamic proj = StartupProject(dte);
                if (proj == null) return "未找到启动项目";
                proj.Properties.Item("ActiveDebugProfile").Value = name;
                return "启动配置已切换为「" + name + "」";
            }
            catch (Exception ex) { return "切换启动配置失败：" + ex.Message; }
        }

        #endregion

        public const string CopilotChatCommand = "View.GitHub.Copilot.Chat";

        /// <summary>在 VS 中打开（并显示）GitHub Copilot 对话窗格。不会把 VS 切到前台。必须在 DteWorker 线程调用。</summary>
        public static bool OpenCopilotChat(VsInstance vs)
        {
            if (vs?.Dte == null) return false;
            try { dynamic dte = vs.Dte; dte.ExecuteCommand(CopilotChatCommand); return true; }
            catch { return false; }
        }

        /// <summary>
        /// 把 Copilot 对话窗格切换为工具窗口（取消“停靠为选项卡式文档”和自动隐藏），
        /// 这样切换文档标签时对话窗格不会被隐藏。必须在 DteWorker 线程调用。
        /// </summary>
        public static string DockCopilotAsToolWindow(VsInstance vs, string keyword)
        {
            if (vs?.Dte == null) return "无法连接到该 VS 的自动化接口 (DTE)";
            if (string.IsNullOrWhiteSpace(keyword)) keyword = "Copilot";
            try
            {
                dynamic dte = vs.Dte;
                try { dte.ExecuteCommand(CopilotChatCommand); }
                catch (Exception ex) { return "无法打开 Copilot 对话窗格：" + ex.Message; }

                dynamic win = null;
                try { if (IsCopilotWindow(dte.ActiveWindow, keyword)) win = dte.ActiveWindow; } catch { }
                if (win == null)
                    foreach (dynamic w in dte.Windows)
                        if (IsCopilotWindow(w, keyword)) { win = w; break; }
                if (win == null) return "未找到 Copilot 对话窗格";

                var changes = new List<string>();
                if (!(bool)win.Linkable) { win.Linkable = true; changes.Add("取消选项卡式文档"); }
                // 浮动窗口不会被文档标签隐藏，保持用户的选择
                try { if ((bool)win.AutoHides) { win.AutoHides = false; changes.Add("取消自动隐藏"); } } catch { }
                if (changes.Count > 0 && !(bool)win.IsFloating && IsStrip(dte, win))
                {
                    // 没有记住的侧边停靠位置时 VS 会停靠成顶部 / 底部的窄条，改为浮动在 VS 右侧
                    try
                    {
                        System.Threading.Thread.Sleep(300);
                        int ml = dte.MainWindow.Left, mt = dte.MainWindow.Top, mw = dte.MainWindow.Width, mh = dte.MainWindow.Height;
                        win.IsFloating = true;
                        int w = Math.Max(480, (int)(mw * 0.36));
                        win.Width = w;
                        win.Height = Math.Max(400, mh - (int)(mh * 0.12));
                        win.Left = ml + mw - w - (int)(mw * 0.1);
                        win.Top = mt + (int)(mh * 0.07);
                        changes.Add("浮动在右侧，可拖到停靠位置，VS 会记住");
                    }
                    catch { }
                }
                try { win.Visible = true; win.Activate(); } catch { }
                return changes.Count == 0 ? "已是工具窗口" : "已切换为工具窗口（" + string.Join("、", changes) + "）";
            }
            catch (Exception ex) { return "切换失败：" + ex.Message; }
        }

        /// <summary>
        /// 显示 Copilot 对话工具窗口：执行 View.GitHub.Copilot.Chat，找到窗口后若为自动隐藏则固定显示、若不可见则设为可见；不改变停靠方式，也不强制前置 VS。
        /// 返回诊断文字，<paramref name="wasAutoHide"/> 为窗口原本是否自动隐藏（未知为 null）。必须在 DteWorker 线程调用。
        /// Shows the Copilot chat tool window: runs View.GitHub.Copilot.Chat, then pins it when auto-hidden and makes it visible;
        /// never changes the docking style or forces VS to the front. Returns diagnostics; <paramref name="wasAutoHide"/> tells
        /// whether the window was auto-hidden (null = unknown). Must run on the DteWorker thread.
        /// </summary>
        public static bool ShowCopilotChatWindow(VsInstance vs, string keyword, out bool? wasAutoHide, out string diagnostics)
        {
            wasAutoHide = null;
            if (vs?.Dte == null) { diagnostics = "无法连接 DTE / DTE unavailable"; return false; }
            if (string.IsNullOrWhiteSpace(keyword)) keyword = "Copilot";
            try
            {
                dynamic dte = vs.Dte;
                try { dte.ExecuteCommand(CopilotChatCommand); }
                catch (Exception ex) { diagnostics = CopilotChatCommand + " 失败 / failed: " + ex.Message; return false; }

                dynamic win = null;
                try { if (IsCopilotWindow(dte.ActiveWindow, keyword)) win = dte.ActiveWindow; } catch { }
                if (win == null)
                    foreach (dynamic w in dte.Windows)
                        if (IsCopilotWindow(w, keyword)) { win = w; break; }
                if (win == null) { diagnostics = "命令已执行，但 DTE 窗口列表中没有对话窗格 / command ran but no chat window in DTE.Windows"; return true; }

                var notes = new List<string> { "命令已执行 / command ran" };
                try
                {
                    bool autoHide = (bool)win.AutoHides;
                    wasAutoHide = autoHide;
                    if (autoHide) { win.AutoHides = false; notes.Add("已取消自动隐藏 / auto-hide unpinned"); }
                }
                catch { notes.Add("无法读取自动隐藏 / auto-hide unknown"); }
                try
                {
                    bool visible = (bool)win.Visible;
                    notes.Add("可见 / visible=" + visible);
                    if (!visible) { win.Visible = true; notes.Add("已设为可见 / made visible"); }
                }
                catch { }
                diagnostics = string.Join("，", notes);
                return true;
            }
            catch (Exception ex) { diagnostics = "显示对话窗格失败 / show failed: " + ex.Message; return false; }
        }

        private static bool IsStrip(dynamic dte, dynamic win)
        {
            try
            {
                System.Threading.Thread.Sleep(300);
                int mw = dte.MainWindow.Width, mh = dte.MainWindow.Height, w = win.Width, h = win.Height;
                return mw > 0 && mh > 0 && w > mw * 0.6 && h < mh * 0.4;
            }
            catch { return false; }
        }

        /// <summary>
        /// 在 DTE 窗口集合中查找 Copilot 对话工具窗口，找不到返回 null。必须在 DteWorker 线程调用。
        /// Finds the Copilot chat tool window among the DTE windows; null when not found. DteWorker thread only.
        /// </summary>
        internal static object FindCopilotWindow(object dteObject, string keyword)
        {
            if (dteObject == null) return null;
            if (string.IsNullOrWhiteSpace(keyword)) keyword = "Copilot";
            dynamic dte = dteObject;
            try { if (IsCopilotWindow(dte.ActiveWindow, keyword)) return dte.ActiveWindow; } catch { }
            try
            {
                foreach (dynamic w in dte.Windows)
                    if (IsCopilotWindow(w, keyword)) return w;
            }
            catch { }
            return null;
        }

        private static bool IsCopilotWindow(dynamic w, string keyword)
        {
            try
            {
                if (w == null) return false;
                string caption = w.Caption ?? "";
                if (caption.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) return false;
                // 排除文件名中带 Copilot 的代码文档
                try { if (w.Document != null) return false; } catch { }
                return string.Equals((string)w.Kind, "Tool", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>把输出窗口/错误列表设为浮动，并摆放到指定屏幕。</summary>
        public static string MoveToolWindows(VsInstance vs, Rectangle area, string layout)
        {
            if (vs.Dte == null) return "无法连接到该 VS 的自动化接口 (DTE)";
            var targets = new List<Tuple<string, Rectangle>>();
            int hw = area.Width / 2, hh = area.Height / 2;
            switch (layout)
            {
                case "左右":
                    targets.Add(Tuple.Create(OutputKind, new Rectangle(area.X, area.Y, hw, area.Height)));
                    targets.Add(Tuple.Create(ErrorListKind, new Rectangle(area.X + hw, area.Y, area.Width - hw, area.Height)));
                    break;
                case "仅输出":
                    targets.Add(Tuple.Create(OutputKind, area));
                    break;
                case "仅错误列表":
                    targets.Add(Tuple.Create(ErrorListKind, area));
                    break;
                default:
                    targets.Add(Tuple.Create(OutputKind, new Rectangle(area.X, area.Y, area.Width, hh)));
                    targets.Add(Tuple.Create(ErrorListKind, new Rectangle(area.X, area.Y + hh, area.Width, area.Height - hh)));
                    break;
            }

            var msgs = new List<string>();
            foreach (var t in targets)
            {
                string name = t.Item1 == OutputKind ? "输出" : "错误列表";
                try
                {
                    dynamic dte = vs.Dte;
                    dynamic w = dte.Windows.Item(t.Item1);
                    w.Visible = true;
                    bool floating = false;
                    try { floating = w.IsFloating; } catch { }
                    if (!floating)
                    {
                        try { w.IsFloating = true; }
                        catch { try { w.Linkable = false; } catch { } }
                    }
                    IntPtr hwnd = WaitForFloatingWindow(vs, t.Item1);
                    if (hwnd != IntPtr.Zero)
                    {
                        Native.MoveWindow(hwnd, t.Item2);
                        msgs.Add(name + " ✓");
                    }
                    else msgs.Add(name + " 未找到浮动窗口");
                }
                catch (Exception ex) { msgs.Add(name + " 失败: " + ex.Message); }
            }
            return string.Join("，", msgs);
        }

        /// <summary>浮动工具窗口的标题统一为 "Microsoft Visual Studio"，需通过 UIA 中 ViewPresenter 的 AutomationId (ST:0:0:{guid}) 识别。</summary>
        private static IntPtr WaitForFloatingWindow(VsInstance vs, string kindGuid)
        {
            string guid = kindGuid.ToLowerInvariant();
            var cond = new PropertyCondition(AutomationElement.ClassNameProperty, "ViewPresenter");
            for (int i = 0; i < 20; i++)
            {
                foreach (var h in Native.GetProcessWindows(vs.Pid))
                {
                    if (h == vs.MainHwnd) continue;
                    try
                    {
                        var root = AutomationElement.FromHandle(h);
                        foreach (AutomationElement p in root.FindAll(TreeScope.Descendants, cond))
                            if ((p.Current.AutomationId ?? "").ToLowerInvariant().Contains(guid)) return h;
                    }
                    catch { }
                }
                Thread.Sleep(150);
            }
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 所有 DTE (COM) 调用集中在一个后台 STA 线程执行，避免 VS 忙碌时阻塞界面线程。
    /// </summary>
    public static class DteWorker
    {
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();
        private static readonly Thread Worker;

        static DteWorker()
        {
            Worker = new Thread(() =>
            {
                MessageFilter.Register();
                foreach (var a in Queue.GetConsumingEnumerable())
                {
                    try { a(); } catch { }
                }
            }) { IsBackground = true, Name = "DteWorker" };
            Worker.SetApartmentState(ApartmentState.STA);
            Worker.Start();
        }

        public static bool IsWorkerThread => Thread.CurrentThread == Worker;

        public static Task<T> Run<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>();
            if (IsWorkerThread)
            {
                try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); }
                return tcs.Task;
            }
            Queue.Add(() =>
            {
                try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public static Task Run(Action a) => Run(() => { a(); return true; });

        /// <summary>在新的 STA 线程执行（UIA / 剪贴板操作）。</summary>
        public static Task<T> RunSta<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); }
            }) { IsBackground = true, Name = "StaTask" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return tcs.Task;
        }
    }
}
