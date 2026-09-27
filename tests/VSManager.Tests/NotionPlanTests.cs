using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>Notion 计划解析与编排测试（不访问网络）。/ Notion plan parsing and orchestration tests (no network).</summary>
    [TestClass]
    public class NotionPlanTests
    {
        private const string Db = "0123456789abcdef0123456789abcdef";
        private static readonly string A = new string('a', 32), B = new string('b', 32);
        private TempDataFolder _data;
        [TestInitialize] public void Init() => _data = new TempDataFolder();
        [TestCleanup] public void Cleanup() => _data.Dispose();

        private static Dictionary<string, object> Page(string id, string title, string target, string status = "rich_text", params string[] deps)
        {
            var props = new Dictionary<string, object>
            {
                ["Name"] = new Dictionary<string, object> { ["type"] = "title", ["title"] = new object[] { new Dictionary<string, object> { ["plain_text"] = title } } },
                ["目标"] = new Dictionary<string, object> { ["type"] = "rich_text", ["rich_text"] = new object[] { new Dictionary<string, object> { ["plain_text"] = target } } },
                ["依赖"] = new Dictionary<string, object> { ["type"] = "relation", ["relation"] = deps.Select(d => (object)new Dictionary<string, object> { ["id"] = d }).ToArray() },
            };
            if (status != null) props[NotionPlanParser.DefaultStatusProperty] = new Dictionary<string, object> { ["type"] = status };
            return new Dictionary<string, object> { ["id"] = id, ["properties"] = props };
        }

        private static SolutionLookup Resolve(string q)
        {
            var e = new SolutionEntry { Alias = "Alpha", Path = @"%USERPROFILE%\src\Alpha.sln" };
            var r = new SolutionLookup { Query = q };
            if (string.Equals(q, "Alpha", StringComparison.OrdinalIgnoreCase)) { r.Hit = e; r.Candidates.Add(new SolutionCandidate { Entry = e, Score = 100 }); }
            return r;
        }

        [TestMethod]
        public void NormalizeId_ExtractsIdFromUrl() =>
            Assert.AreEqual(Db, NotionPlanParser.NormalizeId("https://www.notion.so/workspace/Plan-" + Db + "?v=1"));

        [TestMethod]
        public void UnknownTarget_IsFlaggedNotGuessed()
        {
            var items = NotionPlanParser.Validate(NotionPlanParser.ParseDatabase(new object[] { Page(A, "实现登录页面的表单输入校验逻辑", "Alph") }, null), Resolve, 1000);
            Assert.IsFalse(items[0].Clear);
        }

        [TestMethod]
        public void BriefItemWithoutDetails_IsFlagged()
        {
            var items = NotionPlanParser.Validate(NotionPlanParser.ParseDatabase(new object[] { Page(A, "优化", "Alpha") }, null), Resolve, 1000);
            Assert.IsFalse(items[0].Clear);
        }

        [TestMethod]
        public void MissingStatusProperty_IsFlagged()
        {
            var items = NotionPlanParser.Validate(NotionPlanParser.ParseDatabase(new object[] { Page(A, "实现登录页面的表单输入校验逻辑", "Alpha", null) }, null), Resolve, 1000);
            Assert.IsFalse(items[0].Clear);
        }

        [TestMethod]
        public void CircularDependency_IsFlagged()
        {
            var items = NotionPlanParser.Validate(NotionPlanParser.ParseDatabase(new object[]
            {
                Page(A, "实现登录页面的表单输入校验逻辑", "Alpha", "rich_text", B),
                Page(B, "实现注册页面的表单输入校验逻辑", "Alpha", "rich_text", A),
            }, null), Resolve, 1000);
            Assert.IsTrue(items.All(i => i.Issues.Any(x => x.Contains("循环"))));
        }

        [TestMethod]
        public void Dependencies_AreOrderedFirst()
        {
            var items = NotionPlanParser.Validate(NotionPlanParser.ParseDatabase(new object[]
            {
                Page(A, "实现登录页面的表单输入校验逻辑", "Alpha", "rich_text", B),
                Page(B, "实现注册页面的表单输入校验逻辑", "Alpha"),
            }, null), Resolve, 1000);
            CollectionAssert.AreEqual(new[] { B, A }, items.Select(i => i.PageId).ToArray());
        }

        [TestMethod]
        public async Task DispatchTwice_EnqueuesOnce()
        {
            var host = new FakePlanHost();
            var service = new NotionPlanService(host, new FakeNotion(Page(A, "实现登录页面的表单输入校验逻辑", "Alpha")), () => null, _data.File("plans.json"));
            await service.PreviewAsync(Db, CancellationToken.None);
            await service.DispatchAsync(Db, "1", CancellationToken.None);
            await service.PreviewAsync(Db, CancellationToken.None);
            await service.DispatchAsync(Db, "1", CancellationToken.None);
            Assert.AreEqual(1, host.Enqueued.Count);
        }

        [TestMethod]
        public async Task Dispatch_WithoutPreview_IsRefused()
        {
            var host = new FakePlanHost();
            var service = new NotionPlanService(host, new FakeNotion(), () => null, _data.File("plans.json"));
            await service.DispatchAsync(Db, "all", CancellationToken.None);
            Assert.AreEqual(0, host.Enqueued.Count);
        }

        [TestMethod]
        public async Task Completion_WritesOnlyStatusProperty()
        {
            var host = new FakePlanHost();
            var notion = new FakeNotion(Page(A, "实现登录页面的表单输入校验逻辑", "Alpha"));
            var service = new NotionPlanService(host, notion, () => null, _data.File("plans.json"));
            await service.PreviewAsync(Db, CancellationToken.None);
            await service.DispatchAsync(Db, "all", CancellationToken.None);
            host.Tasks[1].Status = QueueStatus.Done;
            await service.SyncAsync();
            Assert.IsTrue(notion.Updates.All(u => u.Property == NotionPlanParser.DefaultStatusProperty) && notion.Updates.Last().Value.StartsWith("已完成"));
        }

        [TestMethod]
        public async Task CrossTargetDependency_IsHeldUntilDone()
        {
            var host = new FakePlanHost { Aliases = { "Alpha", "Beta" } };
            var notion = new FakeNotion(Page(A, "实现登录页面的表单输入校验逻辑", "Alpha"), Page(B, "实现注册页面的表单输入校验逻辑", "Beta", "rich_text", A));
            var service = new NotionPlanService(host, notion, () => null, _data.File("plans.json"));
            await service.PreviewAsync(Db, CancellationToken.None);
            await service.DispatchAsync(Db, "all", CancellationToken.None);
            Assert.AreEqual(1, host.Enqueued.Count);
        }

        private sealed class FakeNotion : INotionClient
        {
            private readonly List<object> _pages;
            public readonly List<(string Page, string Property, string Value)> Updates = new List<(string, string, string)>();
            public FakeNotion(params object[] pages) => _pages = pages.ToList();
            public Task<List<object>> QueryDatabaseAsync(string databaseId, CancellationToken ct) => Task.FromResult(_pages);
            public Task<List<object>> ReadBlocksAsync(string pageId, CancellationToken ct) => Task.FromResult(new List<object>());
            public Task UpdateStatusAsync(string pageId, string property, string propertyType, string value, CancellationToken ct)
            {
                Updates.Add((pageId, property, value));
                return Task.CompletedTask;
            }
        }

        private sealed class FakePlanHost : IPlanHost
        {
            public readonly HashSet<string> Aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alpha" };
            public readonly List<string> Enqueued = new List<string>();
            public readonly Dictionary<int, QueuedTask> Tasks = new Dictionary<int, QueuedTask>();
            public SolutionLookup ResolveSolution(string query)
            {
                var r = new SolutionLookup { Query = query };
                if (Aliases.Contains(query))
                {
                    r.Hit = new SolutionEntry { Alias = query, Path = @"%USERPROFILE%\src\" + query + ".sln" };
                    r.Candidates.Add(new SolutionCandidate { Entry = r.Hit, Score = 100 });
                }
                return r;
            }
            public Task<int?> EnqueuePlanTask(SolutionEntry entry, string text)
            {
                Enqueued.Add(entry.Alias);
                int id = Tasks.Count + 1;
                Tasks[id] = new QueuedTask { Id = id, Status = QueueStatus.Waiting, Text = text };
                return Task.FromResult<int?>(id);
            }
            public QueuedTask FindTask(int id) => Tasks.TryGetValue(id, out var t) ? t : null;
            public bool SkipFailedPredecessors => true;
            public int MaxTaskText => 12000;
        }
    }
}
