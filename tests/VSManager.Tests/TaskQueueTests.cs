using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>任务清单：编号分配、加载修复、归档流水与保存失败处理。/ Task list: id allocation, load repair, journal and save failures.</summary>
    [TestClass]
    public class TaskQueueTests
    {
        private TempDataFolder _data;
        private FakeClock _clock;
        private MemoryTaskStore _store;
        private RecordingArchive _archive;
        private AppSettings _settings;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _clock = new FakeClock();
            _store = new MemoryTaskStore();
            _archive = new RecordingArchive();
            _settings = new AppSettings();
        }

        [TestCleanup]
        public void Cleanup() => _data.Dispose();

        private TaskQueue NewQueue() => new TaskQueue(_settings, _store, _archive, _clock.Func);

        private static QueuedTask T(int id, string status, string vs = "A", DateTime? created = null) =>
            new QueuedTask { Id = id, VsKey = vs, VsName = vs, Text = "task " + id, Status = status, Created = created ?? new DateTime(2026, 1, 1) };

        [TestMethod]
        public void Add_AssignsIncreasingIds_AndPersistsCounter()
        {
            var q = NewQueue();
            var a = q.Add("A", "A", "one", "AI");
            var b = q.Add("B", "B", "two", "用户");
            Assert.AreEqual(1, a.Id);
            Assert.AreEqual(2, b.Id);
            Assert.AreEqual(QueueStatus.Waiting, a.Status);
            Assert.AreEqual(_clock.Now, a.Created);
            Assert.IsTrue(a.FromAgent);
            Assert.IsFalse(b.FromAgent);
            Assert.AreEqual(3, _settings.TaskNextId);
            Assert.IsTrue(File.Exists(AppSettings.FilePath), "编号计数写入 settings.json / counter saved to settings.json");
            Assert.AreEqual(2, _store.Saved.Count);
        }

        [TestMethod]
        public void Ids_NeverReused_AfterHistoryRemoved()
        {
            var q = NewQueue();
            var a = q.Add("A", "A", "one", "AI");
            q.Remove(a.Id);
            // 模拟重启：清单为空但设置中记着计数 / Simulated restart: empty list but the counter is in the settings
            _store.Initial.Clear();
            var q2 = NewQueue();
            Assert.AreEqual(2, q2.Add("A", "A", "two", "AI").Id);
        }

        [TestMethod]
        public void Load_RenumbersMissingAndDuplicateIds()
        {
            _store.Initial.Add(T(3, QueueStatus.Done, created: new DateTime(2026, 1, 1)));
            _store.Initial.Add(T(3, QueueStatus.Done, created: new DateTime(2026, 1, 2)));
            _store.Initial.Add(T(0, QueueStatus.Failed, created: new DateTime(2026, 1, 3)));
            var q = NewQueue();
            CollectionAssert.AreEqual(new[] { 3, 4, 5 }, q.Items.Select(t => t.Id).ToArray());
            Assert.AreEqual(6, q.NextId);
            StringAssert.Contains(q.LoadWarning, "重新编号");
            Assert.AreEqual(6, q.Add("A", "A", "new", "AI").Id);
        }

        [TestMethod]
        public void Load_RespectsCounterFromSettings()
        {
            _settings.TaskNextId = 50;
            _store.Initial.Add(T(7, QueueStatus.Done));
            var q = NewQueue();
            Assert.AreEqual(50, q.NextId);
            Assert.IsNull(q.LoadWarning);
        }

        [TestMethod]
        public void Load_SendingTaskIsRequeued()
        {
            _store.Initial.Add(T(1, QueueStatus.Sending));
            var q = NewQueue();
            Assert.AreEqual(QueueStatus.Waiting, q.Items[0].Status);
        }

        [TestMethod]
        public void Load_StoreProblemsBecomeWarning()
        {
            _store.InitialProblems.Add("tasks.json 读取不完整");
            var q = NewQueue();
            StringAssert.Contains(q.LoadWarning, "读取不完整");
        }

        [TestMethod]
        public void Remove_RefusesSendingTask()
        {
            var q = NewQueue();
            var a = q.Add("A", "A", "one", "AI");
            a.Status = QueueStatus.Sending;
            Assert.IsFalse(q.Remove(a.Id));
            a.Status = QueueStatus.Done;
            Assert.IsTrue(q.Remove(a.Id));
            Assert.AreEqual(0, q.Items.Count);
            Assert.IsFalse(q.Remove(999));
        }

        [TestMethod]
        public void Commit_WritesJournalEvents()
        {
            var q = NewQueue();
            var a = q.Add("A", "A", "one", "AI");
            q.Commit(); // 无变化不写 / no change, no event
            TaskStateMachine.BeginSend(a, "A"); q.Commit();
            TaskStateMachine.ApplySendResult(a, "失败", _clock.Now); q.Commit();
            TaskStateMachine.BeginSend(a, "A"); q.Commit();
            TaskStateMachine.ApplySendResult(a, "已发送", _clock.Now); q.Commit();
            a.Result = "r"; q.Commit();
            TaskStateMachine.Complete(a, _clock.Now); q.Commit();
            q.Remove(a.Id);
            CollectionAssert.AreEqual(new[]
            {
                "#1:created", "#1:sending", "#1:retry", "#1:sending", "#1:running", "#1:update", "#1:done", "#1:removed"
            }, _archive.Events.ToArray());
        }

        [TestMethod]
        public void Ahead_CountsEarlierAndActiveTasksOfSameVs()
        {
            var q = NewQueue();
            var a = q.Add("A", "A", "1", "AI");
            var b = q.Add("A", "A", "2", "AI");
            var c = q.Add("A", "A", "3", "AI");
            q.Add("B", "B", "4", "AI");
            Assert.AreEqual(0, q.Ahead(a));
            Assert.AreEqual(2, q.Ahead(c));
            a.Status = QueueStatus.Done;
            Assert.AreEqual(1, q.Ahead(c));
            c.Status = QueueStatus.Running;
            Assert.AreEqual(1, q.Ahead(b), "执行中的任务总是在前 / a running task is always ahead");
        }

        [TestMethod]
        public void HistoryLimit_DefaultKeepsAll_PositiveTrimsOldestFinished()
        {
            var q = NewQueue();
            for (int i = 0; i < 5; i++)
            {
                var t = q.Add("A", "A", "t" + i, "AI");
                t.Status = QueueStatus.Done;
                t.Finished = _clock.Now.AddMinutes(i);
            }
            q.Commit();
            Assert.AreEqual(5, q.Items.Count, "默认不裁剪 / no trimming by default");
            _settings.TaskHistoryLimit = 2;
            q.Add("A", "A", "active", "AI");
            CollectionAssert.AreEqual(new[] { 4, 5, 6 }, q.Items.Select(t => t.Id).ToArray());
        }

        [TestMethod]
        public void SaveFailure_KeepsItems_AndRetriesAfter10Seconds()
        {
            var q = NewQueue();
            q.Add("A", "A", "one", "AI");
            _store.FailWith = "IOException：磁盘已满";
            q.Add("A", "A", "two", "AI");
            Assert.AreEqual("IOException：磁盘已满", q.SaveError);
            Assert.AreEqual(2, q.Items.Count, "内存中的清单不被清空 / in-memory list kept");
            StringAssert.Contains(File.ReadAllText(TaskQueue.LogPath, Encoding.UTF8), "保存任务清单失败");

            _store.FailWith = null;
            int before = _store.SaveCount;
            q.RetrySaveIfNeeded();
            Assert.AreEqual(before, _store.SaveCount, "10 秒内不重试 / no retry within 10 s");
            _clock.Advance(TimeSpan.FromSeconds(11));
            bool changed = false;
            q.Changed += () => changed = true;
            q.RetrySaveIfNeeded();
            Assert.IsNull(q.SaveError);
            Assert.IsTrue(changed);
            Assert.AreEqual(2, _store.Saved.Count);
        }

        [TestMethod]
        public void Resend_RemovesFailedEntry_PreservesOrderAndSurvivesRestart()
        {
            var q = NewQueue();
            var first = q.Add("A", "A", "original", "AI");
            var next = q.Add("A", "A", "next", "AI");
            TaskStateMachine.Fail(first, "error", _clock.Now);
            q.Commit();
            _settings.AutoHideResentFailedTasks = false;
            var retry = q.Add("A", "A", "resend #1: corrected instructions", "AI");
            Assert.IsNull(q.Find(first.Id));
            Assert.AreEqual(first.Id, retry.Order);
            CollectionAssert.AreEqual(new[] { first.Id }, retry.Replaces);
            Assert.AreEqual(1, q.Ahead(next));
            CollectionAssert.AreEqual(new[] { retry }, TaskStateMachine.NextToDispatch(q.Items, _clock.Now));
            Assert.IsTrue(_archive.Events.Contains("#1:removed"));
            Assert.IsFalse(_store.Saved.Any(t => t.Id == first.Id));
            Assert.AreSame(retry, q.Add("a", "A", "corrected instructions", "AI"));
            Assert.AreEqual(2, q.Items.Count);

            var restored = new TaskQueue(_settings, new MemoryTaskStore { Initial = _store.Saved }, new RecordingArchive(), _clock.Func);
            Assert.AreEqual(first.Id, restored.Find(retry.Id).Order);
            CollectionAssert.AreEqual(new[] { retry.Id }, TaskStateMachine.NextToDispatch(restored.Items, _clock.Now).Select(t => t.Id).ToArray());
            var path = _data.File("replacement.json");
            var json = new JsonTaskStore(path);
            Assert.IsNull(json.Save(q.Items.ToList()));
            var loaded = json.Load(new System.Collections.Generic.List<string>());
            Assert.AreEqual(first.Id, loaded.Single(t => t.Id == retry.Id).Order);
            CollectionAssert.AreEqual(new[] { first.Id }, loaded.Single(t => t.Id == retry.Id).Replaces);
        }

        [TestMethod]
        public void RepeatedResend_KeepsOriginalPosition()
        {
            var q = NewQueue();
            var first = q.Add("A", "A", "original", "AI");
            q.Add("A", "A", "next", "AI");
            TaskStateMachine.Fail(first, "error", _clock.Now);
            var retry = q.Add("A", "A", "resend #1: corrected", "AI");
            TaskStateMachine.Fail(retry, "error again", _clock.Now);
            var second = q.Add("A", "A", "resend #3: corrected again", "AI");
            Assert.IsNull(q.Find(retry.Id));
            Assert.AreEqual(first.Id, second.Order);
            CollectionAssert.AreEqual(new[] { second }, TaskStateMachine.NextToDispatch(q.Items, _clock.Now));
        }

        [TestMethod]
        public void Load_ReconcilesPreviouslyHiddenDuplicates()
        {
            var failed = T(1, QueueStatus.Failed);
            failed.Text = "the original failed task";
            var resent = T(3, QueueStatus.Waiting);
            resent.Text = failed.Text;
            _store.Initial.AddRange(new[] { failed, T(2, QueueStatus.Waiting), resent });
            var q = NewQueue();
            Assert.IsNull(q.Find(1));
            Assert.AreEqual(1, q.Find(3).Order);
            Assert.IsTrue(_archive.Events.Contains("#1:removed"));
            Assert.IsFalse(_store.Saved.Any(t => t.Id == 1));
        }

        [TestMethod]
        public void HistoryTrimming_DoesNotRemoveFailureBarrier()
        {
            _settings.TaskHistoryLimit = 1;
            var q = NewQueue();
            var failed = q.Add("A", "A", "failed", "AI");
            TaskStateMachine.Fail(failed, "error", _clock.Now);
            for (int i = 0; i < 3; i++)
            {
                var done = q.Add("B", "B", "done " + i, "AI");
                done.Status = QueueStatus.Done;
                done.Finished = _clock.Now.AddMinutes(i + 1);
            }
            var next = q.Add("A", "A", "next", "AI");
            Assert.AreSame(failed, q.Find(failed.Id));
            Assert.AreSame(failed, TaskStateMachine.BlockingTask(q.Items, next));
            Assert.AreEqual(0, TaskStateMachine.NextToDispatch(q.Items, _clock.Now).Count);
        }

        [TestMethod]
        public void StoreException_IsReportedNotThrown()
        {
            var q = new TaskQueue(_settings, new ThrowingStore(), _archive, _clock.Func);
            Assert.IsFalse(q.Save());
            StringAssert.StartsWith(q.SaveError, "UnauthorizedAccessException");
        }

        private sealed class ThrowingStore : ITaskStore
        {
            public System.Collections.Generic.List<QueuedTask> Load(System.Collections.Generic.List<string> problems) => new System.Collections.Generic.List<QueuedTask>();
            public string Save(System.Collections.Generic.IList<QueuedTask> items) => throw new UnauthorizedAccessException("denied");
        }
    }
}
