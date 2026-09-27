using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class VsMentionTests
    {
        private static VsInstance Vs(int pid = 1) => new VsInstance
        {
            Pid = pid, StartTicks = 100 + pid, Key = "shared-solution", SolutionPath = "shared.sln",
            Title = "重复名称 / Duplicate name", Copilot = CopilotState.Idle
        };
        private static VsMentionTarget Target(VsInstance vs, int number = 1) => new VsMentionTarget(vs, number, vs.Title, "负责中文搜索 / Chinese search");

        [DataTestMethod]
        [DataRow("plain task")]
        [DataRow("邮件 user@example.com")]
        [DataRow("npm install @scope/package")]
        [DataRow("var @class = 1;")]
        [DataRow("`@identifier` and ```\r\n@code\r\n```")]
        [DataRow("@@literal")]
        [DataRow("folder\\@file")]
        [DataRow("@")]
        [DataRow("@ ")]
        [DataRow("渲染一下 @后的菜单栏")]
        [DataRow("@不存在 task")]
        [DataRow("请交给@不存在处理")]
        public void NonMention_IsUnchanged(string text)
        {
            var result = new VsMentionSession().Resolve(text, new[] { Vs() });
            Assert.IsFalse(result.HasMention);
            Assert.AreEqual(text, result.Body);
        }

        [DataTestMethod]
        [DataRow("@[#1 unknown|fake] task")]
        [DataRow("@[unfinished")]
        public void UnconfirmedOrUnknown_RejectsWholeMessage(string text)
        {
            var result = new VsMentionSession().Resolve(text, new[] { Vs() });
            Assert.IsTrue(result.HasMention);
            Assert.IsFalse(result.Valid);
            Assert.IsNotNull(result.Error);
        }

        [TestMethod]
        public void Filter_NumberNameResponsibilityAndDuplicateNames()
        {
            var list = new[] { Target(Vs(1), 1), Target(Vs(2), 2) };
            Assert.AreEqual(2, VsMentionSession.Filter(list, "").Length);
            Assert.AreEqual(2, VsMentionSession.Filter(list, "重复").Length);
            Assert.AreEqual(2, VsMentionSession.Filter(list, "中文").Length);
            Assert.AreSame(list[1], VsMentionSession.Filter(list, "#2").Single());
            Assert.AreEqual(0, VsMentionSession.Filter(list, "missing").Length);
        }

        [TestMethod]
        public void Selection_DeduplicatesSameTargetAndRejectsDifferentTargets()
        {
            var session = new VsMentionSession();
            var a = Vs(1); var b = Vs(2); var live = new[] { a, b };
            string token = session.Select(Target(b, 2));
            var result = session.Resolve(token + " 修复编译 / Fix build " + session.Select(Target(b, 9)), live);
            Assert.IsTrue(result.Valid);
            Assert.AreEqual(b.InstanceKey, result.Target.InstanceKey);
            Assert.AreEqual("修复编译 / Fix build", result.Body);
            result = session.Resolve(token + " task " + session.Select(Target(a)), live);
            Assert.IsFalse(result.Valid);
            StringAssert.Contains(result.Error, "nothing sent");
            result = session.Resolve(token + " task @bad", live);
            Assert.IsTrue(result.Valid);
            Assert.AreEqual("task @bad", result.Body);
        }

        [TestMethod]
        public void EditingCopyDraftRestoreAndRenumberNeverRebind()
        {
            var session = new VsMentionSession();
            var a = Vs(1); var b = Vs(2);
            string draft = session.Select(Target(b, 2)) + " task";
            Assert.IsTrue(session.Resolve(draft, new[] { a, b }).Valid);
            session.Resolve("", new[] { a, b });
            Assert.AreEqual(b.InstanceKey, session.Resolve(draft, new[] { b, a }).Target.InstanceKey);
            Assert.IsFalse(session.Resolve(draft.Replace("#2", "#1"), new[] { a, b }).Valid);
            Assert.IsFalse(new VsMentionSession().Resolve(draft, new[] { a, b }).Valid);
            Assert.IsFalse(session.Resolve("task", new[] { a, b }).HasMention);
        }

        [DataTestMethod]
        [DataRow("closed")]
        [DataRow("restart")]
        [DataRow("solution")]
        public void StaleInstanceIsRejected(string change)
        {
            var session = new VsMentionSession(); var a = Vs();
            string text = session.Select(Target(a)) + " task";
            if (change == "restart") a.StartTicks++;
            if (change == "solution") a.SolutionPath = "other.sln";
            var result = session.Resolve(text, change == "closed" ? new VsInstance[0] : new[] { a });
            Assert.AreEqual(VsMentionSession.MissingError, result.Error);
            Assert.AreEqual(SendDecision.Fail, SendRetryPolicy.Decide(1, result.Error));
        }

        [TestMethod]
        public void BodyRequiredExceptForAttachments()
        {
            var session = new VsMentionSession(); var a = Vs(); string token = session.Select(Target(a));
            Assert.IsFalse(session.Resolve(token, new[] { a }).Valid);
            Assert.IsTrue(session.Resolve(token, new[] { a }, true).Valid);
            Assert.IsFalse(session.Resolve("@bad", new[] { a }, true).HasMention);
            Assert.IsFalse(VsMentionSession.HasIntent("@"));
            Assert.IsTrue(VsMentionSession.HasIntent(token + " x"));
        }

        [TestMethod]
        public void QueueAdmission_IsAtomicAndExactEvenForSameSolution()
        {
            using (var data = new TempDataFolder())
            {
                var store = new MemoryTaskStore { FailWith = "写入失败 / Write failed" };
                var clock = new FakeClock();
                var queue = new TaskQueue(new AppSettings(), store, new RecordingArchive(), clock.Func);
                var a = Vs(); var b = Vs(2); var snapshot = Target(b, 2);
                var files = new[] { new AttachmentRef { Name = "image.png", Sha256 = "hash", Kind = AttachmentKind.Image } };
                Assert.IsFalse(queue.AddMention(b, snapshot, "task", files).Accepted);
                Assert.AreEqual(0, queue.Items.Count);
                Assert.AreEqual(1, queue.NextId);
                store.FailWith = null;
                clock.Advance(TimeSpan.FromSeconds(11));
                queue.RetrySaveIfNeeded();
                Assert.IsNull(queue.SaveError);
                Assert.AreEqual(0, queue.Items.Count);
                var accepted = queue.AddMention(b, snapshot, "task", files);
                Assert.IsTrue(accepted.Accepted);
                Assert.AreEqual("用户", accepted.Task.Source);
                Assert.IsFalse(accepted.Task.FromAgent);
                Assert.AreEqual(b.InstanceKey, store.Saved.Single().ExplicitInstanceKey);
                Assert.IsTrue(store.Saved.Single().HasAttachments);
                Assert.AreSame(accepted.Task, queue.AddMention(b, snapshot, "task", files).Task);
                Assert.AreEqual(1, queue.Items.Count);
                Assert.IsFalse(queue.AddMention(a, snapshot, "task").Accepted);
                Assert.IsTrue(queue.AddMention(a, Target(a), "task", files).Accepted);
                Assert.AreEqual(2, queue.Items.Count);
                Assert.AreEqual(b.InstanceKey, queue.Items[0].ExplicitInstanceKey);
            }
        }

        [TestMethod]
        public void MentionAndLegacyTasksRetainSharedQueueBarriers()
        {
            var legacy = new QueuedTask { Id = 1, VsKey = "shared.sln", Status = QueueStatus.Waiting };
            var mentioned = new QueuedTask { Id = 2, VsKey = legacy.VsKey, Status = QueueStatus.Waiting,
                ExplicitInstanceKey = Vs().InstanceKey, ExplicitSolutionPath = legacy.VsKey };
            Assert.AreSame(legacy, TaskStateMachine.BlockingTask(new[] { legacy, mentioned }, mentioned));
            legacy.Status = QueueStatus.Failed;
            Assert.AreSame(legacy, TaskStateMachine.BlockingTask(new[] { legacy, mentioned }, mentioned, false));
            Assert.IsNull(TaskStateMachine.BlockingTask(new[] { legacy, mentioned }, mentioned, true));
            legacy.Status = QueueStatus.Waiting;
            mentioned.Id = 0; mentioned.Status = QueueStatus.Running;
            Assert.AreSame(mentioned, TaskStateMachine.BlockingTask(new[] { legacy, mentioned }, legacy));
        }

        [TestMethod]
        public void Persistence_RoundTripsPinnedIdentityAndAttachments()
        {
            using (var data = new TempDataFolder())
            {
                var store = new JsonTaskStore(data.File("mentions.json"));
                var queue = new TaskQueue(new AppSettings(), store, new RecordingArchive(), null);
                var vs = Vs();
                var files = new[] { new AttachmentRef { Id = "test", Name = "image.png", RelPath = "images\\test.png",
                    Kind = AttachmentKind.Image, Ext = ".png", Sha256 = "hash", Size = 42, Created = DateTime.Now } };
                Assert.IsTrue(queue.AddMention(vs, Target(vs), "task", files).Accepted);
                var loaded = store.Load(new List<string>()).Single();
                Assert.IsTrue(loaded.MatchesExplicitTarget(vs));
                Assert.IsTrue(loaded.Clone().MatchesExplicitTarget(vs));
                Assert.AreEqual("images\\test.png", loaded.Attachments.Single().RelPath);
                Assert.AreEqual(42L, loaded.Attachments.Single().Size);
                vs.StartTicks++;
                Assert.IsFalse(loaded.MatchesExplicitTarget(vs));
            }
        }

        private sealed class Host : ITaskDispatchHost, IExplicitTaskDispatchHost, IManualChatDispatchHost
        {
            internal List<VsInstance> Live = new List<VsInstance> { Vs(1), Vs(2) };
            internal List<int> Sent = new List<int>();
            internal List<string> Announced = new List<string>();
            internal List<string> Statuses = new List<string>();
            internal List<int> Read = new List<int>();
            internal Action<QueuedTask, string, string> Announcement;
            internal Func<Task<ManualChatObservation>> Observe;
            internal Action BeforeSend;
            public VsInstance FindVs(string key) => Live.FirstOrDefault(v => v.Key == key);
            public VsInstance FindTargetVs(QueuedTask task) => FindVs(task.VsKey);
            public VsInstance FindExplicitTarget(QueuedTask task) => Live.FirstOrDefault(task.MatchesExplicitTarget);
            public bool CanDispatch(VsInstance v) => true;
            public string NameOf(VsInstance v) => v.Title;
            public bool IsSending => false;
            public DateTime TrackingReadyAt => DateTime.MinValue;
            public TimeSpan TargetSettleDelay => TimeSpan.Zero;
            public Task<string> SendAsync(VsInstance v, string text) { Sent.Add(v.Pid); return Task.FromResult("已发送"); }
            public Task<string> ReadAnswerAsync(VsInstance v, QueuedTask t)
            {
                Read.Add(v.Pid);
                return Task.FromResult("完成 / Done\r\n" + TaskStateMachine.SuccessReceipt(t));
            }
            public void SetStatus(string text) => Statuses.Add(text);
            public void LogEvent(string name, string text) { }
            public void NotifyAgent(string title, string body) { }
            public void QueueActivityChanged(bool active) { }
            public void AnnounceTask(QueuedTask task, string zh, string en)
            {
                Announced.Add(zh + en);
                Announcement?.Invoke(task, zh, en);
            }
            public Task<ManualChatObservation> ObserveManualChatAsync(VsInstance target) => Observe?.Invoke() ?? Task.FromResult(ManualChatObservation.Idle);
            public Task<string> SendQueuedAsync(VsInstance target, QueuedTask task, Func<bool> guard)
            {
                BeforeSend?.Invoke();
                return guard() ? SendAsync(target, task.Text) : Task.FromResult(ManualChatProtection.WaitPrefix);
            }
        }

        [TestMethod]
        public async Task Dispatch_StartGateExactInstanceCompletionAndNoNamesakeFallback()
        {
            using (var data = new TempDataFolder())
            {
                var queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), null);
                var host = new Host(); var target = host.Live[1];
                var dispatcher = new TaskDispatcher(queue, host);
                var t = queue.AddMention(target, Target(target, 2), "task").Task;
                dispatcher.AcceptQueued(t, "用户");
                await dispatcher.PumpAsync();
                Assert.AreEqual(0, host.Sent.Count);
                dispatcher.Start();
                await dispatcher.PumpAsync();
                CollectionAssert.AreEqual(new[] { 2 }, host.Sent);
                Assert.AreEqual(QueueStatus.Running, t.Status);
                await dispatcher.FinishAsync(t, host.Live[0], null);
                Assert.AreEqual(QueueStatus.Running, t.Status);
                await dispatcher.FinishAsync(t, target, null);
                Assert.AreEqual(QueueStatus.Done, t.Status);
                Assert.IsTrue(host.Statuses.Any(s => s.Contains("delivered")));
                var next = queue.AddMention(target, Target(target, 2), "next").Task;
                host.Live.Remove(target);
                await dispatcher.PumpAsync();
                Assert.AreEqual(QueueStatus.Failed, next.Status);
                Assert.AreEqual(1, host.Sent.Count);
            }
        }

        [TestMethod]
        public async Task Dispatch_TargetChangesDuringAwaitAndImmediatelyBeforeSend()
        {
            using (var data = new TempDataFolder())
            {
                foreach (bool beforeSend in new[] { false, true })
                {
                    var queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), null);
                    var host = new Host(); var v = host.Live[1];
                    var t = queue.AddMention(v, Target(v), "task").Task;
                    var dispatcher = new TaskDispatcher(queue, host);
                    if (beforeSend) host.BeforeSend = () => v.StartTicks++;
                    else host.Observe = () => { v.SolutionPath = "changed.sln"; return Task.FromResult(ManualChatObservation.Idle); };
                    dispatcher.Start();
                    await dispatcher.PumpAsync();
                    Assert.AreEqual(0, host.Sent.Count);
                    Assert.AreNotEqual(QueueStatus.Running, t.Status);
                }
            }
        }

        [TestMethod]
        public void MainHost_LookupUsesSessionNotSolutionOrListOrder()
        {
            var host = (MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));
            var a = Vs(1); var b = Vs(2);
            typeof(MainForm).GetField("_instances", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(host, new List<VsInstance> { a, b });
            var t = new QueuedTask { VsKey = a.Key, ExplicitInstanceKey = b.InstanceKey, ExplicitSolutionPath = b.SolutionPath };
            Assert.AreSame(b, ((IExplicitTaskDispatchHost)host).FindExplicitTarget(t));
            Assert.AreSame(b, ((ITaskDispatchHost)host).FindTargetVs(t));
            t.Worktree = new WorktreeInfo { SolutionPath = a.SolutionPath };
            Assert.AreSame(b, ((ITaskDispatchHost)host).FindTargetVs(t));
            b.StartTicks++;
            Assert.IsNull(((IExplicitTaskDispatchHost)host).FindExplicitTarget(t));
            Assert.IsNull(((ITaskDispatchHost)host).FindTargetVs(t));
            t.ExplicitInstanceKey = null; t.ExplicitSolutionPath = null;
            Assert.AreSame(a, ((ITaskDispatchHost)host).FindTargetVs(t));
            t.Worktree = null;
            Assert.AreSame(a, ((ITaskDispatchHost)host).FindTargetVs(t));
        }

        [DataTestMethod]
        [DataRow(QueueStatus.Waiting)]
        [DataRow(QueueStatus.Running)]
        [DataRow(QueueStatus.Done)]
        [DataRow(QueueStatus.Failed)]
        [DataRow(QueueStatus.WaitingVs)]
        public void MainTaskOpen_UsesExactTargetAndNeverLaunchesReplacement(string taskStatus)
        {
            Sta(() =>
            {
                using (var sidebar = new VsListBox())
                using (var status = new Label())
                {
                    var main = (MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));
                    var a = Vs(1); var b = Vs(2);
                    var live = new List<VsInstance> { a, b };
                    void Set(string name, object value) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(main, value);
                    Set("_instances", live); Set("_list", sidebar); Set("_status", status);
                    sidebar.Items.AddRange(new object[] { a, b });
                    var task = new QueuedTask { VsKey = a.Key, Status = taskStatus, VsName = "目标 B / Target B",
                        ExplicitInstanceKey = b.InstanceKey, ExplicitSolutionPath = b.SolutionPath,
                        Worktree = new WorktreeInfo { SolutionPath = a.SolutionPath } };
                    var open = typeof(MainForm).GetMethod("OnTaskAction", BindingFlags.Instance | BindingFlags.NonPublic);
                    open.Invoke(main, new object[] { task, "open" });
                    Assert.AreSame(b, sidebar.SelectedItem);
                    sidebar.SelectedIndex = -1;
                    live.Remove(b);
                    open.Invoke(main, new object[] { task, "open" });
                    Assert.IsNull(sidebar.SelectedItem);
                    Assert.IsFalse(string.IsNullOrEmpty(status.Text));
                }
            });
        }

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(true, true)]
        [DataRow(false, false)]
        [DataRow(false, true)]
        public void MainCompletion_AutomaticOrYieldedTargetsExactInstanceWithoutFallback(bool automatic, bool removed)
        {
            Sta(() =>
            {
                using (var data = new TempDataFolder())
                using (var sidebar = new VsListBox())
                using (var header = new Panel())
                using (var status = new Label())
                {
                    var fake = new Host();
                    var a = fake.Live[0]; var b = fake.Live[1];
                    var settings = new AppSettings { AutoStartAllTasks = automatic, PendingVsNotify = false, Sound = false };
                    settings.SetAlias(a.InstanceKey, "实例 A / Instance A");
                    settings.SetAlias(b.InstanceKey, "实例 B / Instance B");
                    var queue = new TaskQueue(settings, new MemoryTaskStore(), new RecordingArchive(), null);
                    var first = queue.AddMention(a, Target(a), "first").Task;
                    var second = queue.AddMention(b, Target(b, 2), "second").Task;
                    foreach (var task in queue.Items)
                    {
                        TaskStateMachine.BeginSend(task, task.VsName);
                        TaskStateMachine.ApplySendResult(task, "已发送", DateTime.Now);
                    }
                    var dispatcher = new TaskDispatcher(queue, fake, startSettings: () => settings);
                    var main = (MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));
                    void Set(string name, object value) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(main, value);
                    Set("_instances", fake.Live); Set("_settings", settings); Set("_tasks", queue); Set("_dispatcher", dispatcher);
                    Set("_externals", new List<ExternalChat>()); Set("_list", sidebar); Set("_header", header); Set("_status", status);
                    sidebar.Items.AddRange(new object[] { a, b });
                    int completions = 0;
                    fake.Announcement = (task, zh, en) =>
                    {
                        if (task.Status == QueueStatus.Done)
                        {
                            completions++;
                            if (removed) fake.Live.Remove(b);
                            Assert.AreSame(removed ? null : b, ((ITaskDispatchHost)main).FindTargetVs(task));
                        }
                        ((ITaskDispatchHost)main).AnnounceTask(task, zh, en);
                    };
                    if (automatic) dispatcher.AcceptQueued(second, "用户");
                    else
                    {
                        dispatcher.Start();
                        Field<HashSet<QueuedTask>>(dispatcher, "_yielded").Add(second);
                    }
                    typeof(MainForm).GetMethod("OnCopilotCompleted", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(main, new object[] { b, TimeSpan.FromSeconds(1) });
                    Assert.AreEqual(QueueStatus.Running, first.Status);
                    Assert.AreEqual(QueueStatus.Done, second.Status);
                    CollectionAssert.AreEqual(new[] { b.Pid }, fake.Read);
                    Assert.AreEqual(1, completions);
                    Assert.IsFalse(a.CompletionUnseen);
                    Assert.AreEqual(!removed, b.CompletionUnseen);
                    Assert.IsFalse(status.Text.Contains("Instance A"));
                    if (!removed) StringAssert.Contains(status.Text, "Instance B");
                }
            });
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task QueueAdmission_PrecommitFailureNeverRestoresOrDispatchesRejectedTask(bool backupFailure)
        {
            using (var data = new TempDataFolder())
            {
                var fs = new MemoryAtomicFileSystem { FailReplace = true, FailBackup = backupFailure, FailCopy = !backupFailure };
                const string path = @"store\tasks.json";
                fs.Put(path, "[]");
                var settings = new AppSettings { AutoStartAllTasks = true };
                var store = new JsonTaskStore(path, fs);
                var queue = new TaskQueue(settings, store, new RecordingArchive(), null);
                var target = Vs(2);
                Assert.IsFalse(queue.AddMention(target, Target(target), "rejected").Accepted);
                Assert.AreEqual(0, queue.Items.Count);
                Assert.AreEqual(1, queue.NextId);
                var restarted = new TaskQueue(settings, store, new RecordingArchive(), null);
                var host = new Host();
                var dispatcher = new TaskDispatcher(restarted, host, startSettings: () => settings);
                dispatcher.ApplyAutomaticStart();
                await dispatcher.PumpAsync();
                Assert.AreEqual(0, restarted.Items.Count);
                Assert.AreEqual(0, host.Sent.Count);
                Assert.AreEqual("[]", fs.Text(path));
            }
        }

        public sealed class SyntheticSolution { public string FullName { get; set; } }
        public sealed class SyntheticDte { public SyntheticSolution Solution { get; set; } }

        [TestMethod]
        public void LiveBoundary_RejectsReusedProcessOrChangedDteSolution()
        {
            Sta(() =>
            {
                using (var window = new QuietForm())
                using (var process = Process.GetCurrentProcess())
                {
                    var target = new VsInstance { Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks,
                        MainHwnd = window.Handle, SolutionPath = "chosen.sln",
                        Dte = new SyntheticDte { Solution = new SyntheticSolution { FullName = "chosen.sln" } } };
                    var method = typeof(MainForm).GetMethod("MentionTargetStillLive", BindingFlags.Static | BindingFlags.NonPublic);
                    bool Check() => (bool)method.Invoke(null, new object[] { target, "chosen.sln", true });
                    Assert.IsTrue(Check());
                    ((SyntheticDte)target.Dte).Solution.FullName = "other.sln";
                    Assert.IsFalse(Check());
                    ((SyntheticDte)target.Dte).Solution.FullName = "chosen.sln";
                    target.StartTicks++;
                    Assert.IsFalse(Check());
                    target.StartTicks--;
                    target.Dte = null;
                    Assert.IsFalse(Check());
                    target.MainHwnd = IntPtr.Zero;
                    Assert.IsFalse(Check());
                }
            });
        }

        private sealed class QuietForm : Form { protected override bool ShowWithoutActivation => true; }
        private static void Sta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                string previous = TranscriptView.UserDataFolder;
                TranscriptView.UserDataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MentionBrowser");
                try { action(); }
                catch (Exception ex) { error = ex; }
                finally { TranscriptView.UserDataFolder = previous; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(25)), "界面测试超时 / UI test timeout");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
        private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
        private static void Key(object panel, Keys key) => panel.GetType().GetMethod("Input_KeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(panel, new object[] { null, new KeyEventArgs(key) });
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [TestMethod]
        public void MentionPopup_PeriodicRefreshKeepsUnchangedItems_NoFlicker()
        {
            Sta(() =>
            {
                using (var form = new QuietForm { Width = 950, Height = 650, ShowInTaskbar = false })
                using (var panel = new ChatPanel { Dock = DockStyle.Fill })
                {
                    Field<TranscriptView>(panel, "_transcript").Visible = false;
                    var session = new VsMentionSession(); var vs = new[] { Vs(1), Vs(2) };
                    panel.BindMentions(session, () => vs.Select((v, i) => Target(v, i + 1)).ToArray());
                    form.Controls.Add(panel); form.Show();
                    var input = Field<TextBox>(panel, "_input"); input.Focus();
                    input.SelectedText = "@";
                    var mentions = Field<VsMentionInput>(panel, "_mentions");
                    var list = Field<ListBox>(mentions, "_list");
                    Key(panel, Keys.Down);
                    var first = list.Items[0];
                    var bounds = Field<Form>(mentions, "_popup").Bounds;
                    int selectionChanges = 0;
                    list.SelectedIndexChanged += (s, e) => selectionChanges++;
                    for (int i = 0; i < 3; i++) { mentions.Refresh(); panel.RefreshMentions(); }
                    Assert.IsTrue(mentions.IsOpen);
                    Assert.AreSame(first, list.Items[0], "候选未变时不重建 / Unchanged candidates are not rebuilt");
                    Assert.AreEqual(1, list.SelectedIndex);
                    Assert.AreEqual(0, selectionChanges);
                    Assert.AreEqual(bounds, Field<Form>(mentions, "_popup").Bounds);
                    form.Close();
                }
            });
        }
        [TestMethod]
        public void ChatUi_AtArrowsEnterEscapeMouseAndNoMatchKeepDraft()
        {
            Sta(() =>
            {
                using (var form = new QuietForm { Width = 950, Height = 650, ShowInTaskbar = false })
                using (var panel = new ChatPanel { Dock = DockStyle.Fill })
                {
                    Field<TranscriptView>(panel, "_transcript").Visible = false;
                    var session = new VsMentionSession(); var vs = new[] { Vs(1), Vs(2) };
                    panel.BindMentions(session, () => vs.Select((v, i) => Target(v, i + 1)).ToArray());
                    form.Controls.Add(panel); form.Show();
                    var input = Field<TextBox>(panel, "_input"); input.Focus();
                    int sends = 0, cancellations = 0;
                    panel.SendRequested += _ => sends++;
                    panel.VoiceCancel += () => cancellations++;
                    input.SelectedText = "@";
                    var mentions = Field<VsMentionInput>(panel, "_mentions");
                    Assert.IsTrue(mentions.IsOpen);
                    Assert.AreEqual(2, mentions.CandidateCount);
                    Key(panel, Keys.Down); Key(panel, Keys.Enter);
                    Assert.AreEqual(0, sends);
                    Assert.IsFalse(mentions.IsOpen);
                    Key(panel, Keys.Enter);
                    Assert.AreEqual(0, sends);
                    typeof(Control).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(input, new object[] { new KeyEventArgs(Keys.Enter) });
                    Assert.AreEqual(vs[1].InstanceKey, session.Resolve(input.Text + "task", vs).Target.InstanceKey);
                    input.Text = "@"; input.SelectionStart = 1; mentions.Refresh();
                    Key(panel, Keys.Escape);
                    Assert.AreEqual("@", input.Text);
                    Assert.IsFalse(mentions.IsOpen);
                    panel.RefreshMentions();
                    Assert.IsFalse(mentions.IsOpen);
                    Assert.AreEqual(0, cancellations);
                    input.Text = "@不存在"; input.SelectionStart = input.TextLength; mentions.Refresh();
                    Assert.AreEqual(0, mentions.CandidateCount);
                    Assert.IsFalse(mentions.IsOpen);
                    Key(panel, Keys.Enter);
                    Assert.AreEqual("@不存在", input.Text);
                    Assert.AreEqual(0, sends);
                    input.Text = "@"; input.SelectionStart = 1; mentions.Refresh();
                    var list = Field<ListBox>(mentions, "_list");
                    SendMessage(list.Handle, 0x0201, new IntPtr(1), new IntPtr((4 << 16) | 4));
                    Assert.IsFalse(mentions.IsOpen);
                    Assert.IsTrue(input.Focused);
                    Assert.IsTrue(input.Text.StartsWith("@[#1", StringComparison.Ordinal));
                    input.Text = "@"; input.SelectionStart = 1; mentions.Refresh();
                    form.Width--;
                    Assert.IsFalse(mentions.IsOpen);
                    mentions.Refresh();
                    panel.Visible = false;
                    Assert.IsFalse(mentions.IsOpen);
                    form.Close();
                }
            });
        }

        [TestMethod]
        public void ChatUi_ConfirmedMentionRendersAsChip_AndDeletesAsWhole()
        {
            Sta(() =>
            {
                using (var form = new QuietForm { Width = 950, Height = 650, ShowInTaskbar = false })
                using (var panel = new ChatPanel { Dock = DockStyle.Fill })
                {
                    Field<TranscriptView>(panel, "_transcript").Visible = false;
                    var session = new VsMentionSession(); var vs = new[] { Vs(1) };
                    panel.BindMentions(session, () => vs.Select((v, i) => Target(v, i + 1)).ToArray());
                    form.Controls.Add(panel); form.Show();
                    var input = Field<TextBox>(panel, "_input"); input.Focus();
                    string token = session.Select(Target(vs[0], 1));
                    StringAssert.Matches(token, new System.Text.RegularExpressions.Regex(@"^@\[#1 .+\|[0-9a-f]{6}\]$"));
                    Assert.AreEqual(0, session.Chips("@[#1 forged|abcdef] x").Count());
                    input.Text = "前 " + token + " task";
                    var chip = session.Chips(input.Text).Single();
                    Assert.AreEqual(2, chip.Start);
                    Assert.AreEqual(token.Length, chip.Length);
                    Assert.IsFalse(chip.Label.Contains("|"));
                    StringAssert.StartsWith(chip.Label, "@#1 ");
                    input.Refresh();
                    input.Select(2 + token.Length, 0);
                    Key(panel, Keys.Back);
                    Assert.AreEqual("前  task", input.Text);
                    input.Text = token + " x"; input.Select(0, 0);
                    Key(panel, Keys.Delete);
                    Assert.AreEqual(" x", input.Text);
                    input.Text = token + " x"; input.Select(3, 0);
                    typeof(Control).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(input, new object[] { new KeyEventArgs(Keys.Right) });
                    Assert.AreEqual(token.Length, input.SelectionStart);
                    form.Close();
                }
            });
        }

        [TestMethod]
        public void AgentUi_ExplicitSendWorksWithoutModelAndFailureKeepsAttachments()
        {
            Sta(() =>
            {
                using (var panel = new AgentPanel())
                {
                    var session = new VsMentionSession(); var v = Vs();
                    panel.BindMentions(session, () => new[] { Target(v) });
                    var input = Field<TextBox>(panel, "_input");
                    var pending = Field<List<AttachmentRef>>(panel, "_pending");
                    pending.Add(new AttachmentRef { Name = "sample.txt", Kind = AttachmentKind.Text });
                    string text = session.Select(Target(v)) + " task";
                    input.Text = text;
                    int calls = 0;
                    panel.MentionRequested = (body, files) =>
                    {
                        calls++; Assert.AreEqual(text, body); Assert.AreEqual(1, files.Length);
                        return new MentionSubmission(null, "保存失败 / Save failed");
                    };
                    Key(panel, Keys.Enter);
                    Assert.AreEqual(1, calls); Assert.AreEqual(text, input.Text); Assert.AreEqual(1, pending.Count);
                    panel.MentionRequested = (body, files) => new MentionSubmission(new QueuedTask(), "已接纳 / Accepted");
                    Key(panel, Keys.Enter);
                    Assert.AreEqual("", input.Text); Assert.AreEqual(0, pending.Count);
                }
            });
        }

                [TestMethod]
                public void ChatMainIntegration_MentionImagesPersistBeforeClearingSelectedOtherDraft()
                {
                    Sta(() =>
                    {
                        using (var data = new TempDataFolder())
                        using (var window = new QuietForm())
                        using (var panel = new ChatPanel())
                        using (var taskPanel = new TaskPanel())
                        using (var sidebar = new VsListBox())
                        using (var status = new Label())
                        using (var timer = new System.Windows.Forms.Timer())
                        using (var bitmap = new Bitmap(2, 2))
                        using (var process = Process.GetCurrentProcess())
                        {
                            var target = Vs(2);
                            target.Pid = process.Id; target.StartTicks = process.StartTime.ToUniversalTime().Ticks;
                            target.MainHwnd = window.Handle;
                            var selected = Vs(1);
                            var instances = new List<VsInstance> { selected, target };
                            sidebar.Items.AddRange(instances.Cast<object>().ToArray()); sidebar.SelectedIndex = 0;
                            var settings = new AppSettings();
                            var fs = new MemoryAtomicFileSystem { FailReplace = true, FailCopy = true, FailDelete = true };
                            string taskPath = data.File("mentions.json");
                            fs.Put(taskPath, "[]");
                            var store = new JsonTaskStore(taskPath, fs);
                            var queue = new TaskQueue(settings, store, new RecordingArchive(), null);
                            var dispatcher = new TaskDispatcher(queue, new Host(), startSettings: () => settings);
                            var session = new VsMentionSession();
                            var host = (MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));
                            void Set(string name, object value) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(host, value);
                            Set("_instances", instances); Set("_settings", settings); Set("_mentionSession", session);
                            Set("_chatTitles", new System.Collections.Concurrent.ConcurrentDictionary<int, string>());
                            Set("_chat", panel); Set("_list", sidebar); Set("_tasks", queue); Set("_dispatcher", dispatcher);
                            Set("_taskPanel", taskPanel); Set("_status", status); Set("_taskTimer", timer);
                            var drafts = new Dictionary<int, string>(); var imageDrafts = new Dictionary<int, IReadOnlyList<ChatImage>>();
                            Set("_drafts", drafts); Set("_imageDrafts", imageDrafts);
                            taskPanel.Bind(queue);
                            var send = typeof(MainForm).GetMethod("OnChatSend", BindingFlags.Instance | BindingFlags.NonPublic);
                            panel.SendRequested += text => send.Invoke(host, new object[] { text });
                            panel.SetTarget(selected.Title, "");
                            panel.SetState("忙碌 / Busy", Color.White, Color.Black, Color.Red, true);
                            panel.InputText = session.Select(Target(target, 2)) + " image task";
                            string original = panel.InputText;
                            panel.Images = new[] { ChatImage.FromImage(bitmap, "sample.png") };
                            drafts[selected.Pid] = original; imageDrafts[selected.Pid] = panel.Images;
                            Key(panel, Keys.Enter);
                            Assert.AreEqual(0, queue.Items.Count);
                            Assert.AreEqual(original, panel.InputText); Assert.AreEqual(1, panel.Images.Count);
                            Assert.AreEqual(0, store.Load(new List<string>()).Count);
                            fs.FailCopy = false;
                            Key(panel, Keys.Enter);
                            Assert.AreEqual(1, queue.Items.Count);
                            var task = queue.Items.Single();
                            Assert.AreEqual(task.ExplicitInstanceKey, store.Load(new List<string>()).Single().ExplicitInstanceKey);
                            Assert.IsNull(queue.SaveError);
                            Assert.AreEqual(target.InstanceKey, task.ExplicitInstanceKey);
                            Assert.AreEqual("image task", task.Text);
                            Assert.IsTrue(task.HasAttachments);
                            Assert.IsTrue(File.Exists(AttachmentStore.FullPath(task.Attachments.Single())));
                            CollectionAssert.AreEqual(ChatImage.FromImage(bitmap, "sample.png").PngBytes(),
                                File.ReadAllBytes(AttachmentStore.FullPath(task.Attachments.Single())));
                            Assert.AreEqual("", panel.InputText); Assert.AreEqual(0, panel.Images.Count);
                            Assert.IsFalse(drafts.ContainsKey(selected.Pid)); Assert.IsFalse(imageDrafts.ContainsKey(selected.Pid));
                            Assert.AreEqual(QueueStatus.Waiting, task.Status); Assert.IsFalse(dispatcher.IsStarted);
                            panel.SetState("空闲 / Idle", Color.White, Color.Black, Color.Green, false);
                            Assert.IsTrue(panel.HasTarget);
                            Assert.IsFalse(panel.Busy);
                            Assert.AreSame(selected, sidebar.SelectedItem);
                            panel.InputText = "legacy task";
                            Key(panel, Keys.Enter);
                            Assert.AreEqual(2, queue.Items.Count, status.Text);
                            var legacy = queue.Items.Last();
                            Assert.AreEqual(selected.Key, legacy.VsKey);
                            Assert.IsFalse(legacy.HasExplicitTarget);
                        }
                    });
                }

                [TestMethod]
                public void AgentPopup_EscapeDoesNotStopAndExplicitSendBypassesRunningModel()
                {
                    Sta(() =>
                    {
                        using (var data = new TempDataFolder())
                        using (var window = new QuietForm { Width = 950, Height = 650, ShowInTaskbar = false })
                        using (var panel = new AgentPanel { Dock = DockStyle.Fill })
                        using (var cancellation = new CancellationTokenSource())
                        {
                            var agent = new AgentService(null, () => new AppSettings());
                            typeof(AgentService).GetProperty("Running").SetValue(agent, true);
                            typeof(AgentService).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(agent, cancellation);
                            panel.Bind(agent);
                            Field<TranscriptView>(panel, "_transcript").Visible = false;
                            var v = Vs(); var session = new VsMentionSession();
                            panel.BindMentions(session, () => new[] { Target(v) });
                            window.Controls.Add(panel); window.Show();
                            var input = Field<TextBox>(panel, "_input"); input.Focus(); input.SelectedText = "@";
                            Assert.IsTrue(Field<VsMentionInput>(panel, "_mentions").IsOpen);
                            Key(panel, Keys.Escape);
                            Assert.IsFalse(cancellation.IsCancellationRequested);
                            input.Text = session.Select(Target(v)) + " task";
                            int calls = 0;
                            panel.MentionRequested = (text, files) => { calls++; return new MentionSubmission(new QueuedTask(), "已接纳 / Accepted"); };
                            Key(panel, Keys.Enter);
                            Assert.AreEqual(1, calls);
                            Assert.IsTrue(agent.Running);
                            Assert.IsFalse(cancellation.IsCancellationRequested);
                            Assert.AreEqual(0, agent.Transcript.Messages.Count);
                            window.Close();
                        }
                    });
                }

                [TestMethod]
                public void AgentUi_MentionWithConfiguredModel_TellsAiTheTargetInsteadOfEnqueuing()
                {
                    Sta(() =>
                    {
                        using (var data = new TempDataFolder())
                        using (var panel = new AgentPanel())
                        {
                            var settings = new AppSettings { AgentEndpoint = "http://127.0.0.1:1/v1", AgentModel = "test" };
                            var agent = new AgentService(null, () => settings);
                            Assert.IsTrue(agent.Configured);
                            panel.Bind(agent);
                            var v = Vs(); var session = new VsMentionSession();
                            panel.BindMentions(session, () => new[] { Target(v, 3) });
                            var input = Field<TextBox>(panel, "_input");
                            input.Text = session.Select(Target(v, 3)) + " 笔记本的三个按键能移除吗";
                            int calls = 0;
                            panel.MentionRequested = (text, files) => { calls++; return new MentionSubmission(new QueuedTask(), "已接纳 / Accepted"); };
                            Key(panel, Keys.Enter);
                            Assert.AreEqual(0, calls);
                            Assert.AreEqual("", input.Text);
                            var shown = agent.Transcript.Messages.First().Parts.First().Text;
                            StringAssert.StartsWith(shown, "@#3 ");
                            Assert.IsFalse(shown.Contains("|"));
                            StringAssert.Contains(shown, "笔记本的三个按键能移除吗");
                        }
                    });
                }
    }
}
