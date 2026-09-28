using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 读取 VSManager 自身日志（read_vsmanager_log）：按日志名或文件名在归档日志目录（archive\logs）与程序日志目录（logs）中定位文件，
    /// 返回路径、总行数与末尾 N 行原文（按现有规则脱敏）。只读、只接受文件名，不允许目录跳转。
    /// Reads VSManager's own logs (read_vsmanager_log): locates the file by log name or file name in the archive log folder (archive\logs)
    /// and the application log folder (logs), and returns the path, total line count and the last N lines verbatim (redacted with the existing
    /// rules). Read-only; accepts file names only, never directory traversal.
    /// </summary>
    internal static class VsManagerLogReader
    {
        internal const int DefaultLines = 200, MaxLines = 2000;

        /// <summary>
        /// 不开放的日志：与文件工具一致，AI 对话日志与文件审计日志不给模型读取。
        /// Logs that stay closed: as with the file tools, the AI conversation log and the file audit log are never read by the model.
        /// </summary>
        internal static readonly string[] Denied = { "agent.log", "file-audit.log" };

        private static readonly Regex Stamp = new Regex(@"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}", RegexOptions.CultureInvariant);

        /// <summary>默认搜索目录：归档日志目录优先，其次程序日志目录。/ Default folders: the archive log folder first, then the application log folder.</summary>
        internal static IReadOnlyList<string> DefaultFolders()
        {
            var list = new List<string>();
            void Add(string root) { if (!string.IsNullOrEmpty(root)) list.Add(Path.Combine(root, "logs")); }
            Add(Archive.Root);
            try { Add(Archive.FallbackRoot); } catch { }
            list.Add(AppPaths.LogFolder);
            return list.Where(d => !string.IsNullOrEmpty(d)).Select(d => d.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>把本机路径中的用户目录替换为环境变量，避免在输出中暴露用户名。/ Replaces user folders in a path with environment variables so outputs never expose the user name.</summary>
        internal static string FriendlyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            foreach (var pair in new[]
            {
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
            })
            {
                string root = (pair.Item1 ?? "").TrimEnd('\\');
                if (root.Length > 0 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    return pair.Item2 + path.Substring(root.Length);
            }
            return path;
        }

        /// <summary>
        /// 读取日志末尾若干行。/ Reads the tail of a log.
        /// </summary>
        /// <param name="name">日志名（tasks、send、watchdog…）或文件名（tasks.log、send-20250101.log）/ Log name (tasks, send, watchdog…) or file name.</param>
        /// <param name="lines">末尾行数，1–2000，默认 200 / Tail line count, 1–2000, default 200.</param>
        /// <param name="date">可选日期 yyyy-MM-dd，默认当天 / Optional date yyyy-MM-dd, today by default.</param>
        /// <param name="folders">搜索目录（按优先级）/ Folders to search, by priority.</param>
        /// <param name="today">当天日期 / Today's date.</param>
        /// <param name="redact">脱敏函数 / Redaction function.</param>
        /// <param name="maxChars">输出字符上限 / Output character limit.</param>
        internal static string Read(string name, int lines, string date, IReadOnlyList<string> folders, DateTime today, Func<string, string> redact, int maxChars)
        {
            name = (name ?? "").Trim().Trim('"', '\'');
            if (lines <= 0) lines = DefaultLines;
            lines = Math.Min(lines, MaxLines);
            DateTime day = today.Date;
            bool dateGiven = !string.IsNullOrWhiteSpace(date);
            if (dateGiven && !DateTime.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                return "日期格式无效，应为 yyyy-MM-dd / Invalid date, expected yyyy-MM-dd: " + date;
            if (name.Length == 0) return "请指定日志名 / Please give a log name.\n" + Available(folders);
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name.Contains("..") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "只接受日志名或文件名，不接受路径 / Only a log name or file name is accepted, not a path: " + name;
            string ext = Path.GetExtension(name);
            if (ext.Length > 0 && !ext.Equals(".log", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                return "只能读取 .log / .txt 日志 / Only .log / .txt logs can be read: " + name;

            string file = Locate(name, day, folders, out string denied);
            if (denied != null)
                return "该日志不开放给 AI 读取 / This log is not available to the AI: " + denied + "\n" + Available(folders);
            if (file == null)
                return "未找到日志 / Log not found: " + name + (dateGiven ? "（" + day.ToString("yyyy-MM-dd") + "）" : "") + "\n" + Available(folders);

            // 按天分卷的文件已是当天内容；不分天的文件在显式给出日期时只取当天的记录（续行随上一条）。
            // Per-day files already hold that day; for undated files an explicit date keeps only that day's records (continuation lines follow their entry).
            string fileName = Path.GetFileName(file);
            bool perDay = fileName.IndexOf(day.ToString("yyyyMMdd"), StringComparison.Ordinal) >= 0;
            string prefix = dateGiven && !perDay ? day.ToString("yyyy-MM-dd") : null;

            var tail = new Queue<string>(lines + 1);
            int total = 0, matched = 0;
            bool keep = prefix == null;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, new UTF8Encoding(false), true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        total++;
                        if (prefix != null && Stamp.IsMatch(line)) keep = line.StartsWith(prefix, StringComparison.Ordinal);
                        if (!keep) continue;
                        matched++;
                        tail.Enqueue(line);
                        if (tail.Count > lines) tail.Dequeue();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "日志无法读取 / Log could not be read: " + FriendlyPath(file) + "（" + ex.GetType().Name + "）";
            }

            string body;
            try { body = redact == null ? string.Join("\n", tail) : redact(string.Join("\n", tail)); }
            catch (RegexMatchTimeoutException) { return "脱敏超时，未返回内容，请减少行数 / Redaction timed out; no content returned, use fewer lines"; }

            var head = new StringBuilder();
            head.AppendLine("日志文件 / Log file: " + FriendlyPath(file));
            head.AppendLine("总行数 / Total lines: " + total + (prefix != null ? "；" + prefix + " 的行 / lines on that date: " + matched : ""));
            head.AppendLine("末尾 / Last " + tail.Count + " 行 / lines" + (prefix != null ? "（" + prefix + "）" : "") + "：");
            string text = head + body;
            if (maxChars > 200 && text.Length > maxChars)
            {
                // 超出上限时保留末尾（最新的记录）/ Over the limit: keep the end (the newest records)
                string cut = body.Substring(body.Length - (maxChars - head.Length - 80));
                int nl = cut.IndexOf('\n');
                if (nl >= 0 && nl < cut.Length - 1) cut = cut.Substring(nl + 1);
                text = head + "…（前面的行已截断，请减少 lines / earlier lines truncated; use fewer lines）\n" + cut;
            }
            return text;
        }

        /// <summary>
        /// 定位日志文件：文件名原样查找；日志名依次尝试「名称-yyyyMMdd(.n).log」（取最后分卷）与「名称.log」。
        /// Locates the log: file names are looked up as given; log names try "name-yyyyMMdd(.n).log" (last volume) and then "name.log".
        /// </summary>
        internal static string Locate(string name, DateTime day, IReadOnlyList<string> folders, out string denied)
        {
            denied = null;
            bool hasExt = Path.HasExtension(name);
            var candidates = new List<string>();
            if (hasExt) candidates.Add(name);
            else
            {
                string stem = name + "-" + day.ToString("yyyyMMdd");
                candidates.Add(stem + ".log");
                candidates.Add(name + ".log");
                candidates.Add(name + ".txt");
            }
            foreach (string c in candidates)
                if (Denied.Contains(c, StringComparer.OrdinalIgnoreCase)) { denied = c; return null; }
            foreach (string c in candidates)
            {
                foreach (string dir in folders ?? new string[0])
                {
                    string path = Path.Combine(dir, c);
                    if (!File.Exists(path)) continue;
                    if (!hasExt && c.EndsWith("-" + day.ToString("yyyyMMdd") + ".log", StringComparison.OrdinalIgnoreCase))
                        path = LastVolume(dir, Path.GetFileNameWithoutExtension(c)) ?? path;
                    return path;
                }
            }
            return null;
        }

        private static string LastVolume(string dir, string stem)
        {
            try
            {
                return Directory.GetFiles(dir, stem + ".*.log")
                    .Select(f => new { f, n = int.TryParse(Path.GetFileNameWithoutExtension(f).Substring(stem.Length + 1), out int v) ? v : 0 })
                    .Where(x => x.n > 1).OrderByDescending(x => x.n).Select(x => x.f).FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        /// <summary>列出可读的日志名（去掉日期与分卷）。/ Lists readable log names (dates and volumes removed).</summary>
        internal static string Available(IReadOnlyList<string> folders)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in folders ?? new string[0])
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        string n = Path.GetFileName(f);
                        string ext = Path.GetExtension(n);
                        if (!ext.Equals(".log", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                        if (Denied.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                        names.Add(Regex.Replace(Path.GetFileNameWithoutExtension(n), @"-\d{8}(\.\d+)?$", ""));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return "可用日志 / Available logs: " + (names.Count == 0 ? "（无 / none）" : string.Join(", ", names));
        }
    }
}
