using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class TaskReplyVerdictTests
    {
        private static QueuedTask Task() => new QueuedTask { Id = 1, CompletionToken = "abc", Status = QueueStatus.Running };

        [TestMethod]
        public void FailedReceipt_WhenOnlyRuntimeVerificationMissing_IsUnverified()
        {
            var t = Task();
            string reply = "两项改动都已完成。全部 841 个测试通过，但还没在真实程序里用你现有的笔记试过迁移，所以回执标为未完成。\n**未验证**\n- 在运行中的程序里点击链接\n"
                + TaskStateMachine.FailureReceipt(t);
            Assert.AreEqual(TaskReplyOutcome.Unverified, TaskStateMachine.ReadOutcome(t, reply, out string result));
            Assert.IsFalse(result.Contains(":FAILED]"));
            StringAssert.Contains(result, "841");
        }

        [TestMethod]
        public void FailedReceipt_WithRealFailure_StaysFailed()
        {
            var t = Task();
            foreach (string reply in new[]
            {
                "需要重启后验证",
                "构建失败：CS1002，尚未在运行中的程序里验证",
                "功能尚未实现，测试通过但未验证",
                "测试全部通过，未验证。需要你决定使用哪种方案",
                "Build failed. Not verified.",
            })
                Assert.AreEqual(TaskReplyOutcome.Failed, TaskStateMachine.ReadOutcome(t, reply + "\n" + TaskStateMachine.FailureReceipt(t), out _), reply);
        }

        [TestMethod]
        public void Receipts_SuccessUnverifiedAndMissing()
        {
            var t = Task();
            Assert.AreEqual(TaskReplyOutcome.Succeeded, TaskStateMachine.ReadOutcome(t, "ok\n" + TaskStateMachine.SuccessReceipt(t), out _));
            Assert.AreEqual(TaskReplyOutcome.Unverified, TaskStateMachine.ReadOutcome(t, "done\n" + TaskStateMachine.UnverifiedReceipt(t), out string r));
            Assert.AreEqual("done", r);
            Assert.AreEqual(TaskReplyOutcome.Failed, TaskStateMachine.ReadOutcome(t, "已实现，测试通过，未验证", out _), "缺少回执仍失败 / Missing receipt still fails");
            Assert.AreEqual(TaskReplyOutcome.Failed, TaskStateMachine.ReadOutcome(t,
                TaskStateMachine.FailureReceipt(t) + "\n" + TaskStateMachine.UnverifiedReceipt(t), out _));
            StringAssert.Contains(TaskStateMachine.DispatchText(t), TaskStateMachine.UnverifiedReceipt(t));
        }

        [TestMethod]
        public void UnverifiedTask_PersistsThroughClone_AndResetsOnRequeue()
        {
            var t = Task();
            Assert.IsTrue(TaskStateMachine.Complete(t, System.DateTime.Now, pending: true));
            Assert.AreEqual(QueueStatus.Unverified, t.Status);
            Assert.AreEqual(QueueStatus.Unverified, t.Clone().Status);
            Assert.IsTrue(QueueStatus.Known(t.Status) && !QueueStatus.Active(t.Status) && QueueStatus.Delivered(t.Status));
            Assert.AreEqual("待验证", TaskStateMachine.StatusText(t, System.DateTime.Now));
            Assert.IsTrue(TaskStateMachine.MarkVerified(t));
            Assert.AreEqual(QueueStatus.Done, t.Status);
            Assert.IsFalse(TaskStateMachine.MarkVerified(t));
            t.Status = QueueStatus.Unverified;
            TaskStateMachine.Requeue(t);
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
        }
    }
}