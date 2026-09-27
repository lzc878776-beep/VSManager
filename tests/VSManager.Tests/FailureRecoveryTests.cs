using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class FailureRecoveryTests
    {
        [DataTestMethod]
        [DataRow(null)] [DataRow("")] [DataRow("失败 / Failed")]
        [DataRow("输入框已清空，但未在对话中确认到新消息")]
        [DataRow(ManualChatProtection.UncertainPrefix + "submitted")]
        [DataRow("error: " + ManualChatProtection.WaitPrefix)]
        public async Task FirstFailureIsTerminalAcrossPumpsAndReload(string result)
        {
            using (var data = new TempDataFolder())
            {
                var clock = new FakeClock();
                var store = new JsonTaskStore(data.File("tasks.json"));
                var settings = new AppSettings();
                var queue = new TaskQueue(settings, store, new RecordingArchive(), clock.Func);
                var host = new FakeDispatchHost(); host.AddVs("A");
                host.SendResults.Enqueue(result);
                var task = queue.Add("A", "A", "测试 / Test", "AI");
                var dispatcher = new TaskDispatcher(queue, host, clock.Func);
                dispatcher.Start();
                for (int i = 0; i < 5; i++) { clock.Advance(TimeSpan.FromMinutes(1)); await dispatcher.PumpAsync(); }
                Assert.AreEqual(QueueStatus.Failed, task.Status);
                Assert.AreEqual(1, task.Attempts); Assert.AreEqual(1, host.Sent.Count);
                Assert.AreEqual(1, host.Notices.Count);
                Assert.AreEqual(1, File.ReadAllLines(TaskQueue.LogPath).Count(s => s.Contains("automatic retry stopped")));
                queue = new TaskQueue(settings, store, new RecordingArchive(), clock.Func);
                dispatcher = new TaskDispatcher(queue, host, clock.Func);
                dispatcher.Start(); await dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Failed, queue.Find(task.Id).Status);
                Assert.AreEqual(1, host.Sent.Count);
            }
        }

        [DataTestMethod]
        [DataRow(QueueStatus.Waiting)] [DataRow(QueueStatus.WaitingVs)]
        [DataRow(QueueStatus.Failed)] [DataRow(QueueStatus.Cancelled)]
        public async Task ExplicitRecoveryGrantsOnlySelectedTaskAndStillYields(string state)
        {
            using (var data = new TempDataFolder())
            {
                var clock = new FakeClock(); var settings = new AppSettings { AutoStartAiTasks = false };
                var queue = new TaskQueue(settings, new MemoryTaskStore(), new RecordingArchive(), clock.Func);
                var host = new FakeDispatchHost(); var target = host.AddVs("A"); host.AddVs("B");
                var selected = queue.Add("A", "A", "选中 / Selected", "用户");
                selected.Status = state; selected.Attempts = state == QueueStatus.Failed ? 1 : 0;
                var other = queue.Add("B", "B", "其他 / Other", "用户");
                var dispatcher = new TaskDispatcher(queue, host, clock.Func, startSettings: () => settings);
                host.ManualReader = _ => Task.FromResult(ManualChatObservation.Draft);
                if (state == QueueStatus.Failed || state == QueueStatus.Cancelled) dispatcher.Retry(selected);
                else dispatcher.DispatchNow(selected);
                await dispatcher.PumpAsync();
                Assert.IsFalse(dispatcher.IsStarted); Assert.IsTrue(dispatcher.CanRun(selected));
                Assert.IsFalse(dispatcher.IsAutomatic(selected)); Assert.IsFalse(dispatcher.CanRun(other));
                CollectionAssert.AreEqual(new[] { target }, host.Refreshed.ToArray());
                Assert.AreEqual(QueueStatus.Waiting, selected.Status); Assert.AreEqual(0, selected.Attempts);
                Assert.AreEqual(0, host.Sent.Count);
                host.ManualReader = _ => Task.FromResult(ManualChatObservation.Idle);
                await dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, selected.Status); Assert.AreEqual(1, selected.Attempts);
                Assert.AreEqual(QueueStatus.Waiting, other.Status); Assert.AreEqual(1, host.Sent.Count);
                Assert.IsFalse(host.Announced.Any(s => s.Contains("automatic start enabled")));
                StringAssert.Contains(File.ReadAllText(TaskQueue.LogPath), "Manual recheck requested");
            }
        }

        [DataTestMethod]
        [DataRow("predecessor")] [DataRow("busy")] [DataRow("save")] [DataRow("sending")]
        public async Task ManualRecheckDoesNotBypassExistingGates(string gate)
        {
            using (var data = new TempDataFolder())
            {
                var clock = new FakeClock(); var settings = new AppSettings();
                var store = new MemoryTaskStore();
                var queue = new TaskQueue(settings, store, new RecordingArchive(), clock.Func);
                var host = new FakeDispatchHost(); host.AddVs("A");
                var earlier = gate == "predecessor" ? queue.Add("A", "A", "前序 / Earlier", "用户") : null;
                var selected = queue.Add("A", "A", "选中 / Selected", "用户");
                if (gate == "busy") host.Busy.Add("A");
                if (gate == "sending") host.Sending = true;
                if (gate == "save") { store.FailWith = "test"; queue.Commit(); }
                var dispatcher = new TaskDispatcher(queue, host, clock.Func, startSettings: () => settings);
                dispatcher.DispatchNow(selected); await dispatcher.PumpAsync();
                Assert.AreEqual(0, host.Sent.Count); Assert.AreEqual(0, selected.Attempts);
                Assert.IsFalse(dispatcher.IsStarted);
                if (earlier != null) Assert.IsFalse(dispatcher.CanRun(earlier));
            }
        }

        [TestMethod]
        public void WrittenOrSubmittedWaitBecomesTerminalAndIsWrappedOnlyOnce()
        {
            foreach (string result in new[] { null, "failed", ManualChatProtection.WaitPrefix, SendRetryPolicy.BlockedPrefix })
            {
                var uncertain = ManualChatProtection.DeliveryResult(true, result);
                StringAssert.StartsWith(uncertain, ManualChatProtection.UncertainPrefix);
                Assert.IsFalse(SendRetryPolicy.IsBlocked(uncertain));
                Assert.AreEqual(SendDecision.Fail, SendRetryPolicy.Decide(0, uncertain));
                Assert.AreEqual(uncertain, ManualChatProtection.DeliveryResult(true, uncertain));
                Assert.AreEqual(result, ManualChatProtection.DeliveryResult(false, result));
            }
            Assert.AreEqual("已发送", ManualChatProtection.DeliveryResult(true, "已发送"));
        }
    }
}
