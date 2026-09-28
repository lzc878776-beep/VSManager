using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>
    /// 自测重启：交接单读写、授权恢复与续跑通知。
    /// Self-test restart: handoff persistence, grant restoration and the continuation notice.
    /// </summary>
    [TestClass]
    public class SelfRestartTests
    {
        private static SelfRestartHandoff Sample() => new SelfRestartHandoff
        {
            Id = "h1",
            CreatedUtc = DateTime.UtcNow,
            OldPid = 100,
            VsPid = 200,
            OldExeWriteUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc),
            BuildSummary = "预编译通过",
            TestPlan = "- [ ] list_vs 能列出实例",
            TaskId = 7,
            Scope = "DemoProject",
            WorkflowStarted = true,
            AutoGrants = new List<SelfRestartGrant> { new SelfRestartGrant { Id = 3, All = true } },
            ManualGrants = new List<int> { 4 }
        };

        [TestMethod]
        public void Handoff_RoundTrips_AndIsConsumedOnce()
        {
            using (new TempDataFolder())
            {
                Assert.IsNull(SelfRestart.Save(Sample()));
                Assert.IsTrue(File.Exists(SelfRestart.FilePath));
                var h = SelfRestart.Take(out string error);
                Assert.IsNull(error);
                Assert.AreEqual("h1", h.Id);
                Assert.AreEqual(7, h.TaskId);
                Assert.AreEqual("DemoProject", h.Scope);
                Assert.IsTrue(h.WorkflowStarted);
                Assert.AreEqual(3, h.AutoGrants[0].Id);
                Assert.IsTrue(h.AutoGrants[0].All);
                CollectionAssert.AreEqual(new[] { 4 }, h.ManualGrants);
                Assert.AreEqual(Sample().OldExeWriteUtc, h.OldExeWriteUtc.ToUniversalTime());
                Assert.IsFalse(File.Exists(SelfRestart.FilePath));
                Assert.IsNull(SelfRestart.Take(out error));
                Assert.IsNull(error);
            }
        }

        [TestMethod]
        public void Handoff_Damaged_IsReportedAndDeleted()
        {
            using (new TempDataFolder())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SelfRestart.FilePath));
                File.WriteAllText(SelfRestart.FilePath, "{ not json");
                Assert.IsNull(SelfRestart.Take(out string error));
                Assert.IsNotNull(error);
                Assert.IsFalse(File.Exists(SelfRestart.FilePath));
            }
        }

        [TestMethod]
        public void Validation_AndFreshness()
        {
            Assert.IsNotNull(SelfRestart.ValidatePlan(" "));
            Assert.IsNotNull(SelfRestart.ValidatePlan(new string('x', SelfRestart.MaxPlanChars + 1)));
            Assert.IsNull(SelfRestart.ValidatePlan("- [ ] ok"));
            var now = DateTime.UtcNow;
            var h = Sample();
            h.CreatedUtc = now.AddMinutes(-5);
            Assert.IsTrue(SelfRestart.IsFresh(h, now));
            h.CreatedUtc = now - SelfRestart.MaxAge - TimeSpan.FromMinutes(1);
            Assert.IsFalse(SelfRestart.IsFresh(h, now));
            h.CreatedUtc = now.AddHours(2);
            Assert.IsFalse(SelfRestart.IsFresh(h, now));
        }

        [TestMethod]
        public void BuildHelpers_FindProjectAndMsBuild()
        {
            using (var data = new TempDataFolder())
            {
                string project = data.File(Path.Combine("repo", "src", "App", "VSManager.csproj"));
                Directory.CreateDirectory(Path.GetDirectoryName(project));
                File.WriteAllText(project, "<Project />");
                string exe = Path.Combine(Path.GetDirectoryName(project), "bin", "Release", "net48", "VSManager.exe");
                Assert.AreEqual(project, SelfRestart.FindProjectFile(exe));
                Assert.IsNull(SelfRestart.FindProjectFile(data.File(Path.Combine("other", "VSManager.exe"))));
                Assert.AreEqual("Release", SelfRestart.GuessConfiguration(exe));
                Assert.AreEqual("Debug", SelfRestart.GuessConfiguration(@"C:\work\bin\Debug\net48\VSManager.exe"));
            }
            Assert.AreEqual(@"C:\VS\MSBuild\Current\Bin\MSBuild.exe", SelfRestart.MsBuildFromDevenv(@"C:\VS\Common7\IDE\devenv.exe"));
            Assert.IsNull(SelfRestart.MsBuildFromDevenv(@"C:\tools\devenv.exe"));
            Assert.IsNull(SelfRestart.MsBuildFromDevenv(null));
        }

        [TestMethod]
        public void Notice_ReportsNewBuildPlanAndTools()
        {
            var h = Sample();
            var o = new SelfRestartOutcome { NewPid = 101, NewExeWriteUtc = h.OldExeWriteUtc.AddMinutes(3), DebuggerAttached = true, RestoredGrants = 2, RunningTasks = 1, WaitingTasks = 2 };
            Assert.IsTrue(SelfRestart.LoadedNewBuild(h, o));
            string content = SelfRestart.NoticeContent(h, o);
            StringAssert.Contains(content, "[重启完成通知 / Restart completed]");
            StringAssert.Contains(content, "PID 100 → 101");
            StringAssert.Contains(content, h.TestPlan);
            StringAssert.Contains(content, "#7");
            StringAssert.Contains(content, "mark_test_item");
            StringAssert.Contains(content, "new build loaded");
            StringAssert.Contains(SelfRestart.NoticeDisplay(h, o), "🔁");

            o.NewExeWriteUtc = h.OldExeWriteUtc;
            Assert.IsFalse(SelfRestart.LoadedNewBuild(h, o));
            StringAssert.Contains(SelfRestart.NoticeContent(h, o), "no new build detected");
            StringAssert.Contains(SelfRestart.NoticeDisplay(h, o), "⚠");
        }

        [TestMethod]
        public async Task PlannedRestart_RestoresGrants_KeepsRunningTaskWithoutResend()
        {
            using (var data = new TempDataFolder())
            {
                var clock = new FakeClock();
                var store = new JsonTaskStore(data.File("tasks.json"));
                var settings = new AppSettings { AutoStartAiTasks = true, AutoStartAllTasks = false };
                var queue = new TaskQueue(settings, store, new RecordingArchive(), clock.Func);
                var host = new FakeDispatchHost();
                host.AddVs("A");
                host.AddVs("B");
                var dispatcher = new TaskDispatcher(queue, host, clock.Func, startSettings: () => settings);
                var running = queue.Add("A", "A", "running work", "AI");
                dispatcher.AcceptQueued(running, "AI", fromAgentPanel: true);
                await dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Running, running.Status);
                var granted = queue.Add("A", "A", "granted follow-up", "AI");
                dispatcher.AcceptQueued(granted, "AI", fromAgentPanel: true);
                var manual = queue.Add("B", "B", "manual work", "用户");
                Assert.IsTrue(dispatcher.CanRun(granted));
                Assert.IsFalse(dispatcher.CanRun(manual));
                int sent = host.Sent.Count;
                Assert.IsTrue(queue.Save());

                var handoff = new SelfRestartHandoff { Id = "x", CreatedUtc = DateTime.UtcNow, TestPlan = "- [ ] check" };
                dispatcher.CaptureGrants(handoff);
                Assert.IsNull(SelfRestart.Save(handoff));

                // 新进程：从磁盘恢复队列与交接单 / New process: restore the queue and the handoff from disk
                queue = new TaskQueue(settings, store, new RecordingArchive(), clock.Func);
                dispatcher = new TaskDispatcher(queue, host, clock.Func, startSettings: () => settings);
                dispatcher.ApplyAutomaticStart();
                Assert.IsFalse(dispatcher.CanRun(queue.Find(granted.Id)), "restored tasks need a grant after a normal restart");
                var taken = SelfRestart.Take(out string error);
                Assert.IsNull(error);
                Assert.AreEqual(2, dispatcher.RestoreGrants(taken));
                Assert.IsTrue(dispatcher.CanRun(queue.Find(running.Id)));
                Assert.IsTrue(dispatcher.CanRun(queue.Find(granted.Id)));
                Assert.IsFalse(dispatcher.CanRun(queue.Find(manual.Id)));
                Assert.IsFalse(dispatcher.IsStarted);
                Assert.AreEqual(QueueStatus.Running, queue.Find(running.Id).Status);
                await dispatcher.PumpAsync();
                Assert.AreEqual(sent, host.Sent.Count, "running task must not be resent and the follow-up waits behind it");
                Assert.AreEqual(0, dispatcher.RestoreGrants(taken), "grants are not duplicated");
            }
        }

        [TestMethod]
        public void RestoreGrants_StartsWorkflowWhenItWasStarted()
        {
            using (var data = new TempDataFolder())
            {
                var queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), new FakeClock().Func);
                var dispatcher = new TaskDispatcher(queue, new FakeDispatchHost());
                Assert.AreEqual(0, dispatcher.RestoreGrants(new SelfRestartHandoff { WorkflowStarted = true, AutoGrants = new List<SelfRestartGrant> { new SelfRestartGrant { Id = 99 } } }));
                Assert.IsTrue(dispatcher.IsStarted);
            }
        }
    }
}
