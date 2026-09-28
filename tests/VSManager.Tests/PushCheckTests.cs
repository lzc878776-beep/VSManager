using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>推送核实：只有确认送达才报告成功，否则如实说明原因。/ Push verification: success only when delivery is confirmed; otherwise the real reason.</summary>
    [TestClass]
    public class PushCheckTests
    {
        private FakeClock _clock;
        private MemoryTaskStore _store;
        private TaskQueue _queue;
        private FakeDispatchHost _host;
        private TaskDispatcher _dispatcher;

        [TestInitialize]
        public void Init()
        {
            _clock = new FakeClock();
            _store = new MemoryTaskStore();
            _queue = new TaskQueue(new AppSettings(), _store, new RecordingArchive(), _clock.Func);
            _host = new FakeDispatchHost();
            _dispatcher = new TaskDispatcher(_queue, _host, _clock.Func) { PushDelay = _ => Task.CompletedTask };
            _dispatcher.Start();
        }

        [TestMethod]
        public async Task Delivered_ReportsPushSuccess()
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            var check = await _dispatcher.ConfirmPushAsync(t, TimeSpan.FromSeconds(5));
            Assert.AreEqual(PushOutcome.Delivered, check.Outcome);
            Assert.AreEqual(QueueStatus.Running, t.Status);
            StringAssert.StartsWith(check.Headline(t), "✅ 推送成功");
        }

        [TestMethod]
        public async Task SendFailure_ReportsFailureReason()
        {
            _host.AddVs("A");
            _host.SendResults.Enqueue("发送失败：找不到输入框");
            var t = _queue.Add("A", "A", "a", "AI");
            var check = await _dispatcher.ConfirmPushAsync(t, TimeSpan.FromSeconds(5));
            Assert.AreEqual(PushOutcome.Failed, check.Outcome);
            string head = check.Headline(t);
            StringAssert.StartsWith(head, "❌ 推送失败");
            StringAssert.Contains(head, "找不到输入框");
            Assert.IsTrue(PushCheck.IsRejected(head));
        }

        [TestMethod]
        public async Task BusyTarget_ReportsQueuedNotPushed()
        {
            _host.AddVs("A", CopilotState.Busy);
            var t = _queue.Add("A", "A", "a", "AI");
            var check = await _dispatcher.ConfirmPushAsync(t, TimeSpan.FromSeconds(5));
            Assert.AreEqual(PushOutcome.Held, check.Outcome);
            string head = check.Headline(t);
            StringAssert.StartsWith(head, "⏳");
            StringAssert.Contains(head, "正忙");
            Assert.IsFalse(head.Contains("推送成功"));
            Assert.AreEqual(0, _host.Sent.Count);
        }

        [TestMethod]
        public async Task StillSending_TimesOutAsNotYetPushed()
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            _host.Sending = true;
            var check = await _dispatcher.ConfirmPushAsync(t, TimeSpan.FromSeconds(1));
            Assert.AreEqual(PushOutcome.Held, check.Outcome);
            StringAssert.Contains(check.Reason, "未确认送达");
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
        }

        [TestMethod]
        public void NotInList_IsNotAdmitted()
        {
            var stray = new QueuedTask { Id = 42, VsKey = "A", VsName = "A", Text = "x", Status = QueueStatus.Waiting };
            var check = _dispatcher.CheckPush(stray);
            Assert.AreEqual(PushOutcome.NotAdmitted, check.Outcome);
            Assert.IsTrue(PushCheck.IsRejected(check.Headline(stray)));
            Assert.AreEqual(PushOutcome.NotAdmitted, _dispatcher.CheckPush(null).Outcome);
        }

        [TestMethod]
        public void SaveFailure_IsNotAdmitted()
        {
            _host.AddVs("A", CopilotState.Busy);
            _store.FailWith = "磁盘已满";
            var t = _queue.Add("A", "A", "a", "AI");
            var check = _dispatcher.CheckPush(t);
            Assert.AreEqual(PushOutcome.NotAdmitted, check.Outcome);
            StringAssert.Contains(check.Reason, "磁盘已满");
        }

        [TestMethod]
        public void NotStarted_AndPaused_AreHeldWithReason()
        {
            var fresh = new TaskDispatcher(_queue, _host, _clock.Func);
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "用户");
            var check = fresh.CheckPush(t);
            Assert.AreEqual(PushOutcome.Held, check.Outcome);
            Assert.AreEqual(TaskDispatcher.WaitingForStart, check.Reason);
            _dispatcher.SetPaused(true, false);
            Assert.AreEqual(TaskDispatcher.PausedText, _dispatcher.CheckPush(t).Reason);
        }

        [TestMethod]
        public async Task Predecessor_HoldsWithBlockerId()
        {
            _host.AddVs("A");
            var first = _queue.Add("A", "A", "a1", "AI");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, first.Status);
            var second = _queue.Add("A", "A", "a2", "AI");
            var check = await _dispatcher.ConfirmPushAsync(second, TimeSpan.FromSeconds(5));
            Assert.AreEqual(PushOutcome.Held, check.Outcome);
            StringAssert.Contains(check.Reason, "@" + first.Id);
        }
    }
}