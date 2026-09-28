using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VSManager
{
    /// <summary>
    /// 一次 CAD 调试启动的临时参数注入；Restore 幂等，恢复启动参数原值。
    /// Temporary argument injection for one CAD debug launch; Restore is idempotent and puts the start arguments back.
    /// </summary>
    public sealed class CadDebugSession
    {
        public string Host { get; internal set; }
        public string Dll { get; internal set; }
        public string Script { get; internal set; }
        /// <summary>本次打开的调试图纸；null 表示按默认新建图纸。/ Debug drawing opened this time; null means the default new drawing.</summary>
        public string Drawing { get; internal set; }
        /// <summary>已记录但找不到的图纸路径（改为新建图纸）。/ Recorded drawing path that could not be found (a new drawing is used instead).</summary>
        public string MissingDrawing { get; internal set; }
        /// <summary>"project"（项目调试属性 / project debug properties）或 / or "launchSettings"。</summary>
        public string Source { get; internal set; }
        /// <summary>匹配的项目适配包名称；null 表示未匹配。/ Matched project adapter name; null when none matched.</summary>
        public string Adapter { get; internal set; }
        /// <summary>已加入启动脚本的 AI 动作代理引导 DLL。/ AI action agent boot DLL added to the startup script.</summary>
        public string AgentBoot { get; internal set; }
        /// <summary>适配包或代理的补充说明。/ Extra remark about the adapter or agent.</summary>
        public string AgentNote { get; internal set; }
        /// <summary>启动前由 0 改为 1 的 FILEDIA 所在产品（AutoCAD 注册表）；空表示无需修改。/ Products whose FILEDIA was changed from 0 to 1 before launch (AutoCAD registry); empty when nothing changed.</summary>
        public IReadOnlyList<string> FileDiaFixed { get; internal set; } = new string[0];
        internal string ExtraArgs;
        internal int Pid;
        internal Action RestoreAction;
        private int _restored;
        public bool Restored => Volatile.Read(ref _restored) != 0;

        /// <summary>恢复启动参数；只执行一次，必须在 DteWorker 线程调用。/ Restores the start arguments once; call on the DteWorker thread.</summary>
        public void Restore()
        {
            if (Interlocked.Exchange(ref _restored, 1) != 0) return;
            try { RestoreAction?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// 点击调试时识别 CAD 宿主（启动外部程序为 acad.exe 等），并通过 CAD 标准的 /b 启动脚本自动 NETLOAD 启动项目输出的 DLL。
    /// 参数只在本次启动期间临时注入，调试器进入运行 / 中断状态、生成失败或超时后恢复原值。
    /// Detects a CAD host when debugging starts (external start program such as acad.exe) and NETLOADs the startup project's output DLL
    /// through the CAD-standard /b startup script. Arguments are injected only for this launch and restored once the debugger runs / breaks,
    /// the build fails or a timeout passes.
    /// </summary>
    public static class VsCadDebug
    {
        /// <summary>是否启用（由设置同步）。/ Whether enabled (synced from settings).</summary>
        public static volatile bool Enabled = true;

        /// <summary>
        /// 按解决方案路径查询已记录的调试图纸（由 MainForm 接到设置）；返回 null 表示未记录。
        /// Looks up the recorded debug drawing by solution path (wired to settings by MainForm); null means none recorded.
        /// </summary>
        public static Func<string, string> DrawingLookup;

        /// <summary>
        /// 写入 CAD 代理连接文件并返回路径（由 MainForm 接到设置）；null 表示不加载 AI 动作代理。
        /// Writes the CAD agent connection file and returns its path (wired to settings by MainForm); null disables the AI action agent.
        /// </summary>
        public static Func<string> AgentConnection;

        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);
        private static readonly List<CadDebugSession> Active = new List<CadDebugSession>();

        internal static string ScriptRoot => Path.Combine(Path.GetTempPath(), "VSManager", "CadDebug");

        private static string Str(Func<object> f)
        {
            try { return Convert.ToString(f()) ?? ""; } catch { return ""; }
        }

        private static int Int(Func<object> f, int fallback)
        {
            try { return Convert.ToInt32(f()); } catch { return fallback; }
        }

        /// <summary>
        /// 在 Debug.Start 之前调用（DteWorker 线程）。不是 CAD 调试时返回 null；note 为给用户的说明（可能为 null）。
        /// Call before Debug.Start on the DteWorker thread. Returns null when this is not CAD debugging; note is a user-facing remark (may be null).
        /// </summary>
        public static CadDebugSession Prepare(VsInstance vs, out string note)
        {
            note = null;
            if (!Enabled || vs?.Dte == null) return null;
            lock (Active)
            {
                var pending = Active.FirstOrDefault(s => s.Pid == vs.Pid && !s.Restored);
                if (pending != null) { note = Describe(pending); return null; }
            }
            dynamic dte = vs.Dte;
            dynamic proj = VsService.StartupProject(dte);
            if (proj == null) return null;
            string projectPath = Str(() => proj.FullName);
            if (projectPath.Length == 0) return null;
            string name = Str(() => proj.Name);
            dynamic cfg = null;
            try { cfg = proj.ConfigurationManager.ActiveConfiguration.Properties; } catch { }

            string host = null, args = "", source = null, program = null;
            var json = ReadLaunchProfile(vs, projectPath);
            if (json != null)
            {
                host = CadDebugPlan.DetectHost(json.Program);
                if (host != null) { args = json.Args; source = "launchSettings"; program = json.Program; }
            }
            if (host == null && cfg != null)
            {
                dynamic c = cfg;
                if (Int(() => c.Item("StartAction").Value, 0) == 1)
                {
                    program = Str(() => c.Item("StartProgram").Value);
                    host = CadDebugPlan.DetectHost(program);
                }
                if (host != null) { args = Str(() => c.Item("StartArguments").Value); source = "project"; }
            }
            if (host == null) return null;

            string dll = OutputDll(proj, cfg, projectPath);
            if (dll == null)
            {
                note = "检测到 " + host + " 调试环境，但启动项目输出不是 DLL，未自动加载 / " + host + " debugging detected, but the startup project does not build a DLL; nothing auto-loaded";
                return null;
            }
            if (CadDebugPlan.HasUserScript(args))
            {
                note = "检测到 " + host + " 调试环境；启动参数已含 /b 脚本，保持不变 / " + host + " debugging detected; start arguments already contain a /b script, left unchanged";
                return null;
            }

            Directory.CreateDirectory(ScriptRoot);
            string script = Path.Combine(ScriptRoot, CadDebugPlan.ScriptFileName(name));
            var agent = PrepareAgent(vs, projectPath, host, program, dll);
            // FILEDIA=0 时无法自动加载 DLL：启动前改注册表，脚本首行再兜底改回 1。/ FILEDIA=0 breaks auto-loading: fix the registry before launch, and the script's first line sets it back to 1 as a fallback.
            var fileDia = CadFileDia.EnsureEnabled(host, program);
            WriteScript(script, CadDebugPlan.FileDiaGuard + CadDebugPlan.BuildScript(agent.Dlls, agent.Commands));
            string drawing = null, missing = null;
            if (!CadDebugPlan.HasUserDrawing(CadDebugPlan.StripInjected(args, agent.ExtraArgs)))
            {
                string wanted = null;
                try { wanted = string.IsNullOrEmpty(vs.SolutionPath) ? null : DrawingLookup?.Invoke(vs.SolutionPath); } catch { }
                if (string.IsNullOrWhiteSpace(wanted)) wanted = agent.Adapter?.Launch?.Drawing;
                if (!string.IsNullOrWhiteSpace(wanted))
                {
                    string full = null;
                    try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(wanted.Trim())); } catch { }
                    if (full != null && CadDebugPlan.IsDrawingPath(full) && File.Exists(full)) drawing = full;
                    else missing = wanted.Trim();
                }
            }
            string injected = CadDebugPlan.Inject(CadDebugPlan.StripInjected(args, agent.ExtraArgs), script, drawing, agent.ExtraArgs);
            var session = new CadDebugSession
            {
                Host = host, Dll = dll, Script = script, Drawing = drawing, MissingDrawing = missing, Source = source, Pid = vs.Pid,
                Adapter = agent.Adapter?.Name, AgentBoot = agent.Boot, AgentNote = agent.Note, ExtraArgs = agent.ExtraArgs,
                FileDiaFixed = fileDia,
            };
            string extra = agent.ExtraArgs;

            if (source == "launchSettings")
            {
                json.Profile["commandLineArgs"] = injected;
                string written = new JavaScriptSerializer().Serialize(json.Root);
                byte[] original = File.ReadAllBytes(json.File);
                File.WriteAllText(json.File, written, new UTF8Encoding(false));
                string file = json.File, profileName = json.ProfileName;
                session.RestoreAction = () =>
                {
                    string now = File.ReadAllText(file);
                    if (now == written) { File.WriteAllBytes(file, original); return; }
                    // 期间文件被改过：只去掉本工具的参数，保留其他改动。/ File changed meanwhile: strip only our argument and keep other edits.
                    var root = new JavaScriptSerializer().DeserializeObject(now) as Dictionary<string, object>;
                    if (root != null && root.TryGetValue("profiles", out var p) && p is Dictionary<string, object> ps
                        && ps.TryGetValue(profileName, out var pr) && pr is Dictionary<string, object> profile
                        && profile.TryGetValue("commandLineArgs", out var a))
                    {
                        profile["commandLineArgs"] = CadDebugPlan.StripInjected(Convert.ToString(a), extra);
                        File.WriteAllText(file, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
                    }
                };
                // 项目系统异步重新读取 launchSettings.json。/ The project system reloads launchSettings.json asynchronously.
                Thread.Sleep(1500);
            }
            else
            {
                dynamic c = cfg;
                string restoreTo = CadDebugPlan.StripInjected(args, extra);
                c.Item("StartArguments").Value = injected;
                session.RestoreAction = () =>
                {
                    string now = Str(() => c.Item("StartArguments").Value);
                    c.Item("StartArguments").Value = now == injected ? restoreTo : CadDebugPlan.StripInjected(now, extra);
                };
            }
            lock (Active) Active.Add(session);
            note = Describe(session);
            return session;
        }

        private static string SafeName(string path)
        {
            try { return Path.GetFileName(path); } catch (ArgumentException) { return path; }
        }

        private static string Describe(CadDebugSession s)
        {
            string dll = Path.GetFileName(s.Dll);
            string zh = s.Drawing != null ? "，打开图纸 " + Path.GetFileName(s.Drawing)
                : s.MissingDrawing != null ? "；未找到调试图纸 " + SafeName(s.MissingDrawing) + "，改为打开新图" : "";
            string en = s.Drawing != null ? " and " + Path.GetFileName(s.Drawing) + " will be opened"
                : s.MissingDrawing != null ? "; debug drawing " + SafeName(s.MissingDrawing) + " not found, a new drawing is used" : "";
            return "检测到 " + s.Host + " 调试环境，启动后自动 NETLOAD " + dll + zh
                + (s.AgentBoot != null ? "；已按适配包「" + s.Adapter + "」加载 AI 动作代理" : "")
                + (s.FileDiaFixed.Count > 0 ? "；已把系统变量 FILEDIA 从 0 改为 1" : "")
                + " / " + s.Host + " debugging detected; " + dll + " will be NETLOADed after start" + en
                + (s.AgentBoot != null ? "; the AI action agent is loaded for adapter " + s.Adapter : "")
                + (s.FileDiaFixed.Count > 0 ? "; system variable FILEDIA was changed from 0 to 1" : "")
                + (s.AgentNote != null ? "（" + s.AgentNote + "）" : "");
        }

        private sealed class AgentPlan
        {
            public List<string> Dlls = new List<string>();
            public List<string> Commands = new List<string>();
            public CadAgent.CadAdapter Adapter;
            public string Boot, Note, ExtraArgs;
        }

        /// <summary>
        /// 按项目适配包准备附加 DLL、启动命令与 AI 动作代理引导 DLL；没有匹配的适配包时只加载项目 DLL（原行为）。
        /// Prepares extra DLLs, startup commands and the AI action agent boot DLL from the project adapter; without a matching adapter only the project DLL loads (previous behavior).
        /// </summary>
        private static AgentPlan PrepareAgent(VsInstance vs, string projectPath, string host, string program, string dll)
        {
            var plan = new AgentPlan();
            try { plan.Adapter = CadAgent.CadAdapterStore.Match(new[] { vs.SolutionPath, projectPath }); }
            catch (Exception ex) { plan.Note = "适配包读取失败 / Adapter load failed: " + ex.Message; }
            var a = plan.Adapter;
            var launch = a?.Launch;
            foreach (var raw in launch?.ExtraDlls ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string p;
                try { p = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'))); } catch { p = null; }
                if (p != null && File.Exists(p) && p.IndexOf('"') < 0) plan.Dlls.Add(p);
                else plan.Note = "附加 DLL 不存在 / Extra DLL missing: " + SafeName(raw);
            }
            plan.Dlls.Add(dll);
            if (a == null) return plan;
            string conn = null;
            try { conn = AgentConnection?.Invoke(); } catch (Exception ex) { plan.Note = "代理连接文件写入失败 / Agent connection file failed: " + ex.Message; }
            if (conn != null)
            {
                string cadExe = string.IsNullOrWhiteSpace(launch?.CadPath) ? program : launch.CadPath;
                var boot = CadAgent.CadBootCompiler.Compile(new CadAgent.CadBootOptions
                {
                    CadExe = cadExe, Host = host, Adapter = a, ConnectionFile = conn, Solution = vs.SolutionPath,
                });
                if (boot.Ok) { plan.Boot = boot.BootDll; plan.Dlls.Add(boot.BootDll); }
                else plan.Note = "AI 动作代理未加载 / AI action agent not loaded: " + boot.Error;
                AppLog.Write("cad.log", "引导 DLL / Boot DLL adapter=" + a.Name + " ok=" + boot.Ok + (boot.Cached ? " cached" : "") + (boot.Error == null ? "" : " error=" + boot.Error));
            }
            foreach (var cmd in launch?.StartupCommands ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(cmd)) plan.Commands.Add(cmd.Trim());
            plan.ExtraArgs = string.IsNullOrWhiteSpace(launch?.Args) ? null : launch.Args.Trim();
            return plan;
        }

        private static void WriteScript(string path, string content)
        {
            // 旧版 CAD 按系统代码页读取脚本；无法用代码页表示时退回带 BOM 的 UTF-8。
            // Older CAD versions read scripts in the system code page; fall back to UTF-8 with BOM when the code page cannot represent it.
            var ansi = Encoding.Default;
            bool fits = ansi.GetString(ansi.GetBytes(content)) == content;
            File.WriteAllText(path, content, fits ? ansi : new UTF8Encoding(true));
        }

        private static string OutputDll(dynamic proj, dynamic cfg, string projectPath)
        {
            string outName = Str(() => proj.Properties.Item("OutputFileName").Value);
            if (outName.Length == 0 && Int(() => proj.Properties.Item("OutputType").Value, -1) == 2)
            {
                string asm = Str(() => proj.Properties.Item("AssemblyName").Value);
                if (asm.Length > 0) outName = asm + ".dll";
            }
            if (!outName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return null;
            string outPath = cfg == null ? "" : Str(() => cfg.Item("OutputPath").Value);
            try { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath), outPath, outName)); }
            catch { return null; }
        }

        private sealed class LaunchProfile
        {
            public string File, ProfileName, Program, Args;
            public Dictionary<string, object> Root, Profile;
        }

        private static LaunchProfile ReadLaunchProfile(VsInstance vs, string projectPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(projectPath);
                string file = new[] { @"Properties\launchSettings.json", @"My Project\launchSettings.json" }
                    .Select(r => Path.Combine(dir, r)).FirstOrDefault(System.IO.File.Exists);
                if (file == null) return null;
                var root = new JavaScriptSerializer().DeserializeObject(System.IO.File.ReadAllText(file)) as Dictionary<string, object>;
                if (root == null || !root.TryGetValue("profiles", out var p) || !(p is Dictionary<string, object> profiles)) return null;
                string active = VsService.GetLaunchProfiles(vs).Active;
                if (string.IsNullOrEmpty(active) || !profiles.ContainsKey(active)) active = profiles.Keys.FirstOrDefault();
                if (active == null || !(profiles[active] is Dictionary<string, object> profile)) return null;
                string Get(string k) => profile.TryGetValue(k, out var v) ? Convert.ToString(v) ?? "" : "";
                if (!string.Equals(Get("commandName"), "Executable", StringComparison.OrdinalIgnoreCase)) return null;
                return new LaunchProfile { File = file, ProfileName = active, Program = Get("executablePath"), Args = Get("commandLineArgs"), Root = root, Profile = profile };
            }
            catch { return null; }
        }

        /// <summary>
        /// 监视本次启动：调试器进入运行 / 中断、生成结束后仍未启动、VS 断开或超时即恢复参数。
        /// Watches this launch: restores the arguments once the debugger runs / breaks, the build ends without a launch, VS disconnects or time runs out.
        /// </summary>
        public static void Watch(VsInstance vs, CadDebugSession session)
        {
            if (session == null) return;
            var started = DateTime.UtcNow;
            DateTime? idleSince = null;
            bool sawBuild = false;
            int busy = 0;
            System.Threading.Timer timer = null;
            timer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.Exchange(ref busy, 1) != 0) return;
                DteWorker.Run(() =>
                {
                    try
                    {
                        if (session.Restored) { timer?.Dispose(); return; }
                        int mode; bool building;
                        try
                        {
                            dynamic d = vs.Dte;
                            mode = Convert.ToInt32(d.Debugger.CurrentMode);
                            building = Convert.ToInt32(d.Solution.SolutionBuild.BuildState) == 2;
                        }
                        catch { mode = -1; building = false; }
                        var now = DateTime.UtcNow;
                        bool done = mode != 1 || now - started > MaxWait;
                        if (!done)
                        {
                            if (building) { sawBuild = true; idleSince = null; }
                            else
                            {
                                if (idleSince == null) idleSince = now;
                                done = now - idleSince.Value > TimeSpan.FromSeconds(sawBuild ? 10 : 30);
                            }
                        }
                        if (done) { Finish(session); timer?.Dispose(); }
                    }
                    finally { Volatile.Write(ref busy, 0); }
                });
            }, null, 700, 700);
        }

        /// <summary>恢复单个会话并移出列表（DteWorker 线程）。/ Restores one session and removes it from the list (DteWorker thread).</summary>
        public static void Finish(CadDebugSession session)
        {
            if (session == null) return;
            session.Restore();
            lock (Active) Active.Remove(session);
        }

        /// <summary>退出前恢复所有未恢复的参数。/ Restores every pending argument before exit.</summary>
        public static void RestoreAll(int timeoutMs)
        {
            CadDebugSession[] pending;
            lock (Active) pending = Active.ToArray();
            if (pending.Length == 0) return;
            try { DteWorker.Run(() => { foreach (var s in pending) Finish(s); }).Wait(timeoutMs); } catch { }
        }
    }
}
