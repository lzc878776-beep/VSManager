using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class NotebookTaskJournalTests
    {
        private string _root;

        [TestInitialize]
        public void Init() { _root = Path.Combine(Path.GetTempPath(), "vsm-journal-" + Guid.NewGuid().ToString("N")); }

        [TestCleanup]
        public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        private static QueuedTask Task(int id, string text, int hour) => new QueuedTask
        {
            Id = id, Text = text, VsName = "Demo", Status = QueueStatus.Done, Attempts = 1,
            Created = new DateTime(2026, 9, 27, hour, 0, 0), Started = new DateTime(2026, 9, 27, hour, 1, 0),
            Finished = new DateTime(2026, 9, 27, hour, 5, 30), Result = "short"
        };

        [TestMethod]
        public void Record_CreatesDailyPage_IndexWithTimesAndLinks_AndDetails()
        {
            var store = new NotebookStore(_root);
            var journal = new NotebookTaskJournal(store);
            string first = journal.Record(Task(12, "@[#1 Demo] 修复 登录 按钮\r\n第二行", 9), "完整回复 ```code```");
            string second = journal.Record(Task(13, "整理文档/说明", 14));
            Assert.AreEqual("2026.9.27 任务记录", NotebookTaskJournal.FolderName(new DateTime(2026, 9, 27)));
            string day = store.FindChild("", "2026.9.27 任务记录");
            string index = store.FindChild(day, "已完成任务");
            Assert.IsNotNull(index);
            CollectionAssert.AreEqual(new[] { "2026.9.27 任务记录", "任务 12 - 修复 登录 按钮" }, store.TitlePath(first).ToArray());
            CollectionAssert.AreEqual(new[] { "2026.9.27 任务记录", "任务 13 - 整理文档/说明" }, store.TitlePath(second).ToArray());
            StringAssert.Contains(store.Read(day).Text, "(page:" + index + ")", "日期页面自身也有正文并链接清单 / The day page has content linking the list");

            string list = store.Read(index).Text;
            StringAssert.Contains(list, "- **09:05** [修复 登录 按钮](page:" + first + ") · Demo");
            StringAssert.Contains(list, "- **14:05** [整理文档/说明](page:" + second + ") · Demo");
            Assert.IsTrue(list.IndexOf("09:05", StringComparison.Ordinal) < list.IndexOf("14:05", StringComparison.Ordinal));

            string detail = store.Read(first).Text;
            StringAssert.Contains(detail, "# 任务 #12");
            StringAssert.Contains(detail, "(page:" + index + ")");
            StringAssert.Contains(detail, "第二行");
            StringAssert.Contains(detail, "完整回复 ```code```");
            StringAssert.Contains(detail, "2026-09-27 09:05:30");
            StringAssert.Contains(store.Read(second).Text, "short");

            StringAssert.Contains(NotebookMarkdown.Render(list), "data-note=\"page:" + first + "\"");
            Assert.AreEqual(first, store.ResolveLink(index, "page:" + first));
            Assert.AreEqual(index, store.ResolveLink(first, "page:" + index));
            store.Rename(first, "改名后");
            Assert.AreEqual(first, store.ResolveLink(index, "page:" + first), "改名不影响链接 / Renaming keeps links");
        }

        [TestMethod]
        public void Record_SameTaskTwice_KeepsBothDetails()
        {
            var store = new NotebookStore(_root);
            var journal = new NotebookTaskJournal(store);
            string a = journal.Record(Task(5, "同一任务", 10));
            string b = journal.Record(Task(5, "同一任务", 11));
            Assert.AreNotEqual(a, b);
            Assert.AreEqual("任务 5 - 同一任务 (2)", store.TitlePath(b).Last());
            string day = store.FindChild("", "2026.9.27 任务记录");
            StringAssert.Contains(store.Read(store.FindChild(day, "已完成任务")).Text, "(page:" + b + ")");
        }

        [TestMethod]
        public void NoteLinks_OnlyRelativeMarkdownOrPageIds_AreClickable()
        {
            Assert.IsTrue(NotebookMarkdown.IsNoteLink("a b.md"));
            Assert.IsTrue(NotebookMarkdown.IsNoteLink("sub/%E4%BB%BB.md"));
            Assert.IsTrue(NotebookMarkdown.IsNoteLink("page:" + Guid.NewGuid().ToString("N")));
            Assert.IsFalse(NotebookMarkdown.IsNoteLink("page:../x"));
            Assert.IsFalse(NotebookMarkdown.IsNoteLink("https://example.com/a.md"));
            Assert.IsFalse(NotebookMarkdown.IsNoteLink("C:/x.md"));
            Assert.IsFalse(NotebookMarkdown.IsNoteLink("/x.md"));
            Assert.IsFalse(NotebookMarkdown.IsNoteLink("#x"));
            Assert.IsFalse(NotebookMarkdown.IsNoteLink("a.txt"));
        }
    }
}