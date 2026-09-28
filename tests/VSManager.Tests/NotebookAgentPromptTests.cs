using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class NotebookAgentPromptTests
    {
        private string _root;

        [TestInitialize]
        public void Init() => _root = Path.Combine(Path.GetTempPath(), "vsm-agent-prompt-" + System.Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        [TestMethod]
        public void Ensure_CreatesRootPageOnce_WithTemplateThatIsNotSent()
        {
            var store = new NotebookStore(_root);
            string id = NotebookAgentPrompt.Ensure(store);
            Assert.AreEqual(id, NotebookAgentPrompt.Ensure(store));
            Assert.AreEqual(id, store.FindChild("", NotebookAgentPrompt.PageTitle));
            Assert.IsNull(NotebookAgentPrompt.Load(store), "只有模板时不发送 / Template only sends nothing");

            var doc = store.Read(id);
            store.Save(doc, doc.Text + "- 回复先给结论\n- 发布任务前列出目标 VS\n");
            Assert.AreEqual("- 回复先给结论\n- 发布任务前列出目标 VS", NotebookAgentPrompt.Load(store));
        }

        [TestMethod]
        public void Extract_KeepsUserHeadings_AndTruncatesLongText()
        {
            Assert.AreEqual("## 规则\n不要猜测", NotebookAgentPrompt.Extract("# " + NotebookAgentPrompt.PageTitle + "\r\n\r\n## 规则\r\n不要猜测\r\n"));
            string longText = NotebookAgentPrompt.Extract(new string('x', NotebookAgentPrompt.MaxLength + 50));
            StringAssert.StartsWith(longText, new string('x', NotebookAgentPrompt.MaxLength));
            StringAssert.Contains(longText, "truncated");
        }
    }
}