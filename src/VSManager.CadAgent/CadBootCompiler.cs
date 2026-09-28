using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager.CadAgent
{
    /// <summary>引导 DLL 编译输入。/ Boot DLL compile input.</summary>
    public sealed class CadBootOptions
    {
        /// <summary>CAD 可执行文件完整路径（用于定位托管 DLL）。/ Full path of the CAD executable (used to locate managed DLLs).</summary>
        public string CadExe;
        public string Host;
        public CadAdapter Adapter;
        public string ConnectionFile;
        public string Solution;
        /// <summary>VSManager.CadAgent.dll 路径；缺省为当前程序集。/ Path of VSManager.CadAgent.dll; defaults to this assembly.</summary>
        public string AgentAssembly;
        /// <summary>缓存根目录；缺省 %TEMP%\VSManager\CadAgent。/ Cache root; defaults to %TEMP%\VSManager\CadAgent.</summary>
        public string CacheRoot;
    }

    public sealed class CadBootResult
    {
        public bool Ok;
        public string BootDll;
        public string Error;
        public bool Cached;
    }

    /// <summary>
    /// 为具体 CAD 版本即时编译引导 DLL（C# 5，引用 CAD 安装目录下的托管 DLL）；引导 DLL 在 CAD 中启动 CadAgentHost。
    /// Compiles a boot DLL for the specific CAD version on the fly (C# 5, referencing managed DLLs in the CAD install folder); the boot DLL starts CadAgentHost inside CAD.
    /// </summary>
    public static class CadBootCompiler
    {
        public const string BootAssemblyName = "VSManager.CadBoot";
        public const string StatusCommand = "VSM_AIAGENT";

        public static string DefaultCacheRoot => Path.Combine(Path.GetTempPath(), "VSManager", "CadAgent");

        public static CadApiConfig ResolveApi(string host, CadAdapter adapter) =>
            CadApiConfig.Merge(CadApiConfig.Preset(adapter?.Host ?? host), adapter?.Launch?.Api);

        private static readonly Regex Identifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

        private static string Lit(string s) => "@\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        /// <summary>生成引导源码（C# 5）。/ Generates the boot source (C# 5).</summary>
        public static string GenerateSource(CadBootOptions o, CadApiConfig api)
        {
            if (api == null || string.IsNullOrWhiteSpace(api.Namespace) || string.IsNullOrWhiteSpace(api.ApplicationClass))
                throw new InvalidOperationException("没有 " + (o.Host ?? "CAD") + " 的 API 预设，请在适配包 launch.api 中配置 namespace / applicationClass / references / No API preset for this CAD; configure launch.api in the adapter");
            string ns = api.Namespace.Trim();
            string db = string.IsNullOrWhiteSpace(api.DatabaseNamespace) ? ns : api.DatabaseNamespace.Trim();
            string rt = string.IsNullOrWhiteSpace(api.RuntimeNamespace) ? ns : api.RuntimeNamespace.Trim();
            string app = api.ApplicationClass.Trim();
            foreach (var id in new[] { ns, db, rt, app })
                if (!Identifier.IsMatch(id)) throw new InvalidOperationException("无效的命名空间或类名 / Invalid namespace or class name: " + id);
            string template = api.Template ?? "";
            string src = BootTemplate
                .Replace("$NS$", ns).Replace("$DB$", db).Replace("$RT$", rt).Replace("$APP$", app)
                .Replace("$CONN$", Lit(o.ConnectionFile)).Replace("$ADAPTERDIR$", Lit(o.Adapter?.Directory))
                .Replace("$HOST$", Lit(o.Host)).Replace("$SOLUTION$", Lit(o.Solution)).Replace("$TEMPLATE$", Lit(template))
                .Replace("$STATUS$", StatusCommand);
            string overrides = OverrideSource(o.Adapter, out string usings);
            string register = overrides == null ? "" : "                handler.AddOverride(new VSManagerCadBoot.Custom.Actions());\r\n";
            return usings + src.Replace("$REGISTER$", register) + (overrides ?? "");
        }

        /// <summary>读取适配包 actions.cs；开头的 using 指令移到文件顶部。/ Reads the adapter's actions.cs; leading using directives move to the top of the file.</summary>
        internal static string OverrideSource(CadAdapter adapter, out string usings)
        {
            usings = "";
            if (adapter?.Directory == null) return null;
            string file = Path.Combine(adapter.Directory, CadAdapter.ActionsFileName);
            if (!File.Exists(file)) return null;
            var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n').ToList();
            var head = new StringBuilder();
            int i = 0;
            for (; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("//")) continue;
                if (t.StartsWith("using ") && t.EndsWith(";") && !t.Contains("(")) { head.Append(t).Append("\r\n"); lines[i] = ""; continue; }
                break;
            }
            usings = head.ToString();
            return "\r\n" + string.Join("\r\n", lines);
        }

        /// <summary>定位 CAD 托管引用 DLL。/ Locates the CAD managed reference DLLs.</summary>
        public static List<string> ResolveReferences(string cadExe, CadApiConfig api, out string error)
        {
            error = null;
            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(Environment.ExpandEnvironmentVariables((cadExe ?? "").Trim().Trim('"')))); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { dir = null; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { error = "找不到 CAD 安装目录 / CAD install folder not found"; return null; }
            var list = new List<string>();
            foreach (var name in api?.References ?? new List<string>())
            {
                string p = Path.Combine(dir, name);
                if (!File.Exists(p)) { error = "CAD 安装目录中缺少 / Missing in the CAD install folder: " + name; return null; }
                list.Add(p);
            }
            if (list.Count == 0) error = "未配置 CAD 托管引用 / No CAD managed references configured";
            return list.Count == 0 ? null : list;
        }

        /// <summary>编译（按源码与引用指纹缓存）。/ Compiles (cached by the source and reference fingerprint).</summary>
        public static CadBootResult Compile(CadBootOptions o)
        {
            try
            {
                var api = ResolveApi(o.Host, o.Adapter);
                string source = GenerateSource(o, api);
                var refs = ResolveReferences(o.CadExe, api, out string refError);
                if (refs == null) return new CadBootResult { Error = refError };
                string agent = o.AgentAssembly ?? typeof(CadAgentHost).Assembly.Location;
                string hash = Fingerprint(source, refs.Concat(new[] { agent }));
                string dir = Path.Combine(o.CacheRoot ?? DefaultCacheRoot, hash);
                string boot = Path.Combine(dir, BootAssemblyName + ".dll");
                if (File.Exists(boot) && File.Exists(Path.Combine(dir, Path.GetFileName(agent)))) return new CadBootResult { Ok = true, BootDll = boot, Cached = true };
                Directory.CreateDirectory(dir);
                // 代理 DLL 复制到缓存目录，CAD 锁定的是副本，不影响 VSManager 重新生成。/ The agent DLL is copied so CAD locks the copy, not VSManager's build output.
                string agentCopy = Path.Combine(dir, Path.GetFileName(agent));
                File.Copy(agent, agentCopy, true);
                var p = new CompilerParameters
                {
                    GenerateExecutable = false, GenerateInMemory = false, OutputAssembly = boot,
                    IncludeDebugInformation = false, TreatWarningsAsErrors = false, CompilerOptions = "/optimize /nowarn:1701,1702",
                };
                p.ReferencedAssemblies.AddRange(new[] { "System.dll", "System.Core.dll", "System.Windows.Forms.dll", "System.Drawing.dll", agentCopy });
                p.ReferencedAssemblies.AddRange(refs.ToArray());
                using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
                {
                    var r = provider.CompileAssemblyFromSource(p, source);
                    var errors = r.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToList();
                    if (errors.Count > 0)
                    {
                        TryDelete(boot);
                        return new CadBootResult
                        {
                            Error = "引导 DLL 编译失败（v1 支持 .NET Framework 版 CAD，如 AutoCAD 2024 及更早；AutoCAD 2025+ 基于 .NET 8 暂不支持）/ Boot DLL compile failed (v1 supports .NET Framework CAD hosts such as AutoCAD 2024 and earlier; .NET 8 based AutoCAD 2025+ is not supported yet): "
                                + string.Join("; ", errors.Take(3).Select(e => (e.Line > 0 ? "L" + e.Line + " " : "") + e.ErrorText)),
                        };
                    }
                }
                return new CadBootResult { Ok = true, BootDll = boot };
            }
            catch (Exception ex) { return new CadBootResult { Error = ex.Message }; }
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        private static string Fingerprint(string source, IEnumerable<string> files)
        {
            var sb = new StringBuilder(source);
            foreach (var f in files)
            {
                var fi = new FileInfo(f);
                sb.Append('|').Append(fi.FullName).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks);
            }
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Take(8).Select(b => b.ToString("x2")));
        }

        // C# 5 引导源码模板；$...$ 为占位符。/ C# 5 boot source template; $...$ are placeholders.
        internal const string BootTemplate = @"using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using VSManager.CadAgent;
