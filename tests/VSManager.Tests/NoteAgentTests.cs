using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>笔记 AI 助手与停靠容器的测试。/ Tests for the note AI assistant and its dock host.</summary>
    [TestClass]
    public class NoteAgentTests
    {
        private static AgentService NewNoteAgent(Func<NoteSnapshot> source)
        {
            var settings = new AppSettings { AgentEndpoint = "http://localhost:11434/v1", AgentModel = "test", AgentConfirm = false };
            return new AgentService(new AgentDesktopTests.DesktopHost(), () => settings, null, AgentProfile.Notes) { CurrentNoteSource = source };
        }

        private static List<AIFunction> Tools(AgentService agent)
        {
            var field = typeof(AgentService).GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return ((IEnumerable<AITool>)field.GetValue(agent)).OfType<AIFunction>().ToList();
        }

        [TestMethod]
        public void NoteProfile_RegistersOnlyNoteTools()
        {
            using (var agent = NewNoteAgent(() => null))
            {
                var names = Tools(agent).Select(t => t.Name).ToList();
                CollectionAssert.Contains(names, "read_current_note");
                CollectionAssert.Contains(names, "list_notes");
                CollectionAssert.Contains(names, "read_note");
                CollectionAssert.DoesNotContain(names, "send_task");
                CollectionAssert.DoesNotContain(names, "close_cs_tabs");
                Assert.AreEqual(AgentProfile.Notes, agent.Profile);
                Assert.AreEqual(AgentService.NoteAgentTitle, agent.Transcript.Title);
            }
        }

        [TestMethod]
        public async Task ReadCurrentNote_ReturnsSnapshotOrNoNoteMessage()
        {
            NoteSnapshot current = null;
            using (var agent = NewNoteAgent(() => current))
            {
                var tool = Tools(agent).Single(t => t.Name == "read_current_note");
                StringAssert.Contains((await tool.InvokeAsync(new AIFunctionArguments()))?.ToString(), "No note is open");

                current = new NoteSnapshot { Id = "n1", TitlePath = "Work / Plan", Text = "Ship the release" };
                string text = (await tool.InvokeAsync(new AIFunctionArguments()))?.ToString();
                StringAssert.Contains(text, "Work / Plan");
                StringAssert.Contains(text, "Ship the release");
            }
        }

        [TestMethod]
        public void NoteAgentSystemPrompt_MentionsCurrentNoteToolInBothLanguages()
        {
            StringAssert.Contains(Prompts.NoteAgentSystem(false, DateTime.Now, "Plan"), "read_current_note");
            StringAssert.Contains(Prompts.NoteAgentSystem(true, DateTime.Now, "Plan"), "read_current_note");
        }

        [DataTestMethod]
        [DataRow("left", "Left")]
        [DataRow(" Bottom ", "Bottom")]
        [DataRow("float", "Float")]
        [DataRow("right", "Right")]
        [DataRow("", "Right")]
        [DataRow(null, "Right")]
        [DataRow("nonsense", "Right")]
        public void ParsePosition_DefaultsToRight(string text, string expectedName)
        {
            var expected = (NoteDockPosition)Enum.Parse(typeof(NoteDockPosition), expectedName);
            Assert.AreEqual(expected, NoteAgentDock.ParsePosition(text));
            Assert.AreEqual(expected, NoteAgentDock.ParsePosition(NoteAgentDock.Format(expected)));
        }

        [TestMethod]
        public void Bounds_RoundTripAndRejectInvalid()
        {
            var r = new Rectangle(-100, 20, 640, 480);
            Assert.AreEqual(r, NoteAgentDock.ParseBounds(NoteAgentDock.FormatBounds(r)));
            Assert.AreEqual("", NoteAgentDock.FormatBounds(Rectangle.Empty));
            Assert.AreEqual(Rectangle.Empty, NoteAgentDock.ParseBounds(null));
            Assert.AreEqual(Rectangle.Empty, NoteAgentDock.ParseBounds("1,2,3"));
            Assert.AreEqual(Rectangle.Empty, NoteAgentDock.ParseBounds("1,2,0,5"));
            Assert.AreEqual(Rectangle.Empty, NoteAgentDock.ParseBounds("a,b,c,d"));
        }

        [TestMethod]
        public void HitTest_PicksSideBottomOrFloat()
        {
            var host = new Rectangle(100, 100, 1000, 800);
            Assert.AreEqual(NoteDockPosition.Left, NoteAgentDock.HitTest(host, new Point(150, 500)));
            Assert.AreEqual(NoteDockPosition.Right, NoteAgentDock.HitTest(host, new Point(1050, 300)));
            Assert.AreEqual(NoteDockPosition.Bottom, NoteAgentDock.HitTest(host, new Point(600, 850)));
            Assert.AreEqual(NoteDockPosition.Float, NoteAgentDock.HitTest(host, new Point(600, 400)));
            Assert.AreEqual(NoteDockPosition.Float, NoteAgentDock.HitTest(host, new Point(5000, 400)));
        }

        [TestMethod]
        public void DockArea_TakesThePercentOfTheHost()
        {
            var host = new Rectangle(0, 0, 1000, 800);
            Assert.AreEqual(new Rectangle(0, 0, 500, 800), NoteAgentDock.DockArea(host, NoteDockPosition.Left, 50));
            Assert.AreEqual(new Rectangle(500, 0, 500, 800), NoteAgentDock.DockArea(host, NoteDockPosition.Right, 50));
            Assert.AreEqual(new Rectangle(0, 400, 1000, 400), NoteAgentDock.DockArea(host, NoteDockPosition.Bottom, 50));
            Assert.AreEqual(Rectangle.Empty, NoteAgentDock.DockArea(host, NoteDockPosition.Float, 50));
        }
    }
}
