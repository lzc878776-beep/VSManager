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
        /// <summary>"project"（项目调试属性 / project debug properties）或 / or "launchSettings"。</summary>
        public string Source { get; internal set; }
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

            string host = null, args = "", source = null;
            var json = ReadLaunchProfile(vs, projectPath);
            if (json != null)
            {
                host = CadDebugPlan.DetectHost(json.Program);
                if (host != null) { args = json.Args; source = "launchSettings"; }
            }
            if (host == null && cfg != null)
            {
                dynamic c = cfg;
                if (Int(() => c.Item("StartAction").Value, 0) == 1)
                    host = CadDebugPlan.DetectHost(Str(() => c.Item("StartProgram").Value));
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
            WriteScript(script, CadDebugPlan.BuildScript(new[] { dll }));
            string injected = CadDebugPlan.Inject(args, script);
            var session = new CadDebugSession { Host = host, Dll = dll, Script = script, Source = source, Pid = vs.Pid };

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
                        profile["commandLineArgs"] = CadDebugPlan.StripInjected(Convert.ToString(a));
                        File.WriteAllText(file, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
                    }
                };
                // 项目系统异步重新读取 launchSettings.json。/ The project system reloads launchSettings.json asynchronously.
                Thread.Sleep(1500);
            }
            else
            {
                dynamic c = cfg;
                string restoreTo = CadDebugPlan.StripInjected(args);
                c.Item("StartArguments").Value = injected;
                session.RestoreAction = () =>
                {
                    string now = Str(() => c.Item("StartArguments").Value);
                    c.Item("StartArguments").Value = now == injected ? restoreTo : CadDebugPlan.StripInjected(now);
                };
            }
            lock (Active) Active.Add(session);
            note = Describe(session);
            return session;
        }

        private static string Describe(CadDebugSession s) =>
            "检测到 " + s.Host + " 调试环境，启动后自动 NETLOAD " + Path.GetFileName(s.Dll)
            + " / " + s.Host + " debugging detected; " + Path.GetFileName(s.Dll) + " will be NETLOADed after start";

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
