using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>@小维 提及：识别、令牌、去除与提示词。/ @小维 mention: detection, tokens, stripping and prompt.</summary>
    [TestClass]
    public class AssistantMentionTests
    {
        [DataTestMethod]
        [DataRow("@小维 各 VS 在做什么", true)]
        [DataRow("请 @小维 看看", true)]
        [DataRow("问一下@小维帮我整理", true)]
        [DataRow("`@小维` 是代码", false)]
        [DataRow("@@小维 不识别", false)]
        [DataRow("mail@小维", false)]
        [DataRow("小维你好", false)]
        [DataRow("", false)]
        public void AddressesAssistant_FollowsMentionSyntax(string text, bool expected) =>
            Assert.AreEqual(expected, VsMentionSession.AddressesAssistant(text));

        [TestMethod]
        public void SelectedToken_IsChipButNotVsIntent()
        {
            var session = new VsMentionSession();
            var assistant = VsMentionTarget.Assistant();
            string token = session.Select(assistant);
            StringAssert.Matches(token, new System.Text.RegularExpressions.Regex(@"^@\[小维\|[0-9a-f]{6}\]$"));
            Assert.IsTrue(session.TryGetTarget(token, out var back) && back.IsAssistant);
            Assert.AreEqual("@小维", session.Chips(token + " 你好").Single().Label);
            Assert.IsFalse(VsMentionSession.HasIntent(token + " 你好"));
            Assert.IsTrue(VsMentionSession.AddressesAssistant(token + " 你好"));
            Assert.AreEqual("你好", VsMentionSession.StripAssistant(token + " 你好"));
            Assert.IsFalse(assistant.Matches(new VsInstance { Pid = 1 }));
            Assert.AreSame(assistant, VsMentionSession.Filter(new[] { assistant }, "小维").Single());
            Assert.AreEqual(1, VsMentionSession.Filter(new[] { assistant }, "agent").Length);
        }

        [TestMethod]
        public void VsTokenAlongsideAssistant_StillCountsAsVsIntent()
        {
            var session = new VsMentionSession();
            var vs = new VsInstance { Pid = 7, StartTicks = 1, SolutionPath = @"C:\Src\A.sln" };
            string text = "@小维 让 " + session.Select(new VsMentionTarget(vs, 1, "A")) + " 修复";
            Assert.IsTrue(VsMentionSession.HasIntent(text));
            Assert.AreEqual(1, VsMentionSession.TokenStarts(text).Count());
            string stripped = VsMentionSession.StripAssistant(text);
            StringAssert.StartsWith(stripped, "让 @[#1 A|");
            Assert.IsTrue(session.Resolve(stripped + "", new[] { vs }).Valid);
        }

        [TestMethod]
        public void AssistantPrompt_IsBilingualAndMarksVsAsContext()
        {
            string plain = AgentPanel.AssistantPrompt("各 VS 在做什么", new string[0]);
            StringAssert.StartsWith(plain, "各 VS 在做什么");
            StringAssert.Contains(plain, "@小维");
            StringAssert.Contains(plain, "addressed this message to you");
            Assert.IsFalse(plain.Contains("VS mentioned"));
            string withVs = AgentPanel.AssistantPrompt("让 @#3 A 修复", new[] { "#3 A" });
            StringAssert.Contains(withVs, "#3 A");
            StringAssert.Contains(withVs, "context only");
            StringAssert.Contains(AgentPanel.AssistantPrompt("", new string[0]), "only sent attachments");
        }

        [TestMethod]
        public void SystemPrompt_NamesTheAssistantInBothLanguages()
        {
            foreach (bool english in new[] { false, true })
                StringAssert.Contains(Prompts.AgentSystem(english, System.DateTime.Today, "", ""), "@小维");
        }
    }
}
