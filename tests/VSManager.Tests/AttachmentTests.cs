using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AttachmentTests
    {
        private TempDataFolder _data;

        [TestInitialize] public void Init() => _data = new TempDataFolder();
        [TestCleanup] public void Cleanup() => _data.Dispose();

        [DataTestMethod]
        [DataRow("a.PNG", AttachmentKind.Image)]
        [DataRow("b.webp", AttachmentKind.Image)]
        [DataRow("c.cs", AttachmentKind.Text)]
        [DataRow("d.xaml", AttachmentKind.Text)]
        [DataRow("e.log", AttachmentKind.Text)]
        [DataRow("f.pdf", AttachmentKind.File)]
        [DataRow("g.exe", null)]
        [DataRow("noext", null)]
        public void Classify_ByExtension(string name, string kind) => Assert.AreEqual(kind, AttachmentPolicy.Classify(name));

        [TestMethod]
        public void CheckAdd_EnforcesCountSizeAndType()
        {
            Assert.IsNull(AttachmentPolicy.CheckAdd(0, "a.png", 10 * 1024 * 1024, 5, 10));
            StringAssert.Contains(AttachmentPolicy.CheckAdd(0, "a.png", 10 * 1024 * 1024 + 1, 5, 10), "10 MB");
            StringAssert.Contains(AttachmentPolicy.CheckAdd(5, "a.png", 1, 5, 10), "最多 5 个");
            StringAssert.Contains(AttachmentPolicy.CheckAdd(0, "a.exe", 1, 5, 10), "不支持");
        }

        [TestMethod]
        public void Defaults_AreDocumentedValues()
        {
            var s = new AppSettings();
            Assert.AreEqual(10, s.AttachmentMaxFileMB);
            Assert.AreEqual(5, s.AttachmentMaxCount);
            Assert.AreEqual(30, s.AttachmentKeepDays);
            Assert.AreEqual(20000, s.AttachmentInlineMaxChars);
            Assert.AreEqual(0, AttachmentPolicy.ClampKeepDays(0));
            Assert.AreEqual(100, AttachmentPolicy.ClampMaxFileMB(500));
        }

        [TestMethod]
        public void NewId_IsValid_AndMapsToDateFolder()
        {
            string id = AttachmentPolicy.NewId(new DateTime(2025, 3, 4, 5, 6, 7, 89), new Random(1));
            Assert.IsTrue(AttachmentPolicy.IsValidId(id), id);
            StringAssert.StartsWith(id, "20250304-050607089-");
            Assert.AreEqual("2025-03-04", AttachmentPolicy.DateFolderOf(id));
            Assert.IsFalse(AttachmentPolicy.IsValidId("../../x"));
            Assert.IsNull(AttachmentPolicy.DateFolderOf("..\\evil"));
        }

        [TestMethod]
        public void FileBlock_TruncatesAndGrowsFence()
        {
            var a = new AttachmentRef { Name = "x.cs", Ext = ".cs", Size = 5000 };
            string block = AttachmentPolicy.FileBlock(a, "```inner```" + new string('a', 3000), 1000);
            StringAssert.Contains(block, "````csharp");
            StringAssert.Contains(block, "已截断");
            Assert.IsTrue(block.Length < 1400);
        }

        [TestMethod]
        public void SendPlan_SplitsImagesTextAndReferences()
        {
            var list = Enumerable.Range(0, 5).Select(i => new AttachmentRef { Id = "i" + i, Name = i + ".png", Ext = ".png", Kind = AttachmentKind.Image }).ToList();
            list.Add(new AttachmentRef { Name = "w.webp", Ext = ".webp", Kind = AttachmentKind.Image });
            list.Add(new AttachmentRef { Name = "a.log", Ext = ".log", Kind = AttachmentKind.Text });
            list.Add(new AttachmentRef { Name = "bin.log", Ext = ".log", Kind = AttachmentKind.Text });
            list.Add(new AttachmentRef { Name = "d.pdf", Ext = ".pdf", Kind = AttachmentKind.File });
            var plan = AttachmentSendPlan.Build(list, a => a.Name == "a.log" ? "hello" : null, a => "%APPDATA%\\VSManager\\attachments\\" + a.Name, 4, 1000);
            Assert.AreEqual(4, plan.Images.Count);
            Assert.AreEqual(1, plan.Inlined.Count);
            Assert.AreEqual(4, plan.References.Count);
            StringAssert.Contains(plan.Body, "hello");
            StringAssert.Contains(plan.Body, "d.pdf");
            StringAssert.Contains(plan.Body, "w.webp");
        }

        [TestMethod]
        public void Compose_KeepsReceiptInstructionAtTheEnd()
        {
            var t = new QueuedTask { Id = 7, Text = "Do it", CompletionToken = "tok" };
            t.Text = AttachmentSendPlan.Compose(t.Text, "📎 file block");
            string text = TaskStateMachine.DispatchText(t);
            Assert.IsTrue(text.IndexOf("📎 file block", StringComparison.Ordinal) < text.IndexOf("任务队列回执", StringComparison.Ordinal));
            Assert.AreEqual("Do it", AttachmentSendPlan.Compose("Do it"));
        }

        [TestMethod]
        public void Store_ImportHashesResolvesAndReadsText()
        {
            string src = _data.File("note.txt");
            File.WriteAllText(src, "line1\nline2", new UTF8Encoding(false));
            var a = AttachmentStore.Import(src, 0, 5, 10);
            Assert.AreEqual("note.txt", a.Name);
            Assert.AreEqual(AttachmentKind.Text, a.Kind);
            Assert.AreEqual(64, a.Sha256.Length);
            Assert.IsTrue(a.RelPath.StartsWith(DateTime.Now.ToString("yyyy-MM-dd") + "/", StringComparison.Ordinal));
            string path = AttachmentStore.FullPath(a);
            Assert.IsTrue(path.StartsWith(Path.Combine(_data.Path, "attachments"), StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(path, AttachmentStore.FindById(a.Id));
            Assert.AreEqual("line1\nline2", AttachmentStore.ReadText(a, 100));
            Assert.AreEqual("line", AttachmentStore.ReadText(a, 3).Substring(0, 4));

            var bin = AttachmentStore.SaveBytes(new byte[] { 1, 0, 2 }, "x.log", 0, 5, 10);
            Assert.IsNull(AttachmentStore.ReadText(bin, 100));
            Assert.AreNotEqual(a.Id, bin.Id);
        }

        [TestMethod]
        public void Store_RejectsOversizeAndUnsupported()
        {
            string big = _data.File("big.png");
            File.WriteAllBytes(big, new byte[2 * 1024 * 1024 + 1]);
            Assert.ThrowsException<InvalidDataException>(() => AttachmentStore.Import(big, 0, 5, 2));
            Assert.ThrowsException<InvalidDataException>(() => AttachmentStore.SaveBytes(new byte[1], "a.exe", 0, 5, 10));
            Assert.IsFalse(Directory.Exists(AttachmentStore.Root) && Directory.EnumerateFiles(AttachmentStore.Root, "*", SearchOption.AllDirectories).Any());
        }

        [TestMethod]
        public void Store_CleanupRemovesExpiredButKeepsReferenced()
        {
            string old = Path.Combine(AttachmentStore.Root, "2020-01-01");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "20200101-000000000-aaaaaa.txt"), "x");
            File.WriteAllText(Path.Combine(old, "20200101-000000000-bbbbbb.txt"), "y");
            var fresh = AttachmentStore.SaveBytes(new byte[] { 65 }, "n.txt", 0, 5, 10);

            Assert.AreEqual(0, AttachmentStore.Cleanup(0, null, DateTime.Now).Deleted);
            var r = AttachmentStore.Cleanup(30, new[] { "20200101-000000000-bbbbbb" }, DateTime.Now);
            Assert.AreEqual(1, r.Deleted);
            Assert.AreEqual(1, r.Kept);
            Assert.IsTrue(File.Exists(Path.Combine(old, "20200101-000000000-bbbbbb.txt")));
            Assert.IsNotNull(AttachmentStore.FindById(fresh.Id));
            Assert.IsTrue(File.Exists(AppLog.PathOf(AttachmentStore.LogFile)));
        }

        [TestMethod]
        public void QueuedTask_CloneCopiesAttachments_AndQueueComparesThem()
        {
            var a = new AttachmentRef { Id = "20250101-000000000-aaaaaa", Name = "a.png", Sha256 = "h1" };
            var t = new QueuedTask { Id = 1, Attachments = new[] { a }, AttachmentNote = "note" };
            var c = t.Clone();
            Assert.AreEqual("a.png", c.Attachments[0].Name);
            Assert.AreNotSame(t.Attachments[0], c.Attachments[0]);
            Assert.AreEqual("note", c.AttachmentNote);
            Assert.IsTrue(TaskQueue.SameAttachments(null, new AttachmentRef[0]));
            Assert.IsFalse(TaskQueue.SameAttachments(null, t.Attachments));

            var q = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), () => DateTime.Now);
            var plain = q.Add("k", "VS", "same", "AI");
            var withFile = q.Add("k", "VS", "same", "AI", t.Attachments);
            Assert.AreNotSame(plain, withFile);
            Assert.AreSame(withFile, q.Add("k", "VS", "same", "AI", c.Attachments));
            Assert.AreSame(plain, q.Add("k", "VS", "same", "AI"));
        }

        [TestMethod]
        public void Transcript_UserAttachmentLinesBecomeLinks_OtherTextStaysEncoded()
        {
            var a = new AttachmentRef { Id = "20250101-000000000-abcdef", Name = "a[1]<b>.png", Kind = AttachmentKind.Image, Size = 2048, Sha256 = "0123456789abcdef" };
            string html = TranscriptView.PlainWithAttachments("<script>x</script>\n\n" + a.MarkdownLink());
            StringAssert.Contains(html, "href=\"vsm-attachment:20250101-000000000-abcdef\"");
            StringAssert.Contains(html, "a[1]&lt;b&gt;.png");
            StringAssert.Contains(html, "&lt;script&gt;");
            Assert.IsFalse(html.Contains("<script>"));
            string fake = TranscriptView.PlainWithAttachments("[📎 x](javascript:alert(1))");
            Assert.IsFalse(fake.Contains("<a "));
        }

        [TestMethod]
        public void ModelMessage_ListsManifestAndInlinesText_NotImages()
        {
            var img = new AttachmentRef { Id = "20250101-000000000-aaaaaa", Name = "s.png", Kind = AttachmentKind.Image, Ext = ".png", Size = 1 };
            var txt = new AttachmentRef { Id = "20250101-000000000-bbbbbb", Name = "b.log", Kind = AttachmentKind.Text, Ext = ".log", Size = 1 };
            string m = AgentService.ModelMessage("look", new[] { img, txt }, (a, n) => "LOG CONTENT");
            StringAssert.Contains(m, "id=20250101-000000000-aaaaaa");
            StringAssert.Contains(m, "LOG CONTENT");
            StringAssert.Contains(m, "send_task");
            Assert.AreEqual(1, m.Split(new[] { "LOG CONTENT" }, StringSplitOptions.None).Length - 1);
        }

        [DataTestMethod]
        [DataRow("无法激活该 VS，未发送图片", true)]
        [DataRow("VS 输入框出现新草稿或无法读取，已取消图片发送", false)]
        [DataRow("VS 输入框已有草稿，请先发送或清空后再发送图片（平台草稿已保留）", false)]
        [DataRow("未能确认图片附件已加入，未发送。请确认该 VS / 模型支持图片，并检查 VS 草稿后重试", false)]
        [DataRow("已点击发送，但尚未确认成功。请在 VS 中查看，确认前不要重复发送", false)]
        [DataRow("图片附件无效", true)]
        public void PreSubmitImageFailure_OnlyWhenDraftUntouched(string result, bool expected) =>
            Assert.AreEqual(expected, MainForm.IsPreSubmitImageFailure(result));

        [TestMethod]
        public void Publisher_TreatsAttachmentFolderAsPrivate()
        {
            Assert.IsTrue(PublishScanner.IsPrivateFile("attachments/2025-01-01/x.png"));
            Assert.IsTrue(PublishScanner.IsPrivateFile("src\\attachments\\y.log"));
            Assert.IsFalse(PublishScanner.IsPrivateFile("src/VSManager/Core/Models/AttachmentRef.cs"));
            CollectionAssert.Contains(GitHubPublisher.RequiredIgnores, "attachments/");
        }
    }
}
