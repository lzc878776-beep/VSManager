using System;
using System.IO;

namespace VSManager
{
    /// <summary>
    /// 只读 .git/HEAD 获取解决方案所在仓库与当前分支，不启动 git 进程，支持 worktree 的 .git 文件。
    /// Reads .git/HEAD only (no git process) to get the repository and current branch of a solution; supports worktree .git files.
    /// </summary>
    public sealed class GitHeadInfo
    {
        /// <summary>仓库工作区根目录 / Working-tree root of the repository.</summary>
        public string Root { get; }
        /// <summary>当前分支名；分离 HEAD 时为「detached 短哈希」。/ Current branch; "detached &lt;short sha&gt;" for a detached HEAD.</summary>
        public string Branch { get; }

        private GitHeadInfo(string root, string branch) { Root = root; Branch = branch; }

        /// <summary>从解决方案或项目路径向上查找仓库；不在仓库中或读取失败时返回 null。/ Walks up from a solution or project path; null when not in a repository or unreadable.</summary>
        public static GitHeadInfo Read(string solutionPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(solutionPath)) return null;
                string dir = Directory.Exists(solutionPath) ? solutionPath : Path.GetDirectoryName(solutionPath);
                for (var d = string.IsNullOrEmpty(dir) ? null : new DirectoryInfo(dir); d != null; d = d.Parent)
                {
                    string dotGit = Path.Combine(d.FullName, ".git");
                    string gitDir = Directory.Exists(dotGit) ? dotGit : File.Exists(dotGit) ? LinkedGitDir(dotGit, d.FullName) : null;
                    if (gitDir == null) continue;
                    string branch = ParseHead(File.ReadAllText(Path.Combine(gitDir, "HEAD")));
                    return branch == null ? null : new GitHeadInfo(d.FullName, branch);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return null;
        }

        /// <summary>解析 HEAD 内容 / Parses HEAD content.</summary>
        internal static string ParseHead(string head)
        {
            head = (head ?? "").Trim();
            const string prefix = "ref: refs/heads/";
            if (head.StartsWith(prefix, StringComparison.Ordinal)) return head.Length > prefix.Length ? head.Substring(prefix.Length) : null;
            if (head.StartsWith("ref: ", StringComparison.Ordinal)) return head.Substring(5).Trim();
            return head.Length >= 7 ? "detached " + head.Substring(0, 7) : null;
        }

        private static string LinkedGitDir(string dotGitFile, string root)
        {
            string text = File.ReadAllText(dotGitFile).Trim();
            if (!text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
            string path = text.Substring(7).Trim();
            path = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));
            return Directory.Exists(path) ? path : null;
        }
    }
}
