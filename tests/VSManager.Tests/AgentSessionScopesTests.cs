using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AIMessage = Microsoft.Extensions.AI.ChatMessage;
using AIRole = Microsoft.Extensions.AI.ChatRole;

namespace VSManager.Tests
{
    [TestClass]
    public class AgentSessionScopesTests
    {
        private static readonly Dictionary<AIMessage, string> Tags = new Dictionary<AIMessage, string>();

        private static AIMessage U(string text, string scope)
        {
            var m = new AIMessage(AIRole.User, text);
            if (scope != null) Tags[m] = scope;
            return m;
        }

        private static AIMessage A(string text) => new AIMessage(AIRole.Assistant, text);
        private static string Of(AIMessage m) => Tags.TryGetValue(m, out var s) ? s : null;

        [TestMethod]
        public void Select_KeepsGlobalAndOwnRounds_WithTheirReplies()
        {
            // 一轮 = 用户消息 + 其后的助手 / 工具消息 / A round = a user message plus the assistant / tool messages after it
            var h = new List<AIMessage> { A("开头 / lead"), U("g1", null), A("r-g1"), U("a1", "ProjA"), A("r-a1"), U("b1", "ProjB"), A("r-b1"), U("g2", null), A("r-g2") };
            var a = AgentSessionScopes.Select(h, Of, "proja").Select(m => m.Text).ToList();
            CollectionAssert.AreEqual(new[] { "开头 / lead", "g1", "r-g1", "a1", "r-a1", "g2", "r-g2" }, a);
            var g = AgentSessionScopes.Select(h, Of, null).Select(m => m.Text).ToList();
            CollectionAssert.AreEqual(new[] { "开头 / lead", "g1", "r-g1", "g2", "r-g2" }, g);
        }

        [TestMethod]
        public void Digests_LatestConclusionPerProject_MostRecentFirst_Clipped()
        {
            var h = new List<AIMessage> { U("a1", "ProjA"), A("旧结论 / old"), U("b1", "ProjB"), A(new string('x', 300)), U("a2", "ProjA"), A("新结论\n第二行 / new") };
            var d = AgentSessionScopes.Digests(h, Of);
            Assert.AreEqual(2, d.Count);
            Assert.AreEqual("ProjA", d[0].Key);
            Assert.AreEqual("新结论 第二行 / new", d[0].Value);
            Assert.AreEqual(AgentSessionScopes.DigestLength + 1, d[1].Value.Length);
            Assert.AreEqual(1, AgentSessionScopes.Digests(h, Of, "PROJA").Count);
            Assert.IsNull(AgentSessionScopes.GlobalNote(new List<KeyValuePair<string, string>>()));
            StringAssert.Contains(AgentSessionScopes.GlobalNote(d), "- ProjA：");
            StringAssert.Contains(AgentSessionScopes.ProjectNote("ProjA"), "Session isolation");
        }

        [TestMethod]
        public void Infer_OnlyWhenExactlyOneProjectIsMentioned()
        {
            var names = new[]
            {
                new KeyValuePair<string, string>("Contoso.Rebar", "Contoso.Rebar"),
                new KeyValuePair<string, string>("Contoso.Rebar", "钢筋"),
                new KeyValuePair<string, string>("VSManager", "VSManager"),
                new KeyValuePair<string, string>("X", "x")
            };
            Assert.AreEqual("Contoso.Rebar", AgentSessionScopes.Infer("钢筋项目为什么发不出去", names));
            Assert.AreEqual("Contoso.Rebar", AgentSessionScopes.Infer("看看 contoso.rebar 和钢筋", names));
            Assert.IsNull(AgentSessionScopes.Infer("钢筋和 VSManager 都看看", names));
            Assert.IsNull(AgentSessionScopes.Infer("整体进度 x", names), "过短的名称不参与匹配 / too-short names never match");
            Assert.IsNull(AgentSessionScopes.Infer("", names));
        }
    }
}
