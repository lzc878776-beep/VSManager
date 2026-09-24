using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;

namespace VSManager
{
    /// <summary>本机扫描结果；警告和达到上限必须呈现给用户。/ Local scan results; warnings and limits must be shown to the user.</summary>
    public sealed class SolutionScanResult
    {
        public List<string> Files { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public int DirectoriesVisited { get; internal set; }
        public bool LimitReached { get; internal set; }
    }

    /// <summary>有界、可取消的解决方案目录扫描；不跟随目录链接。/ Bounded, cancellable solution discovery; directory links are not followed.</summary>
    public static class SolutionDirectoryScanner
    {
        public const int DefaultDepth = 3, MaximumDepth = 3;
        public const int MaximumEntries = 50000, MaximumDirectories = 5000, MaximumResults = 2000, TimeoutSeconds = 20;
        private static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", "packages", ".vs", "TestResults", "artifacts", "dist"
        };

        public static bool IsSolutionFile(string path) =>
            string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(path), ".slnx", StringComparison.OrdinalIgnoreCase);

        /// <summary>展开环境变量并验证完整解决方案路径，不要求文件当前存在。/ Expands environment variables and validates a full solution path without requiring the file to exist yet.</summary>
        public static string ResolveSolutionPath(string value)
        {
            string path = Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"')).Trim().Trim('"');
            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 3 || root.EndsWith(":", StringComparison.Ordinal))
                throw new ArgumentException("请提供解决方案完整路径 / A fully qualified solution path is required", nameof(value));
            if (!IsSolutionFile(path)) throw new ArgumentException("仅支持 .sln / .slnx 文件 / Only .sln / .slnx files are supported", nameof(value));
            return Path.GetFullPath(path);
        }

        public static SolutionScanResult Scan(string folder, int depth = DefaultDepth, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("请提供目录路径 / A folder path is required", nameof(folder));
            if (depth < 0 || depth > MaximumDepth) throw new ArgumentOutOfRangeException(nameof(depth), "扫描深度必须为 0–3 / Scan depth must be 0–3");
            string root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')));
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录不存在或不可访问 / Folder does not exist or is inaccessible: " + root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请选择实际目录，不扫描链接目录 / Choose a physical directory; linked directories are not scanned");

            var result = new SolutionScanResult();
            var pending = new Queue<Tuple<string, int>>();
            pending.Enqueue(Tuple.Create(root, 0));
            var clock = Stopwatch.StartNew();
            int entries = 0, directories = 1;
            while (pending.Count > 0 && !result.LimitReached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds >= TimeoutSeconds) { result.LimitReached = true; break; }
                var current = pending.Dequeue();
                result.DirectoriesVisited++;
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(current.Item1))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++entries > MaximumEntries || clock.Elapsed.TotalSeconds >= TimeoutSeconds)
                        {
                            result.LimitReached = true;
                            break;
                        }
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(path); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
                        {
                            result.Warnings.Add("无法读取 / Cannot read: " + path + " — " + ex.Message);
                            continue;
                        }
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (current.Item2 >= depth || Excluded.Contains(Path.GetFileName(path))) continue;
                            if (++directories > MaximumDirectories) { result.LimitReached = true; break; }
                            pending.Enqueue(Tuple.Create(path, current.Item2 + 1));
                        }
                        else if (IsSolutionFile(path))
                        {
                            if (result.Files.Count >= MaximumResults) { result.LimitReached = true; break; }
                            result.Files.Add(path);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
                {
                    result.Warnings.Add("无法扫描目录 / Cannot scan folder: " + current.Item1 + " — " + ex.Message);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (result.LimitReached)
                result.Warnings.Add($"已达到扫描上限，结果不完整（{TimeoutSeconds} 秒、{MaximumDirectories} 个目录、{MaximumEntries} 个条目或 {MaximumResults} 个结果）；请缩小目录范围后重试 / Scan limit reached; results are incomplete ({TimeoutSeconds} seconds, {MaximumDirectories} folders, {MaximumEntries} entries or {MaximumResults} results). Choose a narrower folder and retry.");
            result.Files.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}
