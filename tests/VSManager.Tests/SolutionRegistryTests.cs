using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>解决方案别名解析：精确、同义词、后缀、模糊、候选与未命中。/ Alias resolution: exact, synonyms, suffixes, fuzzy, candidates and no match.</summary>
    [TestClass]
    public class SolutionMatcherTests
    {
        // 示例根目录在运行时生成，避免源码中出现盘符路径 / The sample root is built at run time so no drive path appears in source
        internal static readonly string Root = Path.Combine(Path.GetTempPath(), "VSManager.Tests.Solutions");
        internal static string P(string relative) => Path.Combine(Root, relative);

        private static readonly List<SolutionEntry> Entries = new List<SolutionEntry>
        {
            new SolutionEntry { Alias = "订单项目", Path = P(@"Order\OrderSystem.sln"), Synonyms = new List<string> { "订单", "下单", "order" } },
            new SolutionEntry { Alias = "VS平台项目", Path = P(@"Platform\VsPlatform.slnx"), Description = "Visual Studio 扩展平台" },
            new SolutionEntry { Alias = "报表工具", Path = P(@"Report\ReportKit.sln"), Synonyms = new List<string> { "report" } },
        };

        [DataTestMethod]
        [DataRow("订单项目")]
        [DataRow("订单")]
        [DataRow("下单")]
        [DataRow("order")]
        [DataRow("ＯＲＤＥＲ")]
        [DataRow(" Order ")]
        [DataRow("下单项目")]
        [DataRow("订单解决方案")]
        [DataRow("OrderSystem")]
        public void SynonymsAndVariants_HitSameEntry(string query)
        {
            var r = SolutionMatcher.Resolve(Entries, query);
            Assert.IsTrue(r.Found, query);
            Assert.AreEqual("订单项目", r.Hit.Alias, query);
        }

        [TestMethod]
        public void FullPath_IgnoresCaseAndSeparators()
        {
            var r = SolutionMatcher.Resolve(Entries, P(@"Order\OrderSystem.sln").ToLowerInvariant().Replace('\\', '/'));
            Assert.AreEqual("订单项目", r.Hit?.Alias);
        }

        [TestMethod]
        public void PlatformAlias_IgnoresSuffixAndCase()
        {
            Assert.AreEqual("VS平台项目", SolutionMatcher.Resolve(Entries, "vs平台").Hit?.Alias);
            Assert.AreEqual("VS平台项目", SolutionMatcher.Resolve(Entries, "VS 平台 项目").Hit?.Alias);
            Assert.AreEqual("VS平台项目", SolutionMatcher.Resolve(Entries, "vsplatform").Hit?.Alias);
        }

        [TestMethod]
        public void MultipleMatches_ReturnCandidates()
        {
            // 查询被两条别名同等包含 / The query is contained equally by both aliases
            var list = new List<SolutionEntry>
            {
                new SolutionEntry { Alias = "订单统计", Path = P(@"a\A.sln") },
                new SolutionEntry { Alias = "订单打印", Path = P(@"b\B.sln") },
            };
            var r = SolutionMatcher.Resolve(list, "订单");
            Assert.IsFalse(r.Found);
            Assert.IsTrue(r.Ambiguous);
            CollectionAssert.AreEquivalent(new[] { "订单统计", "订单打印" }, r.Candidates.Select(c => c.Entry.Alias).ToArray());
        }

        [TestMethod]
        public void NoMatch_IsNotFound()
        {
            var r = SolutionMatcher.Resolve(Entries, "天气预报");
            Assert.IsFalse(r.Found);
            Assert.IsFalse(r.Ambiguous);
            Assert.AreEqual(0, r.Candidates.Count);
        }

        [TestMethod]
        public void EmptyQueryOrRegistry_IsNotFound()
        {
            Assert.IsFalse(SolutionMatcher.Resolve(Entries, "  ").Found);
            Assert.IsFalse(SolutionMatcher.Resolve(new SolutionEntry[0], "订单").Found);
        }

        [TestMethod]
        public void SamePath_IgnoresCaseAndSeparators()
        {
            Assert.IsTrue(SolutionMatcher.SamePath(P("A.sln"), P("a.sln").ToUpperInvariant().Replace('\\', '/')));
            Assert.IsFalse(SolutionMatcher.SamePath(P("A.sln"), P("B.sln")));
            Assert.IsFalse(SolutionMatcher.SamePath("", ""));
        }

        [TestMethod]
        public void ParseSynonyms_SplitsCommonSeparators()
        {
            CollectionAssert.AreEqual(new[] { "订单", "下单", "order" }, SolutionEntry.ParseSynonyms("订单、下单，order; Order").ToArray());
        }
    }

    /// <summary>登记表持久化：保存、读取、去重与损坏回退。/ Registry persistence: save, load, de-dup and corrupt fallback.</summary>
    [TestClass]
    public class SolutionRegistryTests
    {
        [TestMethod]
        public void SaveAndLoad_RoundTrip()
        {
            using (var data = new TempDataFolder())
            {
                var reg = new SolutionRegistry().Load();
                Assert.AreEqual(0, reg.Count);
                Assert.IsNull(reg.Upsert(new SolutionEntry { Alias = " 订单项目 ", Path = "\"" + SolutionMatcherTests.P("R.sln") + "\"", Synonyms = new List<string> { "下单", "下单" }, DefaultVs = 2 }));
                Assert.IsTrue(File.Exists(data.File("solutions.json")));

                var again = new SolutionRegistry().Load();
                var e = again.Items.Single();
                Assert.AreEqual("订单项目", e.Alias);
                Assert.AreEqual(SolutionMatcherTests.P("R.sln"), e.Path);
                CollectionAssert.AreEqual(new[] { "下单" }, e.Synonyms.ToArray());
                Assert.AreEqual(2, e.DefaultVs);
                Assert.AreEqual("订单项目", again.Resolve("下单").Hit?.Alias);
            }
        }

        [TestMethod]
        public void Sanitize_DropsInvalidAndDuplicateAliases()
        {
            var list = SolutionRegistry.Sanitize(new[]
            {
                new SolutionEntry { Alias = "A", Path = "a.sln" },
                new SolutionEntry { Alias = "a", Path = "b.sln" },
                new SolutionEntry { Alias = "", Path = "c.sln" },
                new SolutionEntry { Alias = "D", Path = " " },
                null
            });
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("a.sln", list[0].Path);
        }

        [TestMethod]
        public void CorruptFile_FallsBackToBackup()
        {
            using (var data = new TempDataFolder())
            {
                var reg = new SolutionRegistry().Load();
                reg.Upsert(new SolutionEntry { Alias = "A", Path = SolutionMatcherTests.P("a.sln") });
                reg.Upsert(new SolutionEntry { Alias = "B", Path = SolutionMatcherTests.P("b.sln") });
                File.WriteAllText(data.File("solutions.json"), "{ broken");
                var again = new SolutionRegistry().Load();
                Assert.IsNotNull(again.LastError);
                Assert.IsTrue(again.Count >= 1, "从 .bak 恢复 / restored from .bak");
            }
        }
    }

    /// <summary>等待目标 VS：暂存、持久化、打开后自动推送与取消。/ Waiting for the target VS: parking, persistence, auto push and cancel.</summary>
    [TestClass]
    public class ParkedTaskTests
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
        }

        [TestCleanup]
        public void Cleanup() => _data.Dispose();

        [TestMethod]
        public void WaitingVs_IsActiveKnownAndHasText()
        {
            Assert.IsTrue(QueueStatus.Active(QueueStatus.WaitingVs));
            Assert.IsTrue(QueueStatus.Known(QueueStatus.WaitingVs));
            var t = _queue.AddParked(SolutionMatcherTests.P("R.sln"), "订单项目", "x", "AI");
            Assert.AreEqual(QueueStatus.WaitingVs, t.Status);
            StringAssert.Contains(TaskStateMachine.StatusText(t, _clock.Now), "订单项目");
            Assert.AreEqual("订单项目", t.Clone().Target);
        }

        [TestMethod]
        public async Task Parked_StaysUntilTargetOpens_ThenPushedAfterSettle()
        {
            _host.Settle = TimeSpan.FromSeconds(20);
            var t = _queue.AddParked("R", "订单项目", "do it", "AI");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.WaitingVs, t.Status);
            Assert.AreEqual(0, _host.Sent.Count);
            Assert.AreEqual(true, _host.LastActivity, "暂存任务保持调度计时器 / parked tasks keep the timer on");

            _host.AddVs("R");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Waiting, t.Status, "目标打开后转为排队 / back to waiting once open");
            Assert.AreEqual(0, _host.Sent.Count, "等待加载 / waits for the solution to load");
            Assert.AreEqual(1, _host.Announced.Count);
            Assert.AreEqual(1, _host.Notices.Count, "通知 AI / AI notified");
            Assert.IsTrue(_archive.Events.Contains("#" + t.Id + ":target_opened"));

            _clock.Advance(TimeSpan.FromSeconds(21));
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, t.Status);
            CollectionAssert.AreEqual(new[] { "R:do it" }, _host.Sent.ToArray());
        }

        [TestMethod]
        public async Task Parked_DoesNotBlockOtherVs()
        {
            _host.AddVs("A");
            _queue.AddParked("R", "订单项目", "p", "AI");
            var a = _queue.Add("A", "A", "a", "AI");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, a.Status);
        }

        [TestMethod]
        public void Parked_CanBeCancelled_AndSurvivesRestart()
        {
            var t = _queue.AddParked("R", "订单项目", "p", "AI");
            var saved = _store.Saved.Single(x => x.Id == t.Id);
            Assert.AreEqual(QueueStatus.WaitingVs, saved.Status);
            Assert.AreEqual("订单项目", saved.Target);

            var store2 = new MemoryTaskStore { Initial = _store.Saved };
            var q2 = new TaskQueue(new AppSettings(), store2, new RecordingArchive(), _clock.Func);
            Assert.AreEqual(QueueStatus.WaitingVs, q2.Find(t.Id).Status, "重启后仍保留 / kept after restart");

            Assert.IsTrue(TaskStateMachine.Cancel(t, _clock.Now));
            Assert.AreEqual(QueueStatus.Cancelled, t.Status);
        }

        [TestMethod]
        public void JsonStore_RoundTripsTargetAndStatus()
        {
            var path = _data.File("tasks.json");
            var store = new JsonTaskStore(path);
            var t = new QueuedTask { Id = 1, VsKey = SolutionMatcherTests.P("R.sln"), VsName = "订单项目", Text = "x", Source = "AI", Status = QueueStatus.WaitingVs, Created = _clock.Now, Target = "订单项目" };
            Assert.IsNull(store.Save(new[] { t, new QueuedTask { Id = 2, VsKey = "k", VsName = "n", Text = "y", Status = QueueStatus.Waiting, Created = _clock.Now } }));
            Assert.IsFalse(File.ReadAllText(path).Contains("\"Target\":null"), "普通任务不写 Target / no Target for ordinary tasks");
            var back = store.Load(new List<string>());
            Assert.AreEqual(QueueStatus.WaitingVs, back[0].Status);
            Assert.AreEqual("订单项目", back[0].Target);
            Assert.IsNull(back[1].Target);
        }

        [TestMethod]
        public void Settings_SolutionDefaultsAndClamp()
        {
            var s = new AppSettings();
            Assert.IsTrue(s.SolutionCloseConfirm);
            Assert.IsTrue(s.PendingVsNotify);
            Assert.AreEqual(90, s.SolutionOpenWaitSeconds);
            Assert.AreEqual(20, s.PendingVsSettleSeconds);
            s.SolutionOpenWaitSeconds = 5; s.PendingVsSettleSeconds = 999;
            s.ClampSolutions();
            Assert.AreEqual(10, s.SolutionOpenWaitSeconds);
            Assert.AreEqual(300, s.PendingVsSettleSeconds);
            s.SolutionOpenWaitSeconds = 0; s.PendingVsSettleSeconds = -1;
            s.ClampSolutions();
            Assert.AreEqual(90, s.SolutionOpenWaitSeconds);
            Assert.AreEqual(0, s.PendingVsSettleSeconds);
        }
    }
}
