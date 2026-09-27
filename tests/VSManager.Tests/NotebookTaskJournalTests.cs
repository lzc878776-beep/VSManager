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
            string index = day;
            Assert.IsNull(store.FindChild(day, "已完成任务"), "不再有「已完成任务」一层 / No separate list layer");
            CollectionAssert.AreEqual(new[] { "2026.9.27 任务记录", "任务 12 - 修复 登录 按钮" }, store.TitlePath(first).ToArray());
            CollectionAssert.AreEqual(new[] { "2026.9.27 任务记录", "任务 13 - 整理文档/说明" }, store.TitlePath(second).ToArray());

            string list = store.Read(day).Text;
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
        public void Record_UsesAiTitle_AndLimitsFallbackTo20Characters()
        {
            var store = new NotebookStore(_root);
            var journal = new NotebookTaskJournal(store);
            var titled = Task(20, "请扫描本解决方案的代码结构，梳理并说明 VSManager 中 AI 助手部分实现", 9);
            titled.Title = "梳理 AI 助手代码结构";
            string first = journal.Record(titled);
            string second = journal.Record(Task(21, "请出一份架构评估报告，只做分析、不要修改任何代码。报告需覆盖两部分", 10));
            Assert.AreEqual("任务 20 - 梳理 AI 助手代码结构", store.TitlePath(first).Last());
            string list = store.Read(store.FindChild("", "2026.9.27 任务记录")).Text;
            StringAssert.Contains(list, "[梳理 AI 助手代码结构](page:" + first + ")");
            StringAssert.Contains(list, "[请出一份架构评估报告，只做分析、不要修改…](page:" + second + ")");
            StringAssert.Contains(store.Read(first).Text, "# 任务 #20 · 梳理 AI 助手代码结构");
            StringAssert.Contains(store.Read(first).Text, "VSManager 中 AI 助手部分实现", "详情仍保留完整任务 / Details keep the full task");
        }

        [TestMethod]
        public void TaskTitle_IsOneLineAndAtMost20Characters()
        {
            Assert.IsNull(TaskTitle.Normalize("  \r\n "));
            Assert.AreEqual("修复 登录 按钮", TaskTitle.Normalize(" 修复\r\n登录\t按钮 "));
            Assert.AreEqual(20, TaskTitle.Normalize(new string('字', 30)).Length);
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
            StringAssert.Contains(store.Read(day).Text, "(page:" + b + ")");
        }

        [TestMethod]
        public void RecordManual_ListsDetectedManualChat_WithQuestionAndReply()
        {
            var store = new NotebookStore(_root);
            var journal = new NotebookTaskJournal(store);
            var chat = new ExternalChat { VsName = "Demo", Question = "为什么构建失败？\r\n附日志", Answer = "缺少引用 ```x```",
                Started = new DateTime(2026, 9, 27, 16, 0, 0), Finished = new DateTime(2026, 9, 27, 16, 2, 0) };
            string detail = journal.RecordManual(chat);
            string day = store.FindChild("", "2026.9.27 任务记录");
            CollectionAssert.AreEqual(new[] { "2026.9.27 任务记录", "手动 - 为什么构建失败？" }, store.TitlePath(detail).ToArray());
            StringAssert.Contains(store.Read(day).Text, "- **16:02** [为什么构建失败？](page:" + detail + ") · Demo · 手动对话 / Manual chat");
            string text = store.Read(detail).Text;
            StringAssert.Contains(text, "# 手动对话 · 为什么构建失败？");
            StringAssert.Contains(text, "附日志");
            StringAssert.Contains(text, "缺少引用 ```x```");
            StringAssert.Contains(text, "(page:" + day + ")");
        }

        [TestMethod]
        public void Record_MergesLegacyListPageIntoDayPage()
        {
            var store = new NotebookStore(_root);
            string day = store.CreatePage("", "2026.9.26 任务记录", "# 2026.9.26 任务记录\n\n[已完成任务清单 / Completed tasks](page:{index})\n");
            string index = store.CreatePage(day, "已完成任务", "# 清单\n\n点击条目查看任务详情。\n\n- **08:00** [旧任务](page:x) · Demo\n");
            string old = store.CreatePage(day, "任务 1 - 旧任务", "# 任务 #1\n\n[← 返回已完成任务清单 / Back to completed tasks](page:" + index + ")\n");
            new NotebookTaskJournal(store).Record(Task(2, "新任务", 9));
            Assert.IsNull(store.FindChild(day, "已完成任务"));
            StringAssert.Contains(store.Read(day).Text, "- **08:00** [旧任务](page:x) · Demo");
            Assert.IsFalse(store.Read(day).Text.Contains("{index}"));
            StringAssert.Contains(store.Read(old).Text, "[← 返回任务记录 / Back to task records](page:" + day + ")");
            StringAssert.Contains(store.Read(store.FindChild("", "2026.9.27 任务记录")).Text, "[新任务]");
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