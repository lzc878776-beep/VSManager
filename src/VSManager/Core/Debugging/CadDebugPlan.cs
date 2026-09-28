using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// CAD 插件调试的纯逻辑：识别启动程序是否为 CAD 宿主、生成 NETLOAD 启动脚本、在启动参数中注入 / 去除 /b 脚本参数。
    /// Pure logic for CAD plug-in debugging: detect a CAD host start program, build the NETLOAD startup script and inject / strip the /b script argument.
    /// </summary>
    public static class CadDebugPlan
    {
        /// <summary>支持的 CAD 宿主（可执行文件名 → 显示名）；均支持 /b 启动脚本与 NETLOAD。/ Supported CAD hosts (executable → display name); all support /b startup scripts and NETLOAD.</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> Hosts = new[]
        {
            new KeyValuePair<string, string>("acad.exe", "AutoCAD"),
            new KeyValuePair<string, string>("zwcad.exe", "ZWCAD"),
            new KeyValuePair<string, string>("gcad.exe", "GstarCAD"),
            new KeyValuePair<string, string>("bricscad.exe", "BricsCAD"),
        };

        /// <summary>注入脚本所在目录的标记，用于识别并清除本工具写入的参数。/ Marker of the injected script folder, used to recognise and strip arguments written by this tool.</summary>
        public const string ScriptFolderMarker = @"\VSManager\CadDebug\";

        private static readonly Regex Injected = new Regex(@"(?:^|\s+)[/-]b\s+""[^""]*\\VSManager\\CadDebug\\[^""]*""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UserScript = new Regex(@"(?:^|\s)[/-]b(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>返回 CAD 显示名；不是 CAD 宿主时返回 null。/ Returns the CAD display name, or null when the program is not a CAD host.</summary>
        public static string DetectHost(string program)
        {
            if (string.IsNullOrWhiteSpace(program)) return null;
            string path = Environment.ExpandEnvironmentVariables(program.Trim().Trim('"').Trim());
            string file;
            try { file = Path.GetFileName(path); }
            catch (ArgumentException) { file = path.Substring(Math.Max(path.LastIndexOfAny(new[] { '\\', '/' }) + 1, 0)); }
            foreach (var h in Hosts)
                if (string.Equals(file, h.Key, StringComparison.OrdinalIgnoreCase)) return h.Value;
            return null;
        }

        /// <summary>去除本工具之前注入的 /b 参数（自愈残留）。/ Removes /b arguments previously injected by this tool (self-heals leftovers).</summary>
        public static string StripInjected(string args) => Injected.Replace(args ?? "", "").Trim();

        /// <summary>用户是否已自行指定 /b 脚本（CAD 只接受一个脚本，此时不注入）。/ Whether the user already passes a /b script (CAD accepts only one, so nothing is injected).</summary>
        public static bool HasUserScript(string args) => UserScript.IsMatch(StripInjected(args));

        /// <summary>在原参数前加入 /b "脚本"。/ Prepends /b "script" to the original arguments.</summary>
        public static string Inject(string args, string scriptPath)
        {
            string rest = StripInjected(args);
            return ("/b \"" + scriptPath + "\" " + rest).Trim();
        }

        /// <summary>
        /// 生成启动脚本：每个 DLL 一行 LISP (command "_.NETLOAD" "路径")，路径用正斜杠，可含空格。
        /// Builds the startup script: one LISP (command "_.NETLOAD" "path") line per DLL, using forward slashes so spaces are safe.
        /// </summary>
        public static string BuildScript(IEnumerable<string> dlls)
        {
            var sb = new StringBuilder();
            foreach (var dll in (dlls ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (dll.IndexOf('"') >= 0) throw new ArgumentException("DLL 路径不能包含引号 / DLL path must not contain quotes", nameof(dlls));
                sb.Append("(command \"_.NETLOAD\" \"").Append(dll.Replace('\\', '/')).Append("\")\r\n");
            }
            return sb.ToString();
        }

        /// <summary>把项目名转为安全的脚本文件名。/ Turns a project name into a safe script file name.</summary>
        public static string ScriptFileName(string project)
        {
            string name = string.IsNullOrWhiteSpace(project) ? "project" : project.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Replace(' ', '_');
            if (name.Length > 60) name = name.Substring(0, 60);
            return name + ".scr";
        }
    }
}
