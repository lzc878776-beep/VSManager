using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>失败时读取本轮 Copilot 完整回复、识别执行异常并接续重试。/ Reading the whole Copilot turn on failure, detecting run issues and continuing on retry.</summary>
    [TestClass]
    public class FailureReplyTests
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

        private async Task<QueuedTask> Run(string answer, string round = null)
        {
            _host.AddVs("A");
            var t = _queue.Add("A", "A", "实现订单导出功能", "AI");
            _host.AnswerReader = q => Task.FromResult(answer);
            if (round != null) _host.RoundReader = v => Task.FromResult(round);
            await _dispatcher.PumpAsync();
            _clock.Advance(TimeSpan.FromSeconds(31));
            await _dispatcher.PumpAsync();
            return t;
        }

        private static ChatMessage Msg(ChatRole role, params (bool Step, string Text)[] parts)
        {
            var m = new ChatMessage { Role = role };
            foreach (var p in parts) m.Parts.Add(new ChatPart { IsStep = p.Step, Text = p.Text });
            return m;
        }

        [TestMethod]
        public void Round_CollectsEveryAnswerAndStepAfterTheLastUserMessage()
        {
            var chat = new ChatTranscript();
            chat.Messages.Add(Msg(ChatRole.User, (false, "旧任务")));
            chat.Messages.Add(Msg(ChatRole.Assistant, (false, "旧回答")));
            chat.Messages.Add(Msg(ChatRole.User, (false, "新任务")));
            chat.Messages.Add(Msg(ChatRole.Assistant, (false, "开始修改"), (true, "读取\n OrderService.cs")));
            chat.Messages.Add(Msg(ChatRole.Assistant, (false, "Error: unexpected EOF")));
            string round = TaskReply.Round(chat);
            Assert.AreEqual("开始修改\n" + TaskReply.StepPrefix + "读取 OrderService.cs\nError: unexpected EOF", round);
            Assert.IsNull(TaskReply.Round(new ChatTranscript()));
        }

        [TestMethod]
        public void Detect_FindsRunIssuesAtTheEndOfLongReplies()
        {
            string work = new string('改', 2000) + "\n";
            Assert.AreEqual(RunIssue.Eof, RunIssue.Detect(work + "Error: unexpected EOF"));
            Assert.AreEqual(RunIssue.Eof, RunIssue.Detect(work + "请求失败：未预期的 EOF"));
            Assert.AreEqual(RunIssue.TooLarge, RunIssue.Detect(work + "Request failed: the request payload is too large."));
            Assert.AreEqual(RunIssue.TooLarge, RunIssue.Detect(work + "返回体过大，已停止"));
            Assert.AreEqual(RunIssue.IterationLimit, RunIssue.Detect(work + "Copilot has been working on this problem for a while. Continue to iterate?"));
            Assert.AreEqual(RunIssue.IterationLimit, RunIssue.Detect(work + "已达到单轮迭代上限"));
            Assert.AreEqual(RunIssue.Network, RunIssue.Detect(work + "Sorry, your request failed. Please try again."));
            Assert.IsNull(RunIssue.Detect(work + "已完成导出功能并通过单元测试。"));
            Assert.IsNull(RunIssue.Detect("network error 的处理逻辑已修改\n" + work + "全部完成。"), "early mentions are not the ending");
            Assert.IsNull(RunIssue.Detect(null));
        }

        [TestMethod]
        public async Task LongReplyEndingInEof_IsInterruptedAndNoticeCarriesTheWholeTurn()
        {
            string answer = new string('改', 800) + "\n最后的结论写到一半";
            string round = "开始修改 OrderService.cs\n" + TaskReply.StepPrefix + "运行测试\n" + answer + "\nError: unexpected EOF";
            var t = await Run(answer, round);
            Assert.AreEqual(QueueStatus.Failed, t.Status);
            Assert.AreEqual(FailureKind.Interrupted, t.FailureKind, "a long reply cut off by EOF is not a content failure");
            Assert.AreEqual(RunIssue.Eof, t.RunIssue);
            Assert.AreEqual(round, t.Reply);
            Assert.AreEqual(0, t.ContentRuns);
            string body = _host.NoticeBodies.Last();
            StringAssert.Contains(body, "开始修改 OrderService.cs");
            StringAssert.Contains(body, TaskReply.StepPrefix + "运行测试");
            StringAssert.Contains(body, "unexpected EOF");
            StringAssert.Contains(body, RunIssue.Label(RunIssue.Eof));
            StringAssert.Contains(body, "retry_task");
        }

        [TestMethod]
        public async Task PlainNoReceiptFailure_StaysContentFailureWithWholeTurn()
        {
            var t = await Run("已修改一部分，但还没完成导出。", "▸ 读取文件\n已修改一部分，但还没完成导出。");
            Assert.AreEqual(FailureKind.NoReceipt, t.FailureKind);
            Assert.IsNull(t.RunIssue);
            StringAssert.Contains(_host.NoticeBodies.Last(), "▸ 读取文件");
            StringAssert.Contains(_host.NoticeBodies.Last(), "retry_task_with_info");
        }

        [TestMethod]
        public async Task HugeTurn_NoticeShowsHeadAndTail_AndPagesReadEverything()
        {
            string round = "HEAD-" + new string('a', 30000) + "-TAIL too many tokens";
            var t = await Run("x", round);
            Assert.AreEqual(RunIssue.TooLarge, t.RunIssue);
            string body = _host.NoticeBodies.Last();
            StringAssert.Contains(body, "HEAD-");
            StringAssert.Contains(body, "-TAIL too many tokens");
            StringAssert.Contains(body, "read_task_reply");
            Assert.IsTrue(body.Length < round.Length);
            int pages = (t.Reply.Length + TaskReply.PageSize - 1) / TaskReply.PageSize;
            Assert.IsTrue(pages > 1);
            StringAssert.Contains(TaskReply.Page(t, 1), "HEAD-");
            StringAssert.Contains(TaskReply.Page(t, 1), "page=2");
            StringAssert.Contains(TaskReply.Page(t, pages), "-TAIL too many tokens");
            StringAssert.Contains(TaskReply.Page(t, 1), "1/" + pages);
            StringAssert.Contains(TaskReply.Page(t, 99), pages + "/" + pages);
        }

        [TestMethod]
        public void Store_KeepsHeadAndTailOfOversizedTurns()
        {
            string s = "HEAD" + new string('b', TaskReply.StoreMax * 2) + "TAIL";
            string stored = TaskReply.Store(s);
            Assert.IsTrue(stored.Length < s.Length);
            Assert.IsTrue(stored.StartsWith("HEAD", StringComparison.Ordinal));
            Assert.IsTrue(stored.EndsWith("TAIL", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task RetryAfterInterruption_ContinuesWithCauseAndNote_ThenClears()
        {
            var t = await Run("y", "做到一半\n已达到单轮迭代上限");
            Assert.AreEqual(FailureKind.Interrupted, t.FailureKind);
            _host.AnswerReader = q => Task.FromResult("完成\n" + TaskStateMachine.SuccessReceipt(q));
            _dispatcher.Retry(t, "先完成导出按钮，测试留到下一步");
            Assert.IsNull(t.Reply);
            Assert.IsNull(t.RunIssue);
            await _dispatcher.PumpAsync();
            string text = _host.Sent.Last();
            StringAssert.Contains(text, "达到单轮迭代上限");
            StringAssert.Contains(text, "在此基础上继续");
            StringAssert.Contains(text, "【重试说明】先完成导出按钮，测试留到下一步");
            Assert.IsNull(t.ResumeNote, "cleared once delivered");
        }

        [TestMethod]
        public void RetryAfterContentFailure_HasNoContinuationNote()
        {
            var t = _queue.Add("A", "A", "实现订单导出功能", "AI");
            TaskStateMachine.Fail(t, "reported", _clock.Now, FailureKind.Reported);
            TaskStateMachine.Requeue(t);
            Assert.IsNull(t.ResumeNote);
        }

        [TestMethod]
        public void Store_RoundTripsReplyIssueAndResumeNote()
        {
            var t = new QueuedTask { Id = 3, VsKey = "A", VsName = "A", Text = "t", Status = QueueStatus.Failed, Created = DateTime.Now,
                Reply = "▸ 步骤\n内容", RunIssue = RunIssue.TooLarge, ResumeNote = "【继续执行】x" };
            var store = new JsonTaskStore(_data.File("tasks.json"));
            Assert.IsNull(store.Save(new[] { t }));
            var back = store.Load(new List<string>()).Single();
            Assert.AreEqual(t.Reply, back.Reply);
            Assert.AreEqual(RunIssue.TooLarge, back.RunIssue);
            Assert.AreEqual("【继续执行】x", back.ResumeNote);
            Assert.AreEqual(t.Reply, back.Clone().Reply);
        }
    }
}
