using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>
    /// 回执识别：Copilot 中途停顿不误判「缺少回执」、迟到回执纠正失败、回执行的宽松规范化。
    /// Receipt handling: a mid-run pause is not judged "missing receipt", a late receipt corrects the failure, and the receipt line is normalized.
    /// </summary>
    [TestClass]
    public class LateReceiptTests
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

        /// <summary>发布任务并让它进入执行中。/ Publishes a task and brings it to running.</summary>
        private async Task<(QueuedTask Task, VsInstance Vs)> Running()
        {
            var v = _host.AddVs("A");
            var t = _queue.Add("A", "A", "修改局部编号功能", "AI");
            _host.AnswerReader = q => Task.FromResult("Let me see how the outline review page does it.");
            await _dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, t.Status);
            return (t, v);
        }

        [TestMethod]
        public async Task PauseBetweenToolCalls_CopilotResumes_TaskStaysRunning()
        {
            var (t, v) = await Running();
            _dispatcher.ReceiptGrace = TimeSpan.FromSeconds(9);
            int delays = 0;
            // 第二次等待时 Copilot 重新开始运行 / Copilot starts running again during the second wait
            _dispatcher.ReceiptDelay = _ => { if (++delays == 2) v.Copilot = CopilotState.Busy; return Task.CompletedTask; };
            await _dispatcher.FinishAsync(t, v, null);
            Assert.AreEqual(QueueStatus.Running, t.Status, "a pause is not a missing receipt");
            Assert.IsTrue(t.SawBusy);
            Assert.AreEqual(0, _host.Notices.Count, "no failure notice");

            // Copilot 真正结束并输出回执 / Copilot really finishes with the receipt
            v.Copilot = CopilotState.Idle;
            _host.AnswerReader = q => Task.FromResult("已添加左右键。\n\n待处理：请在 Word 中测试\n- [ ] 点击右箭头降一级\n\n" + TaskStateMachine.UnverifiedReceipt(q));
            await _dispatcher.FinishAsync(t, v, null);
            Assert.AreEqual(QueueStatus.Unverified, t.Status);
        }

        [TestMethod]
        public async Task ReceiptAppearsDuringGrace_IsAccepted()
        {
            var (t, v) = await Running();
            _dispatcher.ReceiptGrace = TimeSpan.FromSeconds(9);
            int reads = 0;
            _host.AnswerReader = q => Task.FromResult(++reads < 3 ? "还在改" : "完成\n" + TaskStateMachine.SuccessReceipt(q));
            _dispatcher.ReceiptDelay = _ => Task.CompletedTask;
            await _dispatcher.FinishAsync(t, v, null);
            Assert.AreEqual(QueueStatus.Done, t.Status);
        }

        [TestMethod]
        public async Task NoReceiptAfterGrace_StillFails()
        {
            var (t, v) = await Running();
            _dispatcher.ReceiptGrace = TimeSpan.FromSeconds(9);
            int delays = 0;
            _dispatcher.ReceiptDelay = _ => { delays++; return Task.CompletedTask; };
            await _dispatcher.FinishAsync(t, v, null);
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual(FailureKind.NoReceipt, t.FailureKind);
            Assert.AreEqual(3, delays, "9 s grace polled every 3 s");
        }

        [TestMethod]
        public async Task LateReceipt_CorrectsNoReceiptFailure()
        {
            var (t, v) = await Running();
            await _dispatcher.FinishAsync(t, v, null);
            Assert.AreEqual(FailureKind.NoReceipt, t.FailureKind);
            Assert.AreEqual(1, t.ContentRuns);
            string question = TaskStateMachine.DispatchText(t);
            string answer = "Let me see how the outline review page does it.\n\n已完成改动。\n\n待处理：请测试\n- [ ] 点击右箭头\n\n" + TaskStateMachine.UnverifiedReceipt(t);

            _clock.Advance(TimeSpan.FromMinutes(4));
            Assert.IsTrue(await _dispatcher.RecoverLateReceiptAsync(v, question, answer));
            Assert.AreEqual(QueueStatus.Unverified, t.Status);
            Assert.IsNull(t.FailureKind);
            Assert.AreEqual(1, t.ContentRuns, "the misjudged failure is undone; only the completed run counts");
            Assert.AreEqual(1, t.TestItems.Length);
            StringAssert.Contains(_host.NoticeBodies.Last(), "误判");
            Assert.IsFalse(await _dispatcher.RecoverLateReceiptAsync(v, question, answer), "only once");
        }

        [TestMethod]
        public async Task LateReceipt_IgnoresOtherTurnsAndSupersededFailures()
        {
            var (t, v) = await Running();
            await _dispatcher.FinishAsync(t, v, null);
            string question = TaskStateMachine.DispatchText(t);
            // 另一轮的提问 / A different turn's question
            Assert.IsFalse(await _dispatcher.RecoverLateReceiptAsync(v, "别的问题", "完成\n" + TaskStateMachine.SuccessReceipt(t)));
            // 结尾没有回执 / No receipt at the end
            Assert.IsFalse(await _dispatcher.RecoverLateReceiptAsync(v, question, "还在改"));
            // 另一个 VS / Another VS
            var other = _host.AddVs("B");
            Assert.IsFalse(await _dispatcher.RecoverLateReceiptAsync(other, question, "完成\n" + TaskStateMachine.SuccessReceipt(t)));
            // 已被重新排队取代 / Superseded by a requeue
            var replacement = _queue.Add("A", "A", "修改局部编号功能（重发）", "AI");
            replacement.Replaces = new[] { t.Id };
            Assert.IsFalse(await _dispatcher.RecoverLateReceiptAsync(v, question, "完成\n" + TaskStateMachine.SuccessReceipt(t)));
            Assert.AreEqual(QueueStatus.Failed, t.Status);
        }

        [TestMethod]
        public async Task LateReceipt_ExpiresAfterWindow()
        {
            var (t, v) = await Running();
            await _dispatcher.FinishAsync(t, v, null);
            _clock.Advance(TaskDispatcher.LateReceiptWindow + TimeSpan.FromMinutes(1));
            Assert.IsFalse(await _dispatcher.RecoverLateReceiptAsync(v, TaskStateMachine.DispatchText(t), "完成\n" + TaskStateMachine.SuccessReceipt(t)));
        }

        [TestMethod]
        public void ReceiptLine_ToleratesDecorationButStillNeedsOwnLine()
        {
            var t = new QueuedTask { Id = 1, CompletionToken = "abc" };
            string r = TaskStateMachine.SuccessReceipt(t);
            Assert.AreEqual(TaskReceipt.Success, TaskStateMachine.ReadReceipt(t, "完成\n" + r, out _));
            Assert.AreEqual(TaskReceipt.Success, TaskStateMachine.ReadReceipt(t, "完成\r\n  " + r + " \u200B", out string body));
            Assert.AreEqual("完成", body);
            Assert.AreEqual(TaskReceipt.Success, TaskStateMachine.ReadReceipt(t, "完成\n**`" + r + "`**", out _));
            Assert.AreEqual(TaskReceipt.Success, TaskStateMachine.ReadReceipt(t, "完成\n" + r + "。", out _));
            Assert.AreEqual(TaskReceipt.Success, TaskStateMachine.ReadReceipt(t, r, out string only));
            Assert.AreEqual("任务已成功完成", only);
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, "完成。" + r, out _), "must be on its own line");
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(t, r + "\n后面还有文字", out _), "must be the last line");
            Assert.AreEqual(TaskReceipt.None, TaskStateMachine.ReadReceipt(new QueuedTask { Id = 1, CompletionToken = "other" }, "完成\n" + r, out _), "other attempt");
        }
    }
}
