using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>三种任务回执、失败分类、前次反馈与禁止原样重发。/ Three receipts, failure kinds, previous feedback and verbatim-resend blocking.</summary>
    [TestClass]
    public class TaskOutcomeTests
    {
        private TempDataFolder _data;
        private FakeClock _clock;
        private TaskQueue _queue;
        private FakeDispatchHost _host;
        private TaskDispatcher _dispatcher;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _clock = new FakeClock();
            _queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), _clock.Func);
            _host = new FakeDispatchHost();
            _dispatcher = new TaskDispatcher(_queue, _host, _clock.Func);
            _dispatcher.Start();
        }

        [TestCleanup]
        public void Cleanup() => _data.Dispose();

        private static QueuedTask Sent()
        {
            var t = new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Text = "实现导出功能", Status = QueueStatus.Waiting, Created = new DateTime(2026, 1, 1, 8, 0, 0) };
            TaskStateMachine.BeginSend(t, "A");
            return t;
        }

        private async Task<QueuedTask> RunWithAnswer(string text, Func<QueuedTask, string> answer)
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", text, "AI");
            _host.AnswerReader = q => Task.FromResult(answer(q));
            await _dispatcher.PumpAsync();
            _clock.Advance(TimeSpan.FromSeconds(31));
            await _dispatcher.PumpAsync();
            return t;
        }

        [TestMethod]
        public void ReadReceipt_DistinguishesSuccessUnverifiedFailedAndNone()
        {
            var t = Sent();
            Assert.AreEqual(TaskReceipt.Success, TaskStateMachine.ReadReceipt(t, "ok\n" + TaskStateMachine.SuccessReceipt(t), out string r));
            Assert.AreEqual("ok", r);
            Assert.AreEqual(TaskReceipt.Unverified, TaskStateMachine.ReadReceipt(t, "请手动运行验证\r\n" + TaskStateMachine.UnverifiedReceipt(t), out r));
            Assert.AreEqual("请手动运行验证", r);
            // 旧回执 NEEDS_USER 仍识别为待验证 / The legacy NEEDS_USER receipt still reads as awaiting verification
            Assert.AreEqual(TaskReceipt.Unverified, TaskStateMachine.ReadReceipt(t, "请手动运行验证\r\n" + TaskStateMachine.NeedsUserReceipt(t), out r));
            Assert.AreEqual("请手动运行验证", r);
            Assert.AreEqual(TaskReceipt.Failed, TaskStateMachine.ReadReceipt(t, "无法实现\n" + TaskStateMachine.FailureReceipt(t), out r));
            Assert.AreEqual("无法实现", r);
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, "no receipt", out _));
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, TaskStateMachine.FailureReceipt(t) + "\n" + TaskStateMachine.SuccessReceipt(t), out _));
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, "x " + TaskStateMachine.NeedsUserReceipt(t), out _));
            Assert.IsFalse(TaskStateMachine.TryReadSuccess(t, "x\n" + TaskStateMachine.NeedsUserReceipt(t), out _));
            string text = TaskStateMachine.DispatchText(t);
            StringAssert.Contains(text, TaskStateMachine.UnverifiedReceipt(t));
            Assert.IsFalse(text.Contains(TaskStateMachine.NeedsUserReceipt(t)), "规则只保留三种回执 / Rules list three receipts only");
            StringAssert.Contains(text, "三选一");
            StringAssert.Contains(text, "无关的遗留");
        }

        [TestMethod]
        public void ReadReceipt_IgnoresTrailingNoReplyNote()
        {
            var t = Sent();
            Assert.AreEqual(TaskReceipt.Success, TaskStateMachine.ReadReceipt(t, "done\n" + TaskStateMachine.SuccessReceipt(t) + "\n\n(This turn has no user-facing reply.)", out string r));
            Assert.AreEqual("done", r);
            Assert.AreEqual(TaskReceipt.Unverified, TaskStateMachine.ReadReceipt(t, "待处理：重启验证\r\n" + TaskStateMachine.UnverifiedReceipt(t) + "\r\n*This turn has no user-facing reply*", out _));
            Assert.AreEqual(TaskReceipt.Unverified, TaskStateMachine.ReadReceipt(t, "x\n" + TaskStateMachine.NeedsUserReceipt(t) + "\n（本轮没有面向用户的回复）", out _));
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, "x\n" + TaskStateMachine.SuccessReceipt(t) + "\n还有其他内容", out _));
            StringAssert.Contains(TaskStateMachine.DispatchText(t), "回执行之后不要再输出任何文字");
        }

        [TestMethod]
        public void UnverifiedResult_BlocksAtCompletedAndAwaitingConfirmation()
        {
            var t = new QueuedTask { Status = QueueStatus.Unverified };
            Assert.IsTrue(ReleaseLevels.Blocks(ReleaseLevel.Completed, t));
            Assert.IsTrue(ReleaseLevels.Blocks(ReleaseLevel.NeedsUser, t));
            Assert.IsFalse(ReleaseLevels.Blocks(ReleaseLevel.Failed, t));
            Assert.IsFalse(ReleaseLevels.Blocks(ReleaseLevel.Unlimited, t));
            Assert.IsTrue(TaskStateMachine.Release(t));
            Assert.IsTrue(t.Released);
            Assert.AreEqual(QueueStatus.Unverified, t.Status);
        }

        [TestMethod]
        public void ClipboardBusy_IsRetriedLater()
        {
            Assert.IsTrue(SendRetryPolicy.IsBlocked(SendRetryPolicy.ClipboardBusyPrefix + "held by another app"));
            Assert.IsTrue(SendRetryPolicy.IsClipboardBusy(SendRetryPolicy.ClipboardBusyPrefix + "x"));
            Assert.IsFalse(SendRetryPolicy.IsClipboardBusy("图片发送失败：x"));
        }

        [TestMethod]
        public async Task NeedsUserReceipt_CompletesAsAwaitingVerification()
        {
            var t = await RunWithAnswer("实现导出功能", q => "改动已完成，请手动测试导出按钮\n" + TaskStateMachine.NeedsUserReceipt(q));
            Assert.AreEqual(QueueStatus.Unverified, t.Status);
            Assert.IsFalse(t.NeedsUser);
            Assert.IsNull(t.FailureKind);
            Assert.AreEqual("待验证", TaskStateMachine.StatusText(t, _clock.Now));
            StringAssert.Contains(_host.NoticeBodies.Last(), "do not resend");
        }

        [TestMethod]
        public async Task ReportedFailure_IsClassified_AndGuidanceUsesTheReply()
        {
            var t = await RunWithAnswer("实现导出功能", q => "导出已实现，但项目中原有的编译错误导致无法生成\n" + TaskStateMachine.FailureReceipt(q));
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual(FailureKind.Reported, t.FailureKind);
            string body = _host.NoticeBodies.Last();
            StringAssert.Contains(body, "原有的编译错误");
            StringAssert.Contains(body, "pre-existing");
            StringAssert.Contains(body, "Never resend the same text verbatim");
        }

        [TestMethod]
        public void Guidance_ForDeliveryFailure_AllowsUnchangedRequeue()
        {
            var t = Sent();
            TaskStateMachine.Fail(t, "send failed", DateTime.Now, FailureKind.Delivery);
            StringAssert.Contains(TaskFailureAnalyzer.Guidance(t), "delivery problem");
            Assert.IsNull(TaskFailureAnalyzer.PriorFailureSummary(t));
        }

        [TestMethod]
        public void Requeue_CarriesContentFailureFeedback_IntoNextDispatch()
        {
            var t = Sent();
            t.Result = "缺少数据库连接字符串";
            TaskStateMachine.Fail(t, "reported", DateTime.Now, FailureKind.Reported);
            TaskStateMachine.Requeue(t);
            Assert.IsNull(t.FailureKind);
            StringAssert.Contains(t.PriorFailure, "缺少数据库连接字符串");
            TaskStateMachine.BeginSend(t, "A");
            StringAssert.Contains(TaskStateMachine.DispatchText(t), "【前次尝试反馈】#1：缺少数据库连接字符串");
            var copy = t.Clone();
            Assert.AreEqual(t.PriorFailure, copy.PriorFailure);
        }

        [TestMethod]
        public void Resend_WithMarker_InheritsFeedback_AndVerbatimResendIsDetected()
        {
            var failed = _queue.Add("A", "A", "实现导出功能并补充单元测试", "AI");
            failed.Result = "测试项目缺少引用";
            TaskStateMachine.Fail(failed, "reported", _clock.Now, FailureKind.Reported);

            Assert.AreSame(failed, TaskFailureAnalyzer.FindRepeatedResend(_queue.Items, "A", "重发 @" + failed.Id + "：实现导出功能并补充单元测试"));
            Assert.IsNull(TaskFailureAnalyzer.FindRepeatedResend(_queue.Items, "B", "实现导出功能并补充单元测试"));
            Assert.IsNull(TaskFailureAnalyzer.FindRepeatedResend(_queue.Items, "A", "实现导出功能，先为测试项目添加缺少的引用"));

            var resend = _queue.Add("A", "A", "重发 @" + failed.Id + "：实现导出功能，先为测试项目添加缺少的引用", "AI");
            StringAssert.Contains(resend.PriorFailure, "测试项目缺少引用");

            var delivery = _queue.Add("C", "C", "另一个足够长的任务描述文本", "AI");
            TaskStateMachine.Fail(delivery, "send failed", _clock.Now, FailureKind.Delivery);
            Assert.IsNull(TaskFailureAnalyzer.FindRepeatedResend(_queue.Items, "C", "另一个足够长的任务描述文本"));
        }

        [TestMethod]
        public void NearRepeat_CatchesRewordedRetries_ButAllowsRealRevisions()
        {
            const string task = "实现订单导出功能：在 OrderService 中新增 ExportCsv 方法，并在工具栏添加导出按钮";
            Assert.IsTrue(TaskFailureAnalyzer.IsNearRepeat(task, task));
            Assert.IsTrue(TaskFailureAnalyzer.IsNearRepeat(task, "重发 @3：请再试一次，" + task));
            Assert.IsTrue(TaskFailureAnalyzer.IsNearRepeat(task, task + "。请仔细一点，务必完成！"));
            Assert.IsTrue(TaskFailureAnalyzer.IsNearRepeat(task, "Please try again carefully: " + task));
            Assert.IsFalse(TaskFailureAnalyzer.IsNearRepeat(task, task + "。先修复 OrderService.cs 第 42 行的空引用，忽略 Legacy 项目的警告"));
            Assert.IsFalse(TaskFailureAnalyzer.IsNearRepeat(task, "只在 OrderService 中新增 ExportCsv 方法"), "narrowing the scope is a revision");
        }

        [TestMethod]
        public void AiResend_IsCappedPerRequestChain()
        {
            string text = "实现订单导出功能并补充单元测试";
            var first = _queue.Add("A", "A", text, "AI");
            first.Result = "测试项目缺少引用";
            TaskStateMachine.Fail(first, "reported", _clock.Now, FailureKind.Reported);
            Assert.IsNull(TaskFailureAnalyzer.CheckAiResend(_queue.Items, "A", "重发 @" + first.Id + "：先为测试项目添加 xunit 引用，再实现订单导出功能"));

            var second = _queue.Add("A", "A", "重发 @" + first.Id + "：先为测试项目添加 xunit 引用，再实现订单导出功能", "AI");
            CollectionAssert.Contains(second.Replaces, first.Id);
            second.Result = "xunit 版本冲突";
            TaskStateMachine.Fail(second, "reported", _clock.Now, FailureKind.Reported);
            Assert.AreEqual(2, TaskFailureAnalyzer.LineageAttempts(_queue.Items, new[] { second }));
            StringAssert.Contains(TaskFailureAnalyzer.AttemptsNote(_queue.Items, second), "1");

            // 补充重试一次后再失败：链上共 3 次，AI 不能再自主重发或补充 / One retry with info fails again: 3 runs in the chain, no more AI retries
            Assert.IsNull(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, second, "改用 MSTest，项目已引用 MSTest.TestFramework"));
            TaskStateMachine.Supplement(second, "改用 MSTest，项目已引用 MSTest.TestFramework", out _);
            TaskStateMachine.BeginSend(second, "A");
            second.Result = "仍然失败";
            TaskStateMachine.Fail(second, "reported", _clock.Now, FailureKind.Reported);
            Assert.AreEqual(3, TaskFailureAnalyzer.LineageAttempts(_queue.Items, new[] { second }));
            StringAssert.Contains(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, second, "换一种完全不同的新思路来实现"), "已拒绝");
            StringAssert.Contains(TaskFailureAnalyzer.CheckAiResend(_queue.Items, "A", "重发 @" + second.Id + "：换一个实现思路，用 CsvHelper 库生成导出文件"), "已拒绝");
        }

        [TestMethod]
        public void AiRetry_RequiresNewInformation_AndDeliveryFailuresDoNotCount()
        {
            var t = _queue.Add("A", "A", "实现订单导出功能并补充单元测试", "AI");
            TaskStateMachine.Fail(t, "reported", _clock.Now, FailureKind.Reported);
            Assert.IsNotNull(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, t, "请再试一次，务必完成"));
            Assert.IsNotNull(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, t, "实现订单导出功能"));
            Assert.IsNull(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, t, "导出文件放在 %APPDATA%\\Exports 目录，编码用 UTF-8"));

            var d = _queue.Add("B", "B", "另一个足够长的任务描述文本", "AI");
            TaskStateMachine.Fail(d, "send failed", _clock.Now, FailureKind.Delivery);
            Assert.AreEqual(0, TaskFailureAnalyzer.LineageAttempts(_queue.Items, new[] { d }));
        }

        [TestMethod]
        public void Analyze_FindsPreExistingAndUserTestingClues()
        {
            var h = TaskFailureAnalyzer.Analyze("The change is done; the build fails because of pre-existing errors. Please test manually.");
            Assert.IsTrue(h.PreExisting);
            Assert.IsTrue(h.NeedsUser);
            Assert.IsFalse(TaskFailureAnalyzer.Analyze("编译失败：CS1002").Any);
        }

        [TestMethod]
        public void JsonStore_RoundTripsNewFields()
        {
            string path = _data.File("tasks.json");
            var t = Sent();
            t.Status = QueueStatus.Done; t.Finished = DateTime.Now; t.NeedsUser = true; t.PriorFailure = "#0：x";
            var f = Sent(); f.Id = 2; TaskStateMachine.Fail(f, "e", DateTime.Now, FailureKind.NoReceipt);
            var store = new JsonTaskStore(path);
            Assert.IsNull(store.Save(new[] { t, f }));
            var back = store.Load(new List<string>());
            Assert.AreEqual(QueueStatus.Unverified, back[0].Status, "旧记录迁移为待验证 / Legacy record migrates to awaiting verification");
            Assert.IsFalse(back[0].NeedsUser);
            Assert.AreEqual("#0：x", back[0].PriorFailure);
            Assert.AreEqual(FailureKind.NoReceipt, back[1].FailureKind);
            Assert.IsFalse(back[1].NeedsUser);
            Assert.AreEqual(1, back[1].ContentRuns);
        }

        [TestMethod]
        public void JsonStore_RoundTripsRetryCounters()
        {
            string path = _data.File("tasks.json");
            var t = Sent();
            t.ContentRuns = 2; t.RecoveryRetries = 3; t.PriorRuns = 1; t.SupplementCount = 1; t.Supplement = "改用 UTF-8";
            var store = new JsonTaskStore(path);
            Assert.IsNull(store.Save(new[] { t }));
            var back = store.Load(new List<string>()).Single();
            Assert.AreEqual(2, back.ContentRuns);
            Assert.AreEqual(3, back.RecoveryRetries);
            Assert.AreEqual(1, back.PriorRuns);
            Assert.AreEqual(1, back.SupplementCount);
            Assert.AreEqual("改用 UTF-8", back.Supplement);
            var copy = back.Clone();
            Assert.AreEqual(2, copy.ContentRuns);
            Assert.AreEqual(3, copy.RecoveryRetries);
            Assert.AreEqual(1, copy.PriorRuns);
        }

        [TestMethod]
        public void LooksInterrupted_OnlyForEmptyOrShortErrorReplies()
        {
            Assert.IsTrue(TaskFailureAnalyzer.LooksInterrupted(""));
            Assert.IsTrue(TaskFailureAnalyzer.LooksInterrupted("  "));
            Assert.IsTrue(TaskFailureAnalyzer.LooksInterrupted("抱歉，请求失败，请稍后再试。"));
            Assert.IsTrue(TaskFailureAnalyzer.LooksInterrupted("Sorry, your request failed. Please try again."));
            Assert.IsTrue(TaskFailureAnalyzer.LooksInterrupted("Sorry, there was a network error."));
            Assert.IsFalse(TaskFailureAnalyzer.LooksInterrupted("unconfirmed result"));
            Assert.IsFalse(TaskFailureAnalyzer.LooksInterrupted("已修改 OrderService.cs，新增导出方法"));
            Assert.IsFalse(TaskFailureAnalyzer.LooksInterrupted(new string('改', 600) + " network error"), "long replies are real work");
        }

        [TestMethod]
        public async Task InterruptedRun_IsNotContentFailure_AndNoticeOffersPlainRetry()
        {
            var t = await RunWithAnswer("实现导出功能", q => "Sorry, your request failed. Please try again.");
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual(FailureKind.Interrupted, t.FailureKind);
            Assert.AreEqual(0, t.ContentRuns);
            Assert.AreEqual(0, TaskFailureAnalyzer.LineageAttempts(_queue.Items, new[] { t }));
            Assert.IsNull(TaskFailureAnalyzer.PriorFailureSummary(t));
            string body = _host.NoticeBodies.Last();
            StringAssert.Contains(body, "retry_task");
            StringAssert.Contains(body, "Sorry, your request failed");
            Assert.IsFalse(body.Contains("reached the AI self-retry limit"));
        }

        [TestMethod]
        public void NonContentFailures_AllowRepeats_AndDoNotCountTowardTheLimit()
        {
            var t = _queue.Add("A", "A", "实现订单导出功能并补充单元测试", "AI");
            t.Result = "缺少导出目录";
            TaskStateMachine.Fail(t, "reported", _clock.Now, FailureKind.Reported);
            Assert.AreEqual(1, TaskFailureAnalyzer.LineageAttempts(_queue.Items, new[] { t }));
            StringAssert.Contains(TaskFailureAnalyzer.CheckRecoveryRetry(t), "retry_task_with_info");

            TaskStateMachine.Supplement(t, "导出目录用 %APPDATA%\\Exports", out _);
            TaskStateMachine.BeginSend(t, "A");
            TaskStateMachine.Fail(t, "interrupted", _clock.Now, FailureKind.Interrupted);
            Assert.AreEqual(1, TaskFailureAnalyzer.LineageAttempts(_queue.Items, new[] { t }), "interrupted runs are not counted");
            Assert.IsNull(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, t, "继续"), "'continue' is allowed after an interrupted run");
            Assert.IsNull(TaskFailureAnalyzer.CheckRecoveryRetry(t));
            Assert.IsNull(TaskFailureAnalyzer.FindRepeatedResend(_queue.Items, "A", t.Text));

            t.RecoveryRetries = TaskFailureAnalyzer.MaxRecoveryRetries;
            StringAssert.Contains(TaskFailureAnalyzer.CheckRecoveryRetry(t), "已拒绝");
            Assert.AreEqual(TaskFailureAnalyzer.MaxRecoveryRetries.ToString(), TaskFailureAnalyzer.MaxRecoveryRetriesText);

            var worktree = _queue.Add("B", "B", "另一个足够长的任务描述文本", "AI");
            TaskStateMachine.Fail(worktree, "worktree failed", _clock.Now);
            Assert.IsNotNull(TaskFailureAnalyzer.CheckRecoveryRetry(worktree), "unclassified failures need the user");
        }

        [TestMethod]
        public void AiRetryLimit_FollowsTheSetting()
        {
            Assert.AreEqual(TaskFailureAnalyzer.DefaultAiAttempts, TaskFailureAnalyzer.ClampAttempts(0));
            Assert.AreEqual(TaskFailureAnalyzer.MaxAiAttemptsLimit, TaskFailureAnalyzer.ClampAttempts(99));
            Assert.AreEqual(TaskFailureAnalyzer.DefaultAiAttempts, new AppSettings().AiRetryLimit);
            var settings = new AppSettings { AiRetryLimit = 1 };
            try
            {
                TaskFailureAnalyzer.AttemptsLimitSource = () => settings.AiRetryLimit;
                var t = _queue.Add("A", "A", "实现订单导出功能并补充单元测试", "AI");
                TaskStateMachine.Fail(t, "reported", _clock.Now, FailureKind.Reported);
                StringAssert.Contains(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, t, "导出文件放在 %APPDATA%\\Exports 目录，编码用 UTF-8"), "已拒绝");
                settings.AiRetryLimit = 4;
                Assert.IsNull(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, t, "导出文件放在 %APPDATA%\\Exports 目录，编码用 UTF-8"));
                StringAssert.Contains(Prompts.AgentSystem(false, DateTime.Now, "", null), "Copilot 执行最多 4 次");
                StringAssert.Contains(Prompts.AgentSystem(true, DateTime.Now, "", null), "at most 4 Copilot runs");
            }
            finally { TaskFailureAnalyzer.AttemptsLimitSource = null; }
        }

        [TestMethod]
        public void Resend_TellsCopilotTheRound()
        {
            var first = _queue.Add("A", "A", "实现订单导出功能并补充单元测试", "AI");
            TaskStateMachine.BeginSend(first, "A");
            Assert.IsFalse(TaskStateMachine.DispatchText(first).Contains("【第"));
            first.Result = "测试项目缺少引用";
            TaskStateMachine.Fail(first, "reported", _clock.Now, FailureKind.Reported);

            var second = _queue.Add("A", "A", "重发 @" + first.Id + "：先为测试项目添加 xunit 引用，再实现订单导出功能", "AI");
            Assert.AreEqual(1, second.PriorRuns);
            TaskStateMachine.BeginSend(second, "A");
            StringAssert.Contains(TaskStateMachine.DispatchText(second), "【第 2 轮】");
            TaskStateMachine.Fail(second, "reported", _clock.Now, FailureKind.Reported);
            TaskStateMachine.Requeue(second);
            TaskStateMachine.BeginSend(second, "A");
            StringAssert.Contains(TaskStateMachine.DispatchText(second), "【第 3 轮】");
        }
    }
}
