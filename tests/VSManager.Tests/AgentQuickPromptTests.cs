using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AgentQuickPromptTests
    {
        [TestMethod]
        public void QuickPrompts_OnlyGitSyncAndWorktreeMerge()
        {
            var field = typeof(AgentPanel).GetField("QuickPrompts", BindingFlags.Static | BindingFlags.NonPublic);
            var items = ((System.ValueTuple<string, string>[])field.GetValue(null)).ToArray();
            CollectionAssert.AreEqual(new[] { "🔄 同步 git", "🔀 worktree 并入主分支" }, items.Select(i => i.Item1).ToArray());
            foreach (var i in items)
            {
                StringAssert.Contains(i.Item2, "send_task");
                StringAssert.Contains(i.Item2, "不得丢弃");
            }
            StringAssert.Contains(AgentPanel.SyncGitPrompt, "禁止 force push");
            StringAssert.Contains(AgentPanel.MergeWorktreesPrompt, "git worktree list");
        }
    }
}