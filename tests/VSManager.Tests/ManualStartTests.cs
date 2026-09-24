using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class ManualStartTests
    {
        [DataTestMethod]
        [DataRow(QueueStatus.Waiting, false)]
        [DataRow(QueueStatus.Sending, false)]
        [DataRow(QueueStatus.Running, false)]
        [DataRow(QueueStatus.WaitingVs, false)]
        [DataRow(QueueStatus.Waiting, true)]
        [DataRow(QueueStatus.Sending, true)]
        [DataRow(QueueStatus.Running, true)]
        [DataRow(QueueStatus.WaitingVs, true)]
        public async Task RestoredTasks_AllAutomaticEntrypointsWaitForStart(string savedStatus, bool merge)
        {
            using (var data = new TempDataFolder())
            {
                var clock = new FakeClock();
                var store = new JsonTaskStore(data.File("tasks.json"));
                var info = new WorktreeInfo { Root = data.File("lane"), MainRoot = data.File("main"),
                    SolutionPath = data.File("lane\\Project.slnx"), Branch = "refs/heads/task/lane", MainBranch = "refs/heads/main" };
                var original = new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Target = "A", Text = "restored",
                    Status = savedStatus, Created = clock.Now, Started = clock.Now.AddMinutes(-5), SawBusy = true,
                    Attempts = 1, CompletionToken = "original-attempt", Worktree = merge ? info : null,
                    IsWorktreeMerge = merge, WorktreeBatch = merge ? 1 : 0 };
                Assert.IsNull(store.Save(new[] { original }));
                var queue = new TaskQueue(new AppSettings(), store, new RecordingArchive(), clock.Func);
                var restored = queue.Find(1);
                var host = new FakeDispatchHost();
                var git = new RecordingWorktrees();
                var dispatcher = new TaskDispatcher(queue, host, clock.Func, git);
                int reads = 0;
                host.AnswerReader = t => { reads++; return Task.FromResult("done\n" + TaskStateMachine.SuccessReceipt(t)); };
                queue.Changed += dispatcher.Pump;
                await dispatcher.PumpAsync();
                var vs = host.AddVs("A");
                vs.SolutionPath = info.SolutionPath;
                host.AddVs("B");
                var added = queue.Add("B", "B", "new AI task", "AI");
                var failed = queue.Add("C", "C", "failed", "用户");
                TaskStateMachine.Fail(failed, "existing failure", clock.Now);
                queue.Commit();
                clock.Advance(TimeSpan.FromMinutes(2));
                for (int i = 0; i < 3; i++)
                {
                    await dispatcher.PumpAsync();
                    dispatcher.DispatchNow(restored);
                    dispatcher.Retry(failed);
                    await dispatcher.FinishAsync(restored, vs, TimeSpan.FromMinutes(1));
                }
                Assert.IsFalse(dispatcher.IsStarted);
                Assert.AreEqual(false, host.LastActivity);
                Assert.AreEqual(savedStatus == QueueStatus.Sending ? QueueStatus.Waiting : savedStatus, restored.Status);
                Assert.AreEqual(QueueStatus.Waiting, added.Status);
                Assert.AreEqual(QueueStatus.Failed, failed.Status);
                Assert.AreEqual(1, restored.Attempts);
                Assert.AreEqual("original-attempt", restored.CompletionToken);
                Assert.AreEqual(0, reads);
                Assert.AreEqual(0, host.Sent.Count);
                Assert.AreEqual(0, git.Integrations);
                Assert.AreEqual(0, git.Checks);
                Assert.IsTrue(host.Status.Any(s => s == TaskDispatcher.WaitingForStart));
                Assert.AreEqual(0, host.Announced.Count);

                dispatcher.Start();
                dispatcher.Start();
                await dispatcher.PumpAsync();
                Assert.IsTrue(dispatcher.IsStarted);
                Assert.AreEqual(QueueStatus.Running, added.Status);
                Assert.AreEqual(merge || savedStatus == QueueStatus.Running ? QueueStatus.Done : QueueStatus.Running, restored.Status);
                Assert.AreEqual(merge ? 1 : 0, git.Integrations);
                Assert.AreEqual(savedStatus == QueueStatus.Running ? 1 : 0, reads);
                Assert.AreEqual(merge || savedStatus == QueueStatus.Running ? 1 : 2, host.Sent.Count);
                if (savedStatus == QueueStatus.Running) Assert.AreEqual("original-attempt", restored.CompletionToken);
            }
        }

        [TestMethod]
        public async Task StartIsSessionOnly_RestartKeepsRunningTaskAndQueuedSuccessor()
        {
            using (var data = new TempDataFolder())
            {
                var clock = new FakeClock();
                var store = new JsonTaskStore(data.File("tasks.json"));
                var settings = new AppSettings();
                var queue = new TaskQueue(settings, store, new RecordingArchive(), clock.Func);
                var first = queue.Add("A", "A", "first", "AI");
                var second = queue.Add("A", "A", "second", "AI");
                var host = new FakeDispatchHost();
                host.AddVs("A");
                var dispatcher = new TaskDispatcher(queue, host, clock.Func);
                dispatcher.Start();
                await dispatcher.PumpAsync();
                Assert.AreEqual(1, host.Sent.Count);
                string token = first.CompletionToken;
                queue = new TaskQueue(settings, store, new RecordingArchive(), clock.Func);
                dispatcher = new TaskDispatcher(queue, host, clock.Func);
                clock.Advance(TimeSpan.FromMinutes(10));
                await dispatcher.PumpAsync();
                Assert.IsFalse(dispatcher.IsStarted);
                Assert.AreEqual(QueueStatus.Running, queue.Find(first.Id).Status);
                Assert.AreEqual(QueueStatus.Waiting, queue.Find(second.Id).Status);
                Assert.AreEqual(1, host.Sent.Count);
                host.Vs["A"].Copilot = CopilotState.Busy;
                dispatcher.Start();
                await dispatcher.PumpAsync();
                Assert.AreEqual(token, queue.Find(first.Id).CompletionToken);
                Assert.AreEqual(1, host.Sent.Count);
                Assert.IsTrue(queue.Find(first.Id).SawBusy);
                host.Vs["A"].Copilot = CopilotState.Idle;
                await dispatcher.FinishAsync(queue.Find(first.Id), host.Vs["A"], null);
                Assert.AreEqual(QueueStatus.Done, queue.Find(first.Id).Status);
                Assert.AreEqual(QueueStatus.Running, queue.Find(second.Id).Status);
                Assert.AreEqual(2, host.Sent.Count);
            }
        }

        [TestMethod]
        public async Task PausedCompletion_WaitsForTrackingReadiness_AndDoesNotResend()
        {
            using (var data = new TempDataFolder())
            {
                var clock = new FakeClock();
                var queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), clock.Func);
                var task = queue.Add("A", "A", "running", "AI");
                TaskStateMachine.BeginSend(task, "A");
                TaskStateMachine.ApplySendResult(task, "已发送", clock.Now);
                task.SawBusy = true;
                var host = new FakeDispatchHost { ReadyAt = clock.Now.AddSeconds(45) };
                var vs = host.AddVs("A");
                var dispatcher = new TaskDispatcher(queue, host, clock.Func);
                await dispatcher.FinishAsync(task, vs, null);
                dispatcher.Start();
                await dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, task.Status);
                clock.Advance(TimeSpan.FromSeconds(46));
                host.Busy.Add("A");
                await dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, task.Status);
                host.Busy.Clear();
                await dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Done, task.Status);
                Assert.AreEqual(0, host.Sent.Count);
            }
        }

        [TestMethod]
        public async Task CrashInterruptedSend_StillRequiresExplicitRetryAfterStart()
        {
            using (var data = new TempDataFolder())
            {
                var clock = new FakeClock();
                var store = new MemoryTaskStore();
                store.Initial.Add(new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Text = "interrupted", Status = QueueStatus.Sending });
                var queue = new TaskQueue(new AppSettings(), store, new RecordingArchive(), clock.Func);
                Assert.AreEqual(1, queue.PauseInterruptedSends("may have been delivered"));
                var host = new FakeDispatchHost();
                host.AddVs("A");
                var dispatcher = new TaskDispatcher(queue, host, clock.Func);
                var task = queue.Find(1);
                dispatcher.Retry(task);
                await dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Failed, task.Status);
                dispatcher.Start();
                await dispatcher.PumpAsync();
                Assert.AreEqual(0, host.Sent.Count);
                dispatcher.Retry(task);
                Assert.AreEqual(QueueStatus.Running, task.Status);
                Assert.AreEqual(1, host.Sent.Count);
            }
        }

        private sealed class RecordingWorktrees : IWorktreeTaskService
        {
            internal int Checks, Integrations;
            public Task CheckDevelopmentAsync(WorktreeInfo info) { Checks++; return Task.CompletedTask; }
            public Task<bool> IntegrateAsync(WorktreeInfo info) { Integrations++; return Task.FromResult(false); }
        }
    }
}