using $NS$.ApplicationServices;
using $DB$.DatabaseServices;
using $RT$.Runtime;
using CadApp = $APP$;

[assembly: ExtensionApplication(typeof(VSManagerCadBoot.Boot))]
[assembly: CommandClass(typeof(VSManagerCadBoot.Boot))]

namespace VSManagerCadBoot
{
    public sealed class Boot : IExtensionApplication
    {
        internal static Control Ui;

        public void Initialize()
        {
            try
            {
                Ui = new Control();
                IntPtr handle = Ui.Handle;
                Handler handler = new Handler(Ui);
$REGISTER$                CadAgentStart start = new CadAgentStart();
                start.ConnectionFile = $CONN$;
                start.AdapterDirectory = $ADAPTERDIR$;
                start.Host = $HOST$;
                start.Solution = $SOLUTION$;
                CadAgentHost.Start(handler, start);
            }
            catch (System.Exception ex) { CadAgentHost.ReportError(""boot"", ex); }
        }

        public void Terminate() { CadAgentHost.Stop(); }

        [CommandMethod(""$STATUS$"")]
        public static void Status()
        {
            Document d = CadApp.DocumentManager.MdiActiveDocument;
            if (d != null) d.Editor.WriteMessage(""\nVSManager CAD agent: "" + CadAgentHost.Status + ""\n"");
        }
    }

