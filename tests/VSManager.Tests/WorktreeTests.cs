using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class WorktreeTests
    {
        private TempDataFolder _data;
        private string _main;
        private WorktreeService _git;
        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _main = _data.File("Project");
            Directory.CreateDirectory(_main);
            _git = new WorktreeService(() => new[] { _data.Path });
        }
        [TestCleanup]
        public void Cleanup()
        {
            MakeFixtureWritable(_data.Path);
            _data.Dispose();
        }
        internal static void MakeFixtureWritable(string root)
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
        private void InitRepo()
        {
            WorktreeService.Git(_main, "init", "-b", "main");
            Commit(_main, "Project.slnx", "<Solution />");
            Commit(_main, "file.txt", "base\n");
        }
        private static string Head(string root) => WorktreeService.Git(root, "rev-parse", "HEAD");
        private static void Commit(string root, string file, string content)
        {
            File.WriteAllText(Path.Combine(root, file), content);
            WorktreeService.Git(root, "add", "--", file);
            WorktreeService.Git(root, "commit", "-m", "test change");
        }
        private WorktreeInfo Create(string name = "lane") => _git.Create(Path.Combine(_main, "Project.slnx"), name);
        private WorktreeInfo Info => new WorktreeInfo { MainRoot = _main, Root = _data.File("Project.worktree.lane"),
            SolutionPath = _data.File("Project.worktree.lane\\Project.slnx"), MainBranch = "refs/heads/main", Branch = "refs/heads/task/lane" };
        private TaskQueue Queue(ITaskStore store = null, AppSettings settings = null)
        {
            var queue = new TaskQueue(settings ?? new AppSettings(), store ?? new MemoryTaskStore(), new RecordingArchive(), () => DateTime.Now);
            queue.ResolveWorktree = key => Info;
            return queue;
        }
        private static void Complete(TaskQueue queue, QueuedTask task)
        {
            task.Status = QueueStatus.Running;
            Assert.IsTrue(TaskStateMachine.Complete(task, DateTime.Now));
            queue.Commit();
        }

        [TestMethod]
        public void FiveSuccesses_InsertPriorityBarrier_AndKeepDurableLedger()
        {
            var store = new JsonTaskStore(_data.File("tasks.json"));
            var settings = new AppSettings { TaskHistoryLimit = 1 };
            var queue = Queue(store, settings);
            var tasks = Enumerable.Range(0, 11).Select(i => queue.Add("A", "A", "task " + i, "AI")).ToList();
            foreach (var task in tasks.Take(5)) Complete(queue, task);
            var merge = queue.Items.Single(t => t.IsWorktreeMerge);
            Assert.AreEqual(1, merge.WorktreeBatch);
            Assert.AreSame(merge, queue.BlockingTask(tasks[5]));
            Assert.IsNull(queue.BlockingTask(merge));
            Assert.AreSame(merge, queue.NextToDispatch(DateTime.MaxValue).Single());
            Assert.IsFalse(queue.Remove(merge.Id));
            Assert.IsFalse(queue.Remove(tasks[0].Id));
            queue.Commit();
            queue = Queue(store, settings);
            Assert.AreEqual(12, queue.Items.Count);
            Assert.AreEqual(5, queue.Items.Count(t => t.WorktreeCounted));
            Assert.AreEqual(1, queue.Items.Count(t => t.IsWorktreeMerge));
            merge = queue.Items.Single(t => t.IsWorktreeMerge);
            Complete(queue, merge);
            foreach (var task in queue.Items.Where(t => !t.IsWorktreeMerge).Skip(5).Take(5).ToList()) Complete(queue, task);
            Assert.AreEqual(2, queue.Items.Count(t => t.IsWorktreeMerge));
            Assert.AreEqual(10, queue.Items.Count(t => t.WorktreeCounted));
        }

        [TestMethod]
        public void RetryCancellationFailure_AndDuplicateCompletion_DoNotCountTwiceOrBypassMerge()
        {
            var queue = Queue();
            var tasks = Enumerable.Range(0, 6).Select(i => queue.Add("A", "A", "task " + i, "AI")).ToList();
            TaskStateMachine.Fail(tasks[0], "failed", DateTime.Now);
            TaskStateMachine.Cancel(tasks[1], DateTime.Now);
            queue.Commit();
            Assert.AreEqual(0, queue.Items.Count(t => t.WorktreeCounted));
            foreach (var task in tasks.Take(5)) Complete(queue, task);
            Complete(queue, tasks[0]);
            var merge = queue.Items.Single(t => t.IsWorktreeMerge);
            TaskStateMachine.Cancel(merge, DateTime.Now);
            queue.Commit();
            Assert.AreSame(merge, queue.BlockingTask(tasks[5]));
            TaskStateMachine.Fail(merge, "failure", DateTime.Now);
            queue.Commit();
            queue.SkipFailedPredecessors = true;
            Assert.AreSame(merge, queue.BlockingTask(tasks[5]));
            Assert.AreEqual(5, queue.Items.Count(t => t.WorktreeCounted));
            TaskStateMachine.Requeue(merge);
            Assert.AreSame(merge, queue.NextToDispatch(DateTime.MaxValue).Single());
        }

        [TestMethod]
        public void RegistryEdits_PreserveWorkflowMetadataAcrossRestart()
        {
            var registry = new SolutionRegistry(_data.File("solutions.json"));
            registry.Upsert(new SolutionEntry { Alias = "lane", Path = Info.SolutionPath, Worktree = Info });
            registry.Replace(new[] { new SolutionEntry { Alias = "renamed", Path = Info.SolutionPath } });
            var saved = new SolutionRegistry(registry.FilePath).Load().Items.Single();
            Assert.AreEqual(Info.Branch, saved.Worktree.Branch);
            Assert.AreEqual("renamed", saved.Alias);
        }

        [TestMethod]
        public async Task Dispatcher_FiveReceipts_IntegratesBeforeSixthTask()
        {
            var queue = Queue();
            var host = new FakeDispatchHost();
            var vs = host.AddVs("A");
            vs.SolutionPath = Info.SolutionPath;
            var git = new FakeWorktrees();
            var dispatcher = new TaskDispatcher(queue, host, worktrees: git);
            dispatcher.Start();
            var tasks = Enumerable.Range(0, 6).Select(i => queue.Add("A", "A", "task " + i, "AI")).ToList();
            await dispatcher.PumpAsync();
            foreach (var task in tasks.Take(5)) await dispatcher.FinishAsync(task, vs, null);
            Assert.AreEqual(1, git.Integrations);
            Assert.AreEqual(QueueStatus.Done, queue.Items.Single(t => t.IsWorktreeMerge).Status);
            Assert.AreEqual(QueueStatus.Waiting, tasks[5].Status);
            await dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, tasks[5].Status);
            Assert.AreEqual(6, host.Sent.Count);
        }

        [TestMethod]
        public async Task Dispatcher_ConflictReceiptRequiresNativeVerification_AndRetainsBarrierOnFailure()
        {
            var queue = Queue();
            var host = new FakeDispatchHost();
            var vs = host.AddVs("A");
            vs.SolutionPath = Info.SolutionPath;
            var git = new FakeWorktrees { Conflict = true };
            var dispatcher = new TaskDispatcher(queue, host, worktrees: git);
            dispatcher.Start();
            var tasks = Enumerable.Range(0, 6).Select(i => queue.Add("A", "A", "task " + i, "AI")).ToList();
            foreach (var task in tasks.Take(5)) Complete(queue, task);
            var merge = queue.Items.Single(t => t.IsWorktreeMerge);
            await dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, merge.Status);
            StringAssert.Contains(host.Sent.Single(), WorktreeInfo.MergeInstructions);
            await dispatcher.FinishAsync(merge, vs, null);
            Assert.AreEqual(QueueStatus.Failed, merge.Status);
            Assert.AreSame(merge, queue.BlockingTask(tasks[5]));
            git.Conflict = false;
            dispatcher.Retry(merge);
            Assert.AreEqual(QueueStatus.Done, merge.Status);
            Assert.AreEqual(5, queue.Items.Count(t => t.WorktreeCounted));
        }

        [TestMethod]
        public async Task Dispatcher_DirtyDevelopmentAndPersistenceFailure_DoNotSendOrCount()
        {
            var store = new MemoryTaskStore();
            var queue = Queue(store);
            var host = new FakeDispatchHost();
            host.AddVs("A").SolutionPath = Info.SolutionPath;
            var git = new FakeWorktrees { Dirty = true };
            var dispatcher = new TaskDispatcher(queue, host, worktrees: git);
            dispatcher.Start();
            var task = queue.Add("A", "A", "task", "AI");
            await dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Failed, task.Status);
            Assert.IsFalse(task.WorktreeCounted);
            Assert.AreEqual(0, host.Sent.Count);
            git.Dirty = false;
            store.FailWith = "disk full";
            dispatcher.Retry(task);
            Assert.AreEqual(QueueStatus.Failed, task.Status);
            Assert.AreEqual(0, host.Sent.Count);
        }

        [TestMethod]
        public async Task Dispatcher_RejectsWrongVsSolution()
        {
            var queue = Queue();
            var host = new FakeDispatchHost();
            host.AddVs("A").SolutionPath = Path.Combine(_main, "Project.slnx");
            var task = queue.Add("A", "A", "task", "AI");
            var dispatcher = new TaskDispatcher(queue, host, worktrees: new FakeWorktrees());
            dispatcher.Start();
            await dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Waiting, task.Status);
            Assert.AreEqual(0, host.Sent.Count);
        }

        [TestMethod]
        public async Task Git_CreateAndIntegrate_UsesIndependentBranchAndIsIdempotent()
        {
            InitRepo();
            var info = Create();
            Assert.AreEqual(_data.File("Project.worktree.lane"), info.Root);
            Assert.AreEqual("refs/heads/task/lane", WorktreeService.Git(info.Root, "symbolic-ref", "HEAD"));
            string before = Head(_main);
            Commit(info.Root, "file.txt", "task\n");
            Assert.AreEqual(before, Head(_main));
            Assert.IsFalse(await _git.IntegrateAsync(info));
            Assert.AreEqual(Head(info.Root), Head(_main));
            Assert.AreEqual("refs/heads/main", WorktreeService.Git(_main, "symbolic-ref", "HEAD"));
            Assert.IsFalse(await _git.IntegrateAsync(info));
        }

        [TestMethod]
        public async Task Git_ConflictStaysInWorktree_ThenResolvedCommitFastForwardsMain()
        {
            InitRepo();
            var info = Create();
            Commit(info.Root, "file.txt", "task\n");
            Commit(_main, "file.txt", "main\n");
            string before = Head(_main);
            Assert.IsTrue(await _git.IntegrateAsync(info));
            Assert.AreEqual(before, Head(_main));
            Assert.AreEqual("", WorktreeService.Git(_main, "status", "--porcelain"));
            Assert.AreNotEqual("", WorktreeService.Git(info.Root, "ls-files", "-u"));
            Commit(info.Root, "file.txt", "both\n");
            Assert.IsFalse(await _git.IntegrateAsync(info));
            Assert.AreEqual(Head(info.Root), Head(_main));
            Assert.AreEqual("both\n", File.ReadAllText(Path.Combine(_main, "file.txt")));
        }

        [TestMethod]
        public async Task Git_DirtyMainAndBranchSwitch_RefuseWithoutChangingMain()
        {
            InitRepo();
            var info = Create();
            Commit(info.Root, "file.txt", "task\n");
            string before = Head(_main);
            File.WriteAllText(Path.Combine(_main, "unrelated.txt"), "user content");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _git.IntegrateAsync(info));
            Assert.AreEqual(before, Head(_main));
            Assert.AreEqual("user content", File.ReadAllText(Path.Combine(_main, "unrelated.txt")));
            File.Delete(Path.Combine(_main, "unrelated.txt"));
            WorktreeService.Git(_main, "checkout", "-b", "other");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _git.IntegrateAsync(info));
            Assert.AreEqual("refs/heads/other", WorktreeService.Git(_main, "symbolic-ref", "HEAD"));
            Assert.AreEqual(before, Head(_main));
        }

        [TestMethod]
        public async Task Git_DirtyWorktreeIsNeverAutomaticallyCommitted()
        {
            InitRepo();
            var info = Create();
            string before = Head(info.Root);
            File.WriteAllText(Path.Combine(info.Root, "file.txt"), "uncommitted");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _git.CheckDevelopmentAsync(info));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _git.IntegrateAsync(info));
            Assert.AreEqual(before, Head(info.Root));
            Assert.AreEqual(before, Head(_main));
        }

        [TestMethod]
        public async Task Git_TwoWorktreesSerializeIntegration_WithoutLosingEitherChange()
        {
            InitRepo();
            var one = Create("one");
            var two = Create("two");
            Commit(one.Root, "one.txt", "one");
            Commit(two.Root, "two.txt", "two");
            var results = await Task.WhenAll(_git.IntegrateAsync(one), _git.IntegrateAsync(two));
            Assert.IsTrue(results.All(result => !result));
            Assert.AreEqual("one", File.ReadAllText(Path.Combine(_main, "one.txt")));
            Assert.AreEqual("two", File.ReadAllText(Path.Combine(_main, "two.txt")));
        }

        [TestMethod]
        public void Git_RejectsUnsafeNamesOutsideGrantsAndExecutableFilters()
        {
            InitRepo();
            foreach (var name in new[] { "..", "a/b", "a\\b", "--force", "a\";bad" })
                Assert.ThrowsException<InvalidOperationException>(() => Create(name));
            var limited = new WorktreeService(() => new[] { _main });
            Assert.ThrowsException<AgentFileDeniedException>(() => limited.Create(Path.Combine(_main, "Project.slnx"), "lane"));
            WorktreeService.Git(_main, "config", "filter.unsafe.clean", "some-command");
            Assert.ThrowsException<InvalidOperationException>(() => Create());
            Assert.IsFalse(Directory.Exists(Info.Root));
        }

        [TestMethod]
        public async Task Git_IgnoredUserFileIsNotOverwrittenByFastForward()
        {
            InitRepo();
            var info = Create();
            Commit(info.Root, "output.txt", "task output");
            File.AppendAllText(Path.Combine(_main, ".git", "info", "exclude"), "\noutput.txt\n");
            File.WriteAllText(Path.Combine(_main, "output.txt"), "user output");
            string before = Head(_main);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _git.IntegrateAsync(info));
            Assert.AreEqual("user output", File.ReadAllText(Path.Combine(_main, "output.txt")));
            Assert.AreEqual(before, Head(_main));
        }

        [TestMethod]
        public async Task Git_PathsWithSpacesArePassedAsSingleArguments()
        {
            _main = _data.File("Project with spaces");
            Directory.CreateDirectory(_main);
            InitRepo();
            var info = Create();
            Assert.AreEqual(_data.File("Project with spaces.worktree.lane"), info.Root);
            Commit(info.Root, "file.txt", "task\n");
            Assert.IsFalse(await _git.IntegrateAsync(info));
        }

        [TestMethod]
        public void BatchesAreIndependent_AndStrictDevelopmentFailurePolicyIsRetained()
        {
            var queue = Queue();
            var other = Info;
            other.Root += "-other";
            queue.ResolveWorktree = key => key == "A" ? Info : other;
            var a = Enumerable.Range(0, 6).Select(i => queue.Add("A", "A", "task " + i, "AI")).ToList();
            foreach (var task in a.Take(5)) Complete(queue, task);
            var b = queue.Add("B", "B", "other task", "AI");
            Assert.IsNull(queue.BlockingTask(b));
            Assert.IsTrue(queue.BlockingTask(a[5]).IsWorktreeMerge);
            TaskStateMachine.Fail(b, "failed", DateTime.Now);
            var next = queue.Add("B", "B", "next", "AI");
            queue.SkipFailedPredecessors = false;
            Assert.AreSame(b, queue.BlockingTask(next));
            queue.SkipFailedPredecessors = true;
            Assert.IsNull(queue.BlockingTask(next));
        }

        private sealed class FakeWorktrees : IWorktreeTaskService
        {
            internal bool Dirty, Conflict;
            internal int Integrations;
            public Task CheckDevelopmentAsync(WorktreeInfo info)
            {
                if (Dirty) throw new InvalidOperationException("dirty");
                return Task.CompletedTask;
            }
            public Task<bool> IntegrateAsync(WorktreeInfo info)
            {
                Integrations++;
                return Task.FromResult(Conflict);
            }
        }
    }
}
