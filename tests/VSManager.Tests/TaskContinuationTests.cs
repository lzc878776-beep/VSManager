using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>任务自动接续的判定、正文与上限。/ Task auto-continue: candidates, text and limits.</summary>
    [TestClass]
    public class TaskContinuationTests
    {
        private static QueuedTask Task(int id, string status, string result = "登录页已完成", int depth = 0) => new QueuedTask
        {
            Id = id, VsKey = "A", VsName = "A", Status = status, Result = result, ContinuationDepth = depth, Title = "项目 · 登录注册",
            Text = "实现登录与注册页\n两个页面都要单元测试。" + AgentService.OpenSourceTaskSuffix, Created = DateTime.Now
        };

        [TestMethod]
        public void Candidate_PendingOrPartialOnly()
        {
            Assert.IsTrue(TaskContinuation.Candidate(Task(1, QueueStatus.Unverified)));
            Assert.IsTrue(TaskContinuation.Candidate(Task(2, QueueStatus.Done, "登录页已完成，注册页尚未完成")));
            Assert.IsTrue(TaskContinuation.Candidate(Task(3, QueueStatus.Done, "Login done. Remaining steps: sign-up page")));
            Assert.IsFalse(TaskContinuation.Candidate(Task(4, QueueStatus.Done, "全部完成，单元测试通过")));
            Assert.IsFalse(TaskContinuation.Candidate(Task(5, QueueStatus.Failed)));
            Assert.IsNull(TaskContinuation.Notice(Task(6, QueueStatus.Done, "全部完成")));
        }

        [TestMethod]
        public void Notice_GuidesDecisionAndStopsAtLimit()
        {
            string n = TaskContinuation.Notice(Task(8, QueueStatus.Unverified));
            StringAssert.Contains(n, "[自动接续 / Auto-continue]");
            StringAssert.Contains(n, "continue_task（id=8");
            StringAssert.Contains(n, "retry_task_with_info");
            StringAssert.Contains(n, "不要停下来等用户");
            StringAssert.Contains(n, "0/" + TaskContinuation.MaxDepth);
            string capped = TaskContinuation.Notice(Task(9, QueueStatus.Unverified, depth: TaskContinuation.MaxDepth));
            StringAssert.Contains(capped, "不要再调用 continue_task");
            Assert.AreEqual(TaskContinuation.MaxDepth.ToString(), TaskContinuation.MaxDepthText);
        }

        [TestMethod]
        public void Check_RefusesInvalidRepeatedOrCappedContinuations()
        {
            var parent = Task(10, QueueStatus.Unverified);
            var all = new[] { parent };
            Assert.IsNull(TaskContinuation.Check(all, parent, 10, "实现注册页并补单元测试，编译通过"));
            StringAssert.Contains(TaskContinuation.Check(all, null, 99, "实现注册页并补单元测试"), "#99");
            StringAssert.Contains(TaskContinuation.Check(all, Task(11, QueueStatus.Failed), 11, "实现注册页并补单元测试"), "retry_task_with_info");
            StringAssert.Contains(TaskContinuation.Check(all, parent, 10, "请继续"), "remaining");
            StringAssert.Contains(TaskContinuation.Check(all, parent, 10, "continue"), "remaining");
            StringAssert.Contains(TaskContinuation.Check(all, Task(12, QueueStatus.Unverified, depth: TaskContinuation.MaxDepth), 12, "实现注册页并补单元测试"), "上限");
            var child = Task(13, QueueStatus.Waiting);
            child.ContinuedFrom = 10;
            StringAssert.Contains(TaskContinuation.Check(new[] { parent, child }, parent, 10, "实现注册页并补单元测试"), "#13");
            child.Status = QueueStatus.Cancelled;
            Assert.IsNull(TaskContinuation.Check(new[] { parent, child }, parent, 10, "实现注册页并补单元测试"), "取消的接续不算 / A cancelled continuation does not count");
        }

        [TestMethod]
        public void Compose_OneLineWithoutOriginalSuffix()
        {
            var parent = Task(20, QueueStatus.Unverified, "登录页已完成\n注册页待做", depth: 1);
            string text = TaskContinuation.Compose(parent, "实现注册页\n补单元测试");
            Assert.IsTrue(text.StartsWith(TaskContinuation.Marker));
            StringAssert.Contains(text, "接续任务 #20「项目 · 登录注册」（第 2 次接续");
            StringAssert.Contains(text, "实现登录与注册页 两个页面都要单元测试");
            StringAssert.Contains(text, "本轮只完成以下剩余步骤：实现注册页 补单元测试");
            Assert.IsFalse(text.Contains("\n"), "单行 / One line");
            Assert.IsFalse(text.Contains("【开源约束】"), "原任务的约束去掉，由发布时重新附加 / The original suffix is dropped and re-appended on publish");
        }

        [TestMethod]
        public void ContinuationFields_SurviveClone()
        {
            var t = Task(30, QueueStatus.Waiting);
            t.ContinuedFrom = 29;
            t.ContinuationDepth = 2;
            var c = t.Clone();
            Assert.AreEqual(29, c.ContinuedFrom);
            Assert.AreEqual(2, c.ContinuationDepth);
            Assert.IsTrue(new AppSettings().AgentAutoContinue, "默认开启 / On by default");
        }

        [TestMethod]
        public void Prompt_DescribesAutoContinueLoop()
        {
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                StringAssert.Contains(prompt, en ? "19. Task auto-continue" : "19. 任务自动接续");
                StringAssert.Contains(prompt, "continue_task");
                StringAssert.Contains(prompt, en ? "never stopping to ask the user whether to continue" : "不要停下来问用户「要不要继续」");
            }
        }
    }
}
