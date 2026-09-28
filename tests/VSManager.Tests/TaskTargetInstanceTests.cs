using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>
    /// 同一解决方案被两个 VS 打开（各自是不同的 Copilot 对话）时，普通任务必须只发往入队时选定的实例。
    /// With one solution open in two VS instances (each its own Copilot conversation), ordinary tasks must only go to the
    /// instance chosen when they were queued.
    /// </summary>
    [TestClass]
    public class TaskTargetInstanceTests
    {
        private const string Sln = @"C:\Repo\Shared.slnx";
        private static readonly DateTime Queued = new DateTime(2026, 1, 1, 9, 0, 0);

        private static VsInstance Vs(int pid, DateTime started) =>
            new VsInstance { Pid = pid, Key = Sln, SolutionPath = Sln, Title = "VS" + pid, StartTicks = started.ToUniversalTime().Ticks };

        [TestMethod]
        public void Pick_PinnedInstance_WinsOverEnumerationOrder()
        {
            var first = Vs(1, Queued.AddHours(-2)); var second = Vs(2, Queued.AddHours(-1));
            var task = new QueuedTask { VsKey = Sln, Created = Queued, TargetInstanceKey = second.InstanceKey };
            Assert.AreSame(second, TaskTarget.Pick(new[] { first, second }, task));
        }

        [TestMethod]
        public void Pick_PinnedInstanceClosed_NeverRedirectsToAnotherExistingInstance()
        {
            var other = Vs(1, Queued.AddHours(-2));
            var task = new QueuedTask { VsKey = Sln, Created = Queued, TargetInstanceKey = "inst:2:123" };
            Assert.IsNull(TaskTarget.Pick(new[] { other }, task), "已存在的另一个实例不是原目标 / An instance that already existed is not the original target");
        }

        [TestMethod]
        public void Pick_PinnedInstanceRestarted_UsesTheSingleNewInstance()
        {
            var restarted = Vs(3, Queued.AddMinutes(5));
            var task = new QueuedTask { VsKey = Sln, Created = Queued, TargetInstanceKey = "inst:2:123" };
            Assert.AreSame(restarted, TaskTarget.Pick(new[] { restarted }, task));
            Assert.IsNull(TaskTarget.Pick(new[] { restarted, Vs(4, Queued.AddMinutes(6)) }, task), "多个新实例时不猜 / No guessing among several new instances");
        }

        [TestMethod]
        public void Pick_Unpinned_PrefersRecordedName()
        {
            var a = Vs(1, Queued); var b = Vs(2, Queued);
            var task = new QueuedTask { VsKey = Sln, Created = Queued, VsName = "Shape" };
            Assert.AreSame(b, TaskTarget.Pick(new[] { a, b }, task, v => v == b ? "Shape" : "Params"));
        }

        [TestMethod]
        public async Task Dispatcher_SendsToPinnedInstance_AndKeepsItsName()
        {
            // 复现：任务入队到第二个实例，调度时不能因为第一个实例先被枚举而改投、改名 / Repro: queued for the second instance; dispatch must not redirect or rename it to the first
            using (new TempDataFolder())
            {
                var clock = new FakeClock { Now = Queued };
                var settings = new AppSettings();
                var queue = new TaskQueue(settings, new MemoryTaskStore(), new RecordingArchive(), clock.Func);
                var host = new TwoInstanceHost();
                var parameters = host.Add(Vs(1, Queued.AddHours(-2)), "Params");
                var shape = host.Add(Vs(2, Queued.AddHours(-1)), "Shape");
                var task = queue.AddFor(shape.InstanceKey, Sln, "Shape", "扫描时跳过 / Skip while scanning", "AI");
                var dispatcher = new TaskDispatcher(queue, host, clock.Func);
                dispatcher.Start();
                for (int i = 0; i < 3 && host.Sent.Count == 0; i++) { clock.Advance(TimeSpan.FromMinutes(1)); await dispatcher.PumpAsync(); }
                Assert.AreEqual(1, host.Sent.Count);
                Assert.AreEqual(shape.Pid, host.Sent[0].Pid);
                Assert.AreEqual("Shape", task.VsName);
                Assert.AreEqual(shape.InstanceKey, task.TargetInstanceKey);
                Assert.AreNotSame(parameters, host.FindTaskVs(task, task.VsKey));
            }
        }

        [TestMethod]
        public void AddFor_SameTextForAnotherInstance_IsNotADuplicate()
        {
            using (new TempDataFolder())
            {
                var queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), () => Queued);
                var a = queue.AddFor("inst:1:1", Sln, "Params", "同一需求 / Same request", "AI");
                var b = queue.AddFor("inst:2:2", Sln, "Shape", "同一需求 / Same request", "AI");
                Assert.AreNotSame(a, b);
                Assert.AreSame(a, queue.AddFor("inst:1:1", Sln, "Params", "同一需求 / Same request", "AI"));
            }
        }

        [TestMethod]
        public void TargetInstanceKey_SurvivesSaveAndReload()
        {
            using (var data = new TempDataFolder())
            {
                var store = new JsonTaskStore(data.File("tasks.json"));
                var queue = new TaskQueue(new AppSettings(), store, new RecordingArchive(), () => Queued);
                queue.AddFor("inst:2:2", Sln, "Shape", "任务 / Task", "AI");
                var reloaded = new TaskQueue(new AppSettings(), store, new RecordingArchive(), () => Queued);
                Assert.AreEqual("inst:2:2", reloaded.Items.Single().TargetInstanceKey);
                Assert.AreEqual("inst:2:2", reloaded.Items.Single().Clone().TargetInstanceKey);
            }
        }

        private sealed class TwoInstanceHost : ITaskDispatchHost, ITaskInstanceHost, IManualChatDispatchHost
        {
            public Task<ManualChatObservation> ObserveManualChatAsync(VsInstance target) => Task.FromResult(ManualChatObservation.Idle);
            public Task<string> SendQueuedAsync(VsInstance target, QueuedTask task, Func<bool> valid) => valid() ? SendAsync(target, task.Text) : Task.FromResult(ManualChatProtection.WaitPrefix);
            private readonly List<VsInstance> _list = new List<VsInstance>();
            private readonly Dictionary<VsInstance, string> _names = new Dictionary<VsInstance, string>();
            public readonly List<VsInstance> Sent = new List<VsInstance>();
            public VsInstance Add(VsInstance v, string name) { _list.Add(v); _names[v] = name; v.Copilot = CopilotState.Idle; return v; }
            public VsInstance FindVs(string vsKey) => _list.FirstOrDefault(v => v.Key == vsKey);
            public VsInstance FindTaskVs(QueuedTask task, string vsKey) => TaskTarget.Pick(_list.Where(v => v.Key == vsKey), task, NameOf);
            public bool CanDispatch(VsInstance v) => true;
            public string NameOf(VsInstance v) => _names[v];
            public bool IsSending => false;
            public DateTime TrackingReadyAt => DateTime.MinValue;
            public Task<string> SendAsync(VsInstance v, string text) { Sent.Add(v); return Task.FromResult("已发送"); }
            public Task<string> ReadAnswerAsync(VsInstance v, QueuedTask expectedTask) => Task.FromResult<string>(null);
            public void SetStatus(string text) { }
            public void LogEvent(string vsName, string text) { }
            public void NotifyAgent(string title, string body) { }
            public void QueueActivityChanged(bool anyActive) { }
            public VsInstance FindTargetVs(QueuedTask t) => FindTaskVs(t, t.VsKey);
            public TimeSpan TargetSettleDelay => TimeSpan.Zero;
            public void AnnounceTask(QueuedTask t, string zh, string en) { }
        }
    }
}