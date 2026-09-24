using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class ManualChatWaitTests
    {
        private sealed class Fixture : IDisposable
        {
            internal readonly TempDataFolder Data = new TempDataFolder();
            internal readonly AppSettings Settings = new AppSettings();
            internal readonly MemoryTaskStore Store = new MemoryTaskStore();
            internal readonly FakeClock Clock = new FakeClock();
            internal readonly FakeDispatchHost Host = new FakeDispatchHost();
            internal readonly Git Git = new Git();
            internal readonly TaskQueue Queue;
            internal readonly TaskDispatcher Dispatcher;
            internal Fixture()
            {
                Queue = new TaskQueue(Settings, Store, new RecordingArchive(), Clock.Func);
                Dispatcher = new TaskDispatcher(Queue, Host, Clock.Func, Git, () => Settings);
                Host.AddVs("A"); Host.AddVs("B");
            }
            internal QueuedTask Add(string key = "A", string source = "AI")
            {
                var task = Queue.Add(key, key, "测试 / Test " + Queue.NextId, source);
                Dispatcher.AcceptQueued(task, source);
                return task;
            }
            internal void Observe(ManualChatObservation observation) => Host.ManualReader = _ => Task.FromResult(observation);
            internal int WaitNotices => Host.Announced.Count(s => s.Contains("尚未发送"));
            internal int ResumeNotices => Host.Announced.Count(s => s.Contains("已自动继续发送"));
            public void Dispose() => Data.Dispose();
        }
        private sealed class Git : IWorktreeTaskService
        {
            internal int Checks, Integrations;
            public Task CheckDevelopmentAsync(WorktreeInfo info) { Checks++; return Task.CompletedTask; }
            public Task<bool> IntegrateAsync(WorktreeInfo info) { Integrations++; return Task.FromResult(false); }
        }

        [TestMethod]
        public void Settings_DefaultOldJsonAndRoundTrip()
        {
            using (var data = new TempDataFolder())
            {
                Assert.IsTrue(new AppSettings().WaitForManualChat);
                Assert.AreEqual(300, new AppSettings().ManualChatWaitTimeoutSeconds);
                File.WriteAllText(AppSettings.FilePath, "{\"MonitorCopilot\":false,\"WatchConversations\":false}");
                var settings = AppSettings.Load();
                Assert.IsTrue(settings.WaitForManualChat);
                Assert.AreEqual(300, settings.ManualChatWaitTimeoutSeconds);
                settings.WaitForManualChat = false; settings.ManualChatWaitTimeoutSeconds = 42;
                Assert.IsTrue(settings.Save());
                Assert.IsFalse(AppSettings.Load().WaitForManualChat);
                Assert.AreEqual(42, AppSettings.Load().ManualChatWaitTimeoutSeconds);
            }
        }

        [DataTestMethod]
        [DataRow(-1, 300)] [DataRow(0, 300)] [DataRow(1, 10)] [DataRow(10, 10)]
        [DataRow(300, 300)] [DataRow(86400, 86400)] [DataRow(int.MaxValue, 86400)]
        public void Settings_ClampAndSave(int input, int expected)
        {
            using (var data = new TempDataFolder())
            {
                var settings = new AppSettings { ManualChatWaitTimeoutSeconds = input };
                Assert.AreEqual(expected, ManualChatProtection.ClampTimeout(input));
                Assert.IsTrue(settings.Save());
                Assert.AreEqual(expected, settings.ManualChatWaitTimeoutSeconds);
                Assert.AreEqual(expected, AppSettings.Load().ManualChatWaitTimeoutSeconds);
            }
        }

        [DataTestMethod]
        [DataRow(true, true, false, false, ManualChatObservation.Generating)]
        [DataRow(false, true, true, true, ManualChatObservation.Draft)]
        [DataRow(false, true, true, false, ManualChatObservation.Draft)]
        [DataRow(false, true, false, true, ManualChatObservation.Idle)]
        [DataRow(false, true, false, false, ManualChatObservation.Idle)]
        [DataRow(false, false, false, true, ManualChatObservation.Unknown)]
        [DataRow(false, false, false, false, ManualChatObservation.Unknown)]
        public void Observation_DraftOutlivesFocus_EmptyFocusDoesNotBlock(bool busy, bool readable, bool nonempty, bool focus, ManualChatObservation expected) =>
            Assert.AreEqual(expected, ManualChatProtection.Classify(busy, readable, nonempty, focus));

        [DataTestMethod]
        [DataRow(ManualChatObservation.Draft)] [DataRow(ManualChatObservation.Generating)] [DataRow(ManualChatObservation.Unknown)]
        public async Task ActiveObservation_WaitsWithoutChangingQueueState(ManualChatObservation observation)
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); f.Observe(observation);
                int saves = f.Store.SaveCount;
                await f.Dispatcher.PumpAsync(); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Waiting, task.Status);
                Assert.AreEqual(0, task.Attempts); Assert.AreEqual(DateTime.MinValue, task.NextTry);
                Assert.AreEqual(0, f.Host.Sent.Count); Assert.AreEqual(saves, f.Store.SaveCount);
                Assert.AreEqual(1, f.WaitNotices);
                StringAssert.Contains(f.Queue.StatusText(task, f.Clock.Now), "Waiting for manual chat");
                Assert.IsNull(task.Clone().ManualChatWaitReason);
            }
        }

        [TestMethod]
        public async Task Timeout_WarnsOnceAndNeverForcesSendOrFails()
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); f.Observe(ManualChatObservation.Draft);
                await f.Dispatcher.PumpAsync(); f.Clock.Advance(TimeSpan.FromSeconds(299));
                await f.Dispatcher.PumpAsync(); Assert.IsFalse(f.Host.Announced.Any(s => s.Contains("已超时")));
                f.Clock.Advance(TimeSpan.FromSeconds(1));
                await f.Dispatcher.PumpAsync(); f.Clock.Advance(TimeSpan.FromDays(1)); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(1, f.Host.Announced.Count(s => s.Contains("已超时")));
                Assert.AreEqual(QueueStatus.Waiting, task.Status); Assert.AreEqual(0, task.Attempts);
                Assert.AreEqual(0, f.Host.Sent.Count); Assert.IsNull(task.Error);
                StringAssert.Contains(task.ManualChatWaitReason, "no forced send");
            }
        }

        [DataTestMethod]
        [DataRow(false)] [DataRow(true)]
        public async Task Completion_ResumesOnlyAfterActualDelivery_Once(bool manualStart)
        {
            using (var f = new Fixture())
            {
                f.Settings.AutoStartAiTasks = !manualStart;
                f.Observe(ManualChatObservation.Draft);
                var task = f.Add();
                if (manualStart) f.Dispatcher.Start();
                await f.Dispatcher.PumpAsync();
                f.Observe(ManualChatObservation.Idle); f.Host.Busy.Add("A");
                await f.Dispatcher.PumpAsync(); Assert.AreEqual(0, f.ResumeNotices);
                f.Host.Busy.Clear(); f.Host.SendResults.Enqueue(ManualChatProtection.WaitPrefix);
                await f.Dispatcher.PumpAsync(); Assert.AreEqual(0, f.ResumeNotices); Assert.AreEqual(0, task.Attempts);
                await f.Dispatcher.PumpAsync(); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, task.Status); Assert.AreEqual(1, task.Attempts);
                Assert.AreEqual(1, f.ResumeNotices); Assert.IsNull(task.ManualChatWaitReason);
                int reads = f.Host.ManualReads;
                f.Observe(ManualChatObservation.Draft);
                await f.Dispatcher.FinishAsync(task, f.Host.Vs["A"], TimeSpan.FromSeconds(4));
                Assert.AreEqual(QueueStatus.Done, task.Status); Assert.AreEqual(reads, f.Host.ManualReads);
                Assert.AreEqual(1, f.Host.Announced.Count(s => s.Contains("已完成，")));
                StringAssert.Contains(f.Host.NoticeBodies.Last(), "yielding to manual chat");
            }
        }

        [TestMethod]
        public async Task IndependentTargetsAndPredecessors_OnlyEligibleHeadIsProbed()
        {
            using (var f = new Fixture())
            {
                var first = f.Add(); var next = f.Add(); var other = f.Add("B");
                f.Host.ManualReader = v => Task.FromResult(v.Key == "A" ? ManualChatObservation.Draft : ManualChatObservation.Idle);
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Waiting, first.Status); Assert.AreEqual(QueueStatus.Waiting, next.Status);
                Assert.AreEqual(QueueStatus.Running, other.Status); Assert.AreEqual(2, f.Host.ManualReads);
                Assert.IsNull(next.ManualChatWaitReason);
            }
        }

        [TestMethod]
        public async Task ManagerOwnedRunningIsNotManual_ManualBusyIsExplained()
        {
            using (var f = new Fixture())
            {
                var own = f.Add(); await f.Dispatcher.PumpAsync();
                var next = f.Add(); f.Host.Vs["A"].Copilot = CopilotState.Busy;
                await f.Dispatcher.PumpAsync(); Assert.AreEqual(0, f.WaitNotices); Assert.IsNull(next.ManualChatWaitReason);
                f.Host.Vs["B"].Copilot = CopilotState.Busy; var manualTarget = f.Add("B");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(1, f.WaitNotices); StringAssert.Contains(manualTarget.ManualChatWaitReason, "generating");
                Assert.AreEqual(QueueStatus.Running, own.Status);
            }
        }

        [DataTestMethod]
        [DataRow(false, false)] [DataRow(false, true)] [DataRow(true, false)] [DataRow(true, true)]
        public async Task ProtectionIndependentOfWatchAndMonitor(bool watch, bool monitor)
        {
            using (var f = new Fixture())
            {
                f.Settings.WatchConversations = watch; f.Settings.MonitorCopilot = monitor;
                f.Host.Vs["A"].Copilot = CopilotState.Unknown;
                var task = f.Add(); f.Observe(ManualChatObservation.Draft);
                await f.Dispatcher.PumpAsync(); Assert.AreEqual(QueueStatus.Waiting, task.Status);
                f.Observe(ManualChatObservation.Idle); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, task.Status);
            }
        }

        [TestMethod]
        public async Task DisableClearsWait_ButDoesNotBypassBusyOrExistingHostBlockers()
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); f.Observe(ManualChatObservation.Draft); await f.Dispatcher.PumpAsync();
                f.Settings.WaitForManualChat = false; f.Host.Vs["A"].Copilot = CopilotState.Busy;
                await f.Dispatcher.PumpAsync(); Assert.IsNull(task.ManualChatWaitReason);
                Assert.AreEqual(0, f.Host.Sent.Count);
                f.Host.Vs["A"].Copilot = CopilotState.Idle; f.Host.Busy.Add("A");
                await f.Dispatcher.PumpAsync(); Assert.AreEqual(0, f.Host.Sent.Count);
                f.Host.Busy.Clear(); await f.Dispatcher.PumpAsync(); Assert.AreEqual(QueueStatus.Running, task.Status);
            }
        }

        [DataTestMethod]
        [DataRow(ManualChatObservation.Idle)] [DataRow(ManualChatObservation.PaneMissing)]
        public async Task MissingPaneAllowsExistingOpenPath_EmptyIdleAllowsSend(ManualChatObservation observation)
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); f.Observe(observation); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, task.Status);
            }
        }

        [DataTestMethod]
        [DataRow("cancel")] [DataRow("remove")] [DataRow("replace")] [DataRow("path")] [DataRow("busy")] [DataRow("save")]
        [DataRow("pid")] [DataRow("start")]
        public async Task ObservationAwait_RevalidatesBeforeBeginSend(string change)
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); var pending = new TaskCompletionSource<ManualChatObservation>();
                f.Host.ManualReader = _ => pending.Task;
                var pumping = f.Dispatcher.PumpAsync();
                switch (change)
                {
                    case "cancel": Assert.IsTrue(f.Dispatcher.Cancel(task)); break;
                    case "remove": Assert.IsTrue(f.Queue.Remove(task.Id)); break;
                    case "replace": f.Host.AddVs("A"); break;
                    case "pid": f.Host.Vs["A"].Pid++; break;
                    case "start": f.Host.Vs["A"].StartTicks++; break;
                    case "path": f.Host.Vs["A"].SolutionPath = "changed.sln"; break;
                    case "busy": f.Host.Busy.Add("A"); break;
                    case "save": f.Store.FailWith = "模拟保存失败 / Simulated save failure"; f.Queue.Commit(); break;
                }
                pending.SetResult(ManualChatObservation.Idle); await pumping;
                Assert.AreEqual(0, f.Host.Sent.Count); Assert.AreEqual(0, task.Attempts);
                Assert.AreNotEqual(QueueStatus.Sending, task.Status);
            }
        }

        [TestMethod]
        public async Task NewlyResolvedPredecessorDuringProbe_PreventsSend()
        {
            using (var f = new Fixture())
            {
                var earlier = f.Add("B", "用户"); earlier.Status = QueueStatus.WaitingVs;
                var task = f.Add(); var pending = new TaskCompletionSource<ManualChatObservation>();
                f.Host.ManualReader = _ => pending.Task;
                var pumping = f.Dispatcher.PumpAsync();
                earlier.VsKey = "A";
                pending.SetResult(ManualChatObservation.Idle); await pumping;
                Assert.AreEqual(0, f.Host.Sent.Count); Assert.AreEqual(0, task.Attempts);
                Assert.IsFalse(f.Dispatcher.CanRun(earlier));
            }
        }

        [TestMethod]
        public void BoundaryWait_PreservesRetryScheduleAndNeverExhaustsAttempts()
        {
            var task = new QueuedTask { Status = QueueStatus.Sending, Attempts = 20, NextTry = new DateTime(2026, 1, 1) };
            var next = task.NextTry;
            Assert.AreEqual(SendDecision.Retry, TaskStateMachine.ApplySendResult(task, ManualChatProtection.WaitPrefix, DateTime.Now));
            Assert.AreEqual(19, task.Attempts); Assert.AreEqual(next, task.NextTry);
            Assert.AreEqual(QueueStatus.Waiting, task.Status);
            Assert.IsFalse(MainForm.IsPreSubmitImageFailure(ManualChatProtection.WaitPrefix + "未发送图片"));
        }

        [TestMethod]
        public async Task ProbeException_WaitsInsteadOfFailing()
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); f.Host.ManualReader = _ => throw new InvalidOperationException();
                await f.Dispatcher.PumpAsync(); Assert.AreEqual(QueueStatus.Waiting, task.Status);
                Assert.AreEqual(0, task.Attempts); StringAssert.Contains(task.ManualChatWaitReason, "Cannot confirm");
            }
        }

        [TestMethod]
        public async Task BoundaryRace_AttachmentsStayQueued_NoAttemptsOrFallback()
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); task.Attachments = new[] { new AttachmentRef { Name = "test.png" } };
                f.Host.GuardedSender = (v, t, valid) =>
                {
                    Assert.AreSame(task, t); Assert.IsTrue(t.HasAttachments); Assert.IsTrue(valid());
                    return Task.FromResult(ManualChatProtection.WaitPrefix + ManualChatProtection.Reason(ManualChatObservation.Draft));
                };
                await f.Dispatcher.PumpAsync(); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Waiting, task.Status); Assert.AreEqual(0, task.Attempts);
                Assert.AreEqual(0, f.Host.Sent.Count); Assert.AreEqual(1, f.WaitNotices);
            }
        }

        [TestMethod]
        public async Task SendBoundaryTargetChange_ValidatorRejects()
        {
            using (var f = new Fixture())
            {
                var task = f.Add();
                f.Host.GuardedSender = (v, t, valid) =>
                {
                    f.Host.AddVs("A"); Assert.IsFalse(valid());
                    return Task.FromResult(ManualChatProtection.WaitPrefix);
                };
                await f.Dispatcher.PumpAsync(); Assert.AreEqual(QueueStatus.Waiting, task.Status);
                Assert.AreEqual(0, task.Attempts); Assert.AreEqual(0, f.Host.Sent.Count);
            }
        }

        [TestMethod]
        public async Task UncertainSubmission_FailsOnce_NoAutomaticResendOrResumeNotice()
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); f.Observe(ManualChatObservation.Draft); await f.Dispatcher.PumpAsync();
                f.Observe(ManualChatObservation.Idle); f.Host.SendResults.Enqueue(ManualChatProtection.UncertainPrefix);
                await f.Dispatcher.PumpAsync(); f.Clock.Advance(TimeSpan.FromMinutes(10)); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Failed, task.Status); Assert.AreEqual(1, f.Host.Sent.Count);
                Assert.AreEqual(0, f.ResumeNotices);
            }
        }

        [TestMethod]
        public async Task ManualStartAndAiEligibility_PredecessorsNotAuthorizedByWaiting()
        {
            using (var f = new Fixture())
            {
                var manual = f.Add(source: "用户"); var ai = f.Add();
                f.Observe(ManualChatObservation.Draft); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(0, f.Host.ManualReads); Assert.IsFalse(f.Dispatcher.IsStarted);
                Assert.IsFalse(f.Dispatcher.CanRun(manual)); Assert.IsTrue(f.Dispatcher.CanRun(ai));
                f.Dispatcher.DispatchNow(manual); await f.Dispatcher.PumpAsync(); Assert.AreEqual(0, f.Host.ManualReads);
                f.Dispatcher.Start(); await f.Dispatcher.PumpAsync();
                Assert.IsNotNull(manual.ManualChatWaitReason); Assert.IsNull(ai.ManualChatWaitReason);
            }
        }

        [TestMethod]
        public async Task RetryAndDispatchNow_DoNotBypassWait_CancelClearsEpisode()
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); TaskStateMachine.Fail(task, "测试 / Test", f.Clock.Now);
                f.Observe(ManualChatObservation.Draft); f.Dispatcher.Retry(task); await f.Dispatcher.PumpAsync();
                f.Dispatcher.DispatchNow(task); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(0, task.Attempts); Assert.AreEqual(1, f.WaitNotices);
                Assert.IsTrue(f.Dispatcher.Cancel(task)); Assert.IsNull(task.ManualChatWaitReason);
                f.Dispatcher.Retry(task); await f.Dispatcher.PumpAsync(); Assert.AreEqual(2, f.WaitNotices);
            }
        }

        [TestMethod]
        public async Task ClosedOrReplacedTarget_ClearsOldEpisode()
        {
            using (var f = new Fixture())
            {
                var task = f.Add(); f.Observe(ManualChatObservation.Draft); await f.Dispatcher.PumpAsync();
                f.Host.Vs.Remove("A"); await f.Dispatcher.PumpAsync(); Assert.IsNull(task.ManualChatWaitReason);
                f.Host.AddVs("A"); f.Observe(ManualChatObservation.Idle); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, task.Status); Assert.AreEqual(0, f.ResumeNotices);
            }
        }

        [TestMethod]
        public async Task SaveFailure_DoesNotGrantAiOrProbe()
        {
            using (var f = new Fixture())
            {
                f.Store.FailWith = "模拟保存失败 / Simulated save failure";
                var task = f.Add(); f.Observe(ManualChatObservation.Draft); await f.Dispatcher.PumpAsync();
                Assert.IsFalse(f.Dispatcher.CanRun(task)); Assert.AreEqual(0, f.Host.ManualReads);
                Assert.AreEqual(0, f.Host.Sent.Count);
            }
        }

        [TestMethod]
        public async Task ParkedAndWorktreeIntegration_WaitBeforeGit()
        {
            using (var f = new Fixture())
            {
                var task = f.Add();
                task.Status = QueueStatus.WaitingVs;
                task.Worktree = new WorktreeInfo { SolutionPath = "lane.sln", Root = f.Data.File("lane"), MainRoot = f.Data.File("main") };
                task.IsWorktreeMerge = true; f.Host.Vs["A"].SolutionPath = task.Worktree.SolutionPath;
                f.Observe(ManualChatObservation.Draft); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Waiting, task.Status); Assert.AreEqual(0, f.Git.Integrations);
                Assert.AreEqual(0, f.Git.Checks); Assert.AreEqual(0, task.Attempts);
                f.Observe(ManualChatObservation.Idle); await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Done, task.Status); Assert.AreEqual(1, f.Git.Integrations);
                Assert.AreEqual(0, f.ResumeNotices);
            }
        }
    }
}
