using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class TaskDisplayOrderTests
    {
        [TestMethod]
        public void Normalize_DeduplicatesInvalidKeys_WithoutTruncatingLargeOrders()
        {
            var keys = new[] { null, " ", "t:0", "t:-1", "t:2147483648", "x:1", "t:01", " T:1 ", "c:invalid", "t:2\n3" };
            CollectionAssert.AreEqual(new[] { "t:1" }, TaskDisplayOrder.Normalize(keys));
            CollectionAssert.AreEqual(new[] { "a\\b", TaskGrouping.WaitingOpenKey },
                TaskDisplayOrder.Normalize(new[] { " A/B/ ", "a\\b", "bad\nkey", null, TaskGrouping.WaitingOpenKey }, true));
            Assert.AreEqual(15000, TaskDisplayOrder.Normalize(Enumerable.Range(1, 15000).Select(i => "t:" + i)).Count);
            Assert.AreEqual(TaskGrouping.SortManual, TaskGrouping.NormalizeSort("MANUAL"));
        }

        [TestMethod]
        public void Apply_StableUnrankedTail_AndNeverMutatesTasks()
        {
            var tasks = Enumerable.Range(1, 4).Select(i => new QueuedTask { Id = i, Status = QueueStatus.Waiting }).ToArray();
            var ordered = TaskDisplayOrder.Apply(tasks, new[] { "t:3", "t:1", "t:3", "t:99" }, TaskDisplayOrder.KeyOf);
            CollectionAssert.AreEqual(new[] { 3, 1, 2, 4 }, ordered.Select(t => t.Id).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, tasks.Select(t => t.Order).ToArray());
            Assert.IsTrue(tasks.All(t => t.Status == QueueStatus.Waiting));
        }

        [TestMethod]
        public void MergeVisible_KeepsHiddenDeletedAndAbsentSlots_AndRestoresThem()
        {
            var saved = new[] { "t:1", "t:2", "t:3", "t:4", "t:99" };
            var result = TaskDisplayOrder.MergeVisible(saved, new[] { "t:1", "t:3", "t:4", "t:5" }, new[] { "t:4", "t:3", "t:1" });
            CollectionAssert.AreEqual(new[] { "t:4", "t:2", "t:3", "t:1", "t:99", "t:5" }, result);
            CollectionAssert.AreEqual(new[] { "t:4", "t:2", "t:3", "t:1", "t:5" },
                TaskDisplayOrder.Apply(new[] { "t:1", "t:2", "t:3", "t:4", "t:5" }, result, k => k));
        }

        [TestMethod]
        public void ChatIdentity_SurvivesReassignedIdsAndArchiveMillisecondPrecision()
        {
            var started = new DateTime(2026, 1, 1, 9, 0, 0).AddTicks(1234567);
            var live = new ExternalChat { Id = 1, VsKey = "Example/A", Question = "问题 / Question", Started = started };
            string key = TaskDisplayOrder.KeyOf(live);
            var restored = new ExternalChat { Id = 91, VsKey = "example\\a", Question = live.Question,
                Started = new DateTime(started.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond), Restored = true, ArchiveId = "entry" };
            Assert.AreEqual(key, TaskDisplayOrder.KeyOf(restored));
            CollectionAssert.AreEqual(new[] { key }, TaskDisplayOrder.Normalize(new[] { key.ToUpperInvariant(), key }));
            restored.Started = restored.Started.AddMilliseconds(1);
            Assert.AreNotEqual(key, TaskDisplayOrder.KeyOf(restored));
        }

        private static object[] Rows() => new object[]
        {
            new TaskGroupHeader { Key = "a" }, new QueuedTask { Id = 1 }, new QueuedTask { Id = 2 },
            new TaskGroupHeader { Key = "b", Collapsed = true },
            new TaskGroupHeader { Key = "c" }, new QueuedTask { Id = 3 }
        };

        [TestMethod]
        public void DropSlot_GroupSnapsToWholeGroupEdges_AndBlankTail()
        {
            var rows = Rows();
            Assert.AreEqual(0, TaskDisplayOrder.DropSlot(rows, "c", true, true, 2, false, out _));
            Assert.AreEqual(3, TaskDisplayOrder.DropSlot(rows, "c", true, true, 2, true, out _));
            Assert.AreEqual(4, TaskDisplayOrder.DropSlot(rows, "a", true, true, 3, true, out _));
            Assert.AreEqual(6, TaskDisplayOrder.DropSlot(rows, "a", true, true, 6, false, out _));
            Assert.AreEqual(-1, TaskDisplayOrder.DropSlot(rows, "missing", true, true, 0, false, out _));
        }

        [TestMethod]
        public void DropSlot_TaskCannotChangeGroup_ButFlatIsUnrestricted()
        {
            var rows = Rows();
            Assert.AreEqual(-1, TaskDisplayOrder.DropSlot(rows, "t:1", false, true, 3, false, out string reason));
            Assert.AreEqual(TaskDisplayOrder.CrossGroup, reason);
            Assert.AreEqual(1, TaskDisplayOrder.DropSlot(rows, "t:2", false, true, 0, true, out _));
            Assert.AreEqual(3, TaskDisplayOrder.DropSlot(rows, "t:1", false, true, 2, true, out _));
            Assert.AreEqual(6, TaskDisplayOrder.DropSlot(rows, "t:3", false, true, 6, false, out _));
            var flat = rows.OfType<QueuedTask>().Cast<object>().ToArray();
            Assert.AreEqual(3, TaskDisplayOrder.DropSlot(flat, "t:1", false, false, 3, false, out _));
        }

        [TestMethod]
        public void ManualGroups_StayContiguousAcrossActivityChangesAndMissingGroups()
        {
            var a = new QueuedTask { Id = 1, VsKey = "a", Status = QueueStatus.Waiting };
            var b = new QueuedTask { Id = 2, VsKey = "b", Status = QueueStatus.Running };
            var order = new[] { "a", "missing", "b" };
            var shown = TaskGrouping.Build(new object[] { b, a }, null, TaskGrouping.SortManual, null, order);
            CollectionAssert.AreEqual(new[] { "a", "b" }, shown.OfType<TaskGroupHeader>().Select(h => h.Key).ToArray());
            Assert.AreSame(a, shown[1]);
            Assert.AreSame(b, shown[3]);
            b.Finished = DateTime.Now;
            b.Status = QueueStatus.Done;
            shown = TaskGrouping.Build(new object[] { b, a }, null, TaskGrouping.SortManual, new[] { "a" }, order);
            Assert.AreEqual(3, shown.Count);
            Assert.AreEqual("a", ((TaskGroupHeader)shown[0]).Key);
        }

        [TestMethod]
        public void Settings_RoundTripNormalizesAndRestoresAllDisplayState()
        {
            using (var data = new TempDataFolder())
            {
                var old = AppSettings.Load();
                Assert.IsFalse(old.TaskListManualOrder);
                Assert.AreEqual(0, old.TaskListItemOrder.Count);
                Assert.AreEqual(0, old.TaskListGroupOrder.Count);
                old.TaskListManualOrder = true;
                old.TaskListGroupByVs = false;
                old.TaskListGroupSort = "MANUAL";
                old.TaskListItemOrder = new List<string> { "t:3", "t:1", "t:3", "invalid", "t:2" };
                old.TaskListGroupOrder = new List<string> { " B/ ", "a", "b" };
                old.TaskListCollapsedGroups = new List<string> { "A" };
                Assert.IsTrue(old.Save());
                var back = AppSettings.Load();
                Assert.IsTrue(back.TaskListManualOrder);
                Assert.IsFalse(back.TaskListGroupByVs);
                Assert.AreEqual(TaskGrouping.SortManual, back.TaskListGroupSort);
                CollectionAssert.AreEqual(new[] { "t:3", "t:1", "t:2" }, back.TaskListItemOrder);
                CollectionAssert.AreEqual(new[] { "b", "a" }, back.TaskListGroupOrder);
                CollectionAssert.AreEqual(new[] { "a" }, back.TaskListCollapsedGroups);
            }
        }
    }
}
