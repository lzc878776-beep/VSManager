using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VSManager
{
    /// <summary>从代码中找到的一个评测运行目录。/ An evaluation run folder found in code.</summary>
    public sealed class AgentEvalCandidate
    {
        /// <summary>展开后的目录（配置值，含 runs 的上一级或运行目录本身）。/ Expanded folder (the configured value: the parent of runs or the run folder itself).</summary>
        public string Folder;
        /// <summary>代码里写出的形式（如 %LOCALAPPDATA%\Tool\Eval），用于显示与保存。/ The form written in code (e.g. %LOCALAPPDATA%\Tool\Eval), used for display and saving.</summary>
        public string Display;
        /// <summary>引用该目录的代码文件（相对代码根）。/ Code files referencing the folder (relative to their code root).</summary>
        public List<string> Sources = new List<string>();
        public int RunCount;
        public DateTime Latest;
        /// <summary>目录存在且含评测运行。/ The folder exists and holds evaluation runs.</summary>
        public bool HasRuns => RunCount > 0;
    }

    /// <summary>
    /// 读取代码自动定位评测运行目录：在代码根（解决方案所在仓库）中查找写出 result.json / cases.json 的评测脚本或代码，
    /// 提取其中由环境变量（%LOCALAPPDATA%、$env:APPDATA、Environment.GetFolderPath 等）与字面路径拼出的目录，
    /// 只保留实际存在且含运行子目录的目录，按最近运行时间排序。只读，不执行任何代码。
    /// Locates evaluation run folders by reading code: searches the code roots (repositories of the solutions) for evaluation scripts or code
    /// that write result.json / cases.json, extracts folders built from environment variables (%LOCALAPPDATA%, $env:APPDATA,
    /// Environment.GetFolderPath, ...) and literal path segments, keeps only folders that exist and hold run sub-folders, and orders them by
    /// the latest run. Read-only; no code is executed.
    /// </summary>
    public static class AgentEvalDiscovery
    {
        private static readonly string[] Extensions = { ".ps1", ".psm1", ".py", ".cs", ".js", ".ts", ".cmd", ".bat", ".sh" };
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", ".git", ".vs", "node_modules", "packages", ".idea", "TestResults", "artifacts", "dist", "build" };
        private const long MaxFileBytes = 1024 * 1024;
        private const int MaxFiles = 60000;

        /// <summary>评测代码的标志：写出结果或读取用例文件。/ Marks of evaluation code: writes results or reads case files.</summary>
        private static readonly Regex Marker = new Regex(@"result\.json|cases\.json", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 环境变量 / 特殊目录写法，后面紧跟同一行内的字符串字面量路径段。
        /// Environment-variable / special-folder forms followed by literal path segments on the same line.
        /// </summary>
        private static readonly Regex EnvToken = new Regex(
            @"\$env:(?<n>\w+)" +
            @"|%(?<n>[A-Za-z_]\w*)%" +
            @"|GetEnvironmentVariable\(\s*[""'](?<n>\w+)[""']" +
            @"|SpecialFolder\]?(?:::|\.)(?<s>\w+)" +
            @"|GetFolderPath\(\s*[""'](?<s>\w+)[""']" +
            @"|os\.(?:environ\[\s*|environ\.get\(\s*|getenv\(\s*)[""'](?<n>\w+)[""']" +
            @"|process\.env\.(?<n>\w+)" +
            @"|Path\.GetTempPath\(\)(?<t>)",
            RegexOptions.CultureInvariant);

        private static readonly Regex Literal = new Regex(@"@?""(?<v>(?:[^""\\\r\n]|\\.)*)""|'(?<v>[^'\r\n]*)'", RegexOptions.CultureInvariant);

        /// <summary>代码根：解决方案目录向上找到的仓库根（含 .git），去重并去掉嵌套的根。/ Code roots: the repository root (with .git) above each solution folder, deduplicated without nested roots.</summary>
        public static List<string> CodeRoots(IEnumerable<string> folders)
        {
            var roots = new List<string>();
            foreach (var f in folders ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                string dir;
                try { dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(f.Trim().Trim('"'))); } catch { continue; }
                if (File.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (!Directory.Exists(dir)) continue;
                roots.Add(RepoRoot(dir) ?? dir);
            }
            var distinct = roots.Select(r => r.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return distinct.Where(r => !distinct.Any(o => o.Length < r.Length && r.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private static string RepoRoot(string dir)
        {
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            return null;
        }

        /// <summary>
        /// 在代码根中查找评测运行目录；返回全部候选（含不存在或没有运行的，供说明），有运行的排在前面并按最近运行倒序。
        /// Searches the code roots for evaluation run folders; returns every candidate (including missing or empty ones, for explanation),
        /// folders with runs first, newest run first.
        /// </summary>
        public static List<AgentEvalCandidate> Discover(IEnumerable<string> codeRoots, CancellationToken ct = default(CancellationToken))
        {
            var map = new Dictionary<string, AgentEvalCandidate>(StringComparer.OrdinalIgnoreCase);
            int files = 0;
            foreach (string root in codeRoots ?? Enumerable.Empty<string>())
            {
                foreach (string file in EnumerateCode(root, ct))
                {
                    if (++files > MaxFiles) break;
                    string text;
                    try
                    {
                        if (new FileInfo(file).Length > MaxFileBytes) continue;
                        text = File.ReadAllText(file, Encoding.UTF8);
                    }
                    catch { continue; }
                    if (!Marker.IsMatch(text)) continue;
                    string rel = file.Length > root.Length ? file.Substring(root.TrimEnd('\\').Length).TrimStart('\\') : Path.GetFileName(file);
                    foreach (string display in Extract(text))
                    {
                        string folder = Environment.ExpandEnvironmentVariables(display);
                        if (folder.IndexOf('%') >= 0 || !IsRooted(folder)) continue;
                        if (!map.TryGetValue(folder, out var c)) map[folder] = c = new AgentEvalCandidate { Folder = folder, Display = display };
                        if (!c.Sources.Contains(rel, StringComparer.OrdinalIgnoreCase)) c.Sources.Add(rel);
                    }
                    // 脚本旁边的 runs 目录 / A runs folder next to the script
                    string local = Path.GetDirectoryName(file);
                    if (Directory.Exists(Path.Combine(local, "runs")) && !map.ContainsKey(local))
                        map[local] = new AgentEvalCandidate { Folder = local, Display = local, Sources = { rel } };
                }
            }
            foreach (var c in map.Values) Measure(c);
            return map.Values.OrderByDescending(c => c.HasRuns).ThenByDescending(c => c.Latest).ThenByDescending(c => c.RunCount)
                .ThenBy(c => c.Display, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>最合适的候选（有运行且最近）；没有时返回 null。/ The best candidate (has runs, most recent); null when none.</summary>
        public static AgentEvalCandidate Best(IEnumerable<AgentEvalCandidate> candidates) => candidates?.FirstOrDefault(c => c.HasRuns);

        /// <summary>
        /// 从一段代码中提取「环境变量 + 字面路径段」组成的目录（保留变量形式），并去掉末尾的 runs、文件名与运行编号段。
        /// Extracts folders made of an environment variable plus literal segments (kept in variable form) from code, trimming a trailing runs
        /// segment, file names and run-id segments.
        /// </summary>
        public static IEnumerable<string> Extract(string code)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in (code ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                foreach (Match m in EnvToken.Matches(line))
                {
                    string variable = Variable(m);
                    if (variable == null) continue;
                    var parts = new List<string>();
                    // 只取紧随其后、到行尾或语句结束前的字面量 / Only literals that follow, up to the end of the statement
                    string rest = line.Substring(m.Index + m.Length);
                    int stop = rest.IndexOf(';');
                    if (stop >= 0) rest = rest.Substring(0, stop);
                    if (m.Groups["n"].Success && line[m.Index] == '%')
                    {
                        // %VAR%\a\b 写在字符串里：取到字符串结束 / %VAR%\a\b inside a string: read up to the string end
                        var tail = Regex.Match(rest, @"^[\\/](?<p>[^""'\s;|<>*?()（），,：。]+)");
                        if (tail.Success) parts.Add(tail.Groups["p"].Value);
                    }
                    else
                        foreach (Match lit in Literal.Matches(rest))
                        {
                            string v = lit.Groups["v"].Value.Replace("\\\\", "\\");
                            if (v.Length == 0 || v.IndexOfAny(new[] { '{', '$', '*', '?', '<', '>', '|' }) >= 0 || Regex.IsMatch(v, @"^[a-zA-Z]:")) break;
                            parts.Add(v);
                        }
                    string path = Trim(variable, parts);
                    if (path != null && seen.Add(path)) yield return path;
                }
            }
        }

        private static string Variable(Match m)
        {
            if (m.Groups["t"].Success) return "%TEMP%";
            if (m.Groups["n"].Success) return "%" + m.Groups["n"].Value.ToUpperInvariant() + "%";
            switch (m.Groups["s"].Value)
            {
                case "LocalApplicationData": return "%LOCALAPPDATA%";
                case "ApplicationData": return "%APPDATA%";
                case "UserProfile": return "%USERPROFILE%";
                case "CommonApplicationData": return "%PROGRAMDATA%";
                case "MyDocuments": case "Personal": return "%USERPROFILE%\\Documents";
                default: return null;
            }
        }

        private static string Trim(string variable, List<string> parts)
        {
            var segs = parts.SelectMany(p => p.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim()).Where(s => s.Length > 0 && s != ".").ToList();
            // 去掉文件名、runs 及其后的运行编号 / Drop file names, "runs" and any run id after it
            int runs = segs.FindIndex(s => s.Equals("runs", StringComparison.OrdinalIgnoreCase));
            if (runs >= 0) segs = segs.Take(runs).ToList();
            while (segs.Count > 0 && Path.HasExtension(segs[segs.Count - 1])) segs.RemoveAt(segs.Count - 1);
            if (segs.Count == 0 || segs.Any(s => s == "..")) return null;
            return variable + "\\" + string.Join("\\", segs);
        }

        private static bool IsRooted(string p)
        {
            try { return Path.IsPathRooted(p) && p.Length > 3; } catch { return false; }
        }

        private static void Measure(AgentEvalCandidate c)
        {
            string dir = AgentEvalStore.ResolveRunsDir(c.Folder);
            if (dir == null) return;
            try
            {
                foreach (var d in new DirectoryInfo(dir).GetDirectories())
                {
                    if (!File.Exists(Path.Combine(d.FullName, "result.json")) && !File.Exists(Path.Combine(d.FullName, "cases.json"))) continue;
                    c.RunCount++;
                    var t = AgentEvalStore.ParseRunTime(d.Name) ?? d.LastWriteTime;
                    if (t > c.Latest) c.Latest = t;
                }
            }
            catch { }
        }

        private static IEnumerable<string> EnumerateCode(string root, CancellationToken ct)
        {
            var stack = new Stack<string>();
            if (Directory.Exists(root)) stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                string[] files, subs;
                try { files = Directory.GetFiles(dir); subs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (string f in files)
                    if (Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) yield return f;
                foreach (string s in subs)
                {
                    string name = Path.GetFileName(s);
                    if (SkipDirs.Contains(name) || name.StartsWith(".", StringComparison.Ordinal)) continue;
                    try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    stack.Push(s);
                }
            }
        }

        /// <summary>候选列表的说明文字（供 AI 与状态栏）。/ Description of the candidates (for the AI and the status bar).</summary>
        public static string Describe(IList<AgentEvalCandidate> candidates, IList<string> roots)
        {
            var sb = new StringBuilder();
            sb.AppendLine("已读取代码根 / Code roots read：" + (roots.Count == 0 ? "（无 / none）" : string.Join("；", roots.Select(MaskRoot))));
            if (candidates.Count == 0)
            {
                sb.Append("代码中没有找到写出 result.json / cases.json 的评测目录 / No evaluation folder writing result.json / cases.json was found in code");
                return sb.ToString();
            }
            // 没有运行的候选（日志、提示词等目录）只列出少量，便于排查 / Candidates without runs (log, prompt folders...) are listed sparingly for diagnosis
            foreach (var c in candidates.Where(c => c.HasRuns).Concat(candidates.Where(c => !c.HasRuns && Directory.Exists(c.Folder)).Take(8)))
                sb.AppendLine((c.HasRuns ? $"✓ {c.Display}：{c.RunCount} 次运行，最近 {c.Latest:yyyy-MM-dd HH:mm} / {c.RunCount} runs, latest {c.Latest:yyyy-MM-dd HH:mm}"
                        : $"✗ {c.Display}：" + (Directory.Exists(c.Folder) ? "没有运行 / no runs" : "目录不存在 / folder missing"))
                    + "  ← " + string.Join(", ", c.Sources.Take(3)) + (c.Sources.Count > 3 ? $" 等 {c.Sources.Count} 个 / {c.Sources.Count} files" : ""));
            return sb.ToString().TrimEnd();
        }

        private static string MaskRoot(string root) => Path.GetFileName(root.TrimEnd('\\')) is string n && n.Length > 0 ? n : root;
    }
}
