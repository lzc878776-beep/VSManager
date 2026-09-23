using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>单个进程的内存占用与清理分类。/ Memory usage and cleanup classification of one process.</summary>
    public sealed class MemProc
    {
        public int Pid, ParentPid;
        public string Name = "";
        public long WorkingSet, Private;
        /// <summary>是否可安全清理（修剪工作集）。/ Whether it can be cleaned safely (working-set trim).</summary>
        public bool Trimmable;
        /// <summary>进程角色说明。/ Role of the process.</summary>
        public string Role = "";
        /// <summary>可 / 不可清理的原因。/ Why it can or cannot be cleaned.</summary>
        public string Reason = "";
        public bool Root;
        public bool AccessDenied;
    }

    public enum MemGroupKind { Self, Vs, Shared }

    /// <summary>一组进程：VSManager 本体、某个 VS 实例（devenv 及其子进程）或无归属的共享 VS 组件。/ A process group.</summary>
    public sealed class MemGroup
    {
        public MemGroupKind Kind;
        public int RootPid;
        /// <summary>VS 实例编号（与主界面一致，从 1 开始；0 表示不在列表中）。/ VS number as in the main window (1-based; 0 = not listed).</summary>
        public int Number;
        public string Name = "";
        public readonly List<MemProc> Procs = new List<MemProc>();

        public long WorkingSet => Procs.Sum(p => p.WorkingSet);
        public long Private => Procs.Sum(p => p.Private);
        public bool CanClean => Procs.Any(p => p.Trimmable);
        public string Key => Kind == MemGroupKind.Vs ? "vs:" + RootPid : Kind.ToString();

        /// <summary>界面上显示的归属名称。/ Owner label shown in the UI.</summary>
        public string Owner
        {
            get
            {
                switch (Kind)
                {
                    case MemGroupKind.Self: return "VSManager";
                    case MemGroupKind.Shared: return "共享组件 / Shared";
                    default: return (Number > 0 ? "#" + Number + " " : "") + Name;
                }
            }
        }
    }

    /// <summary>一次测量结果。/ One measurement.</summary>
    public sealed class MemSnapshot
    {
        public DateTime At;
        public readonly List<MemGroup> Groups = new List<MemGroup>();
        public long PhysTotal, PhysAvail;
        public long TotalWorkingSet => Groups.Sum(g => g.WorkingSet);
        public long TotalPrivate => Groups.Sum(g => g.Private);
        public MemGroup Find(string key) => Groups.FirstOrDefault(g => g.Key == key);
    }

    /// <summary>主界面中的 VS 实例引用（在界面线程上采集，后台测量时使用）。/ A VS instance captured on the UI thread.</summary>
    public sealed class VsRef
    {
        public VsInstance Vs;
        public int Number;
        public string Name;
    }

    /// <summary>一次清理的前后对比。/ Before/after comparison of one cleanup.</summary>
    public sealed class MemCleanResult
    {
        public string Target = "";
        public long WsBefore, PrivBefore, WsAfter, PrivAfter;
        public int Trimmed, Failed;
        public readonly List<string> Notes = new List<string>();

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(Target).Append("：工作集 / WS ").Append(VsMemory.Mb(WsBefore)).Append(" → ").Append(VsMemory.Mb(WsAfter))
              .Append("（").Append(VsMemory.Delta(WsAfter - WsBefore)).Append("），私有字节 / Private ")
              .Append(VsMemory.Mb(PrivBefore)).Append(" → ").Append(VsMemory.Mb(PrivAfter))
              .Append("（").Append(VsMemory.Delta(PrivAfter - PrivBefore)).Append("）");
            sb.Append("；修剪 ").Append(Trimmed).Append(" 个进程 / trimmed ").Append(Trimmed);
            if (Failed > 0) sb.Append("，失败 ").Append(Failed).Append(" / failed ").Append(Failed);
            if (Notes.Count > 0) sb.Append("；").Append(string.Join("；", Notes));
            return sb.ToString();
        }
    }

    /// <summary>
    /// VS 内存测量与温和清理。只读取进程内存计数并修剪工作集（EmptyWorkingSet），从不结束任何进程、不注入代码。
    /// 修剪工作集只是把不常用的内存页移出物理内存（需要时由系统自动换回），不会丢失数据或导致崩溃；私有字节（提交内存）通常不会因此下降。
    /// Memory measurement and gentle cleanup for VS. It only reads process memory counters and trims working sets
    /// (EmptyWorkingSet); it never terminates or injects into any process. Trimming only moves rarely used pages out of RAM
    /// (paged back in on demand), so no data is lost and nothing crashes; private (committed) bytes usually stay the same.
    /// </summary>
    public static class VsMemory
    {
        public const int DefaultThresholdMB = 6144;
        public const int MinThresholdMB = 512;
        public const int MaxThresholdMB = 1024 * 1024;

        public static int ClampThreshold(int mb) => mb <= 0 ? DefaultThresholdMB : Math.Min(Math.Max(MinThresholdMB, mb), MaxThresholdMB);

        /// <summary>
        /// 尝试触发 VS 内部 GC 的命令名（仅当该 VS 中存在且可用时才执行，否则跳过）。
        /// Command names that may force a GC inside VS (executed only if present and available; skipped otherwise).
        /// </summary>
        public static readonly string[] GcCommands = { "Tools.ForceGC", "Tools.ForceGarbageCollection" };

        public static string LogPath => AppLog.PathOf("memory.log");

        // ---- 分类规则 / Classification rules ----
        private static readonly Regex SharedNames = new Regex(
            @"^(vbcscompiler|msbuild|msbuildtaskhost|perfwatson2|vshub|servicehub\..+|microsoft\.servicehub\..+|microsoft\.codeanalysis\..+|standardcollector\.service)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DebuggerNames = new Regex(
            @"^(msvsmon|vsdebugconsole|vsdbg|vsdbg-ui|intellitrace|vsdebugeng.*|vsjitdebugger|vsdiagnostics|vshost.*|.*\.vshost)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AgentNames = new Regex(
            @"^(powershell|pwsh|cmd|conhost|openconsole|bash|sh|git|wsl|copilot|node|python|python3|py|dotnet|npm|npx)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>对 VS 子进程（或共享组件）分类。/ Classifies a VS child process (or shared component).</summary>
        public static void Classify(MemProc p)
        {
            string n = p.Name ?? "";
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            string l = n.ToLowerInvariant();
            bool trim = true;
            if (l == "devenv") { p.Role = "VS 主进程 / VS main process"; p.Reason = "可修剪工作集，并尝试触发 VS 内部 GC / trim working set and try an in-VS GC"; }
            else if (l == "msedgewebview2") { p.Role = "WebView2（VS 内网页界面）/ WebView2 (web UI in VS)"; p.Reason = "可修剪工作集 / working set can be trimmed"; }
            else if (l.StartsWith("servicehub.") || l.StartsWith("microsoft.servicehub.")) { p.Role = "ServiceHub 服务 / ServiceHub service"; p.Reason = "可修剪工作集 / working set can be trimmed"; }
            else if (l == "vbcscompiler" || l.StartsWith("microsoft.codeanalysis.")) { p.Role = "Roslyn 编译器 / 代码分析 / Roslyn compiler / analysis"; p.Reason = "可修剪工作集（不结束进程，缓存保留）/ trim only, the process and its caches stay"; }
            else if (l == "msbuild" || l == "msbuildtaskhost") { p.Role = "MSBuild 构建节点 / MSBuild node"; p.Reason = "可修剪工作集（不结束节点）/ trim only, the node keeps running"; }
            else if (l == "devhub") { p.Role = "DevHub 服务 / DevHub service"; p.Reason = "可修剪工作集 / working set can be trimmed"; }
            else if (l == "perfwatson2") { p.Role = "PerfWatson 性能监视 / PerfWatson monitor"; p.Reason = "可修剪工作集 / working set can be trimmed"; }
            else if (l == "copilot-language-server") { p.Role = "Copilot 语言服务 / Copilot language server"; p.Reason = "可修剪工作集 / working set can be trimmed"; }
            else if (l.StartsWith("microsoft.visualstudio.") || l == "vshub" || l == "standardcollector.service") { p.Role = "VS 辅助进程 / VS helper process"; p.Reason = "可修剪工作集 / working set can be trimmed"; }
            else if (DebuggerNames.IsMatch(l)) { trim = false; p.Role = "调试器组件 / Debugger component"; p.Reason = "不建议：调试期间修剪会拖慢单步与断点 / not advised: slows stepping and breakpoints"; }
            else if (l.StartsWith("testhost")) { trim = false; p.Role = "测试宿主 / Test host"; p.Reason = "不建议：可能正在运行测试 / not advised: tests may be running"; }
            else if (AgentNames.IsMatch(l)) { trim = false; p.Role = "终端 / Copilot 代理命令 / Terminal / Copilot agent"; p.Reason = "不建议：可能正在执行命令或 Copilot 代理任务 / not advised: may be running a command or an agent task"; }
            else { trim = false; p.Role = "其他子进程 / Other child"; p.Reason = "不建议：可能是正在调试的程序或第三方工具 / not advised: may be the program under debugging or a third-party tool"; }
            p.Trimmable = trim;
        }

        // ---- 测量 / Measurement ----

        private sealed class Entry
        {
            public int Pid, Ppid;
            public string Exe;
            public bool Queried, Ok, Denied;
            public long Created, Ws, Priv;
        }

        /// <summary>
        /// 测量 VSManager、各 VS 实例（含全部子孙进程）以及无归属的共享 VS 组件。可在后台线程调用。
        /// Measures VSManager, every VS instance (with all descendants) and orphaned shared VS components. Thread-safe.
        /// </summary>
        public static MemSnapshot Take(IList<VsRef> vsRefs)
        {
            var snap = new MemSnapshot { At = DateTime.Now };
            ReadPhysical(snap);
            var all = ListProcesses();
            var byPid = new Dictionary<int, Entry>();
            foreach (var e in all) byPid[e.Pid] = e;
            var kids = new Dictionary<int, List<Entry>>();
            foreach (var e in all)
            {
                if (e.Ppid == e.Pid) continue;
                if (!kids.TryGetValue(e.Ppid, out var l)) kids[e.Ppid] = l = new List<Entry>();
                l.Add(e);
            }
            var used = new HashSet<int>();
            int self = Process.GetCurrentProcess().Id;

            // VSManager 本体（即使它是被 VS 调试启动的子进程，也单独统计）/ VSManager itself, counted separately even when launched by a debugging VS
            var selfGroup = new MemGroup { Kind = MemGroupKind.Self, RootPid = self, Name = "VSManager" };
            if (byPid.TryGetValue(self, out var selfEntry))
                foreach (var e in Tree(selfEntry, kids, used, null))
                {
                    var p = ToProc(e, e.Pid == self);
                    p.Trimmable = true;
                    p.Role = e.Pid == self ? "VSManager 本体 / VSManager itself" : ExeBase(e.Exe).Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) ? "VSManager WebView2" : "VSManager 子进程 / VSManager child";
                    p.Reason = "随「清理 VSManager」回收 / cleaned by \"Clean VSManager\"";
                    selfGroup.Procs.Add(p);
                }
            snap.Groups.Add(selfGroup);

            // 各 VS 实例 / Each VS instance
            var devenvs = all.Where(e => ExeBase(e.Exe).Equals("devenv", StringComparison.OrdinalIgnoreCase)).ToList();
            var devenvPids = new HashSet<int>(devenvs.Select(e => e.Pid));
            var vsGroups = new List<MemGroup>();
            foreach (var d in devenvs)
            {
                if (used.Contains(d.Pid)) continue;
                var r = vsRefs?.FirstOrDefault(x => x.Vs != null && x.Vs.Pid == d.Pid);
                var g = new MemGroup
                {
                    Kind = MemGroupKind.Vs, RootPid = d.Pid, Number = r?.Number ?? 0,
                    Name = r != null && !string.IsNullOrEmpty(r.Name) ? r.Name : "VS（未在列表中）/ VS (not listed)"
                };
                // 嵌套的 devenv（如实验实例）作为独立分组 / Nested devenv (e.g. experimental instance) gets its own group
                foreach (var e in Tree(d, kids, used, x => x.Pid != d.Pid && devenvPids.Contains(x.Pid)))
                {
                    var p = ToProc(e, e.Pid == d.Pid);
                    Classify(p);
                    Deny(p);
                    g.Procs.Add(p);
                }
                vsGroups.Add(g);
            }
            snap.Groups.AddRange(vsGroups.OrderBy(g => g.Number == 0 ? int.MaxValue : g.Number).ThenBy(g => g.RootPid));

            // 无归属的共享组件（如编译服务器）/ Orphaned shared components (e.g. the compiler server)
            var shared = new MemGroup { Kind = MemGroupKind.Shared, Name = "共享组件 / Shared" };
            foreach (var e in all)
            {
                if (used.Contains(e.Pid) || !SharedNames.IsMatch(ExeBase(e.Exe))) continue;
                used.Add(e.Pid);
                Query(e);
                var p = ToProc(e, false);
                Classify(p);
                Deny(p);
                shared.Procs.Add(p);
            }
            if (shared.Procs.Count > 0) snap.Groups.Add(shared);

            foreach (var g in snap.Groups)
            {
                var root = g.Procs.FirstOrDefault(p => p.Root);
                var rest = g.Procs.Where(p => !p.Root).OrderByDescending(p => p.WorkingSet).ToList();
                g.Procs.Clear();
                if (root != null) g.Procs.Add(root);
                g.Procs.AddRange(rest);
            }
            return snap;
        }

        private static void Deny(MemProc p)
        {
            if (!p.AccessDenied) return;
            p.Trimmable = false;
            p.Reason = "无权限访问（可能以管理员身份运行）/ access denied (possibly elevated)";
        }

        /// <summary>广度优先列出子孙进程；子进程创建时间早于父进程时视为 PID 复用并跳过。/ Lists descendants; skips PID reuse.</summary>
        private static List<Entry> Tree(Entry root, Dictionary<int, List<Entry>> kids, HashSet<int> used, Func<Entry, bool> stop)
        {
            var result = new List<Entry>();
            Query(root);
            used.Add(root.Pid);
            result.Add(root);
            var queue = new Queue<Entry>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                if (!kids.TryGetValue(parent.Pid, out var list)) continue;
                foreach (var c in list)
                {
                    if (used.Contains(c.Pid)) continue;
                    if (stop != null && stop(c)) continue;
                    Query(c);
                    if (c.Created != 0 && parent.Created != 0 && c.Created < parent.Created) continue;
                    used.Add(c.Pid);
                    result.Add(c);
                    queue.Enqueue(c);
                }
            }
            return result;
        }

        private static MemProc ToProc(Entry e, bool root) => new MemProc
        {
            Pid = e.Pid, ParentPid = e.Ppid, Name = e.Exe, WorkingSet = e.Ws, Private = e.Priv, Root = root, AccessDenied = e.Denied
        };

        private static string ExeBase(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return "";
            return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
        }

        private static void Query(Entry e)
        {
            if (e.Queried) return;
            e.Queried = true;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, e.Pid);
            if (h == IntPtr.Zero) { e.Denied = true; return; }
            try
            {
                if (GetProcessTimes(h, out long created, out _, out _, out _)) e.Created = created;
                var c = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX)) };
                if (GetProcessMemoryInfo(h, ref c, c.cb))
                {
                    e.Ws = (long)c.WorkingSetSize.ToUInt64();
                    e.Priv = (long)c.PrivateUsage.ToUInt64();
                    e.Ok = true;
                }
                else e.Denied = true;
            }
            finally { CloseHandle(h); }
        }

        private static List<Entry> ListProcesses()
        {
            var list = new List<Entry>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32FirstW(snap, ref pe)) return list;
                do
                {
                    if (pe.th32ProcessID != 0)
                        list.Add(new Entry { Pid = (int)pe.th32ProcessID, Ppid = (int)pe.th32ParentProcessID, Exe = pe.szExeFile ?? "" });
                } while (Process32NextW(snap, ref pe));
            }
            finally { CloseHandle(snap); }
            return list;
        }

        private static void ReadPhysical(MemSnapshot s)
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref m)) { s.PhysTotal = (long)m.ullTotalPhys; s.PhysAvail = (long)m.ullAvailPhys; }
        }

        // ---- 清理 / Cleanup ----

        /// <summary>修剪单个进程的工作集；返回 null 表示成功，否则为失败原因。/ Trims one process; returns null on success, else the reason.</summary>
        public static string TrimProcess(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero) return new Win32Exception(Marshal.GetLastWin32Error()).Message;
            try { return EmptyWorkingSet(h) ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message; }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// 温和清理指定分组：VS 分组会先尝试执行 VS 自带的 GC 命令（存在时），再修剪全部「可安全清理」进程的工作集；VSManager 分组做完整 GC 后修剪。
        /// 不结束任何进程。可在后台线程调用；refs 仅用于重新测量时的命名。
        /// Gently cleans a group: for VS it first runs VS's own GC command (if present), then trims every "safe" process; for
        /// VSManager it runs a full GC and trims. Never terminates anything. Thread-safe; refs are only used for naming.
        /// </summary>
        public static async Task<MemCleanResult> CleanAsync(string groupKey, IList<VsRef> refs)
        {
            var before = await Task.Run(() => Take(refs)).ConfigureAwait(false);
            var g = before.Find(groupKey);
            var r = new MemCleanResult();
            if (g == null) { r.Target = groupKey; r.Notes.Add("目标进程已不存在 / target no longer exists"); return r; }
            r.Target = g.Kind == MemGroupKind.Self ? "VSManager" : g.Owner;
            r.WsBefore = g.WorkingSet;
            r.PrivBefore = g.Private;

            if (g.Kind == MemGroupKind.Self)
            {
                await Task.Run(() => MemoryTrim.CollectNow()).ConfigureAwait(false);
                r.Notes.Add("已完整 GC / full GC done");
            }
            else if (g.Kind == MemGroupKind.Vs)
            {
                var vs = refs?.FirstOrDefault(x => x.Vs != null && x.Vs.Pid == g.RootPid)?.Vs;
                r.Notes.Add(await TryVsGcAsync(vs).ConfigureAwait(false));
            }

            await Task.Run(() =>
            {
                foreach (var p in g.Procs.Where(x => x.Trimmable))
                {
                    string err = TrimProcess(p.Pid);
                    if (err == null) r.Trimmed++;
                    else { r.Failed++; if (r.Notes.Count < 6) r.Notes.Add(p.Name + " " + p.Pid + "：" + err); }
                }
            }).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);
            var after = await Task.Run(() => Take(refs)).ConfigureAwait(false);
            var g2 = after.Find(groupKey);
            if (g2 != null) { r.WsAfter = g2.WorkingSet; r.PrivAfter = g2.Private; }
            Log(r.Summary());
            return r;
        }

        /// <summary>在 DTE 工作线程上尝试 VS 自带的 GC 命令（5 秒超时，失败只记录）。/ Tries VS's own GC command on the DTE worker (5 s timeout).</summary>
        private static async Task<string> TryVsGcAsync(VsInstance vs)
        {
            if (vs?.Dte == null) return "VS 自动化接口不可用，仅修剪工作集 / DTE unavailable, trim only";
            var t = DteWorker.Run(() =>
            {
                dynamic d = vs.Dte;
                foreach (var name in GcCommands)
                {
                    try
                    {
                        dynamic c = d.Commands.Item(name, -1);
                        if (c == null || !(bool)c.IsAvailable) continue;
                        d.ExecuteCommand(name, "");
                        return name;
                    }
                    catch { }
                }
                return null;
            });
            var done = await Task.WhenAny(t, Task.Delay(5000)).ConfigureAwait(false);
            if (done != t) return "VS 忙碌，已跳过内部 GC / VS busy, in-VS GC skipped";
            if (t.IsFaulted || t.Result == null) return "VS 未提供可调用的 GC 命令，仅修剪工作集 / no GC command in VS, trim only";
            return "已执行 VS 命令 / ran VS command " + t.Result;
        }

        // ---- 格式化与日志 / Formatting and log ----

        public static string Mb(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb >= 1024 ? (mb / 1024).ToString("0.00") + " GB" : mb.ToString("0") + " MB";
        }

        public static string Delta(long bytes) => Math.Abs(bytes) < 524288 ? "±0 MB" : (bytes > 0 ? "+" : "-") + Mb(Math.Abs(bytes));

        public static string Percent(long part, long total) => total > 0 ? (part * 100.0 / total).ToString("0.0") + "%" : "-";

        /// <summary>追加到 %APPDATA%\VSManager\logs\memory.log；失败静默。/ Appends to memory.log; failures are ignored.</summary>
        public static void Log(string text) => AppLog.Write("memory.log", text);

        // ---- Win32 ----
        private const uint TH32CS_SNAPPROCESS = 0x2;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int PROCESS_SET_QUOTA = 0x0100;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetProcessMemoryInfo(IntPtr h, ref PROCESS_MEMORY_COUNTERS_EX c, uint cb);
        [DllImport("psapi.dll", SetLastError = true)] private static extern bool EmptyWorkingSet(IntPtr h);
    }

    /// <summary>
    /// 超阈值自动策略：每分钟检查一次（仅在开启时），同一 VS 30 分钟内最多处理一次。
    /// VS 正在调试 / 生成 / Copilot 运行中时不自动清理，只提示。
    /// Threshold policy: checks once a minute (only when enabled); each VS is handled at most once per 30 minutes.
    /// While a VS is debugging / building / running Copilot it is only notified, never auto-cleaned.
    /// </summary>
    public sealed class MemoryGuard
    {
        private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);
        private readonly Dictionary<int, DateTime> _handled = new Dictionary<int, DateTime>();
        private DateTime _next = DateTime.Now.AddMinutes(1);
        private int _busy;

        /// <summary>需要提示用户时触发（界面线程外）。参数：提示文本。/ Raised (off the UI thread) with a message to show.</summary>
        public event Action<string> Notify;

        /// <summary>界面线程每秒调用；refs 为当前 VS 列表的快照。/ Called every second from the UI thread.</summary>
        public void Tick(AppSettings s, Func<IList<VsRef>> refs)
        {
            if (!s.VsMemoryAutoEnabled || DateTime.Now < _next) return;
            _next = DateTime.Now + CheckEvery;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            var list = refs();
            long limit = (long)VsMemory.ClampThreshold(s.VsMemoryThresholdMB) * 1048576;
            bool autoClean = s.VsMemoryAutoClean;
            Task.Run(async () =>
            {
                try
                {
                    var snap = VsMemory.Take(list);
                    foreach (var g in snap.Groups.Where(x => x.Kind == MemGroupKind.Vs && x.WorkingSet > limit))
                    {
                        if (_handled.TryGetValue(g.RootPid, out var at) && DateTime.Now - at < Cooldown) continue;
                        _handled[g.RootPid] = DateTime.Now;
                        var vs = list.FirstOrDefault(x => x.Vs != null && x.Vs.Pid == g.RootPid)?.Vs;
                        bool working = vs != null && (vs.DebugMode == 2 || vs.DebugMode == 3 || vs.Building || vs.Copilot == CopilotState.Busy);
                        string head = g.Owner + " 内存 " + VsMemory.Mb(g.WorkingSet) + " 超过阈值 " + VsMemory.Mb(limit) + " / memory over threshold";
                        if (autoClean && !working)
                        {
                            var r = await VsMemory.CleanAsync(g.Key, list).ConfigureAwait(false);
                            Notify?.Invoke("自动温和清理 / Auto gentle clean · " + r.Summary());
                        }
                        else
                        {
                            string why = autoClean ? "（VS 正在调试 / 生成 / Copilot 运行中，未自动清理 / busy, not auto-cleaned）" : "";
                            VsMemory.Log("提示 / notice: " + head + why);
                            Notify?.Invoke(head + why + "，可在「内存」面板温和清理 / open the Memory panel to clean");
                        }
                    }
                    foreach (var pid in _handled.Keys.Where(p => !snap.Groups.Any(x => x.RootPid == p)).ToList()) _handled.Remove(pid);
                }
                catch (Exception ex) { VsMemory.Log("自动检查失败 / auto check failed: " + ex.Message); }
                finally { Volatile.Write(ref _busy, 0); }
            });
        }
    }
}