    internal sealed class Handler : CadActionHandlerBase
    {
        public Handler(Control ui) : base(ui) { }

        protected override string DefaultTemplate { get { return $TEMPLATE$; } }

        private static string Full(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p ?? """"; }
        }

        private static List<Document> Docs()
        {
            List<Document> list = new List<Document>();
            foreach (Document d in CadApp.DocumentManager) list.Add(d);
            return list;
        }

        protected override bool ActivateOpen(string path)
        {
            foreach (Document d in Docs())
                if (string.Equals(Full(d.Name), Full(path), StringComparison.OrdinalIgnoreCase))
                {
                    CadApp.DocumentManager.MdiActiveDocument = d;
                    return true;
                }
            return false;
        }

        protected override string OpenDrawing(string path, bool readOnly)
        {
            Document d = DocumentCollectionExtension.Open(CadApp.DocumentManager, path, readOnly);
            CadApp.DocumentManager.MdiActiveDocument = d;
            return d.Name;
        }

        protected override string NewDrawing(string template)
        {
            Document d;
            try { d = DocumentCollectionExtension.Add(CadApp.DocumentManager, template ?? """"); }
            catch { d = DocumentCollectionExtension.Add(CadApp.DocumentManager, """"); }
            CadApp.DocumentManager.MdiActiveDocument = d;
            return d.Name;
        }

        protected override string SwitchDrawing(string name)
        {
            List<Document> docs = Docs();
            int index;
            Document hit = null;
            if (int.TryParse(name, out index) && index >= 1 && index <= docs.Count) hit = docs[index - 1];
            if (hit == null)
                foreach (Document d in docs)
                    if (string.Equals(Full(d.Name), Full(name), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(d.Name), name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileNameWithoutExtension(d.Name), name, StringComparison.OrdinalIgnoreCase)) { hit = d; break; }
            if (hit == null) return null;
            CadApp.DocumentManager.MdiActiveDocument = hit;
            return Path.GetFileName(hit.Name);
        }

        protected override int CloseAllDrawings()
        {
            int n = 0;
            foreach (Document d in Docs()) { DocumentExtension.CloseAndDiscard(d); n++; }
            return n;
        }

        protected override string ListDrawings()
        {
            List<string> names = new List<string>();
            foreach (Document d in Docs()) names.Add(Path.GetFileName(d.Name));
            return names.Count == 0 ? ""(no drawing open)"" : ""Open: "" + string.Join("", "", names.ToArray());
        }

        protected override bool HasActiveDrawing() { return CadApp.DocumentManager.MdiActiveDocument != null; }

        protected override void SendCommand(string command)
        {
            Document d = CadApp.DocumentManager.MdiActiveDocument;
            d.SendStringToExecute(command.TrimEnd() + ""\n"", true, false, false);
        }

        protected override bool CommandActive()
        {
            object v = CadApp.GetSystemVariable(""CMDACTIVE"");
            return Convert.ToInt32(v) != 0;
        }

        protected override string GetSystemVariable(string name)
        {
            object v = CadApp.GetSystemVariable(name);
            return v == null ? ""(null)"" : Convert.ToString(v);
        }

        protected override int CountEntities(string dxfType, string layer, out string breakdown)
        {
            Document doc = CadApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Dictionary<string, int> byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            using (DocumentLock l = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    string dxf = id.ObjectClass.DxfName;
                    if (!string.IsNullOrEmpty(dxfType) && !string.Equals(dxf, dxfType, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(layer))
                    {
                        Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e == null || !string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    count++;
                    int c;
                    byType.TryGetValue(dxf, out c);
                    byType[dxf] = c + 1;
                }
                tr.Commit();
            }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in byType) parts.Add(kv.Key + ""="" + kv.Value);
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            breakdown = string.Join(""\n"", parts.ToArray());
            return count;
        }
    }
}
";
    }
}
