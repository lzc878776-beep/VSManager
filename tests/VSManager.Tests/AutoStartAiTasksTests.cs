using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AutoStartAiTasksTests
    {
        private sealed class Fixture : IDisposable
        {
            internal readonly TempDataFolder Data = new TempDataFolder();
            internal readonly AppSettings Settings = new AppSettings();
            internal readonly MemoryTaskStore Store = new MemoryTaskStore();
            internal readonly FakeClock Clock = new FakeClock();
            internal readonly FakeDispatchHost Host = new FakeDispatchHost();
            internal readonly Worktrees Git = new Worktrees();
            internal TaskQueue Queue;
            internal TaskDispatcher Dispatcher;
            internal Fixture() { Reload(); }
            internal void Reload()
            {
                Queue = new TaskQueue(Settings, Store, new RecordingArchive(), Clock.Func);
                Dispatcher = new TaskDispatcher(Queue, Host, Clock.Func, Git, () => Settings);
                Dispatcher.ApplyAutomaticStart();
            }
            internal QueuedTask Add(string key = "A", string source = "AI", bool accept = true)
            {
                var task = Queue.Add(key, key, "测试任务 / Test task " + Queue.NextId, source);
                if (accept) Dispatcher.AcceptQueued(task, source);
                return task;
            }
            internal WorktreeInfo Lane()
            {
                var info = new WorktreeInfo { Root = Data.File("lane"), MainRoot = Data.File("main"),
                    SolutionPath = Data.File("lane\\Demo.slnx"), Branch = "refs/heads/task/demo", MainBranch = "refs/heads/main" };
                Queue.ResolveWorktree = key => key == "A" ? info : null;
                Host.AddVs("A").SolutionPath = info.SolutionPath;
                return info;
            }
            public void Dispose() => Data.Dispose();
        }

        private sealed class Worktrees : IWorktreeTaskService
        {
            internal int Checks, Integrations;
            internal TaskCompletionSource<bool> CheckGate;
            internal bool Conflicts;
            public Task CheckDevelopmentAsync(WorktreeInfo info)
            {
                Checks++;
                return CheckGate?.Task ?? Task.CompletedTask;
            }
            public Task<bool> IntegrateAsync(WorktreeInfo info) { Integrations++; return Task.FromResult(Conflicts); }
        }

        [TestMethod]
        public void Settings_DefaultsAndOldJson_EnableOnlyAi()
        {
            using (var data = new TempDataFolder())
            {
                Assert.IsTrue(new AppSettings().AutoStartAiTasks);
                Assert.IsFalse(new AppSettings().AutoStartAllTasks);
                Assert.IsTrue(AppSettings.Load().AutoStartAiTasks);
                File.WriteAllText(AppSettings.FilePath, "{\"PollMs\":2000}");
                var settings = AppSettings.Load();
                Assert.IsTrue(settings.AutoStartAiTasks);
                Assert.IsFalse(settings.AutoStartAllTasks);
                Assert.AreEqual(2000, settings.PollMs);
            }
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public void Settings_RoundTrip(bool ai, bool all)
        {
            using (var data = new TempDataFolder())
            {
                Assert.IsTrue(new AppSettings { AutoStartAiTasks = ai, AutoStartAllTasks = all }.Save());
                var loaded = AppSettings.Load();
                Assert.AreEqual(ai, loaded.AutoStartAiTasks);
                Assert.AreEqual(all, loaded.AutoStartAllTasks);
            }
        }

        [TestMethod]
        public async Task OnlyExplicitPersistedAiAdmission_RunsWithoutGlobalStart()
        {
            using (var f = new Fixture())
            {
                f.Host.AddVs("A"); f.Host.AddVs("B"); f.Host.AddVs("C");
                var manual = f.Add("A", "用户");
                var unsubmitted = f.Add("B", accept: false);
                var ai = f.Add("C");
                await f.Dispatcher.PumpAsync();
                Assert.IsFalse(f.Dispatcher.IsStarted);
                Assert.IsFalse(f.Dispatcher.CanRun(manual));
                Assert.IsFalse(f.Dispatcher.CanRun(unsubmitted));
                Assert.AreEqual(QueueStatus.Running, ai.Status);
                Assert.AreEqual(1, f.Host.Sent.Count);
                Assert.IsTrue(f.Dispatcher.HasDispatchActivity);
                Assert.AreEqual(true, f.Host.LastActivity);
                Assert.AreEqual(1, f.Host.Announced.Count);
                StringAssert.Contains(f.Host.Announced.Single(), "自动启动");
                StringAssert.Contains(f.Host.Announced.Single(), "automatic start");
            }
        }

        [DataTestMethod]
        [DataRow(QueueStatus.Waiting)]
        [DataRow(QueueStatus.Running)]
        [DataRow(QueueStatus.WaitingVs)]
        public async Task RestoredAi_NeverGetsImplicitGrant(string status)
        {
            using (var f = new Fixture())
            {
                f.Store.Initial.Add(new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Source = "AI",
                    Text = "恢复任务 / Restored", Status = status, Started = f.Clock.Now.AddMinutes(-10), CompletionToken = "old" });
                f.Reload();
                f.Host.AddVs("A"); f.Host.AddVs("B");
                var restored = f.Queue.Find(1);
                var ai = f.Add("B");
                int reads = 0;
                f.Host.AnswerReader = t => { reads++; return Task.FromResult("answer"); };
                await f.Dispatcher.PumpAsync();
                await f.Dispatcher.FinishAsync(restored, f.Host.Vs["A"], null);
                f.Dispatcher.DispatchNow(restored);
                f.Queue.Commit();
                Assert.IsFalse(f.Dispatcher.CanRun(restored));
                Assert.AreEqual(status, restored.Status);
                Assert.AreEqual(0, reads);
                Assert.AreEqual(QueueStatus.Running, ai.Status);
                Assert.AreEqual(1, f.Host.Sent.Count);
            }
        }

        [TestMethod]
        public async Task ManualPredecessorBlocksAi_ButOtherTargetRuns()
        {
            using (var f = new Fixture())
            {
                f.Host.AddVs("A"); f.Host.AddVs("B");
                var manual = f.Add("A", "用户");
                var blocked = f.Add();
                var independent = f.Add("B");
                f.Dispatcher.DispatchNow(blocked);
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Waiting, manual.Status);
                Assert.AreEqual(QueueStatus.Waiting, blocked.Status);
                Assert.AreEqual(QueueStatus.Running, independent.Status);
                StringAssert.Contains(f.Dispatcher.StartStateText(blocked), "predecessor @" + manual.Id);
                f.Dispatcher.Start();
                Assert.AreEqual(QueueStatus.Running, manual.Status);
                await f.Dispatcher.FinishAsync(manual, f.Host.Vs["A"], null);
                Assert.AreEqual(QueueStatus.Running, blocked.Status);
            }
        }

        [TestMethod]
        public async Task ParallelTargetsRun_WhileSameTargetSuccessorWaits()
        {
            using (var f = new Fixture())
            {
                f.Host.AddVs("A"); f.Host.AddVs("B");
                var first = f.Add();
                var next = f.Add();
                var independent = f.Add("B");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, first.Status);
                Assert.AreEqual(QueueStatus.Running, independent.Status);
                Assert.AreEqual(QueueStatus.Waiting, next.Status);
                Assert.AreEqual(2, f.Host.Sent.Count);
                Assert.IsFalse(f.Dispatcher.IsStarted);
            }
        }

        [TestMethod]
        public async Task QueueChangedCannotAuthorizeBeforeAdmission_EvenWhenAllModeIsEnabled()
        {
            using (var f = new Fixture())
            {
                f.Settings.AutoStartAllTasks = true;
                f.Dispatcher.ApplyAutomaticStart();
                f.Host.AddVs("A"); f.Host.AddVs("B");
                f.Add("B");
                await f.Dispatcher.PumpAsync();
                f.Queue.Changed += f.Dispatcher.Pump;
                var pending = f.Add("A", accept: false);
                Assert.AreEqual(QueueStatus.Waiting, pending.Status);
                Assert.IsFalse(f.Dispatcher.CanRun(pending));
                Assert.AreEqual(1, f.Host.Sent.Count);
                f.Dispatcher.AcceptQueued(pending, "AI");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, pending.Status);
                Assert.AreEqual(2, f.Host.Sent.Count);
            }
        }

        [TestMethod]
        public async Task ParkedAi_WaitsForTargetAndSettle_WithoutStart()
        {
            using (var f = new Fixture())
            {
                f.Host.Settle = TimeSpan.FromSeconds(20);
                var parked = f.Queue.AddParked("A", "演示 / Demo", "暂存 / Park", "AI");
                f.Dispatcher.AcceptQueued(parked, "AI");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.WaitingVs, parked.Status);
                f.Host.AddVs("A");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Waiting, parked.Status);
                Assert.AreEqual(0, f.Host.Sent.Count);
                f.Clock.Advance(TimeSpan.FromSeconds(21));
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, parked.Status);
                Assert.IsFalse(f.Dispatcher.IsStarted);
            }
        }

        [TestMethod]
        public async Task ParkedUnauthorizedPredecessor_BlocksResolvedOpenTargetWithoutStateMutation()
        {
            using (var f = new Fixture())
            {
                var old = f.Queue.AddParked("solution", "演示 / Demo", "旧任务 / Old", "用户");
                var target = f.Host.AddVs("A");
                f.Host.Vs["solution"] = target;
                var ai = f.Add();
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.WaitingVs, old.Status);
                Assert.AreEqual("solution", old.VsKey);
                Assert.AreEqual(QueueStatus.Waiting, ai.Status);
                Assert.AreEqual(0, f.Host.Sent.Count);
                StringAssert.Contains(f.Dispatcher.StartStateText(ai), "predecessor @" + old.Id);
            }
        }

        [TestMethod]
        public async Task DuplicateAuthorization_IsIdempotentAndNeverConvertsManualSource()
        {
            using (var f = new Fixture())
            {
                f.Host.AddVs("A"); f.Host.AddVs("B");
                var manual = f.Add("A", "用户");
                var duplicate = f.Queue.Add("A", "A", manual.Text, "AI");
                Assert.AreSame(manual, duplicate);
                f.Dispatcher.AcceptQueued(duplicate, "AI");
                Assert.IsFalse(f.Dispatcher.CanRun(manual));
                Assert.AreEqual("用户", manual.Source);
                var ai = f.Add("B", accept: false);
                f.Dispatcher.AcceptQueued(ai, "用户");
                Assert.IsFalse(f.Dispatcher.CanRun(ai));
                for (int i = 0; i < 3; i++)
                {
                    Assert.AreSame(ai, f.Queue.Add("B", "B", ai.Text, "AI"));
                    f.Dispatcher.AcceptQueued(ai, "AI");
                    await f.Dispatcher.PumpAsync();
                }
                Assert.AreEqual(1, f.Host.Sent.Count);
                Assert.AreEqual(1, f.Host.Announced.Count);
                Assert.AreEqual(2, f.Queue.Items.Count);
            }
        }

        [TestMethod]
        public async Task SaveFailureNeverAuthorizes_EvenAfterBackgroundSaveRecovery()
        {
            using (var f = new Fixture())
            {
                f.Host.AddVs("A");
                f.Queue.Changed += f.Dispatcher.Pump;
                f.Store.FailWith = "磁盘不可写 / Disk not writable";
                var task = f.Add();
                StringAssert.Contains(f.Dispatcher.AcceptQueued(task, "AI"), "Task save failed");
                Assert.IsFalse(f.Dispatcher.CanRun(task));
                f.Store.FailWith = null;
                f.Queue.Commit();
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(0, f.Host.Sent.Count);
                Assert.AreEqual(0, f.Host.Announced.Count);
                f.Dispatcher.AcceptQueued(task, "AI");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, task.Status);
            }
        }

        [TestMethod]
        public async Task SendingStateSaveFailure_DoesNotSend()
        {
            using (var f = new Fixture())
            {
                f.Host.AddVs("A");
                var task = f.Add();
                f.Store.FailWith = "保存失败 / Save failed";
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(0, f.Host.Sent.Count);
                Assert.AreEqual(QueueStatus.Failed, task.Status);
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task FailedPredecessorPolicyAndExplicitRetry_ArePreserved(bool skip)
        {
            using (var f = new Fixture())
            {
                f.Settings.SkipFailedPredecessors = skip;
                f.Host.AddVs("A");
                var failed = f.Add();
                var next = f.Add();
                f.Host.SendException = new InvalidOperationException("模拟失败 / Simulated failure");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Failed, failed.Status);
                f.Host.SendException = null;
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(skip ? QueueStatus.Running : QueueStatus.Waiting, next.Status);
                f.Dispatcher.Retry(failed);
                if (skip) await f.Dispatcher.FinishAsync(next, f.Host.Vs["A"], null);
                Assert.AreEqual(QueueStatus.Running, failed.Status);
                Assert.IsFalse(f.Dispatcher.IsStarted);
            }
        }

        [TestMethod]
        public async Task WorktreeFifthCompletion_AuthorizesOnlyNewBarrier_AndSixthContinues()
        {
            using (var f = new Fixture())
            {
                f.Lane();
                var tasks = Enumerable.Range(0, 6).Select(i => f.Add()).ToArray();
                var manual = f.Add("A", "用户");
                await f.Dispatcher.PumpAsync();
                for (int i = 0; i < 5; i++)
                {
                    Assert.AreEqual(QueueStatus.Running, tasks[i].Status);
                    await f.Dispatcher.FinishAsync(tasks[i], f.Host.Vs["A"], null);
                    await f.Dispatcher.PumpAsync();
                }
                var merge = f.Queue.Items.Single(t => t.IsWorktreeMerge);
                Assert.IsTrue(f.Dispatcher.CanRun(merge));
                Assert.AreEqual(QueueStatus.Done, merge.Status);
                Assert.AreEqual(1, f.Git.Integrations);
                Assert.AreEqual(QueueStatus.Running, tasks[5].Status);
                Assert.AreEqual(QueueStatus.Waiting, manual.Status);
                Assert.IsFalse(f.Dispatcher.CanRun(manual));
                Assert.IsFalse(f.Dispatcher.IsStarted);
                StringAssert.Contains(f.Host.NoticeBodies.Last(b => b.Contains("Start mode")), "AI automatic start");
            }
        }

        [DataTestMethod]
        [DataRow(QueueStatus.Waiting)]
        [DataRow(QueueStatus.Running)]
        [DataRow(QueueStatus.Failed)]
        public async Task RestoredIntegration_IsNeverGrantedByAiSubmission(string state)
        {
            using (var f = new Fixture())
            {
                var info = f.Lane();
                f.Store.Initial.Add(new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Source = "AI", Text = "旧合并 / Old integration",
                    Status = state, Worktree = info, IsWorktreeMerge = true, WorktreeBatch = 1, CompletionToken = "old" });
                f.Reload();
                f.Queue.ResolveWorktree = key => info;
                var old = f.Queue.Find(1);
                f.Dispatcher.AcceptQueued(old, "AI");
                var ai = f.Add();
                await f.Dispatcher.PumpAsync();
                await f.Dispatcher.FinishAsync(old, f.Host.Vs["A"], null);
                f.Dispatcher.Retry(old);
                Assert.IsFalse(f.Dispatcher.CanRun(old));
                Assert.AreEqual(state, old.Status);
                Assert.AreEqual(QueueStatus.Waiting, ai.Status);
                Assert.AreEqual(0, f.Git.Integrations);
                Assert.AreEqual(0, f.Host.Sent.Count);
            }
        }

        [TestMethod]
        public async Task AutomaticMergeFailureStillBlocks_ExplicitRetryKeepsGrant()
        {
            using (var f = new Fixture())
            {
                f.Lane();
                var tasks = Enumerable.Range(0, 6).Select(i => f.Add()).ToArray();
                f.Git.Conflicts = true;
                await f.Dispatcher.PumpAsync();
                for (int i = 0; i < 5; i++)
                    await f.Dispatcher.FinishAsync(tasks[i], f.Host.Vs["A"], null);
                await f.Dispatcher.PumpAsync();
                var merge = f.Queue.Items.Single(t => t.IsWorktreeMerge);
                Assert.AreEqual(QueueStatus.Running, merge.Status);
                await f.Dispatcher.FinishAsync(merge, f.Host.Vs["A"], null);
                Assert.AreEqual(QueueStatus.Failed, merge.Status);
                Assert.AreEqual(QueueStatus.Waiting, tasks[5].Status);
                f.Git.Conflicts = false;
                f.Dispatcher.Retry(merge);
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Done, merge.Status);
                Assert.AreEqual(QueueStatus.Running, tasks[5].Status);
                Assert.IsFalse(f.Dispatcher.IsStarted);
            }
        }

        [TestMethod]
        public async Task CompletionAndSpeech_AreOnceOnlyAndBilingual()
        {
            using (var f = new Fixture())
            {
                var vs = f.Host.AddVs("A");
                var task = f.Add();
                await f.Dispatcher.PumpAsync();
                await f.Dispatcher.FinishAsync(task, vs, null);
                await f.Dispatcher.FinishAsync(task, vs, null);
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Done, task.Status);
                Assert.AreEqual(2, f.Host.Announced.Count);
                StringAssert.Contains(f.Host.Announced[1], "已完成");
                StringAssert.Contains(f.Host.Announced[1], "completed");
                Assert.AreEqual(1, f.Host.Notices.Count);
                StringAssert.Contains(f.Host.NoticeBodies.Single(), "AI 自动启动");
                StringAssert.Contains(f.Host.NoticeBodies.Single(), "AI automatic start");
                Assert.IsFalse(f.Dispatcher.HasDispatchActivity);
            }
        }

        [TestMethod]
        public async Task AllModeIncludesRestoredManual_OffAffectsFutureAdmissions()
        {
            using (var f = new Fixture())
            {
                f.Settings.AutoStartAiTasks = false;
                f.Settings.AutoStartAllTasks = true;
                f.Store.Initial.Add(new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Source = "用户", Status = QueueStatus.Waiting, Text = "恢复 / Restored" });
                f.Reload();
                f.Host.AddVs("A"); f.Host.AddVs("B"); f.Host.AddVs("C");
                var manual = f.Add("B", "用户");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, f.Queue.Find(1).Status);
                Assert.AreEqual(QueueStatus.Running, manual.Status);
                Assert.IsFalse(f.Dispatcher.IsStarted);
                f.Settings.AutoStartAllTasks = false;
                f.Dispatcher.ApplyAutomaticStart();
                var newer = f.Add("C");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Waiting, newer.Status);
                await f.Dispatcher.FinishAsync(manual, f.Host.Vs["B"], null);
                StringAssert.Contains(f.Host.Announced.Last(), "all-tasks automatic");
                f.Dispatcher.Start();
                Assert.AreEqual(QueueStatus.Running, newer.Status);
            }
        }

        [TestMethod]
        public async Task ConfigChangeDuringAwait_DoesNotGrantNextTaskOrLoseCompletion()
        {
            using (var f = new Fixture())
            {
                f.Lane();
                f.Git.CheckGate = new TaskCompletionSource<bool>();
                var current = f.Add();
                var pumping = f.Dispatcher.PumpAsync();
                Assert.IsFalse(pumping.IsCompleted);
                f.Settings.AutoStartAiTasks = false;
                f.Dispatcher.ApplyAutomaticStart();
                var next = f.Add();
                f.Git.CheckGate.SetResult(true);
                await pumping;
                Assert.AreEqual(QueueStatus.Running, current.Status);
                await f.Dispatcher.FinishAsync(current, f.Host.Vs["A"], null);
                Assert.AreEqual(QueueStatus.Done, current.Status);
                Assert.AreEqual(QueueStatus.Waiting, next.Status);
                Assert.AreEqual(1, f.Host.Sent.Count);
                Assert.IsFalse(f.Dispatcher.IsStarted);
            }
        }

        [TestMethod]
        public async Task RestartDropsAutomaticEligibility_ExplicitDuplicateReauthorizesOnlyAi()
        {
            using (var f = new Fixture())
            {
                var task = f.Add();
                f.Store.Initial = f.Store.Saved;
                f.Reload();
                f.Host.AddVs("A");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(0, f.Host.Sent.Count);
                var restored = f.Queue.Find(task.Id);
                Assert.IsFalse(f.Dispatcher.CanRun(restored));
                f.Dispatcher.AcceptQueued(restored, "AI");
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, restored.Status);
            }
        }

        [TestMethod]
        public void Panel_ShowsPerTaskEligibility_AndKeepsManualStartButtonEnabled()
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var f = new Fixture())
                    using (var panel = new TaskPanel())
                    {
                        var manual = f.Add("A", "用户");
                        var ai = f.Add("B");
                        panel.CanRunTask = f.Dispatcher.CanRun;
                        panel.TaskStartText = f.Dispatcher.StartStateText;
                        panel.Bind(f.Queue);
                        panel.SetWorkflowStarted(f.Dispatcher.IsStarted);
                        Assert.IsTrue(panel.IsTaskEligible(ai));
                        Assert.IsFalse(panel.IsTaskEligible(manual));
                        StringAssert.Contains(panel.EligibilityText(ai), "AI automatic start");
                        StringAssert.Contains(panel.EligibilityText(manual), "manual start");
                        var button = (Control)typeof(TaskPanel).GetField("_btnStart", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                        Assert.IsTrue(button.Enabled);
                        Assert.AreEqual("开始流程 / Start", button.Text);
                        panel.SetWorkflowStarted(true);
                        Assert.IsFalse(button.Enabled);
                    }
                }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        [TestMethod]
        public async Task AllModeCanExplicitlyResumeOldIntegration()
        {
            using (var f = new Fixture())
            {
                var info = f.Lane();
                f.Store.Initial.Add(new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Source = "AI", Text = "旧合并 / Old integration",
                    Status = QueueStatus.Waiting, Worktree = info, IsWorktreeMerge = true, WorktreeBatch = 1 });
                f.Reload();
                Assert.IsFalse(f.Dispatcher.CanRun(f.Queue.Find(1)));
                f.Settings.AutoStartAllTasks = true;
                f.Dispatcher.ApplyAutomaticStart();
                f.Dispatcher.ApplyAutomaticStart();
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Done, f.Queue.Find(1).Status);
                Assert.AreEqual(1, f.Git.Integrations);
                Assert.AreEqual(2, f.Host.Announced.Count);
                StringAssert.Contains(f.Host.NoticeBodies.Single(), "All-tasks automatic start");
                Assert.IsFalse(f.Dispatcher.IsStarted);
            }
        }

        [TestMethod]
        public async Task BarrierReconstructedOnLoad_IsNotNewSessionMaintenance()
        {
            using (var f = new Fixture())
            {
                var info = f.Lane();
                for (int i = 1; i <= 5; i++)
                    f.Store.Initial.Add(new QueuedTask { Id = i, VsKey = "A", VsName = "A", Source = "AI", Text = "恢复 / Restored " + i,
                        Status = QueueStatus.Done, Worktree = info, WorktreeCounted = true });
                f.Reload();
                f.Queue.ResolveWorktree = key => info;
                var barrier = f.Queue.Items.Single(t => t.IsWorktreeMerge);
                var next = f.Add();
                await f.Dispatcher.PumpAsync();
                Assert.IsFalse(f.Dispatcher.CanRun(barrier));
                Assert.AreEqual(QueueStatus.Waiting, next.Status);
                Assert.AreEqual(0, f.Git.Integrations);
            }
        }

        [TestMethod]
        public async Task NewMaintenanceWaitsForSaveRecovery_WithoutGrantingOtherTasks()
        {
            using (var f = new Fixture())
            {
                f.Lane();
                var tasks = Enumerable.Range(0, 6).Select(i => f.Add()).ToArray();
                var manual = f.Add("B", "用户");
                f.Host.AddVs("B");
                await f.Dispatcher.PumpAsync();
                for (int i = 0; i < 4; i++) await f.Dispatcher.FinishAsync(tasks[i], f.Host.Vs["A"], null);
                f.Store.FailWith = "保存失败 / Save failed";
                await f.Dispatcher.FinishAsync(tasks[4], f.Host.Vs["A"], null);
                var barrier = f.Queue.Items.Single(t => t.IsWorktreeMerge);
                Assert.IsFalse(f.Dispatcher.CanRun(barrier));
                Assert.AreEqual(0, f.Git.Integrations);
                f.Store.FailWith = null;
                f.Queue.Commit();
                await f.Dispatcher.PumpAsync();
                await f.Dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Done, barrier.Status);
                Assert.AreEqual(QueueStatus.Running, tasks[5].Status);
                Assert.AreEqual(QueueStatus.Waiting, manual.Status);
                Assert.IsFalse(f.Dispatcher.CanRun(manual));
            }
        }

        [TestMethod]
        public void SettingsUi_TogglesPersistAndApplyAllMode_WithoutGlobalStart()
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var f = new Fixture())
                    using (var form = new SettingsForm(f.Settings, null, new SettingsForm.Actions()))
                    {
                        var manual = f.Add("A", "用户");
                        form.Changed += () => { Assert.IsTrue(f.Settings.Save()); f.Dispatcher.ApplyAutomaticStart(); };
                        var controls = Descendants(form).OfType<ToggleSwitch>().ToList();
                        var ai = controls.Single(c => c.Text.Contains("Auto-start AI tasks"));
                        var all = controls.Single(c => c.Text.Contains("Auto-start all tasks"));
                        Assert.IsTrue(ai.Checked);
                        Assert.IsFalse(all.Checked);
                        ai.Checked = false;
                        all.Checked = true;
                        Assert.IsTrue(f.Dispatcher.CanRun(manual));
                        Assert.IsFalse(f.Dispatcher.IsStarted);
                        var saved = AppSettings.Load();
                        Assert.IsFalse(saved.AutoStartAiTasks);
                        Assert.IsTrue(saved.AutoStartAllTasks);
                        all.Checked = false;
                        Assert.IsTrue(f.Dispatcher.CanRun(manual));
                        Assert.IsFalse(f.Dispatcher.CanRun(f.Add("B")));
                        Assert.IsFalse(AppSettings.Load().AutoStartAllTasks);
                    }
                }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }

        [TestMethod]
        public void ProductionWiring_UsesPolicyForBothEntrypointsTimersUiAndVoiceSettings()
        {
            var root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "VSManager"))) root = root.Parent;
            Assert.IsNotNull(root);
            string Read(string file) => File.ReadAllText(Path.Combine(root.FullName, "src", "VSManager", file));
            string main = Read("UI\\MainForm.cs"), parked = Read("UI\\MainForm.Solutions.cs"), settings = Read("UI\\Forms\\SettingsForm.cs");
            StringAssert.Contains(main, "startSettings: () => _settings");
            StringAssert.Contains(main, "_dispatcher.AcceptQueued(q, source)");
            StringAssert.Contains(main, "_taskTimer.Enabled = _dispatcher.HasDispatchActivity");
            StringAssert.Contains(main, "_taskPanel.CanRunTask = _dispatcher.CanRun");
            StringAssert.Contains(main, "_taskPanel.TaskStartText = _dispatcher.StartStateText");
            StringAssert.Contains(main, "if (queued.Status != QueueStatus.Done || automatic) return;");
            StringAssert.Contains(main, "AnnounceCompletion(v, automaticZh, automaticEn)");
            StringAssert.Contains(parked, "NotifyCopilotCompleted(v, (t.Finished ?? DateTime.Now) - (t.Started ?? t.Created), t, zh, en)");
            StringAssert.Contains(parked, "_dispatcher.AcceptQueued(q, \"AI\")");
            StringAssert.Contains(parked, "_dispatcher.AcceptQueued(dup, \"AI\")");
            StringAssert.Contains(parked, "if (_settings.PendingVsNotify) NotifyTask(t, zh, en)");
            StringAssert.Contains(parked, "if (_settings.VoiceEnabled && _settings.HasVoiceKey)");
            StringAssert.Contains(settings, "_s.AutoStartAiTasks, v => _s.AutoStartAiTasks = v");
            StringAssert.Contains(settings, "_s.AutoStartAllTasks, v => _s.AutoStartAllTasks = v");
            StringAssert.Contains(Read("UI\\MainForm.Attachments.cs"), "EnqueueTextTask(v, text, \"AI\", attachments)");
        }
    }
}
