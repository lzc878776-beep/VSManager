using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace VSManager
{
    /// <summary>托盘区中的一个图标。/ One icon in the notification area.</summary>
    public sealed class TrayIconInfo
    {
        /// <summary>提示文字。/ Tooltip text.</summary>
        public string Tooltip;
        /// <summary>所属进程 PID，无法得知时为 0。/ Owner process id; 0 when unknown.</summary>
        public int Pid;
        public string ProcessName;
        /// <summary>所属窗口是否仍存在；false 表示残影，null 表示无法判断。/ Whether the owner window still exists; false = ghost, null = unknown.</summary>
        public bool? OwnerAlive;
        /// <summary>是否位于溢出区（隐藏的图标）。/ Whether it sits in the overflow area (hidden icons).</summary>
        public bool Overflow;
    }

    /// <summary>
    /// 界面探测：前台窗口与托盘图标，供 AI 总控助手验证重启后是否抢焦点、是否留下托盘残影等原本需要人工观察的测试项。
    /// 托盘先读传统工具栏（Windows 10 及更早，可得所属进程与残影）；没有时用 UI 自动化读 Windows 11 托盘（所属进程不公开）。
    /// UI probes: the foreground window and tray icons, so the AI assistant can verify items that used to need a human eye, such as
    /// focus stealing or ghost tray icons after a restart. The tray is read from the classic toolbar first (Windows 10 and earlier,
    /// with owner process and ghost detection); otherwise the Windows 11 tray is read through UI Automation (owners are not exposed).
    /// </summary>
    public static class UiProbe
    {
        /// <summary>描述当前前台窗口：标题、窗口类、所属进程，以及是否属于 VSManager。/ Describes the foreground window: title, class, owner process and whether it belongs to VSManager.</summary>
        public static string ForegroundText(int selfPid, IntPtr selfWindow)
        {
            var h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return "当前没有前台窗口（例如桌面、锁屏或窗口切换中）/ No foreground window (e.g. desktop, lock screen or switching).";
            Native.GetWindowThreadProcessId(h, out uint pid);
            var sb = new StringBuilder();
            sb.Append("前台窗口 / Foreground window: 「").Append(Title(h)).AppendLine("」");
            sb.Append("进程 / Process: ").Append(ProcessName((int)pid)).Append(" (PID ").Append(pid).AppendLine(")");
            sb.Append("窗口类 / Class: ").Append(ClassOf(h)).Append(" | 句柄 / Handle: 0x").Append(h.ToInt64().ToString("X")).AppendLine();
            string owner = pid == (uint)selfPid
                ? (h == selfWindow ? "是，VSManager 主窗口 / yes, the VSManager main window" : "是，VSManager 的其他窗口（对话框等）/ yes, another VSManager window (dialog etc.)")
                : "否 / no";
            sb.Append("属于本 VSManager 进程 / Belongs to this VSManager: ").Append(owner);
            return sb.ToString();
        }

        /// <summary>
        /// 列出托盘图标。openOverflow 为 true 时，Windows 11 上会短暂展开「显示隐藏的图标」以读取溢出区，读完收起并尽量把前台还给原窗口。
        /// Lists tray icons. With openOverflow on Windows 11 the "show hidden icons" flyout is opened briefly to read the overflow area,
        /// then closed, and the foreground is handed back to the previous window where possible.
        /// </summary>
        public static List<TrayIconInfo> TrayIcons(bool openOverflow, out string source, out bool overflowRead)
        {
            var legacy = LegacyTray();
            if (legacy.Count > 0) { source = "传统托盘工具栏 / classic tray toolbar"; overflowRead = true; return legacy; }
            source = "UI 自动化（Windows 11 托盘，不公开所属进程）/ UI Automation (Windows 11 tray; owners not exposed)";
            return AutomationTray(openOverflow, out overflowRead);
        }

        /// <summary>
        /// 生成托盘列表文字，并给出 VSManager 是否留有残影的结论。
        /// Formats the tray list and concludes whether VSManager left a ghost icon.
        /// </summary>
        public static string TrayText(IList<TrayIconInfo> icons, string source, bool overflowRead, string ownTooltip, bool ownVisible, int selfPid, int liveVsManagers)
        {
            var sb = new StringBuilder();
            sb.Append("托盘图标 / Tray icons（").Append(source).Append("）共 / total ").Append(icons.Count).AppendLine(":");
            for (int i = 0; i < icons.Count; i++)
            {
                var t = icons[i];
                sb.Append(i + 1).Append(". 「").Append(OneLine(t.Tooltip)).Append("」");
                if (t.Pid > 0) sb.Append(" | 进程 / process: ").Append(t.ProcessName ?? "?").Append(" (PID ").Append(t.Pid).Append(')');
                if (t.OwnerAlive == false) sb.Append(" | 所属窗口已不存在（残影）/ owner window gone (ghost)");
                if (t.Overflow) sb.Append(" | 溢出区 / overflow");
                sb.AppendLine();
            }
            if (!overflowRead) sb.AppendLine("未读取溢出区（隐藏的图标）；图标可能在那里 / The overflow (hidden icons) area was not read; icons may be there.");
            int matched = string.IsNullOrEmpty(ownTooltip) ? 0 : icons.Count(i => Matches(i, ownTooltip));
            int ghosts = icons.Count(i => i.OwnerAlive == false && Matches(i, ownTooltip));
            sb.Append("VSManager：本进程 PID ").Append(selfPid).Append(" 托盘图标").Append(ownVisible ? "显示中" : "已隐藏").Append("，提示文字「").Append(ownTooltip ?? "?")
              .Append("」；匹配的图标 ").Append(matched).Append(" 个，运行中的 VSManager 进程 ").Append(liveVsManagers).Append(" 个。/ This process's tray icon is ")
              .Append(ownVisible ? "shown" : "hidden").Append("; ").Append(matched).Append(" matching icons, ").Append(liveVsManagers).AppendLine(" VSManager processes running.");
            sb.Append("结论 / Verdict: ").Append(GhostVerdict(matched, ghosts, liveVsManagers, ownVisible, overflowRead));
            return sb.ToString();
        }

        /// <summary>
        /// 残影结论：所属窗口已失效的图标，或图标数多于运行中的 VSManager 进程数，都视为残影。
        /// Ghost verdict: icons whose owner window is gone, or more icons than running VSManager processes, count as ghosts.
        /// </summary>
        public static string GhostVerdict(int matched, int deadOwners, int liveProcesses, bool ownVisible, bool overflowRead)
        {
            if (deadOwners > 0) return "有 " + deadOwners + " 个 VSManager 托盘残影（所属窗口已不存在）/ " + deadOwners + " VSManager ghost icon(s) (owner window gone)";
            if (matched > Math.Max(1, liveProcesses)) return "疑似托盘残影：VSManager 图标 " + matched + " 个多于运行中的进程 " + liveProcesses + " 个 / Possible ghost icon: " + matched + " VSManager icons for " + liveProcesses + " running processes";
            if (matched == 0 && ownVisible)
                return overflowRead ? "没有找到本进程的托盘图标（可能被系统隐藏或提示文字不同）/ This process's tray icon was not found (hidden by the system or a different tooltip)"
                    : "无法判断：本进程图标可能在未读取的溢出区 / Inconclusive: the icon may be in the unread overflow area";
            if (!overflowRead && matched <= 1) return "未读取溢出区，无法排除其中的残影 / The overflow area was not read, so ghosts there cannot be ruled out";
            return "没有托盘残影 / No ghost tray icon";
        }

        private static bool Matches(TrayIconInfo i, string tooltip) =>
            !string.IsNullOrEmpty(tooltip) && (i.Tooltip ?? "").IndexOf(tooltip, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

        internal static string Title(IntPtr h)
        {
            var sb = new StringBuilder(512);
            Native.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            Native.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        internal static string ProcessName(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return "?"; }
        }

        // ---- Windows 11：UI 自动化 / Windows 11: UI Automation ----

        private static readonly Regex OverflowButtonName = new Regex(@"隐藏的图标|hidden icons|Show hidden|chevron", RegexOptions.IgnoreCase);

        private static List<TrayIconInfo> AutomationTray(bool openOverflow, out bool overflowRead)
        {
            var result = new List<TrayIconInfo>();
            overflowRead = false;
            var bar = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            if (bar == null) return result;
            foreach (AutomationElement e in bar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon")))
                result.Add(new TrayIconInfo { Tooltip = SafeName(e) });
            var flyout = FindOverflow();
            AutomationElement chevron = null;
            IntPtr before = Native.GetForegroundWindow();
            if (flyout == null && openOverflow)
            {
                var buttons = bar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon")).Cast<AutomationElement>().ToList();
                chevron = buttons.FirstOrDefault(b => OverflowButtonName.IsMatch(SafeName(b))) ?? buttons.FirstOrDefault(b => SafeClass(b) == "SystemTray.NormalButton");
                if (chevron != null && Invoke(chevron))
                    for (int i = 0; i < 15 && flyout == null; i++) { Thread.Sleep(100); flyout = FindOverflow(); }
            }
            try
            {
                if (flyout != null)
                {
                    overflowRead = true;
                    foreach (AutomationElement e in flyout.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon")))
                        result.Add(new TrayIconInfo { Tooltip = SafeName(e), Overflow = true });
                }
            }
            finally
            {
                if (chevron != null && flyout != null)
                {
                    Invoke(chevron);
                    Thread.Sleep(150);
                    HandBack(before);
                }
            }
            return result;
        }

        private static AutomationElement FindOverflow() =>
            AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland"));

        private static bool Invoke(AutomationElement e)
        {
            try
            {
                if (e.TryGetCurrentPattern(InvokePattern.Pattern, out object p)) { ((InvokePattern)p).Invoke(); return true; }
            }
            catch { }
            return false;
        }

        /// <summary>把前台还给展开溢出区之前的窗口（不改置顶、不改显示状态）。/ Hands the foreground back to the window active before the flyout (no topmost or show-state changes).</summary>
        private static void HandBack(IntPtr before)
        {
            if (before == IntPtr.Zero || !Native.IsWindow(before) || Native.GetForegroundWindow() == before) return;
            var fg = Native.GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
            uint me = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            try { Native.SetForegroundWindow(before); }
            finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
        }

        private static string SafeName(AutomationElement e) { try { return e.Current.Name ?? ""; } catch { return ""; } }
        private static string SafeClass(AutomationElement e) { try { return e.Current.ClassName ?? ""; } catch { return ""; } }

        // ---- Windows 10 及更早：传统托盘工具栏 / Windows 10 and earlier: classic tray toolbar ----

        private const int TB_GETBUTTON = 0x0417, TB_BUTTONCOUNT = 0x0418, TB_GETBUTTONTEXTW = 0x044B;
        private const int PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint MEM_COMMIT = 0x1000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04, SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buffer, UIntPtr size, out UIntPtr read);

        private static List<TrayIconInfo> LegacyTray()
        {
            var result = new List<TrayIconInfo>();
            // 结构体布局按 64 位资源管理器读取；32 位进程读不了 64 位地址，直接改用 UI 自动化 / Layouts assume a 64-bit explorer; a 32-bit process falls back to UI Automation
            if (IntPtr.Size != 8) return result;
            var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            var pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
            ReadToolbar(FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null), false, result);
            ReadToolbar(FindWindowEx(FindWindowEx(IntPtr.Zero, IntPtr.Zero, "NotifyIconOverflowWindow", null), IntPtr.Zero, "ToolbarWindow32", null), true, result);
            return result;
        }

        private static void ReadToolbar(IntPtr toolbar, bool overflow, List<TrayIconInfo> result)
        {
            if (toolbar == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(toolbar, out uint explorer);
            var proc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, (int)explorer);
            if (proc == IntPtr.Zero) return;
            var mem = IntPtr.Zero;
            try
            {
                mem = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)4096, MEM_COMMIT, PAGE_READWRITE);
                if (mem == IntPtr.Zero) return;
                if (SendMessageTimeout(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out IntPtr countPtr) == IntPtr.Zero) return;
                int count = Math.Min(countPtr.ToInt32(), 256);
                for (int i = 0; i < count; i++)
                {
                    // TBBUTTON（x64，32 字节）：idCommand 在偏移 4，dwData 在偏移 16 / TBBUTTON (x64, 32 bytes): idCommand at 4, dwData at 16
                    if (SendMessageTimeout(toolbar, TB_GETBUTTON, (IntPtr)i, mem, SMTO_ABORTIFHUNG, 1000, out IntPtr ok) == IntPtr.Zero || ok == IntPtr.Zero) continue;
                    var button = Read(proc, mem, 32);
                    if (button == null) continue;
                    int command = BitConverter.ToInt32(button, 4);
                    long data = BitConverter.ToInt64(button, 16);
                    // TRAYDATA 开头是所属窗口句柄 / TRAYDATA starts with the owner window handle
                    var tray = data == 0 ? null : Read(proc, (IntPtr)data, 8);
                    var owner = tray == null ? IntPtr.Zero : (IntPtr)BitConverter.ToInt64(tray, 0);
                    string text = "";
                    if (SendMessageTimeout(toolbar, TB_GETBUTTONTEXTW, (IntPtr)command, mem, SMTO_ABORTIFHUNG, 1000, out IntPtr len) != IntPtr.Zero && len.ToInt64() > 0)
                    {
                        var chars = Read(proc, mem, (int)Math.Min(len.ToInt64(), 1000) * 2);
                        if (chars != null) text = Encoding.Unicode.GetString(chars);
                    }
                    var info = new TrayIconInfo { Tooltip = text, Overflow = overflow };
                    if (owner != IntPtr.Zero)
                    {
                        info.OwnerAlive = Native.IsWindow(owner);
                        if (info.OwnerAlive == true)
                        {
                            Native.GetWindowThreadProcessId(owner, out uint pid);
                            info.Pid = (int)pid;
                            info.ProcessName = ProcessName((int)pid);
                        }
                    }
                    result.Add(info);
                }
            }
            catch { }
            finally
            {
                if (mem != IntPtr.Zero) VirtualFreeEx(proc, mem, UIntPtr.Zero, MEM_RELEASE);
                CloseHandle(proc);
            }
        }

        private static byte[] Read(IntPtr proc, IntPtr addr, int size)
        {
            var buffer = new byte[size];
            return ReadProcessMemory(proc, addr, buffer, (UIntPtr)size, out UIntPtr read) && read.ToUInt64() == (ulong)size ? buffer : null;
        }
    }
}
