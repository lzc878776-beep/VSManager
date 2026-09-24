using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>任务状态流转与发送重试判定。/ Task status transitions and send retry rules.</summary>
    [TestClass]
    public class TaskStateMachineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 9, 0, 0);

        private static QueuedTask Waiting(int id = 1, string vs = "A") =>
            new QueuedTask { Id = id, VsKey = vs, VsName = vs, Text = "t" + id, Status = QueueStatus.Waiting, Created = T0 };

        [TestMethod]
        public void SendRetryPolicy_DeliveredOnlyWithPrefix()
        {
            Assert.IsTrue(SendRetryPolicy.IsDelivered("已发送"));
            Assert.IsTrue(SendRetryPolicy.IsDelivered("已发送（后台）"));
            Assert.IsFalse(SendRetryPolicy.IsDelivered("发送失败：找不到输入框"));
            Assert.IsFalse(SendRetryPolicy.IsDelivered(" 已发送"));
            Assert.IsFalse(SendRetryPolicy.IsDelivered(""));
            Assert.IsFalse(SendRetryPolicy.IsDelivered(null));
        }

        [TestMethod]
        public void SendRetryPolicy_RetriesUntilThirdAttempt()
        {
            Assert.AreEqual(3, SendRetryPolicy.MaxAttempts);
            Assert.AreEqual(TimeSpan.FromSeconds(30), SendRetryPolicy.RetryDelay);
            Assert.AreEqual(SendDecision.Retry, SendRetryPolicy.Decide(1, "失败"));
            Assert.AreEqual(SendDecision.Retry, SendRetryPolicy.Decide(2, "失败"));
            Assert.AreEqual(SendDecision.Fail, SendRetryPolicy.Decide(3, "失败"));
            Assert.AreEqual(SendDecision.Fail, SendRetryPolicy.Decide(4, "失败"));
            Assert.AreEqual(SendDecision.Delivered, SendRetryPolicy.Decide(3, "已发送"));
        }

        [TestMethod]
        public void BeginSend_OnlyFromWaiting_CountsAttempt()
        {
            var t = Waiting();
            Assert.IsTrue(TaskStateMachine.BeginSend(t, "VS-A"));
            Assert.AreEqual(QueueStatus.Sending, t.Status);
            Assert.AreEqual("VS-A", t.VsName);
            Assert.AreEqual(1, t.Attempts);
            Assert.IsFalse(TaskStateMachine.BeginSend(t, "VS-A"), "发送中不能再次发送 / cannot send twice");
            Assert.AreEqual(1, t.Attempts);
        }

        [TestMethod]
        public void ApplySendResult_Delivered_BecomesRunning()
        {
            var t = Waiting();
            t.Error = "旧错误";
            t.SawBusy = true;
            TaskStateMachine.BeginSend(t, "A");
            Assert.AreEqual(SendDecision.Delivered, TaskStateMachine.ApplySendResult(t, "已发送", T0));
            Assert.AreEqual(QueueStatus.Running, t.Status);
            Assert.AreEqual(T0, t.Started);
            Assert.IsNull(t.Error);
            Assert.IsFalse(t.SawBusy);
        }

        [TestMethod]
        public void ApplySendResult_Failure_RetriesThenFails()
        {
            var t = Waiting();
            for (int i = 1; i <= 2; i++)
            {
                TaskStateMachine.BeginSend(t, "A");
                Assert.AreEqual(SendDecision.Retry, TaskStateMachine.ApplySendResult(t, "找不到输入框", T0));
                Assert.AreEqual(QueueStatus.Waiting, t.Status);
                Assert.AreEqual("找不到输入框", t.Error);
                Assert.AreEqual(T0.AddSeconds(30), t.NextTry);
                Assert.IsNull(t.Finished);
            }
            TaskStateMachine.BeginSend(t, "A");
            Assert.AreEqual(SendDecision.Fail, TaskStateMachine.ApplySendResult(t, "找不到输入框", T0));
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual(3, t.Attempts);
            Assert.AreEqual(T0, t.Finished);
        }

        [TestMethod]
        public void Complete_OnlyFromRunning()
        {
            var t = Waiting();
            Assert.IsFalse(TaskStateMachine.Complete(t, T0));
            t.Status = QueueStatus.Running;
            Assert.IsTrue(TaskStateMachine.Complete(t, T0));
            Assert.AreEqual(QueueStatus.Done, t.Status);
            Assert.AreEqual(T0, t.Finished);
            Assert.IsFalse(TaskStateMachine.Complete(t, T0), "不会重复完成 / no double completion");
        }

        [TestMethod]
        public void Cancel_OnlyWaitingOrRunning()
        {
            foreach (var s in new[] { QueueStatus.Waiting, QueueStatus.Running })
            {
                var t = Waiting();
                t.Status = s;
                Assert.IsTrue(TaskStateMachine.Cancel(t, T0), s);
                Assert.AreEqual(QueueStatus.Cancelled, t.Status);
                Assert.AreEqual(T0, t.Finished);
            }
            foreach (var s in new[] { QueueStatus.Sending, QueueStatus.Done, QueueStatus.Failed, QueueStatus.Cancelled })
            {
                var t = Waiting();
                t.Status = s;
                Assert.IsFalse(TaskStateMachine.Cancel(t, T0), s);
                Assert.AreEqual(s, t.Status);
            }
        }

        [TestMethod]
        public void Requeue_ResetsEverything()
        {
            var t = Waiting();
            t.Status = QueueStatus.Failed; t.Attempts = 3; t.Error = "e"; t.Result = "r"; t.Started = T0; t.Finished = T0; t.NextTry = T0;
            TaskStateMachine.Requeue(t);
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
            Assert.AreEqual(0, t.Attempts);
            Assert.IsNull(t.Error); Assert.IsNull(t.Result); Assert.IsNull(t.Started); Assert.IsNull(t.Finished);
            Assert.AreEqual(DateTime.MinValue, t.NextTry);
        }

        [TestMethod]
        public void RecoverAfterRestart_SendingGoesBackToQueue()
        {
            var t = Waiting(); t.Status = QueueStatus.Sending;
            TaskStateMachine.RecoverAfterRestart(t);
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
            t.Status = QueueStatus.Running;
            TaskStateMachine.RecoverAfterRestart(t);
            Assert.AreEqual(QueueStatus.Running, t.Status, "执行中保持不变 / running stays running");
        }

        [TestMethod]
        public void CanRemove_NotWhileSending()
        {
            var t = Waiting();
            Assert.IsTrue(TaskStateMachine.CanRemove(t));
            t.Status = QueueStatus.Sending;
            Assert.IsFalse(TaskStateMachine.CanRemove(t));
            Assert.IsFalse(TaskStateMachine.CanRemove(null));
        }

        [TestMethod]
        public void CheckRunning_Verdicts()
        {
            var t = Waiting(); t.Status = QueueStatus.Running; t.Started = T0;
            bool evaluated = false;
            Func<bool> can = () => { evaluated = true; return true; };

            Assert.AreEqual(RunningVerdict.None, TaskStateMachine.CheckRunning(t, false, CopilotState.Unknown, can, T0.AddSeconds(15)));
            Assert.AreEqual(RunningVerdict.VsClosed, TaskStateMachine.CheckRunning(t, false, CopilotState.Unknown, can, T0.AddSeconds(16)));
            Assert.AreEqual(RunningVerdict.SawBusy, TaskStateMachine.CheckRunning(t, true, CopilotState.Busy, can, T0.AddSeconds(100)));
            Assert.IsFalse(evaluated, "忙碌时不调用 canDispatch / canDispatch not evaluated while busy");
            Assert.AreEqual(RunningVerdict.None, TaskStateMachine.CheckRunning(t, true, CopilotState.Idle, can, T0.AddSeconds(30)));
            Assert.AreEqual(RunningVerdict.Unconfirmed, TaskStateMachine.CheckRunning(t, true, CopilotState.Idle, can, T0.AddSeconds(31)));
            Assert.AreEqual(RunningVerdict.None, TaskStateMachine.CheckRunning(t, true, CopilotState.Idle, () => false, T0.AddSeconds(31)));
            Assert.AreEqual(RunningVerdict.None, TaskStateMachine.CheckRunning(t, true, CopilotState.Unknown, can, T0.AddSeconds(31)));
            t.SawBusy = true;
            Assert.AreEqual(RunningVerdict.None, TaskStateMachine.CheckRunning(t, true, CopilotState.Idle, can, T0.AddSeconds(31)),
                "见过忙碌的任务由完成事件处理 / tasks seen busy are finished by the completion event");
        }

        [TestMethod]
        public void NextToDispatch_OneHeadPerIdleVs()
        {
            var items = new List<QueuedTask>
            {
                Waiting(5, "A"), Waiting(2, "A"),
                Waiting(3, "B"), new QueuedTask { Id = 1, VsKey = "B", Text = "x", Status = QueueStatus.Running },
                Waiting(4, "C"), Waiting(6, "D"),
                new QueuedTask { Id = 7, VsKey = "E", Text = "done", Status = QueueStatus.Done },
            };
            items.Find(x => x.Id == 6).NextTry = T0.AddSeconds(10);
            var heads = TaskStateMachine.NextToDispatch(items, T0);
            CollectionAssert.AreEqual(new[] { 2, 4 }, heads.ConvertAll(x => x.Id));
            heads = TaskStateMachine.NextToDispatch(items, T0.AddSeconds(10));
            CollectionAssert.AreEqual(new[] { 2, 4, 6 }, heads.ConvertAll(x => x.Id));
        }

        [TestMethod]
        public void FindActiveDuplicate_IgnoresFinished()
        {
            var a = Waiting(1, "A");
            var done = Waiting(2, "A"); done.Text = "same"; done.Status = QueueStatus.Done;
            var items = new List<QueuedTask> { a, done };
            Assert.AreSame(a, TaskStateMachine.FindActiveDuplicate(items, "A", "t1"));
            Assert.IsNull(TaskStateMachine.FindActiveDuplicate(items, "B", "t1"));
            Assert.IsNull(TaskStateMachine.FindActiveDuplicate(items, "A", "same"));
        }

        [TestMethod]
        public void StatusText_MatchesUi()
        {
            var t = Waiting();
            Assert.AreEqual("排队中", TaskStateMachine.StatusText(t, T0));
            t.Attempts = 1;
            Assert.AreEqual("等待重试", TaskStateMachine.StatusText(t, T0));
            t.Status = QueueStatus.Running; t.Started = T0;
            Assert.AreEqual("执行中（1m05s）", TaskStateMachine.StatusText(t, T0.AddSeconds(65)));
            t.Status = QueueStatus.Done; Assert.AreEqual("已完成", TaskStateMachine.StatusText(t, T0));
            t.Status = QueueStatus.Failed; Assert.AreEqual("失败", TaskStateMachine.StatusText(t, T0));
            t.Status = QueueStatus.Cancelled; Assert.AreEqual("已取消", TaskStateMachine.StatusText(t, T0));
            Assert.AreEqual("1h05m", TextUtil.FormatDuration(TimeSpan.FromMinutes(65)));
            Assert.AreEqual("9s", TextUtil.FormatDuration(TimeSpan.FromSeconds(9)));
        }

        [TestMethod]
        public void SuccessReceipt_MustMatchCurrentAttempt_AndSurvivePersistenceClone()
        {
            var t = Waiting();
            TaskStateMachine.BeginSend(t, "A");
            string oldReceipt = TaskStateMachine.SuccessReceipt(t);
            Assert.IsTrue(TaskStateMachine.TryReadSuccess(t, "Completed\r\n" + oldReceipt, out string result));
            Assert.AreEqual("Completed", result);
            Assert.IsFalse(TaskStateMachine.TryReadSuccess(t, "Request failed", out _));
            Assert.IsFalse(TaskStateMachine.TryReadSuccess(t, TaskStateMachine.FailureReceipt(t), out _));
            Assert.IsFalse(TaskStateMachine.TryReadSuccess(t, oldReceipt + " still working", out _));
            Assert.IsTrue(TaskStateMachine.TryReadSuccess(t.Clone(), "Completed\r\n" + oldReceipt, out _));
            TaskStateMachine.Requeue(t);
            TaskStateMachine.BeginSend(t, "A");
            Assert.AreNotEqual(oldReceipt, TaskStateMachine.SuccessReceipt(t));
            Assert.IsFalse(TaskStateMachine.TryReadSuccess(t, "Completed\r\n" + oldReceipt, out _));
            StringAssert.Contains(TaskStateMachine.DispatchText(t), TaskStateMachine.SuccessReceipt(t));
            StringAssert.Contains(TaskStateMachine.DispatchText(t), TaskStateMachine.FailureReceipt(t));
        }

        [TestMethod]
        public void FailureAndParkedPredecessors_BlockOnlyTheirTarget()
        {
            var failed = Waiting(1); failed.Status = QueueStatus.Failed;
            var next = Waiting(2, "a");
            var other = Waiting(3, "B");
            var items = new[] { failed, next, other };
            CollectionAssert.AreEqual(new[] { other }, TaskStateMachine.NextToDispatch(items, T0));
            StringAssert.Contains(TaskStateMachine.StatusText(next, T0, items), "已暂停");
            failed.Status = QueueStatus.WaitingVs;
            CollectionAssert.AreEqual(new[] { other }, TaskStateMachine.NextToDispatch(items, T0));
            failed.Status = QueueStatus.Cancelled;
            CollectionAssert.AreEqual(new[] { next, other }, TaskStateMachine.NextToDispatch(items, T0));
        }

        [TestMethod]
        public void Clip_CollapsesWhitespace()
        {
            Assert.AreEqual("a b c", TextUtil.Clip("  a\r\n b\t\tc ", 10));
            Assert.AreEqual("abc…", TextUtil.Clip("abcdef", 3));
            Assert.AreEqual("", TextUtil.Clip(null, 3));
        }
    }
}
