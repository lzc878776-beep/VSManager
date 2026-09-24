using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    public interface IWorktreeTaskService
    {
        Task CheckDevelopmentAsync(WorktreeInfo worktree);
        // true 表示仍有冲突，需要 Copilot；false 表示已验证本地合并。/ True needs Copilot; false means verified local integration.
        Task<bool> IntegrateAsync(WorktreeInfo worktree);
    }

    internal sealed class WorktreeService : IWorktreeTaskService
    {
        private readonly AgentFilePolicy _policy;
        internal WorktreeService(Func<IEnumerable<string>> roots) { _policy = new AgentFilePolicy(roots); }

        internal WorktreeInfo Create(string solution, string name)
        {
            if (!Regex.IsMatch(name ?? "", @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,39}\z"))
                throw new InvalidOperationException("工作线名称限 1–40 位字母、数字、下划线或横线 / Invalid worktree name");
            solution = _policy.Require(solution);
            using (_policy.Open(solution, false))
            {
                string root = Path.GetDirectoryName(solution);
                while (!Directory.Exists(Path.Combine(root, ".git")))
                {
                    if (File.Exists(Path.Combine(root, ".git")) || Directory.GetParent(root) == null)
                        throw new InvalidOperationException("必须从主仓库创建 / Use the main checkout");
                    root = Directory.GetParent(root).FullName;
                }
                root = _policy.Require(root);
                string parent = _policy.Require(Directory.GetParent(root).FullName);
                string target = _policy.Require(Path.Combine(parent, Path.GetFileName(root) + ".worktree." + name));
                using (_policy.Open(parent, true))
                using (_policy.Open(root, true))
                using (Lock(root))
                {
                    ValidateMain(root);
                    RequireClean(root);
                    string branch = Git(root, "symbolic-ref", "--quiet", "HEAD");
                    string head = Git(root, "rev-parse", "HEAD");
                    RequireSafeTree(root, head);
                    if (Directory.Exists(target) || File.Exists(target))
                        throw new InvalidOperationException("目标已存在；不会覆盖 / Worktree destination already exists");
                    var info = new WorktreeInfo
                    {
                        MainRoot = root, Root = target, MainBranch = branch,
                        Branch = "refs/heads/task/" + name,
                        SolutionPath = Path.Combine(target, solution.Substring(root.Length + 1))
                    };
                    Git(root, "worktree", "add", "-b", "task/" + name, "--", target, head);
                    if (!File.Exists(info.SolutionPath))
                        throw new InvalidOperationException("工作树已创建但解决方案未被 Git 跟踪；请手动检查 / Created worktree lacks tracked solution");
                    return info;
                }
            }
        }

        public Task CheckDevelopmentAsync(WorktreeInfo info) => Task.Run(() =>
        {
            using (Open(info))
            using (Lock(info.MainRoot))
            {
                Validate(info);
                RequireClean(info.Root);
            }
        });

        public Task<bool> IntegrateAsync(WorktreeInfo info) => Task.Run(() =>
        {
            using (Open(info))
            using (Lock(info.MainRoot))
            {
                Validate(info);
                RequireMainBranch(info);
                RequireClean(info.MainRoot);
                if (Git(info.Root, "ls-files", "-u").Length != 0)
                {
                    string mergeHead = Git(info.Root, "rev-parse", "--verify", "MERGE_HEAD");
                    if (!Ancestor(info.MainRoot, mergeHead, Git(info.MainRoot, "rev-parse", "HEAD")))
                        throw new InvalidOperationException("存在其他 Git 操作的冲突，请手动处理 / Conflicts belong to another Git operation");
                    return true;
                }
                RequireClean(info.Root);
                string main = Git(info.MainRoot, "rev-parse", "HEAD");
                string task = Git(info.Root, "rev-parse", "HEAD");
                RequireSafeTree(info.MainRoot, main);
                RequireSafeTree(info.Root, task);
                if (!Ancestor(info.Root, main, task))
                {
                    int code = Run(info.Root, out _, "merge", "--no-edit", "--no-stat", "--no-overwrite-ignore", main);
                    if (code != 0)
                    {
                        if (Git(info.Root, "ls-files", "-u").Length != 0) return true;
                        throw new InvalidOperationException("工作树合并失败，主项目未更新；请检查 Git 状态 / Worktree merge failed; main unchanged");
                    }
                }
                RequireClean(info.Root);
                task = Git(info.Root, "rev-parse", "HEAD");
                if (!Ancestor(info.Root, main, task)) throw new InvalidOperationException("未包含主项目提交 / Main commits are not integrated");
                RequireMainBranch(info);
                RequireClean(info.MainRoot);
                if (Git(info.MainRoot, "rev-parse", "HEAD") != main)
                    throw new InvalidOperationException("主项目已变化，请重试 / Main changed; retry integration");
                RequireSafeTree(info.Root, task);
                Git(info.MainRoot, "merge", "--ff-only", "--no-edit", "--no-stat", "--no-overwrite-ignore", task);
                RequireMainBranch(info);
                if (Git(info.MainRoot, "rev-parse", "HEAD") != task)
                    throw new InvalidOperationException("无法确认主项目快进结果 / Cannot verify fast-forward result");
                RequireClean(info.MainRoot);
                return false;
            }
        });

        private IDisposable Open(WorktreeInfo info)
        {
            if (info == null) throw new InvalidOperationException("缺少工作树元数据 / Missing worktree metadata");
            _policy.Require(info.MainRoot);
            _policy.Require(info.Root);
            var leases = new Leases();
            try
            {
                leases.Items.Add(_policy.Open(info.MainRoot, true));
                leases.Items.Add(_policy.Open(info.Root, true));
                return leases;
            }
            catch { leases.Dispose(); throw; }
        }

        private static void ValidateMain(string root)
        {
            string dotgit = Path.Combine(root, ".git");
            if (!Directory.Exists(dotgit) || (File.GetAttributes(dotgit) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("需要普通主仓库，拒绝链接 / A non-linked main repository is required");
            foreach (string path in new[] { "config", "objects", "objects\\info", "refs", "worktrees" })
            {
                string metadata = Path.Combine(dotgit, path);
                if ((File.Exists(metadata) || Directory.Exists(metadata)) && (File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Git 元数据链接不受支持 / Linked Git metadata is unsupported");
            }
            if (File.Exists(Path.Combine(dotgit, "commondir")) || File.Exists(Path.Combine(dotgit, "objects\\info\\alternates")))
                throw new InvalidOperationException("Git 外部对象目录不受支持 / External Git object directories are unsupported");
            ValidateConfig(root);
            if (!SolutionMatcher.SamePath(Git(root, "rev-parse", "--show-toplevel"), root))
                throw new InvalidOperationException("仓库根目录不匹配 / Repository root mismatch");
        }

        private static void ValidateConfig(string root)
        {
            string config = Git(root, "config", "--no-includes", "--local", "--list");
            foreach (string line in config.Split('\n'))
            {
                string key = line.Split('=')[0].Trim();
                if (key.StartsWith("include", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase)
                    || (key.StartsWith("merge.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".driver", StringComparison.OrdinalIgnoreCase))
                    || key.Equals("core.worktree", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("extensions.worktreeconfig", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("仓库包含 include/filter/自定义合并驱动或重定向配置，不支持自动工作树 / Unsafe repository configuration for automatic worktrees");
            }
        }

        private static void Validate(WorktreeInfo info)
        {
            ValidateMain(info.MainRoot);
            string common = Git(info.Root, "rev-parse", "--git-common-dir");
            if (!Path.IsPathRooted(common)) common = Path.Combine(info.Root, common);
            if (!SolutionMatcher.SamePath(Path.GetFullPath(common), Path.Combine(info.MainRoot, ".git"))
                || !SolutionMatcher.SamePath(Git(info.Root, "rev-parse", "--show-toplevel"), info.Root)
                || Git(info.Root, "symbolic-ref", "--quiet", "HEAD") != info.Branch)
                throw new InvalidOperationException("工作树或任务分支已改变 / Worktree or task branch changed");
        }

        private static void RequireMainBranch(WorktreeInfo info)
        {
            if (Git(info.MainRoot, "symbolic-ref", "--quiet", "HEAD") != info.MainBranch)
                throw new InvalidOperationException("主项目分支已切换；请恢复原分支再重试 / Restore the captured main branch before retrying");
        }

        private static void RequireClean(string root)
        {
            if (Git(root, "status", "--porcelain", "--untracked-files=all").Length != 0
                || Run(root, out _, "rev-parse", "--verify", "--quiet", "MERGE_HEAD") == 0
                || Directory.Exists(Path.Combine(Git(root, "rev-parse", "--absolute-git-dir"), "rebase-merge"))
                || Directory.Exists(Path.Combine(Git(root, "rev-parse", "--absolute-git-dir"), "rebase-apply"))
                || Run(root, out _, "rev-parse", "--verify", "--quiet", "CHERRY_PICK_HEAD") == 0
                || Run(root, out _, "rev-parse", "--verify", "--quiet", "REVERT_HEAD") == 0)
                throw new InvalidOperationException("存在未提交修改或未完成 Git 操作；不会自动提交/覆盖，请处理后重试 / Dirty checkout or unfinished Git operation; resolve manually and retry");
        }

        private static void RequireSafeTree(string root, string commit)
        {
            if (Git(root, "ls-tree", "-r", commit).Split('\n').Any(l => l.StartsWith("120000 ", StringComparison.Ordinal) || l.StartsWith("160000 ", StringComparison.Ordinal)))
                throw new InvalidOperationException("自动工作树暂不支持符号链接或子模块 / Automatic worktrees do not support symlinks or submodules");
        }

        private static bool Ancestor(string root, string a, string b)
        {
            int code = Run(root, out _, "merge-base", "--is-ancestor", a, b);
            if (code > 1) throw new InvalidOperationException("无法检查 Git 祖先关系 / Cannot check Git ancestry");
            return code == 0;
        }

        private static IDisposable Lock(string root)
        {
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Replace("-", "");
            var mutex = new Mutex(false, "Local\\VSManager.Worktree." + hash);
            try
            {
                try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("主项目合并忙 / Main integration is busy"); }
                catch (AbandonedMutexException) { }
                return new MutexLease(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }

        private sealed class MutexLease : IDisposable
        {
            private readonly Mutex _mutex;
            internal MutexLease(Mutex mutex) { _mutex = mutex; }
            public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
        }
        private sealed class Leases : IDisposable
        {
            internal readonly List<IDisposable> Items = new List<IDisposable>();
            public void Dispose() { for (int i = Items.Count - 1; i >= 0; i--) Items[i].Dispose(); }
        }

        internal static string Git(string root, params string[] args)
        {
            if (Run(root, out string output, args) != 0)
                throw new InvalidOperationException("Git 操作失败 / Git operation failed: " + args[0] + " (请检查本地 Git 状态 / inspect local Git status)");
            return output.Trim();
        }

        private static string InstalledGit()
        {
            var folders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") };
            foreach (string folder in folders.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string file = Path.Combine(folder, "Git", "cmd", "git.exe");
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) return file;
            }
            throw new InvalidOperationException("请在标准目录安装 Git for Windows；不执行仓库或 PATH 中的同名程序 / Install Git for Windows in a standard location; repository/PATH executables are not used");
        }

        // 不经过 shell；禁止 hooks、外部过滤器和环境注入。/ No shell; disable hooks, external filters and environment injection.
        private static int Run(string root, out string output, params string[] args)
        {
            var fixedArgs = new[] { "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "core.attributesFile=NUL",
                "-c", "core.excludesFile=NUL", "-c", "commit.gpgsign=false", "-c", "merge.gpgsign=false",
                "-c", "submodule.recurse=false", "-c", "protocol.allow=never", "-c", "gc.auto=0", "-c", "maintenance.auto=false",
                "-c", "user.name=VSManager", "-c", "user.email=vsmanager@localhost" };
            var start = new ProcessStartInfo(InstalledGit(), string.Join(" ", fixedArgs.Concat(args).Select(Quote)))
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
                start.EnvironmentVariables.Remove(key);
            start.EnvironmentVariables["GIT_CONFIG_NOSYSTEM"] = "1";
            start.EnvironmentVariables["GIT_CONFIG_GLOBAL"] = "NUL";
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GIT_EDITOR"] = "true";
            start.EnvironmentVariables["GIT_MERGE_AUTOEDIT"] = "no";
            using (var process = Process.Start(start))
            {
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000))
                {
                    process.Kill();
                    process.WaitForExit();
                    throw new TimeoutException("Git 超时；请检查工作树后重试 / Git timed out; inspect the worktree before retrying");
                }
                output = stdout.GetAwaiter().GetResult();
                stderr.GetAwaiter().GetResult();
                return process.ExitCode;
            }
        }

        internal static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
