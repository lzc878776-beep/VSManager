using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>定期自动清理 VS 内存的调度、跳过规则与结果。/ Scheduling, skip rules and results of periodic VS memory cleanup.</summary>
    [TestClass]
    public class VsAutoMemoryTrimTests
    {
        private const long Mb = 1048576;
        private DateTime _now = new DateTime(2026, 1, 1, 8, 0, 0);
        private AppSettings _settings;
        private readonly List<AutoTrimTarget> _targets = new List<AutoTrimTarget>();
        private readonly MemSnapshot _snap = new MemSnapshot();
        private readonly List<string> _cleaned = new List<string>();
        private TempDataFolder _data;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _settings = new AppSettings { AutoTrimVsMemory = true, AutoTrimIntervalMinutes = 30, AutoTrimThresholdMB = 0 };
        }

        [TestCleanup]
        public void Cleanup() => _data.Dispose();

        private VsAutoMemoryTrimmer Trimmer() => new VsAutoMemoryTrimmer(() => _settings,
            () => Task.FromResult<IList<AutoTrimTarget>>(_targets), refs => _snap,
            (key, refs) =>
            {
                _cleaned.Add(key);
                var g = _snap.Find(key);
                return Task.FromResult(new MemCleanResult { Target = g.Owner, WsBefore = g.WorkingSet, WsAfter = g.WorkingSet - 100 * Mb });
            }, () => _now);

        private VsInstance AddVs(int pid, long wsMb, bool taskActive = false, string trimmableName = "devenv.exe")
        {
            var vs = new VsInstance { Pid = pid, Key = "vs" + pid, Dte = new object() };
            _targets.Add(new AutoTrimTarget { Ref = new VsRef { Vs = vs, Number = _targets.Count + 1, Name = "Demo" + pid }, TaskActive = taskActive });
            var g = new MemGroup { Kind = MemGroupKind.Vs, RootPid = pid, Number = _targets.Count, Name = "Demo" + pid };
            var p = new MemProc { Pid = pid, Name = trimmableName, WorkingSet = wsMb * Mb, Private = wsMb * Mb };
            VsMemory.Classify(p);
            g.Procs.Add(p);
            _snap.Groups.Add(g);
            return vs;
        }

        [TestMethod]
        public void Defaults_AreOffThirtyMinutesNoThreshold()
        {
            var s = new AppSettings();
            Assert.IsFalse(s.AutoTrimVsMemory);
            Assert.AreEqual(30, s.AutoTrimIntervalMinutes);
            Assert.AreEqual(0, s.AutoTrimThresholdMB);
        }

        [TestMethod]
        public void Clamp_IntervalAndThreshold()
        {
            Assert.AreEqual(30, VsAutoMemoryTrimmer.ClampInterval(0));
            Assert.AreEqual(5, VsAutoMemoryTrimmer.ClampInterval(1));
            Assert.AreEqual(1440, VsAutoMemoryTrimmer.ClampInterval(99999));
            Assert.AreEqual(45, VsAutoMemoryTrimmer.ClampInterval(45));
            Assert.AreEqual(0, VsAutoMemoryTrimmer.ClampThreshold(-5));
            Assert.AreEqual(2048, VsAutoMemoryTrimmer.ClampThreshold(2048));
        }

        [TestMethod]
        public void SkipReason_CoversDebugBuildCopilotTaskAndUnknown()
        {
            Assert.IsNotNull(VsAutoMemoryTrimmer.SkipReason(null, false));
            Assert.IsNotNull(VsAutoMemoryTrimmer.SkipReason(new VsInstance(), false), "无 DTE 状态未知 / no DTE = unknown");
            Assert.IsNotNull(VsAutoMemoryTrimmer.SkipReason(new VsInstance { Dte = new object(), DebugMode = 2 }, false));
            Assert.IsNotNull(VsAutoMemoryTrimmer.SkipReason(new VsInstance { Dte = new object(), DebugMode = 3 }, false));
            Assert.IsNotNull(VsAutoMemoryTrimmer.SkipReason(new VsInstance { Dte = new object(), Building = true }, false));
            Assert.IsNotNull(VsAutoMemoryTrimmer.SkipReason(new VsInstance { Dte = new object(), Copilot = CopilotState.Busy }, false));
            Assert.IsNotNull(VsAutoMemoryTrimmer.SkipReason(new VsInstance { Dte = new object() }, true));
            Assert.IsNull(VsAutoMemoryTrimmer.SkipReason(new VsInstance { Dte = new object(), DebugMode = 1 }, false));
        }

        [TestMethod]
        public async Task Run_CleansOnlyEligibleInstances()
        {
            AddVs(1, 2000);
            AddVs(2, 2000).DebugMode = 2;
            AddVs(3, 2000).Building = true;
            AddVs(4, 2000, taskActive: true);
            AddVs(5, 2000, trimmableName: "testhost.exe");
            var report = await Trimmer().RunNowAsync();
            CollectionAssert.AreEqual(new[] { "vs:1" }, _cleaned);
            Assert.AreEqual(1, report.Cleaned);
            Assert.AreEqual(4, report.Skipped);
            Assert.AreEqual(100 * Mb, report.FreedBytes);
            Assert.IsTrue(report.Worth);
            StringAssert.Contains(report.SummaryZh, "已自动清理 1 个 VS 实例内存");
            StringAssert.Contains(report.SummaryEn, "Auto-cleaned memory of 1 VS instance(s)");
            string log = System.IO.File.ReadAllText(VsMemory.LogPath);
            StringAssert.Contains(log, "正在调试 / debugging");
            StringAssert.Contains(log, "正在生成 / building");
            StringAssert.Contains(log, "定期清理（手动触发）");
        }

        [TestMethod]
        public async Task Run_Threshold_SkipsSmallInstances()
        {
            _settings.AutoTrimThresholdMB = 1500;
            AddVs(1, 1000);
            AddVs(2, 2000);
            var report = await Trimmer().RunNowAsync();
            CollectionAssert.AreEqual(new[] { "vs:2" }, _cleaned);
            Assert.AreEqual(1, report.Skipped);
        }

        [TestMethod]
        public async Task Run_AllSkipped_IsNotWorthAnnouncing()
        {
            AddVs(1, 2000).Copilot = CopilotState.Busy;
            var report = await Trimmer().RunNowAsync();
            Assert.AreEqual(0, _cleaned.Count);
            Assert.IsFalse(report.Worth);
        }

        [TestMethod]
        public async Task Tick_RunsOnlyWhenDueAndEnabled()
        {
            AddVs(1, 2000);
            var t = Trimmer();
            await t.TickAsync();
            Assert.AreEqual(0, _cleaned.Count, "首次只开始计时 / first tick only anchors");
            Assert.AreEqual(_now.AddMinutes(30), t.NextRun);
            _now = _now.AddMinutes(29);
            await t.TickAsync();
            Assert.AreEqual(0, _cleaned.Count);
            _now = _now.AddMinutes(1);
            await t.TickAsync();
            Assert.AreEqual(1, _cleaned.Count);
            Assert.AreEqual(_now.AddMinutes(30), t.NextRun);
            _settings.AutoTrimVsMemory = false;
            _now = _now.AddHours(2);
            await t.TickAsync();
            Assert.AreEqual(1, _cleaned.Count);
            Assert.IsNull(t.NextRun);
        }

        [TestMethod]
        public async Task Run_TargetsFailure_ReportsErrorWithoutThrowing()
        {
            var t = new VsAutoMemoryTrimmer(() => _settings, () => Task.FromException<IList<AutoTrimTarget>>(new InvalidOperationException("closed")),
                refs => _snap, (k, r) => Task.FromResult(new MemCleanResult()), () => _now);
            var report = await t.RunNowAsync();
            Assert.AreEqual("closed", report.Error);
            Assert.IsFalse(report.Worth);
            Assert.AreSame(report, t.LastReport);
        }
    }
}
