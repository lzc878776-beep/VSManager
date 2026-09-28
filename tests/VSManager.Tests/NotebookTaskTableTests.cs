using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class NotebookTaskTableTests
    {
        private string _root;
        private NotebookStore _store;
        private NotebookTaskJournal _journal;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "vsm-task-table-" + Guid.NewGuid().ToString("N"));
            _store = new NotebookStore(_root);
            _journal = new NotebookTaskJournal(_store);
        }

        [TestCleanup]
        public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        private static QueuedTask Item(int id, string project, string status, int hour) => new QueuedTask
        {
            Id = id, Title = "任务 " + id, Text = "完整任务正文 / Full task body", VsName = project, Status = status,
            Started = new DateTime(2026, 9, 28, hour, 0, 0), Finished = new DateTime(2026, 9, 28, hour, 2, 0),
            Result = "完整结果 / Full result\n- [ ] 检查按钮 / Check button", Error = "失败说明 / Failure explanation"
        };

        private string Day => _store.FindChild("", "2026.9.28 任务记录");
        private string Render()
        {
            var doc = _store.Read(Day);
            return NotebookTaskTable.Render(doc.Path, doc.Title, doc.Text, _store.ReadChildHeaders(doc.Path));
        }

        [TestMethod]
        public void ExistingIndex_GroupsProjects_OrdersNewestFirst_AndKeepsFourColumns()
        {
            string early = _journal.Record(Item(1, "Alpha", QueueStatus.Done, 9));
            string late = _journal.Record(Item(2, "Alpha", QueueStatus.Unverified, 11));
            string failed = _journal.Record(Item(3, "Beta", QueueStatus.Failed, 10));
            string original = _store.Read(Day).Text;
            string html = Render();
            StringAssert.Contains(html, "共 3 条 · 完成 1 · 待验证 1 · 失败 1");
            StringAssert.Contains(html, "Alpha（<span class=\"tj-count\">2</span>）");
            StringAssert.Contains(html, "Beta（<span class=\"tj-count\">1</span>）");
            Assert.AreEqual(2, Regex.Matches(html, "<table ").Count);
            Assert.AreEqual(8, Regex.Matches(html, "<th scope=").Count);
            Assert.AreEqual(12, Regex.Matches(html, "<td[ >]").Count);
            foreach (string id in new[] { early, late, failed }) StringAssert.Contains(html, "data-note=\"page:" + id + "\"");
            Assert.IsTrue(html.IndexOf("page:" + late, StringComparison.Ordinal) < html.IndexOf("page:" + early, StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("Full result"));
            Assert.IsFalse(html.Contains("Check button"));
            StringAssert.Contains(_store.Read(late).Text, "Check button");
            StringAssert.Contains(_store.Read(failed).Text, "| 状态 / Status | 失败 / Failed |");
            StringAssert.Contains(_store.Read(failed).Text, "Failure explanation");
            Assert.AreEqual(original, _store.Read(Day).Text, "预览不重写笔记 / Preview does not rewrite notes");
        }

        [TestMethod]
        public void ManualChats_ShareProjectGroup_AndEscapedTextCannotBecomeHtml()
        {
            _journal.Record(Item(1, "Demo <img> & [x]|", QueueStatus.Done, 9));
            _journal.RecordManual(new ExternalChat { VsName = "Demo <img> & [x]|", Question = "[title] <script> & |",
                Started = new DateTime(2026, 9, 28, 10, 0, 0), Finished = new DateTime(2026, 9, 28, 10, 2, 0), Answer = "manual result" });
            string html = Render();
            Assert.AreEqual(1, Regex.Matches(html, "<table ").Count);
            StringAssert.Contains(html, "Demo &lt;img&gt; &amp; [x]|（<span class=\"tj-count\">2");
            StringAssert.Contains(html, "[title] &lt;script&gt;");
            Assert.IsFalse(html.Contains("<script>"));
            Assert.IsFalse(html.Contains("manual result"));
        }

        [TestMethod]
        public void MissingDetail_IsUnknown_AndUserAnnotationsRemainVisible()
        {
            string detail = _journal.Record(Item(1, "Demo", QueueStatus.Done, 9));
            _store.Trash(detail);
            var doc = _store.Read(Day);
            _store.Save(doc, doc.Text + "\n## 用户补记 / User annotation\nKeep this text.\n");
            string html = Render();
            StringAssert.Contains(html, "共 1 条 · 完成 0 · 待验证 0 · 失败 0");
            StringAssert.Contains(html, "未知 / Unknown");
            StringAssert.Contains(html, "Keep this text.");
            StringAssert.Contains(html, "data-note=\"page:" + detail);
        }

        [TestMethod]
        public void MetadataHeaders_ExcludeReplyFields_AndDatesUseDescendingSections()
        {
            string old = _journal.Record(Item(1, "Demo", QueueStatus.Done, 9));
            string recent = _journal.Record(Item(2, "Demo", QueueStatus.Done, 9));
            var detail = _store.Read(recent);
            _store.Save(detail, detail.Text.Replace("2026-09-28 09:02:00", "2026-09-29 09:02:00") + "\n| 状态 / Status | 失败 / Failed |\n");
            string html = Render();
            Assert.IsTrue(html.IndexOf("2026.9.29 任务记录", StringComparison.Ordinal) < html.IndexOf("2026.9.28 任务记录", StringComparison.Ordinal));
            Assert.AreEqual(2, Regex.Matches(html, "class=\"tj-day\"").Count);
            StringAssert.Contains(html, "共 2 条 · 完成 2");
            Assert.IsTrue(_store.ReadChildHeaders(Day).All(d => !d.Text.Contains("Full result")));
        }

        [TestMethod]
        public void EmptyDay_HasFiltersSummaryAndTitle_OrdinaryNotesAreNotJournals()
        {
            Assert.IsFalse(NotebookTaskTable.IsJournal("工作笔记"));
            Assert.IsFalse(NotebookTaskTable.IsJournal("2026.2.30 任务记录"));
            _store.CreatePage("", "2026.9.28 任务记录");
            string html = Render();
            Assert.AreEqual(4, Regex.Matches(html, "data-status-filter=").Count);
            StringAssert.Contains(html, "共 0 条");
            StringAssert.Contains(html, "<h1>2026.9.28 任务记录</h1>");
        }
    }
}
