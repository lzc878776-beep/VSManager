using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class NotebookTests
    {
        private string _root;
        private NotebookStore _store;

        [TestInitialize]
        public void Initialize()
        {
            _root = Path.Combine(Path.GetTempPath(), "vsm-notebooks-" + Guid.NewGuid().ToString("N"));
            _store = new NotebookStore(_root);
        }

        [TestCleanup]
        public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        [TestMethod]
        public void Pages_HaveContentAndSubpages_SearchesTitlesAndContent()
        {
            string project = _store.CreatePage("", "Project", "# Project\nOverview text");
            string decisions = _store.CreatePage(project, "Decisions");
            string note = _store.CreatePage(decisions, "Storage", "# Storage\r\nKeep ideas in SQLite.");
            _store.CreatePage(project, "Tasks", "- [ ] Ship notebooks");
            _store.CreatePage("", "Empty");
            Assert.IsTrue(File.Exists(_store.DatabasePath));
            var tree = _store.LoadTree();
            Assert.AreEqual(2, tree.Count);
            var branch = tree.Single(x => x.Name == "Project");
            Assert.IsTrue(branch.HasChildren);
            Assert.AreEqual("# Project\nOverview text", _store.Read(project).Text, "带子页面的页面也有正文 / Pages with subpages have content");
            Assert.AreEqual("Storage", branch.Children[0].Children[0].Name);
            Assert.AreEqual(note, _store.LoadTree("sqlite").Single().Children.Single().Children.Single().Path);
            Assert.AreEqual(project, _store.LoadTree("overview").Single().Path);
            Assert.AreEqual(2, _store.LoadTree("PROJECT").Single().Children.Count);
            Assert.AreEqual(0, _store.LoadTree("does not exist").Count);
            CollectionAssert.AreEqual(new[] { "Project", "Decisions", "Storage" }, _store.TitlePath(note).ToArray());
            Assert.AreEqual(decisions, _store.ParentOf(note));
            Assert.AreEqual(project, _store.FindChild("", "project"));
        }

        [TestMethod]
        public void Save_PersistsAcrossInstances_AndNormalizesNewLines()
        {
            string id = _store.CreatePage("", "Note", "# Title\nOriginal\n");
            var document = _store.Read(id);
            _store.Save(document, "# Title\r\nUpdated\r\n");
            Assert.AreEqual("# Title\nUpdated\n", new NotebookStore(_root).Read(id).Text);
            _store.Save(document, "Again\n");
            Assert.AreEqual("Again\n", _store.Read(id).Text);
            Assert.AreEqual(1, _store.LoadTree().Count);
        }

        [TestMethod]
        public void Save_RejectsConcurrentChangesAndDeletion_WithoutDestroyingEitherVersion()
        {
            string id = _store.CreatePage("", "Plan", "original");
            var document = _store.Read(id);
            var other = new NotebookStore(_root);
            other.Save(other.Read(id), "external");
            Assert.ThrowsException<NotebookConflictException>(() => _store.Save(document, "draft"));
            Assert.AreEqual("external", _store.Read(id).Text);
            Assert.AreEqual("original", document.Text);
            var fresh = _store.Read(id);
            other.Trash(id);
            Assert.ThrowsException<NotebookConflictException>(() => _store.Save(fresh, "draft"));
            Assert.ThrowsException<IOException>(() => _store.Read(id));
        }

        [TestMethod]
        public void RenameAndTrash_KeepIdsAndRejectDuplicateSiblings()
        {
            string folder = _store.CreatePage("", "Project");
            string ideas = _store.CreatePage(folder, "Ideas", "idea");
            Assert.AreEqual(folder, _store.Rename(folder, "Work"));
            Assert.AreEqual("idea", _store.Read(ideas).Text);
            CollectionAssert.AreEqual(new[] { "Work", "Ideas" }, _store.TitlePath(ideas).ToArray());
            _store.CreatePage(folder, "Tasks", "tasks");
            Assert.ThrowsException<IOException>(() => _store.Rename(ideas, "tasks"));
            Assert.ThrowsException<IOException>(() => _store.CreatePage(folder, "Tasks"));
            _store.CreatePage("", "Tasks");
            _store.Trash(folder);
            Assert.ThrowsException<IOException>(() => _store.Read(ideas), "子页面一起移入废纸篓 / Subpages are trashed too");
            Assert.AreEqual("Tasks", new NotebookStore(_root).LoadTree().Single().Name);
            _store.CreatePage("", "Work");
        }

        [TestMethod]
        public void Ids_AndTitles_AreValidated()
        {
            foreach (var title in new[] { "", " a", "a ", "a\nb", new string('x', 121) })
                Assert.ThrowsException<ArgumentException>(() => _store.CreatePage("", title), title);
            foreach (var id in new[] { "..\\escape.md", "note.md", _root, "", "ABC" })
                Assert.ThrowsException<ArgumentException>(() => _store.Read(id), id);
            Assert.AreNotEqual(null, _store.CreatePage("", "a/b · CON"));
        }

        [TestMethod]
        public void Images_AreStoredInDatabase_AndRemoteImagesAreNeverResolved()
        {
            string page = _store.CreatePage("", "Images");
            string source = Path.Combine(_root, "source.png");
            byte[] bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
            File.WriteAllBytes(source, bytes);
            string relative = _store.ImportImage(page, source);
            File.Delete(source);
            StringAssert.StartsWith(relative, "attachments/");
            Assert.AreEqual("data:image/png;base64," + Convert.ToBase64String(bytes), _store.ImageData(page, relative));
            Assert.IsNull(_store.ImageData(page, "https://example.com/tracking.png"));
            Assert.IsNull(_store.ImageData(page, "attachments/missing.png"));
            Assert.ThrowsException<ArgumentException>(() => _store.ImageData(page, "../outside.png"));
            Assert.AreEqual(1, _store.LoadTree().Count);
        }

        [TestMethod]
        public void Limits_RejectOversizedNotesBeforeReplacingOriginal()
        {
            string id = _store.CreatePage("", "Small", "keep");
            var document = _store.Read(id);
            Assert.ThrowsException<IOException>(() => _store.Save(document, new string('x', NotebookStore.MaxNoteBytes + 1)));
            Assert.AreEqual("keep", _store.Read(id).Text);
        }

        [TestMethod]
        public void LegacyMarkdownFolder_IsImportedOnce_MergingSameNamedFolderAndNote()
        {
            string root = Path.Combine(Path.GetTempPath(), "vsm-legacy-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Project", "attachments"));
                Directory.CreateDirectory(Path.Combine(root, ".trash"));
                File.WriteAllText(Path.Combine(root, "Project.md"), "# Project\nmerged body", new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(root, "Project", "Ideas.md"), "![a](attachments/pic.png)", Encoding.Unicode);
                File.WriteAllBytes(Path.Combine(root, "Project", "attachments", "pic.png"), new byte[] { 1, 2, 3 });
                File.WriteAllText(Path.Combine(root, ".trash", "Old.md"), "gone");
                File.WriteAllText(Path.Combine(root, "readme.txt"), "not a note");
                var store = new NotebookStore(root);
                var tree = store.LoadTree();
                var project = tree.Single();
                Assert.AreEqual("Project", project.Name);
                Assert.AreEqual("# Project\nmerged body", store.Read(project.Path).Text);
                var ideas = project.Children.Single();
                Assert.AreEqual("![a](attachments/pic.png)", store.Read(ideas.Path).Text);
                Assert.AreEqual("data:image/png;base64,AQID", store.ImageData(ideas.Path, "attachments/pic.png"));
                Assert.IsTrue(File.Exists(Path.Combine(root, "Project.md")), "原文件保留作备份 / Legacy files are kept");
                File.WriteAllText(Path.Combine(root, "Later.md"), "later");
                Assert.AreEqual(1, new NotebookStore(root).LoadTree().Count, "只导入一次 / Imported only once");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [TestMethod]
        public void Links_ResolveByIdAndByRelativeTitlePath()
        {
            string project = _store.CreatePage("", "Project");
            string ideas = _store.CreatePage(project, "Ideas");
            string tasks = _store.CreatePage(project, "Tasks");
            Assert.AreEqual(tasks, _store.ResolveLink(ideas, "Tasks.md"));
            Assert.AreEqual(ideas, _store.ResolveLink(project, "Project/Ideas.md"));
            Assert.AreEqual(project, _store.ResolveLink(ideas, "../Project.md"));
            Assert.AreEqual(ideas, _store.ResolveLink(tasks, "page:" + ideas));
            Assert.IsNull(_store.ResolveLink(tasks, "page:" + new string('0', 32)));
            Assert.IsNull(_store.ResolveLink(tasks, "Missing.md"));
            Assert.ThrowsException<ArgumentException>(() => _store.ResolveLink(project, "../../outside.md"));
            StringAssert.Contains(NotebookMarkdown.Render("[x](page:" + ideas + ")"), "data-note=\"page:" + ideas + "\"");
        }

        [TestMethod]
        public void Export_WritesMarkdownTreeWithImages()
        {
            string project = _store.CreatePage("", "Project", "body");
            string source = Path.Combine(_root, "p.png");
            File.WriteAllBytes(source, new byte[] { 9, 9 });
            string child = _store.CreatePage(project, "Ideas:1");
            string image = _store.ImportImage(child, source);
            _store.Save(_store.Read(child), "![i](" + image + ")");
            string folder = _store.ExportMarkdown();
            Assert.AreEqual("body", File.ReadAllText(Path.Combine(folder, "Project.md")));
            Assert.IsTrue(File.Exists(Path.Combine(folder, "Project", "Ideas_1.md")));
            CollectionAssert.AreEqual(new byte[] { 9, 9 }, File.ReadAllBytes(Path.Combine(folder, "Project", "attachments", image.Substring("attachments/".Length))));
        }

        [TestMethod]
        public void Markdown_RendersTasksTablesAndCode_WithoutActiveHtmlOrUnsafeLinks()
        {
            string html = NotebookMarkdown.Render("# Heading\n\n- [ ] Open\n- [x] Done\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```cs\nvar x = 1;\n```\n\n<script>alert(1)</script>\n\n[bad](javascript:alert)\n\n![tracking](https://example.com/x.png)\n\n[safe](https://example.com)");
            StringAssert.Contains(html, "<h1>Heading</h1>");
            StringAssert.Contains(html, "type=\"checkbox\"");
            StringAssert.Contains(html, "<table>");
            StringAssert.Contains(html, "language-cs");
            Assert.IsFalse(html.Contains("<script>"));
            Assert.IsFalse(html.Contains("href=\"javascript:"));
            Assert.IsFalse(html.Contains("<img"));
            StringAssert.Contains(html, "href=\"https://example.com\"");
        }

        [TestMethod]
        public void Markdown_RenderEditable_TagsEveryBlockWithItsExactSourceSpan()
        {
            string md = "# Title\r\n\r\nSetext\r\n===\r\n\r\nPara *one*\r\nsoft line\r\n\r\n- a\r\n  - nested\r\n- [x] done\r\n\r\n1. x\r\n\r\n2. y\r\n\r\n> quote\r\n> more\r\n\r\n| A | B |\r\n|:-:|--:|\r\n| 1 | 2 |\r\n\r\n```cs\r\nvar x = 1;\r\n```\r\n\r\n```card\r\nstatus: done\r\ntitle: T\r\n```\r\n\r\n---\r\n\r\n[ref]: https://example.com\r\n\r\n![pic](attachments/a.png) [bad](javascript:x)\r\n";
            string html = NotebookMarkdown.RenderEditable(md, _ => null, out var spans);
            Assert.AreEqual(spans.Length, System.Text.RegularExpressions.Regex.Matches(html, " data-b=\"").Count, "每个块一个标记 / One marker per block");
            int previous = 0;
            for (int i = 0; i < spans.Length; i++)
            {
                StringAssert.Contains(html, " data-b=\"" + i + "\"");
                Assert.IsTrue(spans[i][0] >= previous && spans[i][1] > spans[i][0], "块位置递增且非空 / Spans increase and are non-empty: " + i + " " + string.Join(";", spans.Select(s => s[0] + "-" + s[1])));
                Assert.AreEqual("", md.Substring(previous, spans[i][0] - previous).Trim(), "块之间只有空白 / Only whitespace between blocks");
                previous = spans[i][1];
            }
            Assert.AreEqual("", md.Substring(previous).Trim());
            // 每个块的原文单独渲染后与整篇渲染中的该块一致 / Each block's source renders exactly like that block within the whole page
            var parts = System.Text.RegularExpressions.Regex.Split(html, "(?=<[a-z0-9]+ data-b=\")").Where(p => p.Length > 0).ToArray();
            Assert.AreEqual(spans.Length, parts.Length);
            for (int i = 0; i < spans.Length; i++)
            {
                if (parts[i].Contains("data-raw")) continue;
                string slice = md.Substring(spans[i][0], spans[i][1] - spans[i][0]);
                Assert.AreEqual(NotebookMarkdown.Render(slice, _ => null).Trim(), parts[i].Replace(" data-b=\"" + i + "\"", "").Trim(), slice);
            }
            StringAssert.Contains(html, "data-alt=\"pic\" data-md=\"attachments/a.png\"");
            StringAssert.Contains(html, "<span class=\"md-link\" data-md=\"javascript:x\">bad</span>");
            Assert.IsFalse(html.Contains("href=\"javascript:"));
        }
    }
}