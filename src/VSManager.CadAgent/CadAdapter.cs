using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace VSManager.CadAgent
{
    /// <summary>
    /// CAD 程序集引用与命名空间配置（引导 DLL 编译用）。/ CAD assembly references and namespaces used to compile the boot DLL.
    /// </summary>
    [DataContract]
    public sealed class CadApiConfig
    {
        /// <summary>ApplicationServices / EditorInput 等所在的根命名空间，如 Autodesk.AutoCAD。/ Root namespace of ApplicationServices / EditorInput, e.g. Autodesk.AutoCAD.</summary>
        [DataMember(Name = "namespace", EmitDefaultValue = false)] public string Namespace { get; set; }
        /// <summary>DatabaseServices 的根命名空间（缺省同 namespace）。/ Root namespace of DatabaseServices (defaults to namespace).</summary>
        [DataMember(Name = "databaseNamespace", EmitDefaultValue = false)] public string DatabaseNamespace { get; set; }
        /// <summary>Runtime 的根命名空间（缺省同 namespace）。/ Root namespace of Runtime (defaults to namespace).</summary>
        [DataMember(Name = "runtimeNamespace", EmitDefaultValue = false)] public string RuntimeNamespace { get; set; }
        /// <summary>Application 类全名。/ Full name of the Application class.</summary>
        [DataMember(Name = "applicationClass", EmitDefaultValue = false)] public string ApplicationClass { get; set; }
        /// <summary>CAD 安装目录下需要引用的托管 DLL 文件名。/ Managed DLL file names referenced from the CAD install folder.</summary>
        [DataMember(Name = "references", EmitDefaultValue = false)] public List<string> References { get; set; }
        /// <summary>新建图纸的默认样板。/ Default template for new drawings.</summary>
        [DataMember(Name = "template", EmitDefaultValue = false)] public string Template { get; set; }

        /// <summary>内置预设（目前为 AutoCAD .NET Framework 版本）；其他 CAD 请在适配包 launch.api 中配置。/ Built-in preset (currently AutoCAD on .NET Framework); configure other CADs in the adapter's launch.api.</summary>
        public static CadApiConfig Preset(string host)
        {
            if (string.Equals(host, "AutoCAD", StringComparison.OrdinalIgnoreCase))
                return new CadApiConfig
                {
                    Namespace = "Autodesk.AutoCAD",
                    ApplicationClass = "Autodesk.AutoCAD.ApplicationServices.Application",
                    References = new List<string> { "accoremgd.dll", "acdbmgd.dll", "acmgd.dll" },
                    Template = "acad.dwt",
                };
            return null;
        }

        /// <summary>用适配包配置覆盖预设中的非空字段。/ Overrides preset fields with non-empty adapter fields.</summary>
        public static CadApiConfig Merge(CadApiConfig preset, CadApiConfig custom)
        {
            if (custom == null) return preset;
            if (preset == null) return custom;
            return new CadApiConfig
            {
                Namespace = Pick(custom.Namespace, preset.Namespace),
                DatabaseNamespace = Pick(custom.DatabaseNamespace, preset.DatabaseNamespace),
                RuntimeNamespace = Pick(custom.RuntimeNamespace, preset.RuntimeNamespace),
                ApplicationClass = Pick(custom.ApplicationClass, preset.ApplicationClass),
                References = custom.References != null && custom.References.Count > 0 ? custom.References : preset.References,
                Template = Pick(custom.Template, preset.Template),
            };
        }

        private static string Pick(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a.Trim();
    }

    /// <summary>
    /// 适配包启动配置：CAD 路径、附加 DLL、启动命令、启动参数与默认图纸（与调试图纸机制对接）。
    /// Adapter launch settings: CAD path, extra DLLs, startup commands, launch arguments and default drawing (integrated with the debug-drawing mechanism).
    /// </summary>
    [DataContract]
    public sealed class CadLaunchConfig
    {
        /// <summary>CAD 可执行文件（可含环境变量）；缺省使用项目调试属性中的启动程序。/ CAD executable (may contain environment variables); defaults to the project's debug start program.</summary>
        [DataMember(Name = "cadPath", EmitDefaultValue = false)] public string CadPath { get; set; }
        /// <summary>在项目 DLL 之前 NETLOAD 的附加 DLL。/ Extra DLLs NETLOADed before the project DLL.</summary>
        [DataMember(Name = "extraDlls", EmitDefaultValue = false)] public List<string> ExtraDlls { get; set; }
        /// <summary>NETLOAD 之后写入启动脚本的命令行。/ Command lines written to the startup script after NETLOAD.</summary>
        [DataMember(Name = "startupCommands", EmitDefaultValue = false)] public List<string> StartupCommands { get; set; }
        /// <summary>附加的 CAD 启动参数，如 /nologo。/ Extra CAD launch arguments such as /nologo.</summary>
        [DataMember(Name = "args", EmitDefaultValue = false)] public string Args { get; set; }
        /// <summary>未记录调试图纸时使用的默认图纸。/ Default drawing used when no debug drawing is recorded.</summary>
        [DataMember(Name = "drawing", EmitDefaultValue = false)] public string Drawing { get; set; }
        [DataMember(Name = "api", EmitDefaultValue = false)] public CadApiConfig Api { get; set; }
    }

    /// <summary>
    /// 项目适配包（adapters\&lt;名称&gt;\adapter.json）：每个 CAD 插件项目一份，换项目只需新增适配包。
    /// Project adapter package (adapters\&lt;name&gt;\adapter.json): one per CAD plug-in project; a new project only needs a new adapter.
    /// </summary>
    [DataContract]
    public sealed class CadAdapter
    {
        public const string FileName = "adapter.json";
        /// <summary>可选的动作实现（C# 5 源码，编译进引导 DLL）。/ Optional action implementation (C# 5 source compiled into the boot DLL).</summary>
        public const string ActionsFileName = "actions.cs";

        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "description", EmitDefaultValue = false)] public string Description { get; set; }
        /// <summary>匹配的解决方案 / 项目文件名（支持 * ?）。/ Matching solution / project file names (supports * and ?).</summary>
        [DataMember(Name = "match", EmitDefaultValue = false)] public List<string> Match { get; set; }
        /// <summary>CAD 宿主显示名，如 AutoCAD。/ CAD host display name such as AutoCAD.</summary>
        [DataMember(Name = "host", EmitDefaultValue = false)] public string Host { get; set; }
        /// <summary>命令映射表：别名 → CAD 命令。/ Command map: alias → CAD command.</summary>
        [DataMember(Name = "commands", EmitDefaultValue = false)] public Dictionary<string, string> Commands { get; set; }
        /// <summary>允许执行映射表以外的命令（缺省 false）。/ Allows commands outside the map (default false).</summary>
        [DataMember(Name = "allowRawCommands", EmitDefaultValue = false)] public bool AllowRawCommands { get; set; }
        /// <summary>getLog 可读取的日志文件（可含环境变量与通配符，取最新文件）。/ Log files readable by getLog (environment variables and wildcards allowed; newest file wins).</summary>
        [DataMember(Name = "logs", EmitDefaultValue = false)] public List<string> Logs { get; set; }
        [DataMember(Name = "launch", EmitDefaultValue = false)] public CadLaunchConfig Launch { get; set; }

        /// <summary>适配包所在目录（加载时设置）。/ Adapter folder (set when loaded).</summary>
        public string Directory { get; internal set; }

        public IEnumerable<string> Patterns => (Match ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim());

        /// <summary>解决方案 / 项目路径是否匹配本适配包。/ Whether a solution / project path matches this adapter.</summary>
        public bool Matches(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string file, stem;
            try { file = Path.GetFileName(path.Trim()); stem = Path.GetFileNameWithoutExtension(file); }
            catch (ArgumentException) { return false; }
            foreach (var p in Patterns)
            {
                var re = new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
                if (re.IsMatch(file) || re.IsMatch(stem)) return true;
            }
            return false;
        }

        /// <summary>
        /// 把别名或命令解析为允许执行的 CAD 命令；不允许时返回 null 并给出原因。
        /// Resolves an alias or command to an allowed CAD command; returns null with a reason when not allowed.
        /// </summary>
        public string ResolveCommand(string input, out string error)
        {
            error = null;
            string cmd = (input ?? "").Trim();
            if (cmd.Length == 0) { error = "缺少 args.command / args.command is required"; return null; }
            if (cmd.IndexOfAny(new[] { '\r', '\n' }) >= 0) { error = "命令不能包含换行 / Commands must be a single line"; return null; }
            var map = Commands ?? new Dictionary<string, string>();
            foreach (var kv in map)
                if (string.Equals(kv.Key, cmd, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)) return kv.Value.Trim();
            foreach (var v in map.Values)
                if (string.Equals((v ?? "").Trim(), cmd, StringComparison.OrdinalIgnoreCase)) return v.Trim();
            if (AllowRawCommands) return cmd;
            error = "命令「" + cmd + "」不在适配包「" + Name + "」的命令映射表中 / Command is not in the adapter's command map"
                + (map.Count > 0 ? "：" + string.Join(", ", map.Keys) : "");
            return null;
        }

        /// <summary>展开日志路径：环境变量 + 文件名通配符（取最新修改的文件）。/ Expands log paths: environment variables + file-name wildcards (newest file).</summary>
        public List<string> ResolveLogs()
        {
            var list = new List<string>();
            foreach (var raw in (Logs ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                string p = Environment.ExpandEnvironmentVariables(raw.Trim());
                try
                {
                    if (!Path.IsPathRooted(p) && Directory != null) p = Path.Combine(Directory, p);
                    string dir = Path.GetDirectoryName(p), name = Path.GetFileName(p);
                    if (name.IndexOfAny(new[] { '*', '?' }) >= 0)
                    {
                        if (!System.IO.Directory.Exists(dir)) continue;
                        var newest = new DirectoryInfo(dir).GetFiles(name).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                        if (newest != null) list.Add(newest.FullName);
                    }
                    else if (File.Exists(p)) list.Add(Path.GetFullPath(p));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) { }
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static CadAdapter Load(string file)
        {
            var a = CadJson.Deserialize<CadAdapter>(File.ReadAllText(file));
            if (a == null) return null;
            a.Directory = Path.GetDirectoryName(Path.GetFullPath(file));
            if (string.IsNullOrWhiteSpace(a.Name)) a.Name = Path.GetFileName(a.Directory);
            a.Name = a.Name.Trim();
            return a;
        }
    }

    /// <summary>
    /// 适配包目录：程序目录\adapters（随程序发布的模板）与 %APPDATA%\VSManager\adapters（本机项目适配包，同名优先）。
    /// Adapter store: &lt;program folder&gt;\adapters (shipped templates) and %APPDATA%\VSManager\adapters (local project adapters, which win on name clashes).
    /// </summary>
    public static class CadAdapterStore
    {
        /// <summary>测试可替换的根目录。/ Root folders, replaceable by tests.</summary>
        public static Func<IReadOnlyList<string>> RootsProvider;

        public static IReadOnlyList<string> DefaultRoots()
        {
            var roots = new List<string>();
            try { roots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adapters")); } catch { }
            try { roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VSManager", "adapters")); } catch { }
            return roots;
        }

        public static IReadOnlyList<string> Roots => RootsProvider?.Invoke() ?? DefaultRoots();

        /// <summary>加载全部适配包；名称以下划线开头的目录（如 _template）只作示例，不参与匹配。/ Loads all adapters; folders starting with an underscore (such as _template) are samples and never match.</summary>
        public static List<CadAdapter> LoadAll(IEnumerable<string> roots = null, List<string> errors = null)
        {
            var byName = new Dictionary<string, CadAdapter>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots ?? Roots)
            {
                string[] dirs;
                try { if (!System.IO.Directory.Exists(root)) continue; dirs = System.IO.Directory.GetDirectories(root); }
                catch { continue; }
                foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (Path.GetFileName(dir).StartsWith("_")) continue;
                    string file = Path.Combine(dir, CadAdapter.FileName);
                    if (!File.Exists(file)) continue;
                    try
                    {
                        var a = CadAdapter.Load(file);
                        if (a != null) byName[a.Name] = a;
                    }
                    catch (Exception ex) { errors?.Add(Path.GetFileName(dir) + ": " + ex.Message); }
                }
            }
            return byName.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static CadAdapter Find(string name, IEnumerable<string> roots = null) =>
            string.IsNullOrWhiteSpace(name) ? null : LoadAll(roots).FirstOrDefault(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>按解决方案或项目路径匹配适配包（任一路径匹配即可）。/ Matches an adapter by solution or project path (either may match).</summary>
        public static CadAdapter Match(IEnumerable<string> paths, IEnumerable<string> roots = null)
        {
            var list = LoadAll(roots);
            foreach (var p in paths ?? Enumerable.Empty<string>())
            {
                var hit = list.FirstOrDefault(a => a.Matches(p));
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
