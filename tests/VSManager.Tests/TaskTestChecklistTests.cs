using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class TaskTestChecklistTests
    {
        private static QueuedTask Task(string status = QueueStatus.Unverified, bool needsUser = false) =>
            new QueuedTask { Id = 7, Text = "任务 / Task", VsName = "VS", Status = status, NeedsUser = needsUser, CompletionToken = "abc", Created = DateTime.Now, Finished = DateTime.Now };

        [TestMethod]
        public void Parse_PrefersCheckboxLines()
        {
            var items = TaskTestChecklist.Parse("改动已完成。\n- [ ] 打开设置页，确认按钮可见\n- [x] **重启** 后状态保留\n1. 不是测试项\n");
            CollectionAssert.AreEqual(new[] { "打开设置页，确认按钮可见", "重启 后状态保留" }, items.Select(i => i.Text).ToArray());
            Assert.IsTrue(items.All(i => !i.Checked), "新清单一律未勾选 / New checklists start unchecked");
        }

        [TestMethod]
        public void Parse_UsesListAfterVerificationHeading()
        {
            string reply = "已修改：\n- 文件 A\n- 文件 B\n\n**未验证项**\n1. 在运行中的程序里点击链接\n2. 拖入图片后预览正常\n\n构建通过。";
            CollectionAssert.AreEqual(new[] { "在运行中的程序里点击链接", "拖入图片后预览正常" }, TaskTestChecklist.Parse(reply).Select(i => i.Text).ToArray());
        }

        [TestMethod]
        public void Parse_FallsBackToSingleGenericItem()
        {
            var items = TaskTestChecklist.Parse("功能已实现，构建通过。");
            Assert.AreEqual(1, items.Length);
            Assert.AreEqual(TaskTestChecklist.FallbackItem, items[0].Text);
            Assert.AreEqual(1, TaskTestChecklist.Parse(null).Length);
        }

        [TestMethod]
        public void CheckingAllItems_CompletesUnverifiedAndNeedsUserTasks()
        {
            var t = Task();
            t.TestItems = TaskTestChecklist.Parse("- [ ] A\n- [ ] B");
            Assert.IsTrue(TaskStateMachine.SetTestItem(t, 0, true, out bool all));
            Assert.IsFalse(all);
            Assert.AreEqual(1, TaskTestChecklist.Remaining(t));
            Assert.IsFalse(TaskStateMachine.SetTestItem(t, 5, true, out _));
            Assert.IsTrue(TaskStateMachine.SetTestItem(t, 1, true, out all));
            Assert.IsTrue(all);
            Assert.IsTrue(TaskStateMachine.MarkVerified(t));
            Assert.AreEqual(QueueStatus.Done, t.Status);
            Assert.IsFalse(TaskTestChecklist.Pending(t));

            var n = Task(QueueStatus.Done, needsUser: true);
            n.Result = "请验证：\n- 打开窗口";
            Assert.IsTrue(TaskTestChecklist.Pending(n));
            Assert.AreEqual("打开窗口", TaskTestChecklist.Ensure(n).Single().Text);
            Assert.IsTrue(TaskStateMachine.MarkVerified(n));
            Assert.IsFalse(n.NeedsUser);
            Assert.IsTrue(n.TestItems.All(i => i.Checked));
            Assert.IsFalse(TaskStateMachine.MarkVerified(n));
        }

        [TestMethod]
        public void RequeueAndFailure_ClearChecklist()
        {
            var t = Task();
            t.TestItems = TaskTestChecklist.Parse("- [ ] A");
            Assert.AreEqual("A", t.Clone().TestItems.Single().Text);
            Assert.AreNotSame(t.TestItems[0], t.Clone().TestItems[0]);
            TaskStateMachine.Requeue(t);
            Assert.IsNull(t.TestItems);
            t.TestItems = TaskTestChecklist.Parse("- [ ] A");
            TaskStateMachine.Fail(t, "x", DateTime.Now);
            Assert.IsNull(t.TestItems);
        }

        [TestMethod]
        public void TaskStore_RoundTripsChecklistAndTitle()
        {
            string dir = Path.Combine(Path.GetTempPath(), "vsm-checklist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new JsonTaskStore(Path.Combine(dir, "tasks.json"));
                var t = Task();
                t.Title = "测试清单";
                t.TestItems = new[] { new TaskTestItem { Text = "A", Checked = true }, new TaskTestItem { Text = "B" } };
                Assert.IsNull(store.Save(new List<QueuedTask> { t }));
                var loaded = store.Load(new List<string>()).Single();
                Assert.AreEqual("测试清单", loaded.Title);
                CollectionAssert.AreEqual(new[] { "A", "B" }, loaded.TestItems.Select(i => i.Text).ToArray());
                CollectionAssert.AreEqual(new[] { true, false }, loaded.TestItems.Select(i => i.Checked).ToArray());
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void ChecklistPanel_ListsOnlyTasksAwaitingTests()
        {
            var unverified = Task();
            var needsUser = Task(QueueStatus.Done, true); needsUser.Id = 8; needsUser.TestItems = TaskTestChecklist.Parse("- [ ] A");
            var legacyNeedsUser = Task(QueueStatus.Done, true); legacyNeedsUser.Id = 9;
            var done = Task(QueueStatus.Done); done.Id = 10;
            var ids = TestChecklistPanel.ListedTasks(new[] { unverified, needsUser, legacyNeedsUser, done }, null).Select(t => t.Id).ToArray();
            CollectionAssert.AreEquivalent(new[] { 7, 8 }, ids);
        }

        [TestMethod]
        public void DispatchRules_AskForCheckboxTestList()
        {
            string text = TaskStateMachine.DispatchText(Task(QueueStatus.Running));
            StringAssert.Contains(text, "- [ ]");
            StringAssert.Contains(text, "测试清单");
        }

        [TestMethod]
        public void UserBusy_IsRetriedLaterWithoutCountingAttempts()
        {
            var t = Task(QueueStatus.Sending);
            t.Attempts = 1;
            var now = DateTime.Now;
            string result = SendRetryPolicy.UserBusyPrefix + "用户正在操作";
            Assert.AreEqual(SendDecision.Retry, TaskStateMachine.ApplySendResult(t, result, now));
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
            Assert.AreEqual(0, t.Attempts);
            Assert.IsTrue(t.NextTry > now);
            Assert.AreEqual("等待用户操作空闲", TaskStateMachine.StatusText(t, now));
        }

        [TestMethod]
        public void WaitIdle_WaitsForQuietInputAndGivesUp()
        {
            int idle = 0, slept = 0;
            Assert.IsTrue(UserActivity.WaitIdle(() => idle, 1500, 4000, ms => { slept += ms; idle += ms; }));
            Assert.IsTrue(slept >= 1500 && slept < 4000);
            slept = 0;
            Assert.IsFalse(UserActivity.WaitIdle(() => 0, 1500, 1000, ms => slept += ms));
            Assert.IsTrue(slept >= 1000);
            Assert.IsTrue(UserActivity.WaitIdle(() => 5000, 1500, 0, ms => Assert.Fail()));
        }
    }
}
