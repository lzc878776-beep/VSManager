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
        public void ReadReceipt_DistinguishesSuccessNeedsUserFailedAndNone()
        {
            var t = Sent();
            Assert.AreEqual(TaskReceipt.Success, TaskStateMachine.ReadReceipt(t, "ok\n" + TaskStateMachine.SuccessReceipt(t), out string r));
            Assert.AreEqual("ok", r);
            Assert.AreEqual(TaskReceipt.NeedsUser, TaskStateMachine.ReadReceipt(t, "请手动运行验证\r\n" + TaskStateMachine.NeedsUserReceipt(t), out r));
            Assert.AreEqual("请手动运行验证", r);
            Assert.AreEqual(TaskReceipt.Failed, TaskStateMachine.ReadReceipt(t, "无法实现\n" + TaskStateMachine.FailureReceipt(t), out r));
            Assert.AreEqual("无法实现", r);
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, "no receipt", out _));
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, TaskStateMachine.FailureReceipt(t) + "\n" + TaskStateMachine.SuccessReceipt(t), out _));
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, "x " + TaskStateMachine.NeedsUserReceipt(t), out _));
            Assert.IsFalse(TaskStateMachine.TryReadSuccess(t, "x\n" + TaskStateMachine.NeedsUserReceipt(t), out _));
            string text = TaskStateMachine.DispatchText(t);
            StringAssert.Contains(text, TaskStateMachine.NeedsUserReceipt(t));
            StringAssert.Contains(text, "无关的遗留");
        }

        [TestMethod]
        public async Task NeedsUserReceipt_CompletesAsAwaitingVerification()
        {
            var t = await RunWithAnswer("实现导出功能", q => "改动已完成，请手动测试导出按钮\n" + TaskStateMachine.NeedsUserReceipt(q));
            Assert.AreEqual(QueueStatus.Done, t.Status);
            Assert.IsTrue(t.NeedsUser);
            Assert.IsNull(t.FailureKind);
            Assert.AreEqual("已完成（待用户验证）", TaskStateMachine.StatusText(t, _clock.Now));
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

            Assert.AreSame(failed, TaskFailureAnalyzer.FindVerbatimResend(_queue.Items, "A", "重发 @" + failed.Id + "：实现导出功能并补充单元测试"));
            Assert.IsNull(TaskFailureAnalyzer.FindVerbatimResend(_queue.Items, "B", "实现导出功能并补充单元测试"));
            Assert.IsNull(TaskFailureAnalyzer.FindVerbatimResend(_queue.Items, "A", "实现导出功能，先为测试项目添加缺少的引用"));

            var resend = _queue.Add("A", "A", "重发 @" + failed.Id + "：实现导出功能，先为测试项目添加缺少的引用", "AI");
            StringAssert.Contains(resend.PriorFailure, "测试项目缺少引用");

            var delivery = _queue.Add("C", "C", "另一个足够长的任务描述文本", "AI");
            TaskStateMachine.Fail(delivery, "send failed", _clock.Now, FailureKind.Delivery);
            Assert.IsNull(TaskFailureAnalyzer.FindVerbatimResend(_queue.Items, "C", "另一个足够长的任务描述文本"));
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
            Assert.IsTrue(back[0].NeedsUser);
            Assert.AreEqual("#0：x", back[0].PriorFailure);
            Assert.AreEqual(FailureKind.NoReceipt, back[1].FailureKind);
            Assert.IsFalse(back[1].NeedsUser);
        }
    }
}
