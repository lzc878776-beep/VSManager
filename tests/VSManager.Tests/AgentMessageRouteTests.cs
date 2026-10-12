using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AI = Microsoft.Extensions.AI;

namespace VSManager.Tests
{
    /// <summary>
    /// 助手消息标题按实际路由显示：发布到哪个 VS、结果来自哪个 VS；没有路由时仍显示进程。
    /// The assistant message title follows the actual route: which VS it published to and which VS the results came from; without a route it still shows the process.
    /// </summary>
    [TestClass]
    public class AgentMessageRouteTests
    {
        private TempDataFolder _data;
        private IDisposable _chatPath;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _chatPath = AgentChatLog.OverrideFilePath(_data.File(AgentChatLog.FileName));
        }

        [TestCleanup]
        public void Cleanup()
        {
            _chatPath.Dispose();
            _data.Dispose();
        }

        [TestMethod]
        public void Label_ShowsPublishedToAndResultSource_FallsBackToProcess()
        {
            var m = new ChatMessage { Role = ChatRole.Assistant };
            m.Targets.Add("#1 Cadmium.RebarParam");
            Assert.AreEqual("AI 助手 · 发布到 #1 Cadmium.RebarParam", AgentPanel.AssistantLabelOf(m));
            m.Sources.Add("Orders");
            Assert.AreEqual("AI 助手 · 发布到 #1 Cadmium.RebarParam · 结果来自 Orders", AgentPanel.AssistantLabelOf(m));
            m.Targets.Clear();
            Assert.AreEqual("AI 助手 · 结果来自 Orders", AgentPanel.AssistantLabelOf(m));
            Assert.AreEqual("发布到 A、B、C 等 5 个", AgentPanel.RouteText(new[] { "A", "B", " a ", "C", "D", "E", "" }, null));
            // 没有路由时保持原来的进程标题 / Without a route the former process title is kept
            Assert.AreEqual("AI 助手 · VSManager · PID 42",
                AgentPanel.AssistantLabelOf(new ChatMessage { Role = ChatRole.Assistant, Process = "42@20261008090000", ProcessName = "VSManager" }));
        }

        [TestMethod]
        public void ChatLog_RoundTripsRoutes_AndResumeRestoresThem()
        {
            AgentChatLog.Append(AgentChatLog.RoleUser, "发布");
            AgentChatLog.Append(AgentChatLog.RoleAssistant, "已发布", to: new[] { "Orders", " orders ", "" }, from: new[] { "Billing" });
            AgentChatLog.Append(AgentChatLog.RoleAssistant, "plain");
            var all = AgentChatLog.ReadAll();
            CollectionAssert.AreEqual(new[] { "Orders" }, all[1].To);
            CollectionAssert.AreEqual(new[] { "Billing" }, all[1].From);
            Assert.AreEqual(0, all[2].To.Count + all[2].From.Count);
            Assert.AreEqual(0, AgentChatLog.Parse("{\"time\":\"2026-10-08T09:00:00\",\"role\":\"assistant\",\"text\":\"old\"}").To.Count, "older records");
            using (var agent = new AgentService(null, () => new AppSettings()))
            {
                agent.RestoreConversation(all);
                var restored = agent.Transcript.Messages.First(x => x.Parts.Any(p => p.Text == "已发布"));
                Assert.AreEqual("AI 助手 · 发布到 Orders · 结果来自 Billing", AgentPanel.AssistantLabelOf(restored));
            }
        }

        [TestMethod]
        public void RouteOf_ResolvesVsArgument_TaskId_AndIgnoresFailures()
        {
            var host = new AgentDesktopTests.DesktopHost();
            host.Instances.Add(new VsInstance { Pid = 42, SolutionPath = _data.Path, Key = "test" });
            host.TaskVs[7] = "Billing";
            using (var agent = new AgentService(host, () => new AppSettings()))
            {
                var publish = agent.RouteOf("send_task", new Dictionary<string, object> { ["vs"] = "1" });
                Assert.IsTrue(publish.Publish);
                Assert.AreEqual("Test VS", publish.Vs);
                var read = agent.RouteOf("read_vs_chat", new Dictionary<string, object> { ["vs"] = "#1" });
                Assert.IsFalse(read.Publish);
                Assert.AreEqual("Test VS", read.Vs);
                Assert.IsNull(agent.RouteOf("read_vs_chat", new Dictionary<string, object> { ["vs"] = "9" }), "unresolved read");
                Assert.AreEqual("Billing", agent.RouteOf("retry_task", new Dictionary<string, object> { ["id"] = 7 }).Vs);
                Assert.IsFalse(agent.RouteOf("read_task_reply", new Dictionary<string, object> { ["id"] = "7" }).Publish);
                Assert.IsNull(agent.RouteOf("list_tasks", new Dictionary<string, object>()));

                var m = new ChatMessage { Role = ChatRole.Assistant };
                AgentService.ApplyRoute(m, publish, "❌ 发送失败");
                AgentService.ApplyRoute(m, publish, "用户拒绝了该操作。");
                AgentService.ApplyRoute(m, publish, "没有编号为 9 的 VS。\n1. Test VS");
                AgentService.ApplyRoute(m, publish, null);
                Assert.AreEqual(0, m.Targets.Count);
                AgentService.ApplyRoute(m, publish, "Queued @3");
                AgentService.ApplyRoute(m, publish, "Queued @4");
                AgentService.ApplyRoute(m, read, "1. user: hi");
                CollectionAssert.AreEqual(new[] { "Test VS" }, m.Targets);
                CollectionAssert.AreEqual(new[] { "Test VS" }, m.Sources);
            }
        }

