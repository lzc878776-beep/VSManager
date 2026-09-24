using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    public sealed partial class AgentService
    {
        private IEnumerable<string> FileRoots()
        {
            var settings = _settings();
            var roots = new List<string>(settings.AgentFileRoots ?? new List<string>());
            if (settings.AgentIncludeSolutionRoots)
                foreach (var solution in _host.Solutions.Items)
                {
                    try
                    {
                        string path = AgentFilePolicy.Canonical(solution.Path);
                        if (SolutionDirectoryScanner.IsSolutionFile(path)) roots.Add(Path.GetDirectoryName(path));
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is UnauthorizedAccessException || ex is NotSupportedException) { }
                }
            return roots;
        }

        private AgentFileService Files => new AgentFileService(FileRoots, _settings);

        [Description("按文件名查找授权目录；只读、跳过敏感/链接/生成目录。默认深度3、结果100；硬上限深度8、结果200、5秒/5000条目/16000字符。/ Find filenames in granted directories; read-only, skips sensitive/linked/generated entries. Defaults depth 3/results 100; caps depth 8/results 200/5 seconds/5000 entries/16000 characters.")]
        internal Task<string> FindFiles(
            [Description("已授权的绝对本机目录 / Granted absolute local directory")] string directory,
            [Description("文件名通配符 * 和 ?，默认 * / Filename glob (* and ?), default *")] string pattern = "*",
            [Description("是否递归，默认 true / Recurse, default true")] bool recursive = true,
            [Description("子目录深度0–8，默认3 / Subdirectory depth 0–8, default 3")] int maxDepth = 3,
            [Description("结果上限1–200，默认100 / Result limit 1–200, default 100")] int maxResults = 100,
            CancellationToken cancellationToken = default)
        {
            var files = Files;
            return Task.Run(() => files.Run("find_files", directory, cancellationToken,
                c => files.Enumerate(c, directory, pattern, recursive, maxDepth, maxResults, null, AgentFileService.MaxBytes)));
        }

        [Description("在授权文本文件中查找字面关键词（忽略大小写），先整文件脱敏再匹配；不回显关键词。默认文件上限1MiB（硬上限），默认深度3/结果100；硬上限深度8/结果200/5秒/5000条目/16000字符，每个匹配行最多1024字符。/ Search literal text case-insensitively after whole-file redaction; never echoes the query. File cap/default 1MiB, default depth 3/results 100; caps depth 8/results 200/5 seconds/5000 entries/16000 characters, 1024 characters per matching line.")]
        internal Task<string> SearchFileContents(
            [Description("已授权的绝对本机目录 / Granted absolute local directory")] string directory,
            [Description("字面关键词1–256字符，不支持正则 / Literal keyword 1–256 characters; not a regex")] string keyword,
            [Description("文件名通配符，默认 * / Filename glob, default *")] string filePattern = "*",
            [Description("是否递归，默认 true / Recurse, default true")] bool recursive = true,
            [Description("子目录深度0–8，默认3 / Subdirectory depth 0–8, default 3")] int maxDepth = 3,
            [Description("匹配行上限1–200，默认100 / Matching line limit 1–200, default 100")] int maxResults = 100,
            [Description("单文件字节上限1–1048576，默认1048576 / Per-file byte limit 1–1048576, default 1048576")] int maxFileBytes = 1048576,
            CancellationToken cancellationToken = default)
        {
            var files = Files;
            return Task.Run(() => files.Run("search_file_contents", directory, cancellationToken,
                c => { if (keyword == null) throw new ArgumentException(); return files.Enumerate(c, directory, filePattern, recursive, maxDepth, maxResults, keyword, maxFileBytes); }));
        }

        [Description("读取授权文本文件并脱敏、附行号和 nextStartLine（0表示结束）；默认200行，最多500行/1MiB/16000字符，单行最多2048字符。截断长行时下一页从下一行继续。/ Read granted text with redaction, line numbers and nextStartLine (0=end); default 200 lines, caps 500 lines/1MiB/16000 characters, 2048 per line. Truncated long lines resume at the following line.")]
        internal Task<string> ReadFile(
            [Description("已授权的绝对本机文件路径 / Granted absolute local file path")] string path,
            [Description("起始行，从1开始 / First line, one-based")] int startLine = 1,
            [Description("行数1–500，默认200 / Line count 1–500, default 200")] int maxLines = 200,
            CancellationToken cancellationToken = default)
        {
            var files = Files;
            return Task.Run(() => files.Run("read_file", path, cancellationToken, c => files.Read(c, path, startLine, maxLines)));
        }

        [Description("只列授权目录的直接子项，含类型、字节数和UTC修改时间；不显示敏感/链接/生成目录，二进制仅列元数据。默认/最多200项，最多5秒/5000条目/16000字符。/ List immediate granted children with type, bytes and UTC modification time; hides sensitive/linked/generated entries, showing metadata only for binary files. Default/cap 200 results, caps 5 seconds/5000 entries/16000 characters.")]
        internal Task<string> ListDirectory(
            [Description("已授权的绝对本机目录 / Granted absolute local directory")] string directory,
            [Description("结果上限1–200，默认200 / Result limit 1–200, default 200")] int maxResults = 200,
            CancellationToken cancellationToken = default)
        {
            var files = Files;
            return Task.Run(() => files.Run("list_directory", directory, cancellationToken, c => files.List(c, directory, maxResults)));
        }

        private string LegacyFileRoot(string vs, string folder)
        {
            if (!Resolve(vs, out var target, out _)) throw new ArgumentException();
            string root = string.IsNullOrWhiteSpace(folder) ? CodeScanner.Root(target.SolutionPath) : folder;
            return new AgentFilePolicy(FileRoots).Require(root);
        }

        [Description("通过相同白名单、脱敏和审计列出解决方案文件元数据；不再使用嵌套代码扫描器。/ List solution file metadata through the same grants, redaction and audit; no nested legacy scanner.")]
        private Task<string> ScanVsCode(
            [Description("VS 编号或名称 / VS number or name")] string vs,
            [Description("可选已授权绝对目录；不会自动授权 / Optional granted absolute directory; never creates a grant")] string folder = "",
            CancellationToken cancellationToken = default)
        {
            var files = Files;
            return Task.Run(() => files.Run("scan_vs_code", folder, cancellationToken,
                c => files.Enumerate(c, LegacyFileRoot(vs, folder), "*", true, 3, 100, null, AgentFileService.MaxBytes)));
        }

        [Description("通过相同白名单、脱敏和审计读取解决方案文件或列目录；folder不能绕过授权。/ Read a solution file or list a directory through the same grants, redaction and audit; folder never bypasses authorization.")]
        private Task<string> ReadVsFile(
            [Description("VS 编号或名称 / VS number or name")] string vs,
            [Description("相对解决方案的文件或目录路径 / File or directory path relative to the solution")] string path,
            [Description("起始行，从1开始 / First line, one-based")] int startLine = 1,
            [Description("可选已授权绝对目录；不会自动授权 / Optional granted absolute directory; never creates a grant")] string folder = "",
            CancellationToken cancellationToken = default)
        {
            var files = Files;
            return Task.Run(() => files.Run("read_vs_file", path, cancellationToken, c =>
            {
                string root = LegacyFileRoot(vs, folder);
                if (string.IsNullOrEmpty(path)) throw new ArgumentException();
                string full = AgentFilePolicy.Canonical(Path.Combine(root, path.Replace('/', '\\')));
                if (!AgentFilePolicy.Within(full, root)) throw new UnauthorizedAccessException();
                return Directory.Exists(full) ? files.List(c, full, 200) : files.Read(c, full, startLine, Math.Min(500, MaxFileLines));
            }));
        }
    }
}
