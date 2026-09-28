using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AgentToolGroupTests
    {
        private static List<AIFunction> AllTools(AgentService agent) =>
            ((IEnumerable<AITool>)typeof(AgentService).GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(agent)).OfType<AIFunction>().ToList();

        private static List<string> Names(IEnumerable<AITool> tools) => tools.Select(t => t.Name).ToList();

        private static int SchemaSize(IEnumerable<AITool> tools) =>
            tools.OfType<AIFunction>().Sum(f => f.Name.Length + (f.Description ?? "").Length + f.JsonSchema.ToString().Length);

        [TestMethod]
        public void Groups_ReferOnlyToRegisteredTools_AndEachToolOnce()
        {
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
            {
                var names = new HashSet<string>(Names(AllTools(agent)));
                var grouped = AgentToolGroups.All.SelectMany(g => g.Tools).ToList();
                foreach (var t in grouped) Assert.IsTrue(names.Contains(t), "未注册的分组工具 / Unregistered grouped tool: " + t);
                Assert.AreEqual(grouped.Count, grouped.Distinct().Count());
                Assert.IsTrue(names.Contains(AgentToolGroups.LoaderName));
                Assert.IsNull(AgentToolGroups.GroupOf("send_task"));
                Assert.IsNull(AgentToolGroups.GroupOf(AgentToolGroups.LoaderName));
            }
        }

        [TestMethod]
        public void DefaultRound_AdvertisesCoreOnly_AndIsMuchSmaller()
        {
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
            {
                var all = AllTools(agent);
                var round = agent.ToolsForRound();
                var names = Names(round);
                CollectionAssert.Contains(names, "send_task");
                CollectionAssert.Contains(names, "continue_task");
                CollectionAssert.Contains(names, AgentToolGroups.LoaderName);
                CollectionAssert.DoesNotContain(names, "run_cad_actions");
                CollectionAssert.DoesNotContain(names, "create_note");
                CollectionAssert.DoesNotContain(names, "arrange_workspace_layout");
                int full = SchemaSize(all), core = SchemaSize(round);
                Console.WriteLine("tools " + all.Count + " -> " + round.Count + ", schema chars " + full + " -> " + core);
                Assert.IsTrue(core * 10 < full * 6, "核心工具应明显小于全部 / core should be much smaller: " + core + " vs " + full);
            }
        }

        [TestMethod]
        public void GroupingOff_AdvertisesEverything()
        {
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings { AgentToolGrouping = false }))
                Assert.AreEqual(AllTools(agent).Count, agent.ToolsForRound().Count);
        }

        [TestMethod]
        public void NoteAssistant_IsNotGrouped()
        {
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings(), null, AgentProfile.Notes))
                Assert.AreEqual(AllTools(agent).Count, agent.ToolsForRound().Count);
        }

        [DataTestMethod]
        [DataRow("在 CAD 中打开图纸验证一下", "cad", "run_cad_actions")]
        [DataRow("把 VS 按三块屏幕布局", "layout", "arrange_workspace_layout")]
        [DataRow("看看 MainForm.cs 里的代码", "inspect", "read_file")]
        [DataRow("把 worktree 并入主分支", "worktree", "list_worktrees")]
        [DataRow("记到笔记里", "notes", "create_note")]
        [DataRow("重启 VSManager 自测一下", "selftest", "restart_vsmanager_for_testing")]
        [DataRow("请调用 list_mcp_servers", "mcp", "list_mcp_servers")]
        public void RoundText_TriggersRelatedGroup_ThenExpires(string text, string group, string tool)
        {
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
            {
                agent.BeginToolRound(text);
                CollectionAssert.Contains(agent.ActiveToolGroups().ToList(), group);
                CollectionAssert.Contains(Names(agent.ToolsForRound()), tool);
                for (int i = 0; i < AgentToolGroups.StickyRounds - 1; i++) agent.BeginToolRound("好的");
                CollectionAssert.Contains(Names(agent.ToolsForRound()), tool, "粘滞期内保留 / kept while sticky");
                agent.BeginToolRound("好的");
                CollectionAssert.DoesNotContain(Names(agent.ToolsForRound()), tool, "过期后移除 / removed after expiry");
            }
        }

        [TestMethod]
        public async Task LoadTools_ReturnsSchemas_AndActivatesGroup()
        {
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
            {
                var loader = AllTools(agent).Single(t => t.Name == AgentToolGroups.LoaderName);
                StringAssert.Contains(loader.Description, "cad");
                StringAssert.Contains(loader.Description, "run_verify_check");
                string r = (await loader.InvokeAsync(new AIFunctionArguments { ["groups"] = "cad, inspect" }))?.ToString();
                StringAssert.Contains(r, "run_cad_actions");
                StringAssert.Contains(r, "read_file");
                StringAssert.Contains(r, "parameters: {");
                var names = Names(agent.ToolsForRound());
                CollectionAssert.Contains(names, "run_cad_actions");
                CollectionAssert.Contains(names, "search_file_contents");
                string bad = (await loader.InvokeAsync(new AIFunctionArguments { ["groups"] = "nope" }))?.ToString();
                StringAssert.Contains(bad, "Unknown group");
                StringAssert.Contains(bad, "layout");
            }
        }

        [TestMethod]
        public async Task UnadvertisedTool_StillInvocableByName_AndStaysOfferedNextRound()
        {
            var settings = new AppSettings { AgentEndpoint = "http://localhost:11434/v1", AgentModel = "test", AgentConfirm = false };
            var client = new CallingClient("list_worktrees");
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => settings, client))
            {
                await agent.RunAsync("你好");
                CollectionAssert.DoesNotContain(client.Advertised[0], "list_worktrees");
                var result = client.Results.Single();
                Assert.IsNull(result.Exception, result.Exception?.Message);
                StringAssert.DoesNotMatch(result.Result?.ToString() ?? "", new System.Text.RegularExpressions.Regex("not found", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                CollectionAssert.Contains(Names(agent.ToolsForRound()), "list_worktrees");
            }
        }

        [TestMethod]
        public void Prompt_ExplainsOnDemandTools()
        {
            StringAssert.Contains(Prompts.AgentSystem(false, DateTime.Today, "", ""), "load_tools");
            StringAssert.Contains(Prompts.AgentSystem(true, DateTime.Today, "", ""), "call load_tools first");
            Assert.IsTrue(new AppSettings().AgentToolGrouping);
        }

        /// <summary>第一次请求按名调用指定工具，之后回复文字；记录每次公布的工具与工具结果。/ Calls the given tool by name first, then replies; records advertised tools and results.</summary>
        private sealed class CallingClient : IAiClientFactory, IChatClient
        {
            private readonly string _tool;
            public readonly List<List<string>> Advertised = new List<List<string>>();
            public readonly List<FunctionResultContent> Results = new List<FunctionResultContent>();
            public CallingClient(string tool) { _tool = tool; }
            public IChatClient Create(Uri endpoint, string model, string apiKey) => this;
            public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options = null, System.Threading.CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                Advertised.Add((options?.Tools ?? new List<AITool>()).Select(t => t.Name).ToList());
                if (Advertised.Count == 1)
                    yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", _tool, new Dictionary<string, object>()) });
                else
                {
                    Results.AddRange(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>());
                    yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done");
                }
            }
            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }
    }
}
