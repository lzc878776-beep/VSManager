using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>任务调度：发布、重试、失败、完成与 VS 关闭。/ Dispatch: publish, retry, failure, completion and closed VS.</summary>
    [TestClass]
    public class TaskDispatcherTests
    {
        private TempDataFolder _data;
        private FakeClock _clock;
        private MemoryTaskStore _store;
        private RecordingArchive _archive;
        private TaskQueue _queue;
        private FakeDispatchHost _host;
        private TaskDispatcher _dispatcher;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _clock = new FakeClock();
            _store = new MemoryTaskStore();
            _archive = new RecordingArchive();
            _queue = new TaskQueue(new AppSettings(), _store, _archive, _clock.Func);
            _host = new FakeDispatchHost();
            _dispatcher = new TaskDispatcher(_queue, _host, _clock.Func);
            _dispatcher.Start();
        }

        [TestCleanup]
        public void Cleanup() => _data.Dispose();

        [TestMethod]
        public async Task Publishes_OldestTaskPerIdleVs()
        {
            _host.AddVs("A");
            _host.AddVs("B");
            var a1 = _queue.Add("A", "A", "a1", "AI");
            var a2 = _queue.Add("A", "A", "a2", "AI");
            var b1 = _queue.Add("B", "B", "b1", "用户");
            await _dispatcher.PumpAsync();
            CollectionAssert.AreEqual(new[] { "A:" + TaskStateMachine.DispatchText(a1), "B:" + TaskStateMachine.DispatchText(b1) }, _host.Sent.ToArray());
            Assert.AreEqual(QueueStatus.Running, a1.Status);
            Assert.AreEqual(_clock.Now, a1.Started);
            Assert.AreEqual(QueueStatus.Waiting, a2.Status, "同一 VS 一次只执行一个任务 / one task per VS at a time");
            Assert.AreEqual(QueueStatus.Running, b1.Status);
            Assert.AreEqual(true, _host.LastActivity);
            Assert.IsTrue(_host.Status.Any(s => s.Contains("#1 已发布到「A」")));
        }

        [TestMethod]
        public async Task BusyOrClosedVs_KeepsTaskWaiting()
        {
            _host.AddVs("A", CopilotState.Busy);
            var a = _queue.Add("A", "A", "a", "AI");
            var c = _queue.Add("C", "C", "c", "AI");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(0, _host.Sent.Count);
            Assert.AreEqual(QueueStatus.Waiting, a.Status);
            Assert.AreEqual(QueueStatus.Waiting, c.Status);
            Assert.AreEqual(0, a.Attempts);
        }

        [TestMethod]
        public async Task WhileSending_NothingIsDispatched()
        {
            _host.AddVs("A");
            _queue.Add("A", "A", "a", "AI");
            _host.Sending = true;
            await _dispatcher.PumpAsync();
            Assert.AreEqual(0, _host.Sent.Count);
        }

        [TestMethod]
        public async Task SendFailure_RetriesAfterDelay_ThenFailsOnThirdAttempt()
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            for (int i = 0; i < 3; i++) _host.SendResults.Enqueue("发送失败：找不到输入框");

            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
            Assert.AreEqual(1, t.Attempts);
            Assert.IsTrue(_host.Status.Last().EndsWith("30 秒后重试"));

            await _dispatcher.PumpAsync();
            Assert.AreEqual(1, _host.Sent.Count, "未到重试时间不发送 / not before the retry time");

            _clock.Advance(TimeSpan.FromSeconds(31));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(2, t.Attempts);
            _clock.Advance(TimeSpan.FromSeconds(31));
            await _dispatcher.PumpAsync();

            Assert.AreEqual(3, _host.Sent.Count);
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual("发送失败：找不到输入框", t.Error);
            Assert.AreEqual(_clock.Now, t.Finished);
            Assert.AreEqual(1, _host.Notices.Count);
            StringAssert.Contains(_host.Notices[0], "任务 #1 失败");
            Assert.AreEqual(false, _host.LastActivity);
        }

        [TestMethod]
        public async Task ModalDialog_WaitsWithoutFailing_ThenResumesOriginalTask()
        {
            _host.AddVs("A");
            var first = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            for (int i = 0; i < 5; i++)
            {
                _host.SendResults.Enqueue(SendRetryPolicy.BlockedPrefix + "dialog");
                await _dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Waiting, first.Status);
                Assert.AreEqual(0, first.Attempts);
                Assert.AreEqual(QueueStatus.Waiting, next.Status);
                Assert.AreEqual(i + 1, _host.Sent.Count);
                Assert.AreEqual(0, _host.Notices.Count);
                Assert.AreEqual(true, _host.LastActivity);
                await _dispatcher.PumpAsync();
                Assert.AreEqual(i + 1, _host.Sent.Count, "Must respect the dialog polling delay");
                _clock.Advance(SendRetryPolicy.BlockedRetryDelay);
            }
            _host.AddVs("B");
            var other = _queue.Add("B", "B", "independent", "用户");
            _host.SendResults.Enqueue(SendRetryPolicy.BlockedPrefix + "dialog");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, other.Status);
            Assert.AreEqual(QueueStatus.Waiting, first.Status);
            _clock.Advance(SendRetryPolicy.BlockedRetryDelay);
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, first.Status);
            Assert.AreEqual(1, first.Attempts);
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.IsNull(first.Error);
            Assert.AreSame(first, _queue.Find(first.Id));
        }

        [TestMethod]
        public async Task UserTaskFailure_DoesNotNotifyAgent()
        {
            var t = _queue.Add("A", "A", "a", "用户");
            _host.AddVs("A");
            for (int i = 0; i < 3; i++) _host.SendResults.Enqueue("失败");
            for (int i = 0; i < 3; i++) { await _dispatcher.PumpAsync(); _clock.Advance(TimeSpan.FromSeconds(31)); }
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual(0, _host.Notices.Count);
        }

        [TestMethod]
        public async Task RunningTask_FailsWhenVsClosedLongerThan15Seconds()
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            await _dispatcher.PumpAsync();
            _host.Vs.Remove("A");
            _clock.Advance(TimeSpan.FromSeconds(10));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, t.Status);
            _clock.Advance(TimeSpan.FromSeconds(6));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual(TaskStateMachine.VsClosedError, t.Error);
        }

        [TestMethod]
        public async Task RunningTask_BusyThenIdle_IsNotAssumedDoneByTimeout()
        {
            var v = _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            await _dispatcher.PumpAsync();
            v.Copilot = CopilotState.Busy;
            await _dispatcher.PumpAsync();
            Assert.IsTrue(t.SawBusy);
            v.Copilot = CopilotState.Idle;
            _clock.Advance(TimeSpan.FromMinutes(5));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, t.Status, "完成由忙→闲的状态变化驱动 / completion is driven by busy→idle");
        }

        [TestMethod]
        public async Task RunningTask_NeverBusy_RequiresSuccessReceiptAfter30Seconds()
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            var next = _queue.Add("A", "A", "b", "AI");
            await _dispatcher.PumpAsync();
            _clock.Advance(TimeSpan.FromSeconds(31));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Done, t.Status);
            Assert.AreEqual("完成了", t.Result);
            Assert.AreEqual(QueueStatus.Running, next.Status, "完成后继续发布下一个 / next task published after completion");
        }

        [TestMethod]
        public async Task Finish_WaitsForReply_BeforeDispatchingNext_AndIgnoresDuplicateCompletion()
        {
            var v = _host.AddVs("A");
            var first = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            await _dispatcher.PumpAsync();
            var reply = new TaskCompletionSource<string>();
            int reads = 0;
            _host.AnswerReader = t => { reads++; return reply.Task; };
            var finishing = _dispatcher.FinishAsync(first, v, null);
            await _dispatcher.FinishAsync(first, v, null);
            _clock.Advance(TimeSpan.FromMinutes(1));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, first.Status);
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.AreEqual(1, reads);
            Assert.AreEqual(1, _host.Sent.Count);
            Assert.IsFalse(_archive.Events.Contains("#1:done"));

            reply.SetResult("Completed\r\n" + TaskStateMachine.SuccessReceipt(first));
            await finishing;
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Done, first.Status);
            Assert.AreEqual("Completed", first.Result);
            Assert.AreEqual(QueueStatus.Running, next.Status);
            Assert.AreEqual(2, _host.Sent.Count);
        }

        [TestMethod]
        public async Task StrictFailure_BlocksTargetUntilSuperseded_ResendRemainsAtTail()
        {
            _queue.SkipFailedPredecessors = false;
            var v = _host.AddVs("A");
            _host.AddVs("B");
            var first = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            await _dispatcher.PumpAsync();
            _dispatcher.Fail(first, "execution failed");
            var independent = _queue.Add("B", "B", "independent", "AI");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.AreEqual(QueueStatus.Running, independent.Status);
            _dispatcher.DispatchNow(next);
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.AreEqual(0, _host.Announced.Count);
            StringAssert.Contains(_host.NoticeBodies.Single(), "Strict mode");

            var retry = _queue.Add("A", "A", "resend #1: corrected first task", "AI");
            Assert.AreSame(first, _queue.Find(first.Id));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, next.Status);
            Assert.AreEqual(QueueStatus.Waiting, retry.Status);
            Assert.AreEqual(QueueStatus.Failed, first.Status);
            await _dispatcher.FinishAsync(next, v, null);
            Assert.AreEqual(QueueStatus.Running, retry.Status);
            _dispatcher.Retry(retry);
            Assert.AreEqual(1, retry.Attempts);
            Assert.AreEqual(4, _host.Sent.Count);
        }

        [TestMethod]
        public async Task ReadException_FailsTask_WithoutReleasingSuccessor()
        {
            _queue.SkipFailedPredecessors = false;
            var v = _host.AddVs("A");
            var first = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            await _dispatcher.PumpAsync();
            _host.AnswerReader = t => Task.FromException<string>(new InvalidOperationException("read failed"));
            await _dispatcher.FinishAsync(first, v, null);
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, first.Status);
            StringAssert.Contains(first.Error, "read failed");
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.AreEqual(1, _host.Sent.Count);
        }

        [TestMethod]
        public async Task SendException_FailsTask_WithoutAutomaticResendOrSuccessor()
        {
            _queue.SkipFailedPredecessors = false;
            _host.AddVs("A");
            var first = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            _host.SendException = new InvalidOperationException("unknown delivery");
            await _dispatcher.PumpAsync();
            _clock.Advance(TimeSpan.FromMinutes(2));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, first.Status);
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.AreEqual(1, _host.Sent.Count);
        }

        [TestMethod]
        public async Task IdleTimeout_WithoutSuccessReceipt_DoesNotReleaseSuccessor()
        {
            _queue.SkipFailedPredecessors = false;
            _host.AddVs("A");
            var first = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            _host.IncludeSuccessReceipt = false;
            _host.Answer = "Request failed";
            await _dispatcher.PumpAsync();
            _clock.Advance(TimeSpan.FromSeconds(31));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, first.Status);
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.AreEqual(1, _host.Sent.Count);
        }

        [TestMethod]
        public async Task DefaultFailure_AnnouncesSkipOnlyOnDelivery_Once_WithoutChangingHistory()
        {
            var v = _host.AddVs("A");
            var failed = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            failed.Result = "original result";
            failed.Started = _clock.Now.AddMinutes(-1);
            _dispatcher.Fail(failed, "original error");
            var snapshot = failed.Clone();
            StringAssert.Contains(_host.NoticeBodies.Single(), "Failed predecessors are skipped");
            StringAssert.Contains(_host.NoticeBodies.Single(), "do not duplicate queued tasks or automatically retry failed tasks");
            Assert.IsFalse(_host.NoticeBodies.Single().Contains("后续任务已暂停"));

            _host.SendResults.Enqueue(SendRetryPolicy.BlockedPrefix + "dialog");
            await _dispatcher.PumpAsync();
            Assert.IsNull(next.PredecessorNotice);
            Assert.AreEqual(0, _host.Announced.Count);
            Assert.IsFalse(_host.Events.Any(e => e.Contains("skipped and continued")));
            _clock.Advance(SendRetryPolicy.BlockedRetryDelay);
            _host.SendResults.Enqueue("failed send");
            await _dispatcher.PumpAsync();
            Assert.IsNull(next.PredecessorNotice);
            Assert.AreEqual(0, _host.Announced.Count);
            Assert.IsFalse(_host.Events.Any(e => e.Contains("skipped and continued")));

            string uiNotice = null;
            _queue.Changed += () => { if (next.Status == QueueStatus.Running) uiNotice = next.PredecessorNotice; };
            _clock.Advance(SendRetryPolicy.RetryDelay);
            await _dispatcher.PumpAsync();
            const string notice = "前序 @1 失败，已跳过继续 / Predecessor @1 failed; skipped and continued";
            Assert.AreEqual(QueueStatus.Running, next.Status);
            Assert.AreEqual(notice, next.PredecessorNotice);
            Assert.AreEqual(notice, uiNotice);
            Assert.AreEqual(notice, _host.Announced.Single());
            StringAssert.Contains(_host.Status.Last(), notice);
            Assert.AreEqual(1, _host.Events.Count(e => e.Contains(notice)));
            StringAssert.Contains(System.IO.File.ReadAllText(TaskQueue.LogPath), notice);
            await _dispatcher.PumpAsync();
            Assert.AreEqual(1, _host.Announced.Count);
            Assert.AreEqual(1, _host.Events.Count(e => e.Contains(notice)));
            Assert.AreEqual(1, System.IO.File.ReadAllLines(TaskQueue.LogPath).Count(line => line.Contains(notice)));
            Assert.IsNull(_store.Saved.Single(t => t.Id == next.Id).PredecessorNotice);
            Assert.AreSame(failed, _queue.Find(failed.Id));
            var stored = _store.Saved.Single(t => t.Id == failed.Id);
            Assert.AreEqual(snapshot.Status, stored.Status);
            Assert.AreEqual(snapshot.Error, stored.Error);
            Assert.AreEqual(snapshot.Result, stored.Result);
            Assert.AreEqual(snapshot.Started, stored.Started);
            Assert.AreEqual(snapshot.Finished, stored.Finished);
            Assert.AreEqual(snapshot.Attempts, stored.Attempts);
            Assert.IsFalse(_archive.Events.Contains("#1:removed"));
            await _dispatcher.FinishAsync(next, v, null);
            StringAssert.Contains(_host.NoticeBodies.Last(), notice);
        }

        [TestMethod]
        public async Task MissingReceipt_RemainsFailed_ButSuccessorContinuesByDefault()
        {
            _host.AddVs("A");
            var failed = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            _host.IncludeSuccessReceipt = false;
            _host.Answer = "unconfirmed result";
            await _dispatcher.PumpAsync();
            _clock.Advance(TimeSpan.FromSeconds(31));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, failed.Status);
            Assert.AreEqual("unconfirmed result", failed.Result);
            StringAssert.Contains(failed.Error, "No success receipt");
            Assert.AreEqual(QueueStatus.Running, next.Status);
            Assert.IsFalse(_archive.Events.Contains("#1:done"));
            Assert.IsTrue(_archive.Events.Contains("#1:failed"));
            Assert.AreEqual(2, _host.Sent.Count);
            Assert.AreEqual(1, _host.Announced.Count);
        }

        [TestMethod]
        public async Task TerminalSendFailures_AllowNextPumpWithoutAnnouncingFailedDelivery()
        {
            _host.AddVs("A");
            var failed = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            failed.Attempts = SendRetryPolicy.MaxAttempts - 1;
            _host.SendResults.Enqueue("send failed");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, failed.Status);
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.AreEqual(0, _host.Announced.Count);
            _host.SendException = new InvalidOperationException("unknown delivery");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, next.Status);
            Assert.IsNull(next.PredecessorNotice);
            Assert.AreEqual(0, _host.Announced.Count);
            Assert.IsFalse(_host.Events.Any(e => e.Contains("skipped and continued")));
            _host.SendException = null;
            var third = _queue.Add("A", "A", "third", "用户");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, third.Status);
            StringAssert.Contains(third.PredecessorNotice, "@1, @2");
            Assert.AreEqual(1, _host.Announced.Count);
            Assert.AreEqual(3, _host.Sent.Count);
        }

        [TestMethod]
        public async Task RuntimePolicyChange_ResumesWithoutRestart()
        {
            _host.AddVs("A");
            var failed = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            _dispatcher.Fail(failed, "failed");
            _queue.SkipFailedPredecessors = false;
            await _dispatcher.PumpAsync();
            Assert.AreEqual(0, _host.Sent.Count);
            Assert.AreEqual(0, _host.Announced.Count);
            _queue.SkipFailedPredecessors = true;
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, next.Status);
            Assert.AreEqual(1, _host.Announced.Count);
        }

        [TestMethod]
        public async Task RetryingOldFailure_DoesNotOverlapNewerRunningTask()
        {
            _host.AddVs("A");
            var failed = _queue.Add("A", "A", "first", "AI");
            var next = _queue.Add("A", "A", "next", "AI");
            _dispatcher.Fail(failed, "failed");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, next.Status);
            _dispatcher.Retry(failed);
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Waiting, failed.Status);
            Assert.AreEqual(QueueStatus.Running, next.Status);
            Assert.AreEqual(1, _host.Sent.Count);
        }

        [TestMethod]
        public async Task CancellationDuringReplyRead_IsNotOverwrittenByLateSuccess()
        {
            var v = _host.AddVs("A");
            var first = _queue.Add("A", "A", "first", "AI");
            await _dispatcher.PumpAsync();
            var reply = new TaskCompletionSource<string>();
            _host.AnswerReader = t => reply.Task;
            var finishing = _dispatcher.FinishAsync(first, v, null);
            Assert.IsTrue(_dispatcher.Cancel(first));
            _dispatcher.Retry(first);
            Assert.AreEqual(QueueStatus.Cancelled, first.Status);
            reply.SetResult("Completed\r\n" + TaskStateMachine.SuccessReceipt(first));
            await finishing;
            Assert.AreEqual(QueueStatus.Cancelled, first.Status);
            Assert.AreEqual(0, _host.Notices.Count);
        }

        [TestMethod]
        public async Task TrackingNotReady_RunningTasksAreNotChecked()
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            await _dispatcher.PumpAsync();
            _host.Vs.Remove("A");
            _host.ReadyAt = _clock.Now.AddHours(1);
            _clock.Advance(TimeSpan.FromMinutes(1));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, t.Status);
        }

        [TestMethod]
        public async Task Finish_StoresAnswer_AndNotifiesAgent()
        {
            var v = _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            await _dispatcher.PumpAsync();
            _host.Answer = new string('x', 2000);
            _clock.Advance(TimeSpan.FromSeconds(65));
            _dispatcher.Finish(t, v, null);
            Assert.AreEqual(QueueStatus.Done, t.Status);
            Assert.AreEqual(1500 + 1, t.Result.Length, "结果截断到 1500 字并加「…」/ result clipped to 1500 chars plus \"…\"");
            Assert.IsTrue(t.Result.EndsWith("…"));
            Assert.AreEqual(1, _host.Notices.Count);
            StringAssert.Contains(_host.Notices[0], "任务 #1 已完成");
            StringAssert.Contains(_host.Notices[0], TextUtil.FormatDuration(TimeSpan.FromSeconds(65)));

            _dispatcher.Finish(t, v, null); // 重复完成被忽略 / a second finish is ignored
            Assert.AreEqual(1, _host.Notices.Count);
            Assert.AreEqual(1, _archive.Events.Count(e => e == "#1:done"));
        }

        [TestMethod]
        public async Task Finish_UnreadableAnswer_FailsAndBlocksSuccessor()
        {
            _queue.SkipFailedPredecessors = false;
            var v = _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "用户");
            var next = _queue.Add("A", "A", "b", "用户");
            await _dispatcher.PumpAsync();
            _host.Answer = "  ";
            await _dispatcher.FinishAsync(t, v, TimeSpan.FromSeconds(3));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual(QueueStatus.Waiting, next.Status);
            Assert.AreEqual(1, _host.Sent.Count);
            Assert.AreEqual(0, _host.Notices.Count);
        }

        [TestMethod]
        public async Task CancelAndRetry()
        {
            _host.AddVs("A", CopilotState.Busy);
            var t = _queue.Add("A", "A", "a", "AI");
            Assert.IsTrue(_dispatcher.Cancel(t));
            Assert.AreEqual(QueueStatus.Cancelled, t.Status);
            Assert.IsFalse(_dispatcher.Cancel(t));

            _host.Vs["A"].Copilot = CopilotState.Idle;
            _dispatcher.Retry(t);
            await Task.Yield();
            Assert.AreEqual(QueueStatus.Running, t.Status);
            Assert.AreEqual(1, t.Attempts);
        }

        [TestMethod]
        public void DispatchNow_ExplainsWhyItCannotPublish()
        {
            _dispatcher.DispatchNow(_queue.Add("Z", "Z", "z", "AI"));
            StringAssert.Contains(_host.Status.Last(), "当前未打开");

            _host.AddVs("A", CopilotState.Busy);
            _dispatcher.DispatchNow(_queue.Add("A", "A", "a", "AI"));
            StringAssert.Contains(_host.Status.Last(), "仍在忙");
        }

        [TestMethod]
        public async Task DispatchNow_SkipsRetryDelay()
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "a", "AI");
            _host.SendResults.Enqueue("失败");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
            _dispatcher.DispatchNow(t);
            Assert.AreEqual(QueueStatus.Running, t.Status);
            Assert.AreEqual(2, t.Attempts);
        }
    }
}