        [TestMethod]
        public async Task LiveRound_TitleShowsTheVsTheTaskWasPublishedTo()
        {
            var settings = new AppSettings { AgentEndpoint = "http://localhost:11434/v1", AgentModel = "test", AgentConfirm = false };
            var host = new AgentDesktopTests.DesktopHost();
            host.Instances.Add(new VsInstance { Pid = 42, SolutionPath = _data.Path, Key = "test" });
            host.Queue = new TaskQueue(settings, new MemoryTaskStore(), new RecordingArchive(), () => DateTime.Now);
            using (var agent = new AgentService(host, () => settings, new SendTaskClient()))
            {
                await agent.RunAsync("发个任务");
                Assert.AreEqual(1, host.Queue.Items.Count);
                var reply = agent.Transcript.Messages.Last(x => x.Role == ChatRole.Assistant);
                CollectionAssert.AreEqual(new[] { "Test VS" }, reply.Targets, "失败的第二次调用不计入 / the failed second call is not counted");
                Assert.AreEqual(0, reply.Sources.Count);
                StringAssert.StartsWith(AgentPanel.AssistantLabelOf(reply), "AI 助手 · 发布到 Test VS");
            }
            var record = AgentChatLog.ReadAll().Last(x => x.Role == AgentChatLog.RoleAssistant);
            CollectionAssert.AreEqual(new[] { "Test VS" }, record.To);
        }

        [TestMethod]
        public async Task Notice_RecordsTheVsTheResultCameFrom()
        {
            using (var agent = new AgentService(null, () => new AppSettings()))
            {
                agent.Notify("任务 #3 已完成", "body", null, " Billing ");
                for (int i = 0; i < 100 && agent.Transcript.Messages.Count == 0; i++) await Task.Delay(50);
                var m = agent.Transcript.Messages.Single();
                Assert.AreEqual("AI 助手 · 结果来自 Billing", AgentPanel.AssistantLabelOf(m));
            }
            CollectionAssert.AreEqual(new[] { "Billing" }, AgentChatLog.ReadAll().Single().From);
        }

        [TestMethod]
        public async Task Dispatcher_PassesTheTaskVsAsNoticeSource()
        {
            var host = new FakeDispatchHost();
            var queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), () => DateTime.Now);
            var dispatcher = new TaskDispatcher(queue, host, () => DateTime.Now);
            dispatcher.Start();
            var v = host.AddVs("Billing");
            var t = queue.Add("Billing", "Billing", "task", "AI");
            await dispatcher.PumpAsync();
            dispatcher.Finish(t, v, null);
            Assert.AreEqual(1, host.Notices.Count);
            CollectionAssert.AreEqual(new[] { "Billing" }, host.NoticeSources);
        }

        /// <summary>第一次请求发布到 1 号 VS 并尝试发布到不存在的 9 号，之后回复文字。/ Publishes to VS 1 and tries a missing VS 9 on the first request, then replies with text.</summary>
        private sealed class SendTaskClient : IAiClientFactory, AI.IChatClient
        {
            private int _requests;
            public AI.IChatClient Create(Uri endpoint, string model, string apiKey) => this;
            public Task<AI.ChatResponse> GetResponseAsync(IEnumerable<AI.ChatMessage> messages, AI.ChatOptions options = null, System.Threading.CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public async IAsyncEnumerable<AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<AI.ChatMessage> messages, AI.ChatOptions options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                if (_requests++ == 0)
                    yield return new AI.ChatResponseUpdate(AI.ChatRole.Assistant, new List<AI.AIContent>
                    {
                        new AI.FunctionCallContent("c1", "send_task", new Dictionary<string, object> { ["vs"] = "1", ["task"] = "Route task", ["title"] = "Route" }),
                        new AI.FunctionCallContent("c2", "send_task", new Dictionary<string, object> { ["vs"] = "9", ["task"] = "Missing task", ["title"] = "Missing" }),
                    });
                else
                    yield return new AI.ChatResponseUpdate(AI.ChatRole.Assistant, "done");
            }
            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }
    }
}
