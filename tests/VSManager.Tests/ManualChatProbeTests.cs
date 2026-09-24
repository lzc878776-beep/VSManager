using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class ManualChatProbeTests
    {
        [TestMethod]
        public void Cache_TwoIdleSamples_ThrottleAndStableTarget()
        {
            var clock = new FakeClock(); var target = new VsInstance { Pid = 12, StartTicks = 1, SolutionPath = "one.sln" };
            int calls = 0;
            var cache = new ManualChatProbeCache(_ => { calls++; return Task.FromResult(ManualChatObservation.Idle); }, clock.Func);
            var alive = new[] { target };
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(1, calls);
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Idle, cache.Read(target, alive));
            Assert.AreEqual(2, calls);
            target.SolutionPath = "other.sln";
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(3, calls);
        }

        [TestMethod]
        public void Cache_SlowProbeDoesNotBlockOtherTargetsOrSpawnDuplicates()
        {
            var clock = new FakeClock(); var first = new VsInstance(); var second = new VsInstance();
            var slow = new TaskCompletionSource<ManualChatObservation>(); int calls = 0;
            var cache = new ManualChatProbeCache(v => { calls++; return v == first ? slow.Task : Task.FromResult(ManualChatObservation.Draft); }, clock.Func);
            var alive = new[] { first, second };
            cache.Read(first, alive); clock.Advance(TimeSpan.FromMinutes(20));
            for (int i = 0; i < 20; i++) Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(first, alive));
            cache.Read(second, alive);
            Assert.AreEqual(ManualChatObservation.Draft, cache.Read(second, alive));
            Assert.AreEqual(2, calls);
            slow.SetResult(ManualChatObservation.Idle);
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(first, alive));
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public void Cache_UnknownResetsIdle_ClearRequiresFreshObservations()
        {
            var clock = new FakeClock(); var target = new VsInstance(); var alive = new[] { target };
            var result = ManualChatObservation.Idle;
            var cache = new ManualChatProbeCache(_ => Task.FromResult(result), clock.Func);
            cache.Read(target, alive); cache.Read(target, alive);
            clock.Advance(TimeSpan.FromSeconds(2)); result = ManualChatObservation.Unknown;
            cache.Read(target, alive); cache.Read(target, alive);
            clock.Advance(TimeSpan.FromSeconds(2)); result = ManualChatObservation.Idle;
            cache.Read(target, alive); Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            clock.Advance(TimeSpan.FromSeconds(2)); cache.Read(target, alive);
            Assert.AreEqual(ManualChatObservation.Idle, cache.Read(target, alive));
            cache.Clear(); Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, new VsInstance[0]));
        }

        [TestMethod]
        public void Cache_OutstandingCallsAreBoundedEvenAfterClearAndClosedTargets()
        {
            var tasks = new List<TaskCompletionSource<ManualChatObservation>>();
            var targets = Enumerable.Range(0, 8).Select(_ => new VsInstance()).ToArray();
            var cache = new ManualChatProbeCache(_ => { var t = new TaskCompletionSource<ManualChatObservation>(); tasks.Add(t); return t.Task; });
            foreach (var target in targets) { cache.Read(target, new[] { target }); cache.Clear(); }
            Assert.AreEqual(4, tasks.Count);
            tasks[0].SetResult(ManualChatObservation.Unknown);
            cache.Read(targets[7], targets); Assert.AreEqual(5, tasks.Count);
        }

        [TestMethod]
        public void Cache_FaultAndNullAreUnknown()
        {
            var target = new VsInstance(); var alive = new[] { target };
            var faulted = new ManualChatProbeCache(_ => Task.FromException<ManualChatObservation>(new InvalidOperationException()));
            faulted.Read(target, alive); Assert.AreEqual(ManualChatObservation.Unknown, faulted.Read(target, alive));
            var empty = new ManualChatProbeCache(_ => null);
            empty.Read(target, alive); Assert.AreEqual(ManualChatObservation.Unknown, empty.Read(target, alive));
        }
    }
}
