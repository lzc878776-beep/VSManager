using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VSManager
{
    /// <summary>
    /// 敏感信息自检的一条命中。
    /// A single hit of the pre-publish sensitive-content check.
    /// </summary>
    public sealed class PublishFinding
    {
        public string File;
        public int Line;
        public string Rule;
        public string Text;
        public override string ToString() => File + ":" + Line + "  [" + Rule + "]  " + Text;
    }

    /// <summary>
    /// 发布参数（由设置项生成）。
    /// Publish options (built from the settings).
    /// </summary>
    public sealed class PublishOptions
    {
        public string RepoPath;
        public string Owner;
        public string RepoName;
        public bool Private;
        public string Branch;
        public string AuthorName;
        public string AuthorEmail;
        public string CommitMessage;
        public string Token;
        /// <summary>本机已配置的密钥（仅用于自检比对，不会输出）。/ Local secrets, used only for matching and never printed.</summary>
        public List<string> KnownSecrets = new List<string>();
    }

    public sealed class PublishResult
    {
        public bool Ok;
        public bool Aborted;
        public string Url;
        public string Error;
        public string Hint;
        public List<PublishFinding> Findings = new List<PublishFinding>();
    }

    /// <summary>
    /// 发布前的敏感信息自检：盘符绝对路径、用户名、机器名、邮箱、Key/Token/Secret、内部项目名。
    /// Pre-publish scan for absolute drive paths, user/machine names, e-mails, keys/tokens/secrets and internal project names.
    /// </summary>
    public static class PublishScanner
    {
        /// <summary>
        /// 内部项目名等自定义词表（每行一个，# 开头为注释），保存在本机配置目录，不进入仓库。
        /// Custom term list (one per line, # for comments), kept in the local config folder and never committed.
        /// </summary>
        public static string TermsFile => Path.Combine(AppPaths.DataFolder, "publish-scan-terms.txt");

        private const long MaxFileBytes = 5L * 1024 * 1024;
        private const int MaxFindingsPerFile = 50;

        private static readonly HashSet<string> BinaryExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ico", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".dll", ".exe", ".pdb", ".zip", ".7z", ".nupkg", ".snk", ".pfx", ".mp3", ".wav", ".ttf", ".woff", ".woff2"
        };

        private static readonly Regex DrivePath = new Regex(@"(?<![A-Za-z0-9_%$])[A-Za-z]:(\\\\|\\|/)(?=[A-Za-z0-9_.\u4e00-\u9fff])", RegexOptions.Compiled);
        private static readonly Regex Email = new Regex(@"(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
        private static readonly Regex KnownKey = new Regex(
            @"(sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|xox[abprs]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----)",
            RegexOptions.Compiled);
        private static readonly Regex SecretAssign = new Regex(
            @"(?i)(api[_-]?key|secret|token|password|passwd|pwd|access[_-]?key)[A-Za-z_]*[""']?\s*[:=]\s*[""']([^""'\s]{16,})[""']",
            RegexOptions.Compiled);

        /// <summary>读取自定义词表；文件不存在时创建一个只含说明的模板。/ Loads the custom terms; creates a commented template when missing.</summary>
        public static List<string> LoadTerms()
        {
            try
            {
                if (!File.Exists(TermsFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(TermsFile));
                    File.WriteAllText(TermsFile,
                        "# 发布前自检词表：每行一个词（不区分大小写），命中即提示。用于内部项目名、客户名等不应公开的内容。\r\n" +
                        "# Publish scan terms: one per line (case-insensitive). Use it for internal project / customer names that must not be published.\r\n",
                        new UTF8Encoding(false));
                }
                return File.ReadAllLines(TermsFile, Encoding.UTF8)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        private sealed class Rule
        {
            public string Name;
            public Func<string, IEnumerable<string>> Match;
            public bool Secret;
        }

        private static List<Rule> BuildRules(IEnumerable<string> knownSecrets)
        {
            var rules = new List<Rule>
            {
                new Rule { Name = "盘符绝对路径 / Absolute path", Match = l => DrivePath.Matches(l).Cast<Match>().Select(m => m.Value) },
                new Rule
                {
                    Name = "邮箱 / E-mail",
                    Match = l => Email.Matches(l).Cast<Match>().Select(m => m.Value).Where(v => !IsAllowedEmail(v))
                },
                new Rule { Name = "Key/Token/Secret", Secret = true, Match = l => KnownKey.Matches(l).Cast<Match>().Select(m => m.Value) },
                new Rule
                {
                    Name = "疑似密钥赋值 / Secret assignment", Secret = true,
                    Match = l => SecretAssign.Matches(l).Cast<Match>().Select(m => m.Groups[2].Value).Where(v => !IsPlaceholder(v))
                },
            };
            foreach (var name in new[] { SafeEnv(() => Environment.UserName), SafeEnv(() => Environment.MachineName) }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length < 3) continue;
                var re = new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(name) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
                bool isUser = string.Equals(name, SafeEnv(() => Environment.UserName), StringComparison.OrdinalIgnoreCase);
                rules.Add(new Rule { Name = isUser ? "用户名 / User name" : "机器名 / Machine name", Match = l => re.Matches(l).Cast<Match>().Select(m => m.Value) });
            }
            var secrets = knownSecrets.Where(s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 8).Select(s => s.Trim()).Distinct().ToList();
            if (secrets.Count > 0)
                rules.Add(new Rule { Name = "本机已配置的密钥 / Configured secret", Secret = true, Match = l => secrets.Where(s => l.Contains(s)) });
            var terms = LoadTerms();
            if (terms.Count > 0)
            {
                var re = new Regex(string.Join("|", terms.Select(Regex.Escape)), RegexOptions.IgnoreCase);
                rules.Add(new Rule { Name = "内部名称 / Internal term", Match = l => re.Matches(l).Cast<Match>().Select(m => m.Value) });
            }
            return rules;
        }

        private static string SafeEnv(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

        private static bool IsAllowedEmail(string v)
        {
            v = v.ToLowerInvariant();
            return v.EndsWith("@users.noreply.github.com") || v.EndsWith("@example.com") || v.EndsWith("@example.org") || v.StartsWith("git@");
        }

        private static bool IsPlaceholder(string v)
        {
            string l = v.ToLowerInvariant();
            if (Regex.IsMatch(v, "^[A-Z0-9_]+$")) return true;   // 环境变量名等常量 / env-var names and similar constants
            return l.Contains("your") || l.Contains("xxx") || l.Contains("<") || l.Contains("{") || l.Contains("$") || l.Contains("%") ||
                   l.Contains("example") || l.Contains("placeholder") || l.All(c => c == '*' || c == 'x' || c == '0');
        }

        private static string Mask(string v) => v.Length <= 6 ? "***" : v.Substring(0, 4) + "***";

        /// <summary>扫描一段文本（label 为显示用的文件名）。/ Scans text; label is the file name shown in the report.</summary>
        public static List<PublishFinding> ScanText(string label, string text, IEnumerable<string> knownSecrets)
            => ScanLines(label, (text ?? "").Replace("\r\n", "\n").Split('\n'), BuildRules(knownSecrets ?? new string[0]));

        private static List<PublishFinding> ScanLines(string label, string[] lines, List<Rule> rules)
        {
            var list = new List<PublishFinding>();
            for (int i = 0; i < lines.Length && list.Count < MaxFindingsPerFile; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                foreach (var r in rules)
                {
                    var hits = r.Match(line).Distinct().ToList();
                    if (hits.Count == 0) continue;
                    string shown = line.Trim();
                    if (r.Secret) foreach (var h in hits) shown = shown.Replace(h, Mask(h));
                    foreach (var s in rules.Where(x => x.Secret)) foreach (var h in s.Match(shown).ToList()) shown = shown.Replace(h, Mask(h));
                    if (shown.Length > 160) shown = shown.Substring(0, 157) + "…";
                    list.Add(new PublishFinding { File = label, Line = i + 1, Rule = r.Name, Text = shown });
                }
            }
            return list;
        }

        /// <summary>
        /// 本机私有数据文件（设置、任务清单、AI 对话记录、自检词表、归档）：即使绕过 .gitignore 被加入提交，也会被自检拦下。
        /// Local private data files (settings, task list, AI chat history, scan terms, archives): blocked by the scan even if
        /// they bypass .gitignore.
        /// </summary>
        private static readonly Regex PrivateFile = new Regex(
            @"(^|/)(settings\.json(\.bak|\.corrupt-.*)?|tasks\.json(\.bak)?|solutions\.json(\.bak|\.corrupt-.*)?|agent-chat[^/]*|publish-scan-terms\.txt|[^/]+\.jsonl)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>是否为不得提交的本机私有数据文件。/ Whether the path is a local private data file that must never be committed.</summary>
        public static bool IsPrivateFile(string relativePath) => PrivateFile.IsMatch((relativePath ?? "").Replace('\\', '/'));

        /// <summary>扫描仓库中的待提交文件（相对路径）。/ Scans the files (relative paths) that are about to be committed.</summary>
        public static List<PublishFinding> ScanFiles(string root, IEnumerable<string> relativeFiles, IEnumerable<string> knownSecrets, out int scanned)
        {
            var rules = BuildRules(knownSecrets ?? new string[0]);
            var all = new List<PublishFinding>();
            scanned = 0;
            foreach (var rel in relativeFiles)
            {
                if (IsPrivateFile(rel))
                {
                    all.Add(new PublishFinding { File = rel, Line = 0, Rule = "本机私有数据文件 / Local private data file", Text = "该文件只应保存在本机，请从提交中移除 / Keep it local; remove it from the commit" });
                    continue;
                }
                try
                {
                    string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (BinaryExt.Contains(Path.GetExtension(rel)) || !File.Exists(full)) continue;
                    var fi = new FileInfo(full);
                    if (fi.Length > MaxFileBytes) continue;
                    var bytes = File.ReadAllBytes(full);
                    if (bytes.Take(8000).Any(b => b == 0)) continue;
                    scanned++;
                    string text = Encoding.UTF8.GetString(bytes);
                    all.AddRange(ScanLines(rel, text.Replace("\r\n", "\n").Split('\n'), rules));
                }
                catch { }
            }
            return all;
        }
    }

    /// <summary>
    /// 发布到 GitHub：git init → 更新 .gitignore → 自检 → git add → 提交 → 创建 / 关联远程仓库 → push。
    /// Token 只在内存中使用：通过环境变量注入 HTTP 头传给 git，不写入远程地址、.git/config、日志或界面。
    /// Publish to GitHub: git init → update .gitignore → scan → git add → commit → create/link remote → push.
    /// The token stays in memory: it is passed to git as an HTTP header via environment variables and is never written to
    /// the remote URL, .git/config, logs or the UI.
    /// </summary>
    public sealed class GitHubPublisher
    {
        /// <summary>.gitignore 中必须包含的条目。/ Entries that must be present in .gitignore.</summary>
        public static readonly string[] RequiredIgnores =
        {
            "bin/", "obj/", "dist/", "publish/", ".vs/", "*.user",
            "settings.json", "settings.json.bak", "settings.json.corrupt-*", "tasks.json", "tasks.json.bak",
            "solutions.json", "solutions.json.bak", "solutions.json.corrupt-*",
            "*.log", "logs/", "archive/", "Archive/", "*.jsonl", "agent-chat*"
        };

        public static string LogFile => Path.Combine(AppPaths.LogFolder, "publish-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

        private static string Api = "https://api.github.com";
        private readonly PublishOptions _o;
        private readonly Action<string> _log;
        private readonly Func<List<PublishFinding>, bool> _confirm;
        private string _authHeader;

        public GitHubPublisher(PublishOptions options, Action<string> log, Func<List<PublishFinding>, bool> confirmFindings)
        {
            _o = options;
            _log = log ?? (s => { });
            _confirm = confirmFindings ?? (f => false);
        }

        /// <summary>
        /// 查找要发布的目录：配置优先，否则从程序目录向上查找 VSManager.csproj；
        /// 项目位于 src\VSManager\ 等子目录时，继续向上找到包含 VSManager.slnx（或 .git）的仓库根目录。
        /// Resolves the folder to publish: the configured one, otherwise walks up from the executable to find VSManager.csproj;
        /// when the project lives in a subfolder such as src\VSManager\, keeps walking up to the repository root that contains
        /// VSManager.slnx (or .git).
        /// </summary>
        public static string ResolveRepoPath(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim())).TrimEnd('\\'); } catch { return configured.Trim(); }
            }
            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
                {
                    if (!File.Exists(Path.Combine(dir.FullName, "VSManager.csproj"))) continue;
                    var root = dir;
                    for (int j = 0; root != null && j < 4; j++, root = root.Parent)
                        if (File.Exists(Path.Combine(root.FullName, "VSManager.slnx")) || Directory.Exists(Path.Combine(root.FullName, ".git")))
                            return root.FullName.TrimEnd('\\');
                    return dir.FullName.TrimEnd('\\');
                }
            }
            catch { }
            return "";
        }

        #region 日志 / Logging

        private string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (!string.IsNullOrEmpty(_o.Token)) s = s.Replace(_o.Token, "***");
            if (!string.IsNullOrEmpty(_authHeader)) s = s.Replace(_authHeader, "***");
            foreach (var k in _o.KnownSecrets) if (!string.IsNullOrWhiteSpace(k) && k.Length >= 8) s = s.Replace(k, "***");
            return Regex.Replace(s, @"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|x-access-token:[^@\s]+)", "***");
        }

        private void Log(string msg)
        {
            msg = Redact(msg);
            try { _log(msg); } catch { }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }

        #endregion

        #region git

        private sealed class GitResult
        {
            public int Code;
            public string Out = "", Err = "";
            public bool Ok => Code == 0;
            public string Text => (Err + "\n" + Out).Trim();
        }

        private GitResult Git(string args, int timeoutMs = 60000, bool auth = false, string workDir = null)
        {
            var psi = new ProcessStartInfo("git", "-c core.quotepath=false " + args)
            {
                WorkingDirectory = workDir ?? _o.RepoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            if (auth && _authHeader != null)
            {
                // 通过环境变量注入配置（git ≥ 2.31），不出现在命令行与 .git/config 中
                // Inject config via environment variables (git >= 2.31) so it never appears on the command line or in .git/config
                psi.EnvironmentVariables["GIT_CONFIG_COUNT"] = "2";
                psi.EnvironmentVariables["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
                psi.EnvironmentVariables["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + _authHeader;
                psi.EnvironmentVariables["GIT_CONFIG_KEY_1"] = "credential.helper";
                psi.EnvironmentVariables["GIT_CONFIG_VALUE_1"] = "";
            }
            var r = new GitResult();
            Process started;
            try { started = Process.Start(psi); }
            catch (Exception ex) { r.Code = -2; r.Err = ex.Message; return r; }
            using (var p = started)
            {
                try { p.StandardInput.Close(); } catch { }
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    r.Code = -1;
                    r.Err = "git 超时 / git timed out: " + args.Split(' ')[0];
                    return r;
                }
                p.WaitForExit();
                r.Code = p.ExitCode;
                r.Out = o.Result ?? "";
                r.Err = e.Result ?? "";
            }
            return r;
        }

        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

        #endregion

        #region GitHub API

        private int Http(string method, string url, string body, out Dictionary<string, object> json, out string raw)
        {
            json = null;
            raw = "";
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "VSManager-Publisher";
            req.Accept = "application/vnd.github+json";
            req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            req.Headers["Authorization"] = "Bearer " + _o.Token;
            req.Timeout = 30000;
            req.ServicePoint.Expect100Continue = false;
            if (body != null)
            {
                var b = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex) when (ex.Response is HttpWebResponse er) { resp = er; }
            using (resp)
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                raw = sr.ReadToEnd();
                try { json = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>; } catch { }
                return (int)resp.StatusCode;
            }
        }

        private static string Str(Dictionary<string, object> j, string key) => j != null && j.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) : "";

        private static string ApiMessage(Dictionary<string, object> j, string raw)
        {
            string m = Str(j, "message");
            if (j != null && j.TryGetValue("errors", out var e) && e is object[] arr && arr.Length > 0 && arr[0] is Dictionary<string, object> first)
                m += "（" + Str(first, "message") + "）";
            return m.Length > 0 ? m : (raw ?? "").Trim();
        }

        #endregion

        private PublishResult Fail(PublishResult r, string error, string hint)
        {
            r.Ok = false;
            r.Error = Redact(error);
            r.Hint = hint;
            Log("✗ " + r.Error);
            if (!string.IsNullOrEmpty(hint)) Log("  建议 / Hint: " + hint);
            return r;
        }

        /// <summary>
        /// 只做自检（不修改仓库）。/ Scan only, without modifying the repository.
        /// </summary>
        public PublishResult ScanOnly()
        {
            var r = new PublishResult();
            try
            {
                if (!Git("--version", 15000, workDir: Path.GetTempPath()).Ok) return Fail(r, "未找到 git / git not found", "安装 Git for Windows 并确保 git 在 PATH 中 / Install Git for Windows and add it to PATH");
                if (!Directory.Exists(_o.RepoPath)) return Fail(r, "目录不存在 / Folder not found: " + _o.RepoPath, "在发布窗口中设置本地仓库目录 / Set the local repository folder");
                r.Findings = ScanCandidates(out int n);
                Log($"自检完成：扫描 {n} 个文件，命中 {r.Findings.Count} 处 / Scan done: {n} files, {r.Findings.Count} hit(s)");
                foreach (var f in r.Findings) Log("  " + f);
                r.Ok = r.Findings.Count == 0;
                return r;
            }
            catch (Exception ex) { return Fail(r, "自检失败 / Scan failed: " + ex.Message, null); }
        }

        private List<string> CandidateFiles()
        {
            bool isRepo = IsOwnRepo();
            if (isRepo)
            {
                var g = Git("ls-files -z --cached --others --exclude-standard");
                if (g.Ok) return g.Out.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            // 未初始化时按 .gitignore 规则在临时索引中列出 / Not a repo yet: list via a throw-away git dir honoring .gitignore
            string tmp = Path.Combine(Path.GetTempPath(), "vsm-publish-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (!Git("init -q " + Q(tmp), 30000, workDir: Path.GetTempPath()).Ok) return new List<string>();
                var g = Git("--git-dir=" + Q(Path.Combine(tmp, ".git")) + " --work-tree=" + Q(_o.RepoPath) + " ls-files -z --others --exclude-standard");
                return g.Ok ? g.Out.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        private List<PublishFinding> ScanCandidates(out int scanned)
        {
            var files = CandidateFiles();
            var list = PublishScanner.ScanFiles(_o.RepoPath, files, _o.KnownSecrets, out scanned);
            list.AddRange(PublishScanner.ScanText("<提交信息 / commit message>", _o.CommitMessage, _o.KnownSecrets));
            list.AddRange(PublishScanner.ScanText("<提交作者 / commit author>", (_o.AuthorName ?? "") + " " + (_o.AuthorEmail ?? ""), _o.KnownSecrets));
            return list;
        }

        private bool IsOwnRepo()
        {
            if (!Directory.Exists(Path.Combine(_o.RepoPath, ".git")) && !File.Exists(Path.Combine(_o.RepoPath, ".git"))) return false;
            var g = Git("rev-parse --show-toplevel");
            if (!g.Ok) return false;
            try
            {
                return string.Equals(Path.GetFullPath(g.Out.Trim().Replace('/', '\\')).TrimEnd('\\'), Path.GetFullPath(_o.RepoPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>补齐 .gitignore 中缺少的条目，返回新增条数。/ Appends missing .gitignore entries; returns how many were added.</summary>
        public static int EnsureGitIgnore(string root)
        {
            string path = Path.Combine(root, ".gitignore");
            string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            var existing = new HashSet<string>(text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()), StringComparer.Ordinal);
            var missing = RequiredIgnores.Where(e => !existing.Contains(e)).ToList();
            if (missing.Count == 0) return 0;
            var sb = new StringBuilder(text);
            if (sb.Length > 0 && !text.EndsWith("\n")) sb.Append("\r\n");
            sb.Append("\r\n# 由 VSManager 发布功能补充：本地数据与构建输出不入库\r\n# Added by VSManager publish: keep local data and build output out of the repository\r\n");
            foreach (var m in missing) sb.Append(m).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return missing.Count;
        }

        /// <summary>执行完整发布流程（在后台线程调用）。/ Runs the full publish flow (call from a background thread).</summary>
        public PublishResult Run()
        {
            var r = new PublishResult();
            try { return RunCore(r); }
            catch (WebException ex) { return Fail(r, "访问 GitHub 失败 / GitHub request failed: " + ex.Message, "检查网络或代理后重试；已完成的本地提交会保留 / Check the network or proxy and retry; local commits are kept"); }
            catch (Exception ex) { return Fail(r, "发布异常 / Unexpected error: " + ex.Message, "查看发布日志 / See the publish log: " + LogFile); }
        }

        private PublishResult RunCore(PublishResult r)
        {
            string owner = (_o.Owner ?? "").Trim(), name = (_o.RepoName ?? "").Trim(), branch = string.IsNullOrWhiteSpace(_o.Branch) ? "main" : _o.Branch.Trim();

            // 1. git
            Log("① 检查 git / Checking git…");
            var ver = Git("--version", 15000, workDir: Path.GetTempPath());
            if (!ver.Ok) return Fail(r, "未找到 git / git not found", "安装 Git for Windows（https://git-scm.com）并确保 git 在 PATH 中 / Install Git for Windows and add it to PATH");
            Log("  " + ver.Out.Trim());
            var vm = Regex.Match(ver.Out, @"(\d+)\.(\d+)");
            if (vm.Success && (int.Parse(vm.Groups[1].Value) < 2 || (int.Parse(vm.Groups[1].Value) == 2 && int.Parse(vm.Groups[2].Value) < 31)))
                return Fail(r, "git 版本过低 / git is too old: " + ver.Out.Trim(), "升级到 git 2.31 或更高版本 / Upgrade to git 2.31 or later");

            if (string.IsNullOrWhiteSpace(_o.RepoPath) || !Directory.Exists(_o.RepoPath))
                return Fail(r, "本地仓库目录不存在 / Local folder not found: " + _o.RepoPath, "在发布窗口中设置本地仓库目录 / Set the local repository folder");
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9._-]{1,100}$"))
                return Fail(r, "仓库名无效 / Invalid repository name: " + name, "仅使用字母、数字、. _ - / Use letters, digits, '.', '_' or '-'");
            if (!Regex.IsMatch(branch, @"^[A-Za-z0-9._/-]{1,100}$"))
                return Fail(r, "分支名无效 / Invalid branch name: " + branch, "例如 main / e.g. main");
            if (string.IsNullOrWhiteSpace(_o.Token))
                return Fail(r, "未配置 GitHub Token / GitHub token is not configured",
                    "在发布窗口填写 Token，或设置环境变量 " + AppSettings.GitHubTokenEnvVar + " / Enter a token or set " + AppSettings.GitHubTokenEnvVar);
            _authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:" + _o.Token.Trim()));

            // 2. token
            Log("② 验证 GitHub Token / Verifying token…");
            int st;
            Dictionary<string, object> j;
            string raw;
            try { st = Http("GET", Api + "/user", null, out j, out raw); }
            catch (Exception ex) { return Fail(r, "无法连接 GitHub / Cannot reach GitHub: " + ex.Message, "检查网络或代理设置 / Check the network or proxy"); }
            if (st == 401) return Fail(r, "Token 无效或已过期 / Token is invalid or expired", "在 GitHub → Settings → Developer settings 重新生成 Token / Regenerate the token on GitHub");
            if (st != 200) return Fail(r, $"GitHub API 返回 {st} / GitHub API returned {st}: " + ApiMessage(j, raw), null);
            string login = Str(j, "login"), uid = Str(j, "id");
            if (owner.Length == 0) owner = login;
            Log("  已登录 / Signed in as " + login);
            string email = string.IsNullOrWhiteSpace(_o.AuthorEmail) ? uid + "+" + login + "@users.noreply.github.com" : _o.AuthorEmail.Trim();
            string author = string.IsNullOrWhiteSpace(_o.AuthorName) ? "VSManager contributors" : _o.AuthorName.Trim();
            _o.AuthorEmail = email;
            _o.AuthorName = author;

            // 3. init
            bool isRepo = IsOwnRepo();
            if (!isRepo)
            {
                Log("③ 初始化仓库 / git init…");
                var init = Git("init -b " + Q(branch));
                if (!init.Ok) init = Git("init");
                if (!init.Ok) return Fail(r, "git init 失败 / git init failed: " + init.Text, "检查目录写入权限 / Check folder permissions");
            }
            else Log("③ 已是 git 仓库 / Already a git repository");

            // 4. .gitignore
            int added = EnsureGitIgnore(_o.RepoPath);
            Log(added > 0 ? $"④ .gitignore 已补充 {added} 条 / Added {added} entries to .gitignore" : "④ .gitignore 已完整 / .gitignore is complete");

            // 5. scan
            Log("⑤ 敏感信息自检 / Sensitive-content scan…");
            r.Findings = ScanCandidates(out int scanned);
            Log($"  扫描 {scanned} 个文件，命中 {r.Findings.Count} 处 / {scanned} files scanned, {r.Findings.Count} hit(s)");
            if (r.Findings.Count > 0)
            {
                foreach (var f in r.Findings) Log("  " + f);
                if (!_confirm(r.Findings))
                {
                    r.Aborted = true;
                    return Fail(r, "自检发现敏感信息，已中止发布 / Sensitive content found, publish aborted",
                        "修改或删除命中的内容（或加入 .gitignore）后重试 / Fix the hits (or ignore those files) and try again");
                }
                Log("  用户已确认，继续发布 / Confirmed by user, continuing");
            }

            // 6. add + commit
            Log("⑥ 提交 / Committing…");
            var add = Git("add -A", 120000);
            if (!add.Ok) return Fail(r, "git add 失败 / git add failed: " + add.Text, null);
            var status = Git("status --porcelain");
            bool hasHead = Git("rev-parse --verify -q HEAD").Ok;
            if (status.Out.Trim().Length > 0)
            {
                string msgFile = Path.Combine(Path.GetTempPath(), "vsm-commit-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    File.WriteAllText(msgFile, string.IsNullOrWhiteSpace(_o.CommitMessage) ? DefaultCommitMessage() : _o.CommitMessage.Replace("\r\n", "\n").Trim() + "\n", new UTF8Encoding(false));
                    var c = Git("-c user.name=" + Q(author) + " -c user.email=" + Q(email) + " -c commit.gpgsign=false commit -q -F " + Q(msgFile), 120000);
                    if (!c.Ok) return Fail(r, "git commit 失败 / git commit failed: " + c.Text, null);
                }
                finally { try { File.Delete(msgFile); } catch { } }
                Log("  " + Git("log -1 --format=%h%x20%s").Out.Trim());
            }
            else if (!hasHead) return Fail(r, "没有可提交的文件 / Nothing to commit", "检查目录与 .gitignore / Check the folder and .gitignore");
            else Log("  没有新的改动，直接推送 / No changes, pushing existing commits");
            var br = Git("branch -M " + Q(branch));
            if (!br.Ok) return Fail(r, "切换分支失败 / Failed to set branch: " + br.Text, null);

            // 7. remote repo
            Log("⑦ 远程仓库 / Remote repository " + owner + "/" + name + "…");
            st = Http("GET", Api + "/repos/" + Uri.EscapeDataString(owner) + "/" + Uri.EscapeDataString(name), null, out j, out raw);
            if (st == 404)
            {
                bool isUser = string.Equals(owner, login, StringComparison.OrdinalIgnoreCase);
                string body = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "name", name }, { "private", _o.Private }, { "auto_init", false } });
                st = Http("POST", Api + (isUser ? "/user/repos" : "/orgs/" + Uri.EscapeDataString(owner) + "/repos"), body, out j, out raw);
                if (st != 201)
                    return Fail(r, $"创建仓库失败（{st}）/ Failed to create repository ({st}): " + ApiMessage(j, raw),
                        st == 403 || st == 404 ? "Token 需要创建仓库的权限（classic: repo；fine-grained: Administration 读写）/ The token needs permission to create repositories" : null);
                Log("  已创建 / Created (" + (_o.Private ? "private" : "public") + ")");
            }
            else if (st == 200)
            {
                bool isPrivate = j != null && j.TryGetValue("private", out var p) && p is bool b && b;
                Log("  已存在 / Exists (" + (isPrivate ? "private" : "public") + ")" + (isPrivate != _o.Private ? " — 可见性与配置不同，未修改 / visibility differs from settings, left unchanged" : ""));
            }
            else return Fail(r, $"查询仓库失败（{st}）/ Failed to query repository ({st}): " + ApiMessage(j, raw), null);
            string url = "https://github.com/" + owner + "/" + name;

            var cur = Git("config --get remote.origin.url");
            if (!cur.Ok)
            {
                var ra = Git("remote add origin " + Q(url + ".git"));
                if (!ra.Ok) return Fail(r, "添加远程失败 / git remote add failed: " + ra.Text, null);
            }
            else if (!string.Equals(cur.Out.Trim().TrimEnd('/'), url + ".git", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(cur.Out.Trim().TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase))
            {
                Log("  origin 由 " + cur.Out.Trim() + " 改为 / changed to " + url + ".git");
                var su = Git("remote set-url origin " + Q(url + ".git"));
                if (!su.Ok) return Fail(r, "修改远程失败 / git remote set-url failed: " + su.Text, null);
            }

            // 8. push
            Log("⑧ 推送 / Pushing " + branch + "…");
            var push = Git("push -u origin " + Q(branch), 600000, auth: true);
            if (!push.Ok)
            {
                string t = push.Text;
                string hint =
                    Regex.IsMatch(t, "rejected|non-fast-forward|fetch first", RegexOptions.IgnoreCase) ? "远程分支已有本地没有的提交：先 git pull --rebase 合并，或改用一个空仓库 / The remote has commits you don't have: pull --rebase first or use an empty repository" :
                    Regex.IsMatch(t, "workflow", RegexOptions.IgnoreCase) ? "推送 .github/workflows 需要 Token 的 workflow 权限 / Pushing workflows requires the 'workflow' scope" :
                    Regex.IsMatch(t, "403|denied|permission", RegexOptions.IgnoreCase) ? "Token 没有该仓库的写入权限（classic: repo；fine-grained: Contents 读写）/ The token lacks write access (Contents: read & write)" :
                    Regex.IsMatch(t, "401|Authentication failed|could not read Username", RegexOptions.IgnoreCase) ? "Token 无效或已过期 / The token is invalid or expired" :
                    Regex.IsMatch(t, "resolve host|Failed to connect|timed out|超时", RegexOptions.IgnoreCase) ? "检查网络或 git 代理设置 / Check the network or git proxy" : null;
                return Fail(r, "git push 失败 / git push failed: " + t, hint);
            }
            r.Ok = true;
            r.Url = url;
            Log("✓ 发布成功 / Published: " + url);
            return r;
        }

        /// <summary>默认的中英双语提交信息。/ Default bilingual commit message.</summary>
        public static string DefaultCommitMessage() =>
            "发布更新 / Publish update (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")\n\n" +
            "由 VSManager 发布功能提交。\nCommitted by the VSManager publish feature.\n";
    }
}
