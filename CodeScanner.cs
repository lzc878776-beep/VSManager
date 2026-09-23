using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>快速扫描解决方案目录，生成代码结构概要（项目、目录、主要类型、README），供 AI 总控助手撰写职责描述。</summary>
    public static class CodeScanner
    {
        private const int MaxFiles = 20000;
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "packages", "node_modules", "TestResults", "Debug", "Release", "x64", "x86", "artifacts", "dist", "out"
        };
        private static readonly HashSet<string> CodeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".vb", ".fs", ".cpp", ".c", ".h", ".hpp", ".xaml", ".ts", ".tsx", ".js", ".py", ".razor", ".cshtml", ".sql", ".lsp"
        };
        private static readonly Regex TypeRx = new Regex(@"^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe)\s+)*(class|interface|struct|enum|record)\s+([A-Za-z_]\w*)", RegexOptions.Multiline | RegexOptions.Compiled);

        public static string Root(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath)) return null;
            if (Directory.Exists(solutionPath)) return Path.GetFullPath(solutionPath);
            string dir = Path.GetDirectoryName(solutionPath);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? Path.GetFullPath(dir) : null;
        }

        /// <summary>扫描代码结构。solutionFile 为 .sln / .slnx 时只统计其中包含的项目目录，避免把同目录下其他解决方案的代码算进来。</summary>
        public static string Scan(string root, int max, string solutionFile = null)
        {
            var files = new List<FileInfo>();
            var dirs = SolutionProjectDirs(solutionFile);
            if (dirs.Count > 0)
            {
                files.AddRange(new DirectoryInfo(root).EnumerateFiles());
                foreach (var d in dirs) Walk(new DirectoryInfo(d), files, 0);
            }
            else Walk(new DirectoryInfo(root), files, 0);
            files = files.GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Where(f => f.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList();
            string Rel(string full) => full.Substring(root.Length).TrimStart('\\', '/');

            var sb = new StringBuilder();
            sb.AppendLine("根目录：" + root + (files.Count >= MaxFiles ? $"（文件过多，仅统计前 {MaxFiles} 个）" : $"（{files.Count} 个文件，已排除 bin/obj 等）"));
            if (dirs.Count > 0) sb.AppendLine($"仅统计解决方案 {Path.GetFileName(solutionFile)} 中的 {dirs.Count} 个项目目录");

            var projects = files.Where(f => Regex.IsMatch(f.Extension, @"^\.(csproj|vbproj|fsproj|vcxproj|sqlproj|shproj)$", RegexOptions.IgnoreCase)).ToList();
            if (projects.Count > 0)
            {
                sb.AppendLine().AppendLine($"项目（{projects.Count}）：");
                foreach (var p in projects.Take(40)) sb.AppendLine("- " + Rel(p.FullName) + ProjectInfo(p.FullName));
            }
            foreach (var pkg in files.Where(f => f.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase)).Take(5))
                sb.AppendLine("- " + Rel(pkg.FullName) + "（Node 项目）");

            sb.AppendLine().Append("文件类型：").AppendLine(string.Join("，", files.GroupBy(f => f.Extension.ToLowerInvariant())
                .Where(g => g.Key.Length > 0).OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key + " " + g.Count())));

            var code = files.Where(f => CodeExts.Contains(f.Extension)).ToList();
            sb.AppendLine().AppendLine("目录（代码文件数）：");
            foreach (var g in code.GroupBy(f => DirKey(Rel(f.FullName))).OrderByDescending(g => g.Count()).Take(25))
                sb.AppendLine($"- {(g.Key.Length == 0 ? "(根目录)" : g.Key)}：{g.Count()}");

            var readme = files.Where(f => f.Name.StartsWith("README", StringComparison.OrdinalIgnoreCase) && f.Length < 200000)
                .OrderBy(f => f.FullName.Length).FirstOrDefault();
            if (readme != null)
            {
                try
                {
                    var lines = File.ReadLines(readme.FullName).Where(l => l.Trim().Length > 0).Take(20).ToList();
                    sb.AppendLine().AppendLine("README（" + Rel(readme.FullName) + "）：");
                    foreach (var l in lines) sb.AppendLine("  " + Cut(l.Trim(), 160));
                }
                catch { }
            }

            var types = new List<string>();
            foreach (var f in code.Where(f => f.Length < 400000 && Regex.IsMatch(f.Extension, @"^\.(cs|vb|ts|tsx|cpp|h|hpp)$", RegexOptions.IgnoreCase))
                         .OrderByDescending(f => f.Length).Take(300))
            {
                try
                {
                    var names = TypeRx.Matches(File.ReadAllText(f.FullName)).Cast<Match>().Select(m => m.Groups[2].Value).Distinct().Take(6).ToList();
                    if (names.Count > 0) types.Add(Rel(f.FullName) + "：" + string.Join(", ", names));
                }
                catch { }
                if (types.Count >= 80) break;
            }
            if (types.Count > 0)
            {
                sb.AppendLine().AppendLine("主要代码文件与类型（按文件大小）：");
                foreach (var t in types) sb.AppendLine("- " + t);
            }

            string s = sb.ToString();
            return s.Length > max ? s.Substring(0, max) + "\n…（已截断）" : s;
        }

        /// <summary>读取根目录内的文件片段；拒绝访问根目录之外的路径。</summary>
        public static string ReadFile(string root, string path, int startLine, int maxLines, int max)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return "未指定文件";
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path)); }
            catch (Exception ex) { return "路径无效：" + ex.Message; }
            string r = root.TrimEnd('\\') + "\\";
            if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return "只能读取该解决方案目录内的文件：" + root;
            if (Directory.Exists(full))
            {
                var entries = new DirectoryInfo(full).EnumerateFileSystemInfos().Where(e => !SkipDirs.Contains(e.Name) && !e.Name.StartsWith("."))
                    .Take(200).Select(e => (e is DirectoryInfo ? "[目录] " : "") + e.Name);
                return "目录 " + full + "：\n" + string.Join("\n", entries);
            }
            if (!File.Exists(full)) return "文件不存在：" + full;
            var sb = new StringBuilder();
            int n = 0, from = Math.Max(1, startLine);
            foreach (var line in File.ReadLines(full))
            {
                n++;
                if (n < from) continue;
                if (n >= from + maxLines || sb.Length > max) { sb.AppendLine("…（后续省略，可用 startLine=" + n + " 继续读取）"); break; }
                sb.Append(n).Append(": ").AppendLine(line);
            }
            return sb.Length == 0 ? "（文件为空或超出行数）" : sb.ToString();
        }

        /// <summary>解析 .sln / .slnx 中的项目目录（去掉嵌套的子目录）。</summary>
        private static List<string> SolutionProjectDirs(string sln)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(sln) || !File.Exists(sln) || !Regex.IsMatch(sln, @"\.slnx?$", RegexOptions.IgnoreCase)) return result;
            try
            {
                string text = File.ReadAllText(sln), baseDir = Path.GetDirectoryName(sln);
                var paths = sln.EndsWith("x", StringComparison.OrdinalIgnoreCase)
                    ? Regex.Matches(text, "<Project\\s+[^>]*Path=\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value)
                    : Regex.Matches(text, "^Project\\(\"[^\"]*\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value);
                var dirs = new List<string>();
                foreach (var p in paths)
                {
                    if (!Regex.IsMatch(p, @"\.\w*proj$", RegexOptions.IgnoreCase)) continue;
                    try
                    {
                        string d = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(baseDir, p.Replace('/', '\\'))));
                        if (Directory.Exists(d)) dirs.Add(d.TrimEnd('\\') + "\\");
                    }
                    catch { }
                }
                foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Length))
                    if (!result.Any(r => d.StartsWith(r, StringComparison.OrdinalIgnoreCase))) result.Add(d);
                // 项目就在解决方案目录时等同于全目录扫描
                if (result.Any(r => string.Equals(r.TrimEnd('\\'), baseDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) result.Clear();
            }
            catch { result.Clear(); }
            return result;
        }

        private static void Walk(DirectoryInfo dir, List<FileInfo> files, int depth)
        {
            if (files.Count >= MaxFiles || depth > 12) return;
            try
            {
                foreach (var f in dir.EnumerateFiles())
                {
                    files.Add(f);
                    if (files.Count >= MaxFiles) return;
                }
                foreach (var d in dir.EnumerateDirectories())
                {
                    if (d.Name.StartsWith(".") || SkipDirs.Contains(d.Name) || (d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    Walk(d, files, depth + 1);
                }
            }
            catch { }
        }

        private static string DirKey(string rel)
        {
            var parts = rel.Split('\\', '/');
            return parts.Length <= 1 ? "" : string.Join("\\", parts.Take(Math.Min(2, parts.Length - 1)));
        }

        private static string ProjectInfo(string path)
        {
            try
            {
                string x = File.ReadAllText(path);
                string Tag(string name) { var m = Regex.Match(x, "<" + name + ">([^<]+)</" + name + ">"); return m.Success ? m.Groups[1].Value.Trim() : null; }
                var info = new[] { Tag("TargetFramework") ?? Tag("TargetFrameworks") ?? Tag("TargetFrameworkVersion"), Tag("OutputType"), Tag("UseWPF") == "true" ? "WPF" : null, Tag("UseWindowsForms") == "true" ? "WinForms" : null }
                    .Where(s => !string.IsNullOrEmpty(s)).ToList();
                var refs = Regex.Matches(x, "<PackageReference\\s+Include=\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value)
                    .Concat(Regex.Matches(x, "<Reference\\s+Include=\"([^\",]+)").Cast<Match>().Select(m => m.Groups[1].Value)
                        .Where(n => !n.StartsWith("System", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Microsoft.CSharp") && !n.StartsWith("mscorlib")))
                    .Distinct().Take(10).ToList();
                if (refs.Count > 0) info.Add("引用：" + string.Join(", ", refs));
                return info.Count > 0 ? "（" + string.Join("；", info) + "）" : "";
            }
            catch { return ""; }
        }

        private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
