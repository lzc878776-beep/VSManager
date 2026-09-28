using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>待处理内容与失败原因的记录与展示。/ Recording and showing pending items and failure reasons.</summary>
    [TestClass]
    public class TaskHoldNoteTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 9, 0, 0);

        private static QueuedTask Running(int id = 1) =>
            new QueuedTask { Id = id, VsKey = "A", VsName = "A", Text = "t", Status = QueueStatus.Running, Created = T0, Started = T0, CompletionToken = "tok" };

        [TestMethod]
        public void Pending_UsesTaggedParagraph_WithFollowingList()
        {
            string reply = "已修改设置页。\n\n**待处理：** 请手动验证：\n- 打开设置窗口\n- 切换主题\n\n其他说明。";
            Assert.AreEqual("请手动验证：\n- 打开设置窗口\n- 切换主题", TaskHoldNote.Pending(reply));
            Assert.AreEqual("- 运行程序", TaskHoldNote.Pending("done\n\nPending:\n- 运行程序"));
        }

        [TestMethod]
        public void Pending_FallsBackToLastParagraph_OrDefault()
        {
            Assert.AreEqual("请重启 VS 后确认菜单出现。", TaskHoldNote.Pending("改好了。\n\n请重启 VS 后确认菜单出现。"));
            StringAssert.Contains(TaskHoldNote.Pending(null), "待验证");
        }

        [TestMethod]
        public void Reason_UsesTag_IgnoresLookalikeWords()
        {
            Assert.AreEqual("缺少配置文件路径", TaskHoldNote.Reason("尝试了两种方法。\n\n失败原因：缺少配置文件路径\n\n建议：提供路径。"));
            Assert.AreEqual("last", TaskHoldNote.Reason("Reasoning: x\n\nlast"));
        }

        [TestMethod]
        public void Complete_NeedsUser_RecordsPending_AndSuccessClearsIt()
        {
            var t = Running();
            t.Result = "改完了\n\n待处理：请运行并检查托盘图标";
            Assert.IsTrue(TaskStateMachine.Complete(t, T0, pending: true));
            Assert.AreEqual("请运行并检查托盘图标", t.PendingNote);
            Assert.IsNull(t.FailureReason);
            StringAssert.Contains(TaskHoldNote.DetailTitle(t), "待验证");
            StringAssert.Contains(TaskHoldNote.DetailText(t), "请运行并检查托盘图标");

            TaskStateMachine.Requeue(t);
            Assert.IsNull(t.PendingNote);
            t.Status = QueueStatus.Running;
            Assert.IsTrue(TaskStateMachine.Complete(t, T0));
            Assert.IsNull(t.PendingNote);
            Assert.IsNull(TaskHoldNote.DetailTitle(t), "成功任务不显示详情 / success shows no detail");
        }

        [TestMethod]
        public void Fail_RecordsReason_ByKind()
        {
            var reported = Running();
            reported.Result = "做了一半\n\n失败原因：接口不存在\n\n下一步：确认接口";
            TaskStateMachine.Fail(reported, "Copilot 回报本任务未完成: ...", T0, FailureKind.Reported);
            Assert.AreEqual("接口不存在", reported.FailureReason);
            Assert.IsNull(reported.PendingNote);
            StringAssert.Contains(TaskHoldNote.DetailTitle(reported), "失败原因");
            StringAssert.Contains(TaskHoldNote.DetailText(reported), "接口不存在");

            var delivery = Running(2);
            TaskStateMachine.Fail(delivery, "窗口未响应", T0, FailureKind.Delivery);
            StringAssert.Contains(delivery.FailureReason, "投递失败");
            StringAssert.Contains(delivery.FailureReason, "窗口未响应");

            var plain = Running(3);
            TaskStateMachine.Fail(plain, "worktree broken", T0);
            Assert.AreEqual("worktree broken", plain.FailureReason);
        }

        [TestMethod]
        public void FillHoldNote_BackfillsOldRecords_AndClearsStale()
        {
            var failed = new QueuedTask { Id = 1, Status = QueueStatus.Failed, Error = "旧错误", PendingNote = "stale" };
            TaskStateMachine.FillHoldNote(failed);
            Assert.AreEqual("旧错误", failed.FailureReason);
            Assert.IsNull(failed.PendingNote);

            var verify = new QueuedTask { Id = 2, Status = QueueStatus.Unverified, Result = "a\n\n请确认按钮位置" };
            TaskStateMachine.FillHoldNote(verify);
            Assert.AreEqual("请确认按钮位置", verify.PendingNote);

            var done = new QueuedTask { Id = 3, Status = QueueStatus.Done, PendingNote = "x", FailureReason = "y" };
            TaskStateMachine.FillHoldNote(done);
            Assert.IsNull(done.PendingNote);
            Assert.IsNull(done.FailureReason);
        }

        [TestMethod]
        public void Rules_AskForTaggedParagraphs()
        {
            var t = Running();
            string text = TaskStateMachine.DispatchText(t);
            StringAssert.Contains(text, TaskHoldNote.PendingTag);
            StringAssert.Contains(text, TaskHoldNote.ReasonTag);
        }

        [TestMethod]
        public void Store_RoundTripsNotes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "vsm-holdnote-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new JsonTaskStore(Path.Combine(dir, "tasks.json"));
                var items = new[]
                {
                    new QueuedTask { Id = 5, VsKey = "A", VsName = "A", Text = "x", Status = QueueStatus.Failed, Created = T0, Finished = T0, Error = "e", FailureReason = "r" },
                    new QueuedTask { Id = 6, VsKey = "A", VsName = "A", Text = "y", Status = QueueStatus.Done, Created = T0, Finished = T0, NeedsUser = true, PendingNote = "p" }
                };
                Assert.IsNull(store.Save(items));
                var loaded = store.Load(new System.Collections.Generic.List<string>());
                Assert.AreEqual("r", loaded.Find(t => t.Id == 5).FailureReason);
                Assert.AreEqual("p", loaded.Find(t => t.Id == 6).PendingNote);
                // 旧的「已完成（待用户验证）」加载后并入待验证 / Legacy done-awaiting-verification loads as awaiting verification
                Assert.AreEqual(QueueStatus.Unverified, loaded.Find(t => t.Id == 6).Status);
                Assert.IsFalse(loaded.Find(t => t.Id == 6).NeedsUser);
                Assert.AreEqual("r", items[0].Clone().FailureReason);
                Assert.AreEqual("p", items[1].Clone().PendingNote);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}