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

        /// <summary>
        /// 输入框不可见（窗格被遮挡或未渲染）不再让任务一直礼让：与空闲一样连续两次确认后放行，写入前仍重新检查草稿。
        /// A hidden input (pane covered or not rendered) no longer makes the task yield forever: like idle it passes after two
        /// consecutive samples, and drafts are still re-checked before writing.
        /// </summary>
        [TestMethod]
        public void Cache_HiddenInput_PassesAfterTwoSamples_AndDoesNotBlock()
        {
            var clock = new FakeClock(); var target = new VsInstance { Pid = 7, StartTicks = 1, SolutionPath = "one.sln" };
            var cache = new ManualChatProbeCache(_ => Task.FromResult(ManualChatObservation.InputHidden), clock.Func);
            var alive = new[] { target };
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            clock.Advance(TimeSpan.FromSeconds(2));
            cache.Read(target, alive);
            var settled = cache.Read(target, alive);
            Assert.AreEqual(ManualChatObservation.InputHidden, settled);
            Assert.IsFalse(ManualChatProtection.Blocks(settled));
            Assert.IsTrue(ManualChatProtection.Blocks(ManualChatObservation.Draft));
            Assert.IsTrue(ManualChatProtection.Blocks(ManualChatObservation.Unknown));
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

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Cache_RepeatedInvalidationKeepsOnePendingProbeAndOtherTargetsHealthy(bool clear)
        {
            var clock = new FakeClock();
            var target = new VsInstance(); var other = new VsInstance();
            var alive = new[] { target, other };
            var pending = new List<TaskCompletionSource<ManualChatObservation>>();
            int calls = 0, healthyCalls = 0;
            var cache = new ManualChatProbeCache(v =>
            {
                if (v == other) { healthyCalls++; return Task.FromResult(ManualChatObservation.Draft); }
                calls++;
                var work = new TaskCompletionSource<ManualChatObservation>();
                pending.Add(work);
                return work.Task;
            }, clock.Func);
            try
            {
                cache.Read(target, alive);
                for (int i = 0; i < 4; i++)
                {
                    if (clear) cache.Clear(); else cache.Invalidate(target);
                    clock.Advance(TimeSpan.FromMinutes(20));
                    Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
                }
                Assert.AreEqual(1, calls);
                cache.Read(other, alive);
                Assert.AreEqual(ManualChatObservation.Draft, cache.Read(other, alive));
                Assert.AreEqual(1, healthyCalls);
            }
            finally { foreach (var work in pending) work.TrySetResult(ManualChatObservation.Unknown); }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Cache_InvalidatedPendingResultCannotReleaseUntilFreshProbeCompletes(bool clear)
        {
            var clock = new FakeClock(); var target = new VsInstance(); var alive = new[] { target };
            var old = new TaskCompletionSource<ManualChatObservation>();
            var fresh = new TaskCompletionSource<ManualChatObservation>();
            int calls = 0;
            var cache = new ManualChatProbeCache(_ => ++calls == 1 ? old.Task : fresh.Task, clock.Func);
            try
            {
                cache.Read(target, alive);
                if (clear) cache.Clear(); else cache.Invalidate(target);
                Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
                Assert.AreEqual(1, calls);
                old.SetResult(ManualChatObservation.PaneMissing);
                Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
                Assert.AreEqual(2, calls);
                Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
                fresh.SetResult(ManualChatObservation.Idle);
                Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
                clock.Advance(TimeSpan.FromSeconds(2));
                Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
                Assert.AreEqual(ManualChatObservation.Idle, cache.Read(target, alive));
                Assert.AreEqual(3, calls);
            }
            finally { old.TrySetResult(ManualChatObservation.Unknown); fresh.TrySetResult(ManualChatObservation.Unknown); }
        }

        [TestMethod]
        public void Cache_InvalidatingCompletedResultRefreshesWithoutThrottleOrStaleIdlePasses()
        {
            var clock = new FakeClock(); var target = new VsInstance(); var alive = new[] { target };
            int calls = 0;
            var cache = new ManualChatProbeCache(_ => { calls++; return Task.FromResult(ManualChatObservation.Idle); }, clock.Func);
            cache.Read(target, alive); cache.Read(target, alive);
            clock.Advance(TimeSpan.FromSeconds(2));
            cache.Read(target, alive);
            Assert.AreEqual(ManualChatObservation.Idle, cache.Read(target, alive));
            cache.Invalidate(target);
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(3, calls);
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            clock.Advance(TimeSpan.FromSeconds(2));
            cache.Read(target, alive);
            Assert.AreEqual(ManualChatObservation.Idle, cache.Read(target, alive));
            Assert.AreEqual(4, calls);
        }

        [DataTestMethod]
        [DataRow("path")]
        [DataRow("pid")]
        [DataRow("start")]
        [DataRow("instance")]
        public void Cache_ChangedIdentityCanProbeWhileOldPendingResultNeverReplacesNewResult(string change)
        {
            var target = new VsInstance { Pid = 12, StartTicks = 1, SolutionPath = "one.sln" };
            var old = new TaskCompletionSource<ManualChatObservation>();
            var fresh = new TaskCompletionSource<ManualChatObservation>();
            int calls = 0;
            var cache = new ManualChatProbeCache(_ => ++calls == 1 ? old.Task : fresh.Task);
            try
            {
                cache.Read(target, new[] { target });
                switch (change)
                {
                    case "path": target.SolutionPath = "two.sln"; break;
                    case "pid": target.Pid++; break;
                    case "start": target.StartTicks++; break;
                    case "instance": target = new VsInstance { Pid = 12, StartTicks = 1, SolutionPath = "one.sln" }; break;
                }
                Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, new[] { target }));
                Assert.AreEqual(2, calls);
                fresh.SetResult(ManualChatObservation.Draft);
                Assert.AreEqual(ManualChatObservation.Draft, cache.Read(target, new[] { target }));
                old.SetResult(ManualChatObservation.PaneMissing);
                Assert.AreEqual(ManualChatObservation.Draft, cache.Read(target, new[] { target }));
                Assert.AreEqual(2, calls);
            }
            finally { old.TrySetResult(ManualChatObservation.Unknown); fresh.TrySetResult(ManualChatObservation.Unknown); }
        }

        [TestMethod]
        public void Cache_ReturningToPendingIdentityDoesNotSpawnOrReuseInvalidatedObservation()
        {
            var target = new VsInstance { SolutionPath = "one.sln" }; var alive = new[] { target };
            var old = new TaskCompletionSource<ManualChatObservation>();
            int calls = 0;
            var cache = new ManualChatProbeCache(_ => ++calls == 1 ? old.Task : Task.FromResult(ManualChatObservation.Draft));
            try
            {
                cache.Read(target, alive);
                target.SolutionPath = "two.sln";
                cache.Read(target, alive);
                Assert.AreEqual(ManualChatObservation.Draft, cache.Read(target, alive));
                target.SolutionPath = "one.sln";
                Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
                Assert.AreEqual(2, calls);
                old.SetResult(ManualChatObservation.PaneMissing);
                Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
                Assert.AreEqual(ManualChatObservation.Draft, cache.Read(target, alive));
                Assert.AreEqual(3, calls);
            }
            finally { old.TrySetResult(ManualChatObservation.Unknown); }
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

        [TestMethod]
        public void InvalidateOneTargetDiscardsStaleResultAndRequiresTwoFreshIdleSamples()
        {
            var clock = new FakeClock(); var target = new VsInstance(); var other = new VsInstance();
            var alive = new[] { target, other }; int calls = 0;
            var old = new TaskCompletionSource<ManualChatObservation>();
            var cache = new ManualChatProbeCache(v =>
            {
                if (v == other) return Task.FromResult(ManualChatObservation.Draft);
                return ++calls == 1 ? old.Task : Task.FromResult(ManualChatObservation.Idle);
            }, clock.Func);
            cache.Read(target, alive); cache.Read(other, alive); cache.Read(other, alive);
            cache.Invalidate(target);
            old.SetResult(ManualChatObservation.Draft);
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Draft, cache.Read(other, alive));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.AreEqual(ManualChatObservation.Unknown, cache.Read(target, alive));
            Assert.AreEqual(ManualChatObservation.Idle, cache.Read(target, alive));
            Assert.AreEqual(3, calls);
        }
    }
}
