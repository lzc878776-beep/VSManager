using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AgentQuickPromptTests
    {
        [TestMethod]
        public void QuickPrompts_IncludeLayoutAndPreserveGitActions()
        {
            var field = typeof(AgentPanel).GetField("QuickPrompts", BindingFlags.Static | BindingFlags.NonPublic);
            var items = ((System.ValueTuple<string, string>[])field.GetValue(null)).ToArray();
            CollectionAssert.AreEqual(new[] { "屏幕布局 / Layout", AgentPanel.SyncGitText, "🔀 worktree 并入主分支", AgentPanel.CloseCsTabsText }, items.Select(i => i.Item1).ToArray());
            StringAssert.Contains(items[0].Item2, "get_displays");
            StringAssert.Contains(items[0].Item2, "place_workspace_windows");
            Assert.IsFalse(items[0].Item2.Contains("send_task"));
            StringAssert.Contains(items[2].Item2, "send_task");
            foreach (var i in items.Skip(1).Take(2)) StringAssert.Contains(i.Item2, "不得丢弃");
            StringAssert.Contains(items[3].Item2, "不保存");
            StringAssert.Contains(AgentPanel.SyncGitTask, "禁止 force push");
            StringAssert.Contains(AgentPanel.MergeWorktreesPrompt, "git worktree list");
        }

        [TestMethod]
        public void CloseCsTabsSummary_ReportsUnsavedFilesHonestly()
        {
            var a = new VsService.CsTabCloseResult { Found = 3, Closed = 2 };
            a.Dirty.Add("Dirty.cs");
            var b = new VsService.CsTabCloseResult { Found = 0 };
            var c = VsService.CsTabCloseResult.Fail("DTE unavailable");
            string summary = VsService.CsTabCloseResult.Summarize(new[] { ("#1 A", a), ("#2 B", b), ("#3 C", c) });
            string[] lines = summary.Split('\n');
            Assert.AreEqual(4, lines.Length);
            StringAssert.Contains(lines[0], "关闭 2 个");
            StringAssert.Contains(lines[0], "1 个有未保存修改已保留（未保存）");
            StringAssert.Contains(lines[0], "1 个 VS 无法处理");
            StringAssert.Contains(lines[1], "Dirty.cs");
            StringAssert.Contains(lines[1], "未保存");
            StringAssert.Contains(lines[2], "No open .cs tabs");
            StringAssert.Contains(lines[3], "DTE unavailable");
            Assert.AreEqual("没有打开的 VS / No open VS", VsService.CsTabCloseResult.Summarize(new (string, VsService.CsTabCloseResult)[0]));
        }

        [TestMethod]
        public void SyncGitPrompt_TargetsOnlyChosenVs()
        {
            var target = new VsMentionTarget(new VsInstance { SolutionPath = "C:\\repo\\App.sln" }, 3, "App");
            string prompt = AgentPanel.SyncGitPrompt(target, "main");
            StringAssert.Contains(prompt, "#3 App");
            StringAssert.Contains(prompt, "vs 参数填 \"3\"");
            StringAssert.Contains(prompt, "不要发给其他 VS");
            StringAssert.Contains(prompt, "main");
            StringAssert.Contains(prompt, AgentPanel.SyncGitTask);
            Assert.IsFalse(AgentPanel.SyncGitTask.Contains("每个仓库"));
        }

        [TestMethod]
        public void SyncMenuEntries_ShowBranch_DisableNonGit_MarkSharedRepo()
        {
            string root = Path.Combine(Path.GetTempPath(), "vsm-git-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "main", ".git"));
                File.WriteAllText(Path.Combine(root, "main", ".git", "HEAD"), "ref: refs/heads/main\n");
                Directory.CreateDirectory(Path.Combine(root, "main", "sub"));
                Directory.CreateDirectory(Path.Combine(root, "gitdirs", "wt"));
                File.WriteAllText(Path.Combine(root, "gitdirs", "wt", "HEAD"), "ref: refs/heads/task/feature1\n");
                Directory.CreateDirectory(Path.Combine(root, "wt"));
                File.WriteAllText(Path.Combine(root, "wt", ".git"), "gitdir: ../gitdirs/wt\n");
                Directory.CreateDirectory(Path.Combine(root, "plain"));
                var targets = new[]
                {
                    new VsMentionTarget(new VsInstance { SolutionPath = Path.Combine(root, "main", "A.sln") }, 1, "A"),
                    new VsMentionTarget(new VsInstance { SolutionPath = Path.Combine(root, "main", "sub", "B.sln") }, 2, "B"),
                    new VsMentionTarget(new VsInstance { SolutionPath = Path.Combine(root, "wt", "C.sln") }, 3, "C"),
                    new VsMentionTarget(new VsInstance { SolutionPath = Path.Combine(root, "plain", "D.sln") }, 4, "D"),
                };
                var entries = AgentPanel.SyncMenuEntries(targets, GitHeadInfo.Read);
                Assert.AreEqual("main", entries[0].Branch);
                Assert.IsFalse(entries[0].Text.Contains("same repo"));
                StringAssert.Contains(entries[1].Text, "same repo as #1");
                Assert.AreEqual("task/feature1", entries[2].Branch);
                Assert.IsNull(entries[3].Branch);
                StringAssert.Contains(entries[3].Text, "not a git repo");
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [TestMethod]
        public void GitHead_ParsesBranchAndDetached()
        {
            Assert.AreEqual("dev/x", GitHeadInfo.ParseHead("ref: refs/heads/dev/x\n"));
            Assert.AreEqual("detached 0123456", GitHeadInfo.ParseHead("0123456789abcdef0123456789abcdef01234567"));
            Assert.IsNull(GitHeadInfo.ParseHead(""));
            Assert.IsNull(GitHeadInfo.Read(null));
        }
    }
}
