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

        /// <summary>
        /// 启动脚本首行：FILEDIA 为 0 时改回 1（FILEDIA=0 会导致无法自动加载 DLL）；对 AutoCAD 以外的宿主也生效。
        /// First startup-script line: sets FILEDIA back to 1 when it is 0 (FILEDIA=0 breaks auto-loading the DLL); also works for hosts other than AutoCAD.
        /// </summary>
        public const string FileDiaGuard = "(if (= (getvar \"FILEDIA\") 0) (setvar \"FILEDIA\" 1))\r\n";

        /// <summary>可作为调试图纸打开的扩展名。/ Extensions that can be opened as the debug drawing.</summary>
        public static readonly IReadOnlyList<string> DrawingExtensions = new[] { ".dwg", ".dxf" };

        // 注入时图纸紧挨在 /b 之前，一并识别与清除。/ The injected drawing sits right before /b and is recognised and stripped together with it.
        private static readonly Regex Injected = new Regex(@"(?:^|\s+)(?:""[^""]*\.(?:dwg|dxf)""\s+)?[/-]b\s+""[^""]*\\VSManager\\CadDebug\\[^""]*""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UserScript = new Regex(@"(?:^|\s)[/-]b(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UserDrawing = new Regex(@"(?:^|\s)(?:""[^""]*\.(?:dwg|dxf)""|[^\s""]+\.(?:dwg|dxf))(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        /// <summary>去除注入的 /b 参数及紧随其后的适配包附加参数。/ Removes the injected /b argument and the adapter's extra arguments that follow it.</summary>
        public static string StripInjected(string args, string extra)
        {
            string rest = StripInjected(args);
            string e = (extra ?? "").Trim();
            if (e.Length > 0 && rest.StartsWith(e, StringComparison.OrdinalIgnoreCase) && (rest.Length == e.Length || char.IsWhiteSpace(rest[e.Length])))
                rest = rest.Substring(e.Length).Trim();
            return rest;
        }

        /// <summary>用户是否已自行指定 /b 脚本（CAD 只接受一个脚本，此时不注入）。/ Whether the user already passes a /b script (CAD accepts only one, so nothing is injected).</summary>
        public static bool HasUserScript(string args) => UserScript.IsMatch(StripInjected(args));

        /// <summary>用户启动参数是否已指定要打开的图纸（此时不再注入图纸）。/ Whether the user's arguments already name a drawing to open (no drawing is injected then).</summary>
        public static bool HasUserDrawing(string args) => UserDrawing.IsMatch(StripInjected(args));

        /// <summary>是否为可打开的图纸路径（.dwg / .dxf，且不含引号）。/ Whether the path is an openable drawing (.dwg / .dxf without quotes).</summary>
        public static bool IsDrawingPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('"') >= 0) return false;
            string ext;
            try { ext = Path.GetExtension(path.Trim()); } catch (ArgumentException) { return false; }
            return DrawingExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 在原参数前加入 ["图纸"] /b "脚本"；drawing 为空时 CAD 按默认新建图纸启动。
        /// Prepends ["drawing"] /b "script" to the original arguments; without a drawing the CAD host starts with its default new drawing.
        /// </summary>
        public static string Inject(string args, string scriptPath, string drawing = null, string extra = null)
        {
            string rest = StripInjected(args, extra);
            string open = IsDrawingPath(drawing) ? "\"" + drawing.Trim() + "\" " : "";
            string more = string.IsNullOrWhiteSpace(extra) ? "" : extra.Trim() + " ";
            return (open + "/b \"" + scriptPath + "\" " + more + rest).Trim();
        }

        /// <summary>
        /// 生成启动脚本：每个 DLL 一行 LISP (command "_.NETLOAD" "路径")，路径用正斜杠，可含空格。
        /// Builds the startup script: one LISP (command "_.NETLOAD" "path") line per DLL, using forward slashes so spaces are safe.
        /// </summary>
        public static string BuildScript(IEnumerable<string> dlls) => BuildScript(dlls, null);

        /// <summary>
        /// 生成启动脚本：先 NETLOAD 全部 DLL，再逐行写入适配包的启动命令（单行，忽略空行）。
        /// Builds the startup script: NETLOAD every DLL first, then write the adapter's startup commands line by line (single-line, blanks ignored).
        /// </summary>
        public static string BuildScript(IEnumerable<string> dlls, IEnumerable<string> commands)
        {
            var sb = new StringBuilder();
            foreach (var dll in (dlls ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (dll.IndexOf('"') >= 0) throw new ArgumentException("DLL 路径不能包含引号 / DLL path must not contain quotes", nameof(dlls));
                sb.Append("(command \"_.NETLOAD\" \"").Append(dll.Replace('\\', '/')).Append("\")\r\n");
            }
            foreach (var cmd in (commands ?? Enumerable.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                if (cmd.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("启动命令必须为单行 / Startup commands must be single-line", nameof(commands));
                sb.Append(cmd.Trim()).Append("\r\n");
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
