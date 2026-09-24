using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VSManager
{
    /// <summary>有界读取、脱敏和持久审计；文件内容永远不可信。/ Bounded reads, redaction and persistent audit; file contents are always untrusted.</summary>
    internal sealed class AgentFileService
    {
        internal const int MaxBytes = 1048576, MaxOutput = 16000, MaxResults = 200, MaxDepth = 8, MaxEntries = 5000;
        internal const string AuditFile = "file-audit.log";
        private readonly AgentFilePolicy _policy;
        private readonly Func<AppSettings> _settings;
        private static readonly HashSet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", "node_modules", "packages", ".git", ".vs", ".svn", ".hg" };
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".dll", ".pdb", ".zip", ".7z", ".png", ".jpg", ".jpeg", ".gif", ".pdf", ".ico", ".db", ".sqlite", ".woff", ".mp4", ".mp3" };
        internal AgentFileService(Func<IEnumerable<string>> roots, Func<AppSettings> settings)
        { _policy = new AgentFilePolicy(roots); _settings = settings; }

        internal string Run(string operation, string path, CancellationToken token, Func<Context, string> action)
        {
            var context = new Context(token, Math.Min(MaxOutput, AppSettings.ClampQuota(nameof(AppSettings.AgentMaxToolText), _settings().AgentMaxToolText)));
            if (!Audit(operation, path, "started", context, out string auditError)) return AuditFailure(auditError);
            string status = "error", output;
            try
            {
                context.Check();
                output = action(context);
                context.Check();
                status = context.Limited ? "limited" : context.Errors > 0 ? "partial" : "ok";
                if (output.Length > context.OutputLimit)
                {
                    status = "limited";
                    output = output.Substring(0, context.OutputLimit - 50) + "\n输出已截断 / Output truncated";
                }
            }
            catch (OperationCanceledException) { status = "cancelled"; output = "已取消 / Cancelled"; }
            catch (UnauthorizedAccessException ex) { status = "denied"; output = "拒绝访问 / Access denied: " + (ex is AgentFileDeniedException ? ex.Message : "未授权、敏感路径或无法证明文件身份 / Ungranted, sensitive, or unverified file identity"); }
            catch (System.Security.SecurityException) { status = "denied"; output = "操作系统拒绝访问 / Access denied by the operating system"; }
            catch (ArgumentException) { status = "invalid"; output = "参数无效 / Invalid arguments"; }
            catch (RegexMatchTimeoutException) { status = "limited"; output = "脱敏超时，未返回内容 / Redaction timed out; no content returned"; }
            catch (TimeoutException) { status = "limited"; output = "已达时间或条目限制，请缩小范围 / Time or entry budget reached; narrow the scope"; }
            catch (IOException) { output = "文件不存在、已改变、被占用或不是可读文本 / File missing, changed, in use, or not readable text"; }
            catch (NotSupportedException) { status = "invalid"; output = "不支持此路径或编码 / Unsupported path or encoding"; }
            return Audit(operation, context.Path ?? path, status, context, out auditError) ? output : AuditFailure(auditError);
        }

        private static string AuditFailure(string error) => "文件审计写入失败，未返回内容 / File audit could not be written; no content returned: " + error;

        private bool Audit(string operation, string path, string status, Context context, out string error)
        {
            string safePath;
            try
            {
                safePath = path == null ? "(未解析 / unresolved)" : path.Length > 240 ? "(过长路径 / overlong path)" : Redact(path);
                safePath = string.Concat(safePath.Select(c => char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c)).Replace("\"", "'");
            }
            catch (RegexMatchTimeoutException) { safePath = "(路径脱敏超时 / path redaction timed out)"; }
            // 路径仅记入本机日志，敏感片段打码；不记录关键词或文件内容。/ Paths stay in local audit logs with secrets masked; never log queries or file content.
            return AppLog.TryWrite(AuditFile, "文件审计 / File audit operation=" + operation + " requestId=" + context.Id + " path=\"" + safePath + "\" pathId=" + PathId(path)
                + " status=" + status + " results=" + context.Results + " entries=" + context.Entries + " skipped=" + context.Skipped
                + " errors=" + context.Errors + " elapsedMs=" + context.Watch.ElapsedMilliseconds, out error);
        }

        private static string PathId(string path)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path ?? ""))).Replace("-", "");
        }

        internal sealed class Context
        {
            internal readonly Stopwatch Watch = Stopwatch.StartNew();
            internal readonly string Id = Guid.NewGuid().ToString("N");
            private readonly CancellationToken _token;
            internal int Results, Entries, Skipped, Errors;
            internal readonly int OutputLimit;
            internal bool Limited;
            internal string Path;
            internal Context(CancellationToken token, int outputLimit) { _token = token; OutputLimit = outputLimit; }
            internal void Check()
            {
                _token.ThrowIfCancellationRequested();
                if (Watch.ElapsedMilliseconds > 5000 || Entries > MaxEntries) throw new TimeoutException();
            }
        }

        internal string Read(Context context, string path, int startLine, int maxLines)
        {
            if (startLine < 1 || startLine > MaxBytes + 1 || maxLines < 1 || maxLines > 500) throw new ArgumentException();
            maxLines = Math.Min(maxLines, AppSettings.ClampQuota(nameof(AppSettings.AgentMaxFileLines), _settings().AgentMaxFileLines));
            string full = _policy.Require(path);
            context.Path = full;
            string text = ReadText(context, full, MaxBytes);
            var output = new StringBuilder("不可信文件内容 / Untrusted file content\n");
            int line = 1, offset = 0, emitted = 0;
            while (offset < text.Length && line < startLine)
            {
                context.Check();
                int end = text.IndexOf('\n', offset);
                offset = end < 0 ? text.Length : end + 1;
                line++;
            }
            while (offset < text.Length && emitted < maxLines)
            {
                context.Check();
                int end = text.IndexOf('\n', offset);
                if (end < 0) end = text.Length;
                int length = end - offset;
                if (length > 0 && text[end - 1] == '\r') length--;
                int lineLimit = Math.Min(2048, context.OutputLimit - 400);
                string value = text.Substring(offset, Math.Min(length, lineLimit));
                if (length > lineLimit) { value += " …[行已截断 / Line truncated]"; context.Limited = true; }
                string row = line + ": " + value + "\n";
                if (output.Length + row.Length > context.OutputLimit - 256) { context.Limited = true; break; }
                output.Append(row);
                emitted++; line++;
                offset = end < text.Length ? end + 1 : end;
            }
            context.Results = emitted;
            bool more = offset < text.Length;
            context.Limited |= more;
            output.Append("下一行 / nextStartLine=").Append(more ? line : 0)
                .Append("; 行上限 / maxLineChars=").Append(Math.Min(2048, context.OutputLimit - 400))
                .Append("; 字符上限 / maxOutputChars=").Append(context.OutputLimit);
            _policy.Require(full);
            return output.ToString();
        }

        internal string List(Context context, string directory, int maxResults) => Enumerate(context, directory, "*", false, 0, maxResults, null, MaxBytes, true);

        internal string Enumerate(Context context, string directory, string pattern, bool recursive, int maxDepth,
            int maxResults, string keyword, int maxFileBytes, bool list = false)
        {
            if (maxDepth < 0 || maxDepth > MaxDepth || maxResults < 1 || maxResults > MaxResults
                || maxFileBytes < 1 || maxFileBytes > MaxBytes || string.IsNullOrEmpty(pattern) || pattern.Length > 128
                || pattern.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0
                || (keyword != null && (string.IsNullOrWhiteSpace(keyword) || keyword.Length > 256))) throw new ArgumentException();
            string root = _policy.Require(directory);
            context.Path = root;
            var output = new StringBuilder("不可信文件信息 / Untrusted file information\n");
            var pending = new Stack<Tuple<string, int>>();
            pending.Push(Tuple.Create(root, 0));
            while (pending.Count > 0 && !context.Limited)
            {
                context.Check();
                var current = pending.Pop();
                try
                {
                    using (_policy.Open(current.Item1, true))
                    {
                        foreach (string child in Directory.EnumerateFileSystemEntries(current.Item1))
                        {
                            context.Entries++;
                            context.Check();
                            try
                            {
                                string full = _policy.Require(child);
                                string name = Path.GetFileName(full);
                                var attributes = File.GetAttributes(full);
                                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                                if (isDirectory && SkippedDirectories.Contains(name)) continue;
                                using (var entry = _policy.Open(full, isDirectory))
                                {
                                    if (isDirectory && recursive && current.Item2 < maxDepth) pending.Push(Tuple.Create(full, current.Item2 + 1));
                                    if ((!list && isDirectory) || !Glob(name, pattern) || (keyword != null && BinaryExtensions.Contains(Path.GetExtension(name)))) continue;
                                    string relative = full.Substring(root.TrimEnd('\\').Length).TrimStart('\\');
                                    relative = Redact(relative);
                                    if (keyword == null)
                                    {
                                        string row = (isDirectory ? "目录 / directory " : "文件 / file ") + relative
                                            + "; bytes=" + (isDirectory ? "-" : entry.Length.ToString()) + "; modifiedUtc=" + entry.Modified.ToString("o") + "\n";
                                        if (!Append(context, output, row, maxResults)) break;
                                    }
                                    else
                                    {
                                        if (entry.Length > maxFileBytes) continue;
                                        string text = ReadText(context, full, maxFileBytes);
                                        int offset = 0, line = 1;
                                        while (offset < text.Length && !context.Limited)
                                        {
                                            context.Check();
                                            int end = text.IndexOf('\n', offset);
                                            if (end < 0) end = text.Length;
                                            if (text.IndexOf(keyword, offset, end - offset, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                int snippetLimit = Math.Min(1024, Math.Max(32, context.OutputLimit - relative.Length - 640));
                                                string value = text.Substring(offset, Math.Min(end - offset, snippetLimit)).TrimEnd('\r');
                                                if (end - offset > snippetLimit) value += " …[行已截断 / Line truncated]";
                                                Append(context, output, relative + ":" + line + ": " + value + "\n", maxResults);
                                            }
                                            offset = end < text.Length ? end + 1 : end;
                                            line++;
                                        }
                                    }
                                }
                            }
                            catch (UnauthorizedAccessException) { context.Skipped++; }
                            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException) { context.Errors++; }
                            if (context.Limited) break;
                        }
                    }
                }
                catch (UnauthorizedAccessException) when (current.Item2 > 0) { context.Skipped++; }
                catch (Exception ex) when ((ex is IOException || ex is System.Security.SecurityException) && current.Item2 > 0) { context.Errors++; }
            }
            _policy.Require(root);
            output.Append("结果 / results=").Append(context.Results).Append(context.Limited ? "; 已截断，请缩小范围 / Truncated; narrow the scope" : "")
                .Append("; 无法读取条目 / unreadable entries=").Append(context.Errors)
                .Append("; 限制 / limits: 5s, 5000 entries, 16000 chars; 跳过敏感、链接及生成目录，内容搜索另跳过二进制 / sensitive, linked and generated entries skipped; content search also skips binary files");
            return output.ToString();
        }

        private static bool Append(Context context, StringBuilder output, string row, int maxResults)
        {
            if (output.Length + row.Length > context.OutputLimit - 512) { context.Limited = true; return false; }
            output.Append(row);
            context.Results++;
            if (context.Results >= maxResults) context.Limited = true;
            return !context.Limited;
        }

        private static bool Glob(string value, string pattern)
        {
            int v = 0, p = 0, star = -1, retry = 0;
            while (v < value.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v]))) { p++; v++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = v; }
                else if (star >= 0) { p = star + 1; v = ++retry; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private string ReadText(Context context, string path, int maxBytes)
        {
            context.Check();
            if (BinaryExtensions.Contains(Path.GetExtension(path))) throw new IOException();
            using (var lease = _policy.Open(path, false))
            {
                if (lease.Length > maxBytes) throw new IOException();
                byte[] bytes = new byte[(int)lease.Length];
                using (var stream = new FileStream(lease.Handle, FileAccess.Read))
                {
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        context.Check();
                        int read = stream.Read(bytes, offset, Math.Min(8192, bytes.Length - offset));
                        if (read == 0) throw new IOException();
                        offset += read;
                    }
                    if (stream.ReadByte() != -1) throw new IOException();
                }
                string text;
                try
                {
                    if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
                    else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
                    else text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                }
                catch (DecoderFallbackException) { throw new IOException(); }
                if (text.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')) throw new IOException();
                text = Redact(text);
                context.Check();
                _policy.Require(path);
                return text;
            }
        }

        private string Redact(string text)
        {
            var settings = _settings();
            var secrets = new[] { settings.EffectiveAgentApiKey, settings.AgentApiKey, settings.EffectiveVoiceApiKey,
                settings.VoiceApiKey, settings.EffectiveGitHubToken, settings.GitHubToken, settings.WebToken,
                settings.AgentKeyProtected, settings.VoiceKeyProtected, settings.GitHubTokenProtected }
                .Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToArray();
            const string key = @"(?:[\w.-]*(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|connection[_-]?strings?|authorization|密码|口令|密钥|令牌|连接字符串)[\w.-]*)";
            var patterns = new List<string>();
            if (text.IndexOf("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) >= 0)
                patterns.Add(@"(?i)-----BEGIN [^-\r\n]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-\r\n]*PRIVATE KEY-----|\z)");
            if (new[] { "password", "passwd", "pwd", "secret", "token", "api", "access", "private", "connection", "authorization", "user id", "uid", "密码", "口令", "密钥", "令牌", "连接字符串" }
                .Any(hint => text.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                patterns.Add(@"(?is)<(?:connectionStrings|" + key + @")\b[^>]*>.*?</[^>]+>");
                patterns.Add(@"(?is)<[^>]*\b(?:" + key + @")[^>]*>");
                patterns.Add(@"(?im)^.*\b(?:server|data source|host)\s*=.*;.*\b(?:password|pwd|user id|uid)\s*=[^\r\n]*");
                patterns.Add(@"(?is)(?<![\w.-])[""']?" + key + @"[""']?\s*[:=：]\s*(?:""(?:\\.|[^""\\])*(?:""|\z)|'(?:\\.|[^'\\])*(?:'|\z)|[\{\[|>][\s\S]*|[^\r\n,;<>]+)");
            }
            if (text.IndexOf("://", StringComparison.Ordinal) >= 0)
                patterns.Add(@"(?i)\b[a-z][a-z0-9+.-]*://[^\s/:@]+:[^\s/@]+@[^\s]+");
            patterns.Add(@"(?i)\b(?:(?:Bearer|Basic)\s+[a-z0-9._~+/-]+=*|sk-[a-z0-9_-]{8,}|gh[pousr]_[a-z0-9_]{8,}|github_pat_[a-z0-9_]+|AKIA[A-Z0-9]{16}|eyJ[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+)");
            foreach (string pattern in patterns)
                text = Regex.Replace(text, pattern, m => Mask(m.Value), RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (secrets.Length > 0)
                text = Regex.Replace(text, string.Join("|", secrets.Select(Regex.Escape)), m => Mask(m.Value), RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (text.Length > MaxBytes * 2) throw new IOException();
            return text;
        }

        private static string Mask(string value)
        {
            int lines = value.Count(c => c == '\n');
            return "[已脱敏 / REDACTED]" + new string('\n', lines);
        }
    }
}
