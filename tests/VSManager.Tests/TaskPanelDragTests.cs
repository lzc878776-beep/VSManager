using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class TaskPanelDragTests
    {
        private sealed class Fixture : IDisposable
        {
            internal readonly TempDataFolder Data = new TempDataFolder();
            internal readonly AppSettings Settings = new AppSettings();
            internal readonly MemoryTaskStore Store = new MemoryTaskStore();
            internal readonly RecordingArchive Archive = new RecordingArchive();
            internal readonly TaskQueue Queue;
            internal readonly TaskPanel Panel;
            internal readonly TaskDragListBox List;
            internal DateTime? Cleared;
            internal readonly List<HiddenTaskMark> Hidden = new List<HiddenTaskMark>();
            internal readonly List<ExternalChat> Chats = new List<ExternalChat>();
            internal int Changes;
            internal Fixture()
            {
                Queue = new TaskQueue(Settings, Store, Archive, () => DateTime.Now);
                Panel = new TaskPanel { ExpandedWidth = 500, Height = 1400 };
                Panel.SetCollapsed(false);
                Panel.VsProvider = () => new[] { new TaskGroupVs { Key = "a", Number = 1 }, new TaskGroupVs { Key = "b", Number = 2 }, new TaskGroupVs { Key = "c", Number = 3 } };
                Panel.SetViewOptions(true, TaskGrouping.SortByNumber, null);
                Panel.Bind(Queue, () => Chats, () => Cleared, () => Hidden);
                List = Panel.Controls.OfType<TaskDragListBox>().Single();
                Panel.CreateControl();
                var handle = List.Handle;
                Panel.PerformLayout();
                Panel.ViewOptionsChanged += () => Changes++;
            }
            internal QueuedTask Add(string group = "a") => Queue.Add(group, group, "测试任务 / Test task " + Queue.NextId, "AI");
            internal string[] Keys => List.Items.Cast<object>().Select(TaskDisplayOrder.KeyOf).ToArray();
            internal int[] Ids => List.Items.OfType<QueuedTask>().Select(t => t.Id).ToArray();
            internal string[] Groups => List.Items.OfType<TaskGroupHeader>().Select(h => h.Key).ToArray();
            internal int Index(string key) => Array.IndexOf(Keys, key);
            internal Point At(int index, bool after = false)
            {
                var r = List.GetItemRectangle(index);
                return new Point(30, after ? r.Bottom - 4 : r.Top + 4);
            }
            public void Dispose() { Panel.Dispose(); Data.Dispose(); }
        }

        private static void Sta(Action<Fixture> test)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { using (var f = new Fixture()) test(f); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "STA 测试超时 / STA test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Invoke(object control, string name, object args) =>
            control.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new[] { args });

        private static void Native(TaskDragListBox list, int message, Point point)
        {
            var m = Message.Create(list.Handle, message, message == 0x0202 ? IntPtr.Zero : (IntPtr)1, (IntPtr)((point.Y << 16) | (point.X & 0xffff)));
            typeof(TaskDragListBox).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, new object[] { m });
        }

        private static DragEventArgs DragArgs(TaskDragListBox list, IDataObject data, Point at)
        {
            var screen = list.PointToScreen(at);
            return new DragEventArgs(data, 1, screen.X, screen.Y, DragDropEffects.Move, DragDropEffects.None);
        }

        private static void Gesture(Fixture f, string key, Action<IDataObject> during)
        {
            Point origin = f.At(f.Index(key));
            int runs = 0;
            f.List.DragRunner = data => { runs++; during(data); return DragDropEffects.Move; };
            Native(f.List, 0x0201, origin);
            Native(f.List, 0x0200, new Point(origin.X + SystemInformation.DragSize.Width + 5, origin.Y));
            Native(f.List, 0x0202, origin);
            f.List.DragRunner = null;
            Assert.AreEqual(1, runs, "原生鼠标消息必须启动一次拖拽 / Native mouse messages must start exactly one drag");
        }

        private static DragDropEffects Drop(Fixture f, IDataObject data, Point target)
        {
            var enter = DragArgs(f.List, data, target);
            Invoke(f.List, "OnDragEnter", enter);
            if (enter.Effect == DragDropEffects.Move) Assert.IsTrue(f.List.InsertionSlot >= 0);
            var drop = DragArgs(f.List, data, target);
            Invoke(f.List, "OnDragDrop", drop);
            Assert.AreEqual(-1, f.List.InsertionSlot);
            return drop.Effect;
        }

        [TestMethod]
        public void NativeHeader_ClickWaitsForMouseUp_AndSmallMovementDoesNotDrag()
        {
            Sta(f =>
            {
                f.Add(); f.Add();
                f.List.SelectedItem = f.Queue.Items[1];
                int runs = 0;
                f.List.DragRunner = data => { runs++; return DragDropEffects.None; };
                var p = f.At(0);
                Native(f.List, 0x0201, p);
                Assert.AreEqual(0, f.Panel.CollapsedGroups.Count);
                Assert.AreSame(f.Queue.Items[1], f.List.SelectedItem);
                Invoke(f.List, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, p.X + 1, p.Y, 0));
                Assert.AreEqual(0, runs);
                Native(f.List, 0x0202, p);
                CollectionAssert.AreEqual(new[] { "a" }, f.Panel.CollapsedGroups);
                Assert.AreEqual(1, f.Changes);
                Native(f.List, 0x0201, p);
                Native(f.List, 0x0202, p);
                Assert.AreEqual(0, f.Panel.CollapsedGroups.Count);
                Assert.AreSame(f.Queue.Items[1], f.List.SelectedItem);
            });
        }

        [TestMethod]
        public void NativeHeader_DragMovesWholeGroupToTail_WithoutCollapsing()
        {
            Sta(f =>
            {
                f.Add("a"); f.Add("a"); f.Add("b"); f.Add("c");
                int selected = f.Queue.Items[2].Id;
                f.List.SelectedItem = f.Queue.Items[2];
                Gesture(f, "a", data => Assert.AreEqual(DragDropEffects.Move, Drop(f, data, new Point(30, f.List.GetItemRectangle(f.List.Items.Count - 1).Bottom + 15))));
                CollectionAssert.AreEqual(new[] { "b", "c", "a" }, f.Groups);
                CollectionAssert.AreEqual(new[] { 3, 4, 1, 2 }, f.Ids);
                Assert.AreEqual(TaskGrouping.SortManual, f.Panel.GroupSort);
                Assert.AreEqual(0, f.Panel.CollapsedGroups.Count);
                Assert.AreEqual(selected, ((QueuedTask)f.List.SelectedItem).Id);
                Assert.AreEqual(1, f.Changes);
                f.Panel.SetGroupByVs(false);
                f.Panel.SetGroupByVs(true);
                CollectionAssert.AreEqual(new[] { "b", "c", "a" }, f.Groups);
                f.Queue.Items[0].Status = QueueStatus.Running;
                f.Panel.RefreshItems();
                CollectionAssert.AreEqual(new[] { "b", "c", "a" }, f.Groups);
            });
        }

        [TestMethod]
        public void GroupHeader_DropInsideGroupSnapsAndCollapsedGroupsCanMove()
        {
            Sta(f =>
            {
                f.Add("a"); f.Add("a"); f.Add("b"); f.Add("c");
                f.Panel.ToggleGroup("c");
                Gesture(f, "c", data => Drop(f, data, f.At(f.Index("t:2"), true)));
                CollectionAssert.AreEqual(new[] { "a", "c", "b" }, f.Groups);
                CollectionAssert.AreEqual(new[] { "c" }, f.Panel.CollapsedGroups);
                Assert.IsFalse(f.Ids.Contains(4));
                f.Panel.ToggleGroup("c");
                CollectionAssert.AreEqual(new[] { 1, 2, 4, 3 }, f.Ids);
            });
        }

        [TestMethod]
        public void Tasks_SameGroupReorders_CrossGroupRejected_FlatAllowsBoth()
        {
            Sta(f =>
            {
                f.Add(); f.Add(); f.Add("b");
                Gesture(f, "t:2", data => Drop(f, data, f.At(f.Index("t:1"))));
                CollectionAssert.AreEqual(new[] { 2, 1, 3 }, f.Ids);
                Assert.IsTrue(f.Panel.ManualOrder);
                int changes = f.Changes;
                Gesture(f, "t:1", data => Assert.AreEqual(DragDropEffects.None, Drop(f, data, f.At(f.Index("t:3")))));
                Assert.AreEqual(changes, f.Changes);
                f.Panel.SetGroupByVs(false);
                Gesture(f, "t:3", data => Drop(f, data, f.At(0)));
                CollectionAssert.AreEqual(new[] { 3, 2, 1 }, f.Ids);
                CollectionAssert.AreEqual(new[] { "a", "a", "b" }, f.Queue.Items.Select(t => t.VsKey).ToArray());
                f.Panel.SetManualOrder(false);
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, f.Ids);
                f.Panel.SetManualOrder(true);
                CollectionAssert.AreEqual(new[] { 3, 2, 1 }, f.Ids);
                f.Panel.ResetDisplayOrder();
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, f.Ids);
                Assert.AreEqual(0, f.Panel.ItemOrder.Count);
            });
        }

        [TestMethod]
        public void EscapeAndForeignData_NeverCommit_AndRemoveInsertionFeedback()
        {
            Sta(f =>
            {
                f.Add(); f.Add("b");
                Gesture(f, "a", data =>
                {
                    var target = f.At(f.Index("b"), true);
                    var over = DragArgs(f.List, data, target);
                    Invoke(f.List, "OnDragOver", over);
                    Assert.AreEqual(DragDropEffects.Move, over.Effect);
                    var cancel = new QueryContinueDragEventArgs(1, true, DragAction.Continue);
                    Invoke(f.List, "OnQueryContinueDrag", cancel);
                    Assert.AreEqual(DragAction.Cancel, cancel.Action);
                    Assert.AreEqual(-1, f.List.InsertionSlot);
                    Assert.AreEqual(DragDropEffects.None, Drop(f, data, target));
                });
                Assert.AreEqual(0, f.Changes);
                Assert.AreEqual(0, f.Panel.CollapsedGroups.Count);
                Assert.AreEqual(DragDropEffects.None, Drop(f, new DataObject("text", "foreign"), f.At(0)));
                CollectionAssert.AreEqual(new[] { "a", "b" }, f.Groups);
            });
        }

        [TestMethod]
        public void ReloadDuringDrag_RevalidatesDeletedSourcesAndChangedGroups()
        {
            Sta(f =>
            {
                f.Add(); f.Add(); f.Add("b");
                Gesture(f, "t:2", data =>
                {
                    f.Queue.Remove(2);
                    Assert.AreEqual(DragDropEffects.None, Drop(f, data, f.At(f.Index("t:1"))));
                });
                Assert.AreEqual(0, f.Changes);
                Gesture(f, "t:1", data =>
                {
                    f.Queue.Items[0].VsKey = "b";
                    f.Panel.RefreshItems();
                    Assert.AreEqual(DragDropEffects.None, Drop(f, data, f.At(f.Index("t:3"))));
                });
                Assert.AreEqual(0, f.Changes);
                Gesture(f, "b", data =>
                {
                    foreach (var t in f.Queue.Items.ToArray()) f.Queue.Remove(t.Id);
                    Assert.AreEqual(DragDropEffects.None, Drop(f, data, new Point(20, 20)));
                });
                Assert.AreEqual(0, f.Changes);
            });
        }

        [TestMethod]
        public void ReloadDuringDrag_KeepsValidSourceAndSelectedTopAnchor()
        {
            Sta(f =>
            {
                f.Panel.SetGroupByVs(false);
                for (int i = 0; i < 12; i++) f.Add();
                f.Panel.Height = 420;
                f.List.SelectedItem = f.Queue.Items[4];
                f.List.TopIndex = 4;
                f.Panel.RefreshItems();
                Assert.AreSame(f.Queue.Items[4], f.List.SelectedItem);
                Assert.AreEqual("t:5", TaskDisplayOrder.KeyOf(f.List.Items[f.List.TopIndex]));
                Gesture(f, "t:6", data =>
                {
                    f.Panel.RefreshItems();
                    Drop(f, data, f.At(f.Index("t:5")));
                });
                Assert.IsTrue(f.Panel.ManualOrder);
                CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 6, 5, 7, 8, 9, 10, 11, 12 }, f.Ids);
                Assert.AreEqual("t:5", TaskDisplayOrder.KeyOf(f.List.Items[f.List.TopIndex]));
            });
        }

        [TestMethod]
        public void EdgeScroll_MovesViewport_AndLeaveStopsFeedback()
        {
            Sta(f =>
            {
                f.Panel.SetGroupByVs(false);
                for (int i = 0; i < 15; i++) f.Add();
                f.Panel.Height = 400;
                Gesture(f, "t:1", data =>
                {
                    var bottom = new Point(30, f.List.ClientSize.Height - 2);
                    Invoke(f.List, "OnDragOver", DragArgs(f.List, data, bottom));
                    int before = f.List.TopIndex;
                    f.List.ScrollDrag(); f.List.ScrollDrag();
                    Assert.IsTrue(f.List.TopIndex > before);
                    Invoke(f.List, "OnDragOver", DragArgs(f.List, data, new Point(30, 1)));
                    before = f.List.TopIndex;
                    f.List.ScrollDrag();
                    Assert.IsTrue(f.List.TopIndex < before);
                    Invoke(f.List, "OnDragLeave", EventArgs.Empty);
                    Assert.AreEqual(-1, f.List.InsertionSlot);
                });
                Assert.IsFalse(f.Panel.ManualOrder);
            });
        }

        [TestMethod]
        public void HiddenHistoryAndFailedEntries_RetainRanksAcrossReorderRetryAndRemoval()
        {
            Sta(f =>
            {
                f.Panel.SetGroupByVs(false);
                for (int i = 0; i < 4; i++) f.Add();
                f.Panel.SetManualOrder(true);
                f.Queue.Items[1].Status = QueueStatus.Done;
                f.Queue.Items[1].Finished = DateTime.Now.AddMinutes(-2);
                f.Queue.Items[2].Status = QueueStatus.Failed;
                f.Cleared = DateTime.Now;
                f.Hidden.Add(new HiddenTaskMark { Id = 3, ReplacedBy = 4, At = DateTime.Now });
                f.Panel.RefreshItems();
                CollectionAssert.AreEqual(new[] { 1, 4 }, f.Ids);
                Gesture(f, "t:4", data => Drop(f, data, f.At(0)));
                CollectionAssert.AreEqual(new[] { "t:4", "t:2", "t:3", "t:1" }, f.Panel.ItemOrder);
                var history = f.List.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(m => m.Text == "显示已清除的历史");
                history.PerformClick();
                CollectionAssert.AreEqual(new[] { 4, 2, 3, 1 }, f.Ids);
                history.PerformClick();
                f.Cleared = null;
                f.Hidden.Clear();
                TaskStateMachine.Requeue(f.Queue.Find(3));
                f.Panel.RefreshItems();
                CollectionAssert.AreEqual(new[] { 4, 2, 3, 1 }, f.Ids);
                f.Queue.Remove(2);
                f.Add();
                CollectionAssert.AreEqual(new[] { 4, 3, 1, 5 }, f.Ids);
                Assert.IsTrue(f.Panel.ItemOrder.Contains("t:2"));
            });
        }

        [TestMethod]
        public void SettingsRestart_RestoresTaskGroupAndCollapsedOrdering()
        {
            Sta(f =>
            {
                f.Add(); f.Add(); f.Add("b");
                Gesture(f, "t:2", data => Drop(f, data, f.At(f.Index("t:1"))));
                Gesture(f, "b", data => Drop(f, data, f.At(0)));
                f.Panel.ToggleGroup("b");
                var settings = f.Settings;
                settings.TaskListManualOrder = f.Panel.ManualOrder;
                settings.TaskListItemOrder = f.Panel.ItemOrder;
                settings.TaskListGroupOrder = f.Panel.GroupOrder;
                settings.TaskListGroupSort = f.Panel.GroupSort;
                settings.TaskListCollapsedGroups = f.Panel.CollapsedGroups;
                Assert.IsTrue(settings.Save());
                var restored = AppSettings.Load();
                using (var panel = new TaskPanel())
                {
                    panel.Bind(f.Queue);
                    panel.SetViewOptions(restored.TaskListGroupByVs, restored.TaskListGroupSort, restored.TaskListCollapsedGroups,
                        restored.TaskListManualOrder, restored.TaskListItemOrder, restored.TaskListGroupOrder);
                    var rows = panel.Controls.OfType<VsListBox>().Single().Items.Cast<object>().ToArray();
                    CollectionAssert.AreEqual(new[] { "b", "a", "t:2", "t:1" }, rows.Select(TaskDisplayOrder.KeyOf).ToArray());
                }
            });
        }

        [TestMethod]
        public void DragDisplayOrder_DoesNotChangeDispatchIdsOrWorktreeBarrier()
        {
            Sta(f =>
            {
                f.Panel.SetGroupByVs(false);
                f.Queue.ResolveWorktree = key => new WorktreeInfo { MainRoot = "example", Root = "example.worktree", SolutionPath = "example.worktree\\Example.slnx", Branch = "refs/heads/task/example" };
                for (int i = 0; i < 6; i++) f.Add();
                int saves = f.Store.SaveCount, archived = f.Archive.Events.Count;
                Gesture(f, "t:6", data => Drop(f, data, f.At(0)));
                Assert.AreEqual(saves, f.Store.SaveCount);
                Assert.AreEqual(archived, f.Archive.Events.Count);
                Assert.AreEqual(6, f.Ids[0]);
                Assert.AreEqual(1, f.Queue.NextToDispatch(DateTime.MaxValue).Single().Id);
                for (int i = 1; i <= 5; i++)
                {
                    var task = f.Queue.Find(i);
                    task.Status = QueueStatus.Running;
                    Assert.IsTrue(TaskStateMachine.Complete(task, DateTime.Now));
                    f.Queue.Commit();
                }
                var barrier = f.Queue.Items.Single(t => t.IsWorktreeMerge);
                Assert.AreSame(barrier, f.Queue.NextToDispatch(DateTime.MaxValue).Single());
                Assert.AreSame(barrier, f.Queue.BlockingTask(f.Queue.Find(6)));
                Assert.AreEqual(6, f.Ids[0]);
                Assert.AreEqual(6, f.Queue.Find(6).Order);
            });
        }

        [TestMethod]
        public void InsertionLine_RepaintsPersistently_AndModeChangeCancelsPendingDrop()
        {
            Sta(f =>
            {
                f.Add(); f.Add();
                Gesture(f, "t:2", data =>
                {
                    var point = f.At(f.Index("t:1"));
                    Invoke(f.List, "OnDragOver", DragArgs(f.List, data, point));
                    int y = f.List.GetItemRectangle(f.List.InsertionSlot).Top;
                    using (var bitmap = new Bitmap(f.List.Width, f.List.Height))
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        Invoke(f.List, "OnPaint", new PaintEventArgs(graphics, f.List.ClientRectangle));
                        Assert.AreEqual(Theme.Accent.ToArgb(), bitmap.GetPixel(f.List.Width / 2, y).ToArgb());
                        graphics.Clear(Color.Black);
                        Invoke(f.List, "OnPaint", new PaintEventArgs(graphics, f.List.ClientRectangle));
                        Assert.AreEqual(Theme.Accent.ToArgb(), bitmap.GetPixel(f.List.Width / 2, y).ToArgb());
                    }
                    f.Panel.SetGroupByVs(false);
                    int changes = f.Changes;
                    Assert.AreEqual(DragDropEffects.None, Drop(f, data, f.At(0)));
                    Assert.AreEqual(changes, f.Changes);
                });
                Assert.IsFalse(f.Panel.ManualOrder);
            });
        }

        [TestMethod]
        public void ManualChat_DragsWithTasks_AndRetainsOrderAfterRestore()
        {
            Sta(f =>
            {
                f.Panel.SetGroupByVs(false);
                f.Add();
                var chat = new ExternalChat { Id = 1, VsKey = "a", Question = "对话 / Chat", Started = DateTime.Now, Generating = true };
                f.Chats.Add(chat);
                f.Panel.RefreshItems();
                string chatKey = TaskDisplayOrder.KeyOf(chat);
                Gesture(f, "t:1", data => Drop(f, data, f.At(f.Index(chatKey))));
                CollectionAssert.AreEqual(new[] { "t:1", chatKey }, f.Keys);
                f.Chats[0] = new ExternalChat { Id = 99, VsKey = "a", Question = chat.Question, Started = chat.Started, Restored = true };
                f.Panel.RefreshItems();
                CollectionAssert.AreEqual(new[] { "t:1", chatKey }, f.Keys);
            });
        }
    }
}
