using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class TaskGroupingTests
    {
        private static readonly DateTime T0 = new DateTime(2025, 1, 1, 9, 0, 0);

        private static QueuedTask Task(int id, string key, string name, string status, int minutes = 0) =>
            new QueuedTask { Id = id, VsKey = key, VsName = name, Status = status, Created = T0.AddMinutes(minutes), Text = "task " + id };

        private static readonly TaskGroupVs[] Open =
        {
            new TaskGroupVs { Key = "%USERPROFILE%\\source\\A\\A.slnx", Name = "A", Number = 1 },
            new TaskGroupVs { Key = "%USERPROFILE%\\source\\B\\B.slnx", Name = "Example.B", Number = 2 }
        };

        [TestMethod]
        public void Build_OneGroupPerVs_KeepsInGroupOrder_AndCountsStatuses()
        {
            var items = new object[]
            {
                Task(3, Open[1].Key, "Example.B", QueueStatus.Running, 5),
                Task(1, Open[0].Key, "A", QueueStatus.Waiting, 1),
                Task(4, Open[1].Key, "Example.B", QueueStatus.Waiting, 2),
                Task(2, Open[1].Key.ToUpperInvariant().Replace('\\', '/'), "Example.B", QueueStatus.Done, 3),
                Task(5, Open[1].Key, "Example.B", QueueStatus.Failed, 4),
                Task(6, Open[0].Key, "A", QueueStatus.Cancelled, 0),
                Task(7, Open[1].Key, "Example.B", QueueStatus.Sending, 1),
            };
            var shown = TaskGrouping.Build(items, Open, TaskGrouping.SortByActivity, null);
            var headers = shown.OfType<TaskGroupHeader>().ToList();
            Assert.AreEqual(2, headers.Count);
            Assert.AreEqual("@2 Example.B", headers[0].Title);
            Assert.AreEqual(5, headers[0].Tasks);
            Assert.AreEqual(2, headers[0].Running);
            Assert.AreEqual(1, headers[0].Failed);
            StringAssert.StartsWith(headers[0].StatsText, "5 个任务，2 个执行中");
            StringAssert.Contains(headers[0].StatsText, "/ 5 tasks, 2 running");
            CollectionAssert.AreEqual(new[] { 3, 4, 2, 5, 7 }, Members(shown, headers[0]).Select(t => t.Id).ToArray());
            Assert.AreEqual("@1 A", headers[1].Title);
            CollectionAssert.AreEqual(new[] { 1, 6 }, Members(shown, headers[1]).Select(t => t.Id).ToArray());
            Assert.AreEqual(items.Length + 2, shown.Count);
        }

        [TestMethod]
        public void WaitingVs_GoesToOpenTargetGroup_OrToWaitingOpenGroup()
        {
            var items = new object[]
            {
                new QueuedTask { Id = 1, VsKey = "%USERPROFILE%\\source\\Closed\\Closed.slnx", VsName = "Closed project", Target = "Closed project", Status = QueueStatus.WaitingVs, Created = T0 },
                new QueuedTask { Id = 2, VsKey = Open[0].Key, VsName = "A", Status = QueueStatus.WaitingVs, Created = T0 },
                new QueuedTask { Id = 3, VsKey = "%USERPROFILE%\\source\\Closed\\Closed.slnx", VsName = "Closed project", Status = QueueStatus.Done, Created = T0 },
            };
            var shown = TaskGrouping.Build(items, Open, TaskGrouping.SortByNumber, null);
            var headers = shown.OfType<TaskGroupHeader>().ToList();
            Assert.AreEqual("@1 A", headers[0].Title);
            CollectionAssert.AreEqual(new[] { 2 }, Members(shown, headers[0]).Select(t => t.Id).ToArray());
            Assert.AreEqual("Closed project（未打开 / not open）", headers[1].Title);
            CollectionAssert.AreEqual(new[] { 3 }, Members(shown, headers[1]).Select(t => t.Id).ToArray());
            Assert.IsTrue(headers[2].IsWaitingOpen);
            Assert.AreEqual(TaskGrouping.WaitingOpenKey, headers[2].Key);
            CollectionAssert.AreEqual(new[] { 1 }, Members(shown, headers[2]).Select(t => t.Id).ToArray());
            Assert.AreEqual(1, headers[2].Parked);
        }

        [TestMethod]
        public void Sort_Activity_RunningFirstThenLatest_Number_ByVsNumber()
        {
            var items = new object[]
            {
                Task(1, Open[0].Key, "A", QueueStatus.Done, 50),
                Task(2, Open[1].Key, "Example.B", QueueStatus.Done, 10),
                Task(3, "%USERPROFILE%\\source\\C\\C.slnx", "C", QueueStatus.Done, 90),
            };
            var byActivity = TaskGrouping.Build(items, Open, TaskGrouping.SortByActivity, null).OfType<TaskGroupHeader>().Select(h => h.Title).ToArray();
            CollectionAssert.AreEqual(new[] { "C（未打开 / not open）", "@1 A", "@2 Example.B" }, byActivity);

            ((QueuedTask)items[1]).Status = QueueStatus.Running;
            byActivity = TaskGrouping.Build(items, Open, TaskGrouping.SortByActivity, null).OfType<TaskGroupHeader>().Select(h => h.Title).ToArray();
            Assert.AreEqual("@2 Example.B", byActivity[0]);

            var byNumber = TaskGrouping.Build(items, Open, TaskGrouping.SortByNumber, null).OfType<TaskGroupHeader>().Select(h => h.Title).ToArray();
            CollectionAssert.AreEqual(new[] { "@1 A", "@2 Example.B", "C（未打开 / not open）" }, byNumber);
        }

        [TestMethod]
        public void CollapsedGroup_ShowsHeaderOnly_WithStats()
        {
            var items = new object[] { Task(1, Open[0].Key, "A", QueueStatus.Waiting), Task(2, Open[0].Key, "A", QueueStatus.Done), Task(3, Open[1].Key, "B", QueueStatus.Done) };
            var collapsed = new HashSet<string> { TaskGrouping.NormalizeKey(Open[0].Key.ToUpperInvariant()) };
            var shown = TaskGrouping.Build(items, Open, TaskGrouping.SortByNumber, collapsed);
            Assert.AreEqual(3, shown.Count);
            var h = (TaskGroupHeader)shown[0];
            Assert.IsTrue(h.Collapsed);
            Assert.AreEqual(2, h.Tasks);
            Assert.IsInstanceOfType(shown[1], typeof(TaskGroupHeader));
        }

        [TestMethod]
        public void ExternalChats_JoinTheirVsGroup_AndEmptyInputHasNoHeaders()
        {
            var chat = new ExternalChat { VsKey = Open[1].Key, VsName = "B", Generating = true, Started = T0 };
            var shown = TaskGrouping.Build(new object[] { chat, Task(1, Open[1].Key, "B", QueueStatus.Done) }, Open, null, null);
            var h = (TaskGroupHeader)shown[0];
            Assert.AreEqual(1, h.Chats);
            Assert.AreEqual(1, h.Running);
            Assert.AreEqual(3, shown.Count);
            Assert.AreEqual(0, TaskGrouping.Build(new object[0], Open, null, null).Count);
        }

        [TestMethod]
        public void Settings_DefaultToGroupedByActivity_AndNormalize()
        {
            var s = new AppSettings();
            Assert.IsTrue(s.TaskListGroupByVs);
            Assert.AreEqual(TaskGrouping.SortByActivity, s.TaskListGroupSort);
            Assert.AreEqual(0, s.TaskListCollapsedGroups.Count);
            Assert.AreEqual(TaskGrouping.SortByActivity, TaskGrouping.NormalizeSort("bogus"));
            Assert.AreEqual(TaskGrouping.SortByNumber, TaskGrouping.NormalizeSort("NUMBER"));
            var keys = TaskGrouping.NormalizeCollapsed(Enumerable.Range(0, 150).Select(i => "K" + i).Concat(new[] { "k1", " ", null }));
            Assert.AreEqual(TaskGrouping.MaxCollapsed, keys.Count);
            Assert.AreEqual(keys.Count, keys.Distinct().Count());
        }

        private static IEnumerable<QueuedTask> Members(List<object> shown, TaskGroupHeader h) =>
            shown.SkipWhile(x => !ReferenceEquals(x, h)).Skip(1).TakeWhile(x => !(x is TaskGroupHeader)).Cast<QueuedTask>();
    }
}
