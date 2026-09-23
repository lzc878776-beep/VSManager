using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>可控的模拟 AI 助手。/ Controllable fake AI assistant.</summary>
    internal sealed class FakeAgent : IRestartableAgent
    {
        public bool Running { get; set; }
        public DateTime LastProgress { get; set; }
        public bool AwaitingUser { get; set; }
        public event Action<AgentFaultKind, string> Faulted;
        public readonly List<string> Restarts = new List<string>();

        public void Restart(string reason)
        {
            Restarts.Add(reason);
            Running = false;
        }

        public void Fault(AgentFaultKind kind, string detail) => Faulted?.Invoke(kind, detail);
    }

    /// <summary>防重启风暴计数器。/ Restart-storm limiter.</summary>
    [TestClass]
    public class RestartLimiterTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 9, 0, 0);

        [TestMethod]
        public void AllowsMaxCount_ThenBlocks_UntilWindowPasses()
        {
            var l = new RestartLimiter(3, TimeSpan.FromMinutes(5));
            Assert.IsTrue(l.TryAcquire(T0));
            Assert.IsTrue(l.TryAcquire(T0.AddMinutes(1)));
            Assert.IsTrue(l.TryAcquire(T0.AddMinutes(2)));
            Assert.IsFalse(l.TryAcquire(T0.AddMinutes(3)), "5 分钟内第 4 次被拒绝 / 4th within 5 min refused");
            Assert.IsTrue(l.Tripped);
            // 第一次记录过期后仍保持暂停，直到全部记录过期 / Stays tripped until every record expires
            Assert.IsFalse(l.TryAcquire(T0.AddMinutes(5.5)));
            Assert.IsTrue(l.TryAcquire(T0.AddMinutes(7.5)), "全部过期后恢复 / recovers after all expire");
            Assert.IsFalse(l.Tripped);
        }

        [TestMethod]
        public void Reset_ClearsHistory()
        {
            var l = new RestartLimiter(1, TimeSpan.FromMinutes(5));
            Assert.IsTrue(l.TryAcquire(T0));
            Assert.IsFalse(l.TryAcquire(T0));
            l.Reset();
            Assert.IsFalse(l.Tripped);
            Assert.AreEqual(0, l.Count(T0));
            Assert.IsTrue(l.TryAcquire(T0));
        }
    }

    /// <summary>AI 助手监督：卡住检测、故障重启、关闭开关与防风暴。/ Assistant supervisor: hang detection, fault restarts, switch and storm guard.</summary>
    [TestClass]
    public class AgentSupervisorTests
    {
        private TempDataFolder _data;
        private FakeClock _clock;
        private FakeAgent _agent;
        private AppSettings _settings;
        private AgentSupervisor _sup;
        private List<string> _notices;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _clock = new FakeClock();
            _agent = new FakeAgent { LastProgress = _clock.Now };
            _settings = new AppSettings();
            _sup = new AgentSupervisor(_agent, () => _settings, _clock.Func);
            _notices = new List<string>();
            _sup.Notice += _notices.Add;
        }

        [TestCleanup]
        public void Cleanup() => _data.Dispose();

        [TestMethod]
        public void Defaults_AutoRestartOn_Timeout120()
        {
            Assert.IsTrue(_settings.AgentAutoRestart);
            Assert.AreEqual(120, _settings.AgentHangTimeoutSeconds);
            Assert.AreEqual(3, _settings.AgentFailureThreshold);
            Assert.IsFalse(_settings.ProcessWatchdogEnabled);
            Assert.AreEqual(3, _settings.AutoRestartMaxCount);
            Assert.AreEqual(5, _settings.AutoRestartWindowMinutes);
        }

        [TestMethod]
        public void Tick_RestartsOnlyAfterTimeout()
        {
            _agent.Running = true;
            _clock.Advance(TimeSpan.FromSeconds(119));
            _sup.Tick();
            Assert.AreEqual(0, _agent.Restarts.Count);
            _clock.Advance(TimeSpan.FromSeconds(2));
            _sup.Tick();
            Assert.AreEqual(1, _agent.Restarts.Count);
            Assert.AreEqual(1, _sup.AutoRestarts);
            Assert.IsTrue(_notices.Last().Contains("AI 助手已自动重启"));
        }

        [TestMethod]
        public void Tick_IgnoresIdleOrWaitingForUser()
        {
            _clock.Advance(TimeSpan.FromHours(1));
            _sup.Tick();
            _agent.Running = true;
            _agent.AwaitingUser = true;
            _sup.Tick();
            Assert.AreEqual(0, _agent.Restarts.Count);
        }

        [TestMethod]
        public void Disabled_OnlyWarnsOncePerHang()
        {
            _settings.AgentAutoRestart = false;
            _agent.Running = true;
            _clock.Advance(TimeSpan.FromMinutes(5));
            _sup.Tick();
            _sup.Tick();
            _clock.Advance(TimeSpan.FromMinutes(5));
            _sup.Tick();
            Assert.AreEqual(0, _agent.Restarts.Count);
            Assert.AreEqual(1, _notices.Count);
            Assert.IsTrue(_notices[0].StartsWith("⚠"));
        }

        [TestMethod]
        public void Faults_StopAfterLimit_ManualRestartResets()
        {
            for (int i = 0; i < 3; i++) _agent.Fault(AgentFaultKind.Exception, "boom");
            Assert.AreEqual(3, _agent.Restarts.Count);
            _agent.Fault(AgentFaultKind.RequestFailures, null);
            _agent.Fault(AgentFaultKind.RequestFailures, null);
            Assert.AreEqual(3, _agent.Restarts.Count, "超过上限不再重启 / no restart beyond the limit");
            Assert.AreEqual(1, _notices.Count(n => n.StartsWith("⚠")), "防风暴只提示一次 / storm notice only once");
            Assert.IsTrue(_sup.Limiter.Tripped);

            _sup.RestartNow();
            Assert.AreEqual(4, _agent.Restarts.Count);
            Assert.IsFalse(_sup.Limiter.Tripped);
            _agent.Fault(AgentFaultKind.Exception, "again");
            Assert.AreEqual(5, _agent.Restarts.Count);
        }

        [TestMethod]
        public void HangWhileLimited_RetriesAtMostOncePerMinute()
        {
            _settings.AutoRestartMaxCount = 1;
            _agent.Fault(AgentFaultKind.Exception, null);
            Assert.AreEqual(1, _agent.Restarts.Count);
            _agent.Running = true;
            _agent.LastProgress = _clock.Now;
            _clock.Advance(TimeSpan.FromMinutes(3));
            int before = _notices.Count;
            for (int i = 0; i < 30; i++) { _sup.Tick(); _clock.Advance(TimeSpan.FromSeconds(1)); }
            Assert.AreEqual(1, _agent.Restarts.Count);
            Assert.AreEqual(before + 1, _notices.Count, "受限时不刷屏 / no notice spam while limited");
            // 窗口过期后下一次尝试成功 / Succeeds after the window expires
            _clock.Advance(TimeSpan.FromMinutes(3));
            _sup.Tick();
            Assert.AreEqual(2, _agent.Restarts.Count);
        }

        [TestMethod]
        public void ClampRestart_RestoresDefaultsAndBounds()
        {
            var s = new AppSettings
            {
                AgentHangTimeoutSeconds = 0, AgentFailureThreshold = -1, AutoRestartMaxCount = 0, AutoRestartWindowMinutes = -5
            };
            s.ClampRestart();
            Assert.AreEqual(120, s.AgentHangTimeoutSeconds);
            Assert.AreEqual(3, s.AgentFailureThreshold);
            Assert.AreEqual(3, s.AutoRestartMaxCount);
            Assert.AreEqual(5, s.AutoRestartWindowMinutes);
            s.AgentHangTimeoutSeconds = 5;
            s.AutoRestartMaxCount = 999;
            s.ClampRestart();
            Assert.AreEqual(30, s.AgentHangTimeoutSeconds);
            Assert.AreEqual(20, s.AutoRestartMaxCount);
        }

        [TestMethod]
        public void RestartSettings_RoundTripThroughSettingsJson()
        {
            var s = new AppSettings { AgentAutoRestart = false, AgentHangTimeoutSeconds = 300, ProcessWatchdogEnabled = true, AutoRestartMaxCount = 4, AutoRestartWindowMinutes = 10 };
            s.Save();
            var r = AppSettings.Load();
            Assert.IsFalse(r.AgentAutoRestart);
            Assert.AreEqual(300, r.AgentHangTimeoutSeconds);
            Assert.IsTrue(r.ProcessWatchdogEnabled);
            Assert.AreEqual(4, r.AutoRestartMaxCount);
            Assert.AreEqual(10, r.AutoRestartWindowMinutes);
        }
    }

    /// <summary>重启相关：发送判定、异常退出后暂停中断的发送、命令行参数。/ Restart helpers: failure classification, pausing interrupted sends, arguments.</summary>
    [TestClass]
    public class RestartRecoveryTests
    {
        [TestMethod]
        public void TransientFailures_AreClassified()
        {
            Assert.IsTrue(AgentService.IsTransientRequestFailure(new System.Net.Http.HttpRequestException("x")));
            Assert.IsTrue(AgentService.IsTransientRequestFailure(new TimeoutException()));
            Assert.IsTrue(AgentService.IsTransientRequestFailure(new System.Net.WebException("x")));
            Assert.IsFalse(AgentService.IsTransientRequestFailure(new InvalidOperationException()));
            Assert.IsFalse(AgentService.IsTransientRequestFailure(new NullReferenceException()));
        }

        [TestMethod]
        public void PauseInterruptedSends_FailsOnlyTasksThatWereSending()
        {
            using (new TempDataFolder())
            {
                var store = new MemoryTaskStore();
                store.Initial.Add(new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Text = "a", Status = QueueStatus.Sending, Created = new DateTime(2026, 1, 1) });
                store.Initial.Add(new QueuedTask { Id = 2, VsKey = "B", VsName = "B", Text = "b", Status = QueueStatus.Waiting, Created = new DateTime(2026, 1, 1) });
                store.Initial.Add(new QueuedTask { Id = 3, VsKey = "C", VsName = "C", Text = "c", Status = QueueStatus.Running, Created = new DateTime(2026, 1, 1) });
                var clock = new FakeClock();
                var q = new TaskQueue(new AppSettings(), store, new RecordingArchive(), clock.Func);
                Assert.AreEqual(1, q.InterruptedSends.Count);
                Assert.AreEqual(QueueStatus.Waiting, q.Find(1).Status, "默认仍按原规则恢复为排队 / still recovered to waiting by default");

                Assert.AreEqual(1, q.PauseInterruptedSends("paused"));
                Assert.AreEqual(QueueStatus.Failed, q.Find(1).Status);
                Assert.AreEqual("paused", q.Find(1).Error);
                Assert.AreEqual(QueueStatus.Waiting, q.Find(2).Status);
                Assert.AreEqual(QueueStatus.Running, q.Find(3).Status);
                Assert.AreEqual(QueueStatus.Failed, store.Saved.Single(t => t.Id == 1).Status, "已写盘 / saved");
                Assert.AreEqual(0, q.PauseInterruptedSends("again"), "只处理一次 / only once");
            }
        }

        [TestMethod]
        public void IntArg_ParsesNamedIntegers()
        {
            var args = new[] { "--restarted", "-1073741819", "--watched-by", "42" };
            Assert.AreEqual(-1073741819, ProcessWatchdog.IntArg(args, ProcessWatchdog.RestartedArg));
            Assert.AreEqual(42, ProcessWatchdog.IntArg(args, ProcessWatchdog.WatchedByArg));
            Assert.IsNull(ProcessWatchdog.IntArg(args, ProcessWatchdog.WaitPidArg));
            Assert.IsNull(ProcessWatchdog.IntArg(new[] { "--wait-pid" }, ProcessWatchdog.WaitPidArg));
            Assert.AreEqual("0xC0000005 (-1073741819)", ProcessWatchdog.FormatExitCode(-1073741819));
        }
    }
}
