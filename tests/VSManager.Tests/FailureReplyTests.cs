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
        public void Round_AddsTopOfStepLogsAndVisibleNotices()
        {
            var chat = new ChatTranscript();
            chat.Messages.Add(Msg(ChatRole.User, (false, "任务")));
            chat.Messages.Add(Msg(ChatRole.Assistant, (true, "Run command: dotnet build"), (false, "构建中")));
            string buildLog = "error CS0103: 当前上下文中不存在名称“Foo”\n" + string.Join("\n", Enumerable.Range(1, 50).Select(i => "detail " + i));
            var log = new TurnLog();
            log.Steps.Add(new StepLog { Header = "Run command: dotnet build", Detail = buildLog });
            log.Steps.Add(new StepLog { Header = "正在使用 Autopilot 重试请求...", Detail = "Request failed: 413 Payload Too Large\n\n以下省略" });
            log.Notices.Add("此响应被截断，因为它太长了。请尝试重新描述你的问题。");
            string round = TaskReply.Round(chat, log);
            var lines = round.Split('\n');
            Assert.AreEqual(TaskReply.StepPrefix + "Run command: dotnet build", lines[0]);
            Assert.AreEqual(TaskReply.DetailPrefix + "error CS0103: 当前上下文中不存在名称“Foo”", lines[1], "the top of the log comes first");
            Assert.IsFalse(round.Contains("detail 30"), "only the head of each log is kept");
            StringAssert.Contains(round, "构建中");
            StringAssert.Contains(round, TaskReply.StepPrefix + "正在使用 Autopilot 重试请求...\n" + TaskReply.DetailPrefix + "Request failed: 413 Payload Too Large");
            Assert.IsTrue(round.EndsWith(TaskReply.NoticePrefix + "此响应被截断，因为它太长了。请尝试重新描述你的问题。", StringComparison.Ordinal));
            Assert.AreEqual(RunIssue.TooLarge, RunIssue.Detect(round));
        }

        [TestMethod]
        public void LogHead_LimitsLinesAndCharacters()
        {
            Assert.IsNull(TaskReply.LogHead("  \n "));
            Assert.AreEqual("a\nb", TaskReply.LogHead("a\r\n\r\nb"));
            string head = TaskReply.LogHead(string.Join("\n", Enumerable.Range(1, 40)), 3, 1000);
            Assert.AreEqual("1\n2\n3\n…", head);
            Assert.IsTrue(TaskReply.LogHead(new string('x', 5000)).Length <= TaskReply.LogHeadChars + 2);
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
        public void RetryContext_GivesOneInstructionInsteadOfRepeatingPerSection()
        {
            var t = _queue.Add("A", "A", "实现订单导出功能", "AI");
            t.CompletionToken = "tok";
            t.ContentRuns = 1;
            t.PriorFailure = "#1：缺少导出目录";
            t.Supplement = "导出目录用 %APPDATA%\\Exports";
            string text = TaskStateMachine.DispatchText(t);
            StringAssert.Contains(text, "【第 2 轮】");
            StringAssert.Contains(text, "【前次尝试反馈】#1：缺少导出目录");
            StringAssert.Contains(text, "【补充信息】导出目录用 %APPDATA%\\Exports");
            StringAssert.Contains(text, "以补充信息为准");
            Assert.AreEqual(1, Count(text, "不要原样重复"), "one closing instruction / 只给一次处理要求");
            Assert.IsFalse(text.Contains("请参考前次反馈调整做法"));
        }

        [TestMethod]
        public void SupplementReplace_SendsOnlyTheConsolidatedBrief()
        {
            var t = _queue.Add("A", "A", "实现订单导出功能", "AI");
            t.CompletionToken = "tok";
            TaskStateMachine.Fail(t, "err", _clock.Now, FailureKind.Interrupted);
            t.RunIssue = RunIssue.TooLarge;
            t.PriorFailure = "#1：旧反馈";
            t.Supplement = "旧补充一 ｜ 旧补充二";
            Assert.IsTrue(TaskStateMachine.Supplement(t, "对话已清空：重新阅读导出相关代码后完成剩余的导出按钮", out string error, false, replace: true, freshContext: true), error);
            string text = TaskStateMachine.DispatchText(t);
            StringAssert.Contains(text, "【补充信息】对话已清空：重新阅读导出相关代码后完成剩余的导出按钮 ");
            Assert.IsFalse(text.Contains("旧补充"));
            Assert.IsFalse(text.Contains("旧反馈"));
            Assert.IsFalse(text.Contains("返回体或上下文过大"), "the old continuation note is replaced / 旧接续说明被替换");
            StringAssert.Contains(text, TaskStateMachine.FreshContextNote);
        }

        [TestMethod]
        public async Task RetryWithFreshContext_AsksToReread_ThenClears()
        {
            var t = await Run("y", "做到一半\n已达到单轮迭代上限");
            Assert.AreEqual(FailureKind.Interrupted, t.FailureKind);
            _host.AnswerReader = q => Task.FromResult("完成\n" + TaskStateMachine.SuccessReceipt(q));
            _dispatcher.Retry(t, null, freshContext: true);
            await _dispatcher.PumpAsync();
            string text = _host.Sent.Last();
            StringAssert.Contains(text, "达到单轮迭代上限");
            StringAssert.Contains(text, "重新阅读");
            Assert.IsFalse(text.Contains("对话中的已有进度"), "no reliance on the cleared conversation / 不依赖已清空的对话");
            Assert.IsFalse(t.FreshContext, "cleared once delivered");
        }

        [TestMethod]
        public void CheckAiRetry_ReplaceComparesWithEarlierInfo_FreshContextIsNew()
        {
            var t = _queue.Add("A", "A", "实现订单导出功能", "AI");
            TaskStateMachine.Fail(t, "reported", _clock.Now, FailureKind.Reported);
            t.Supplement = "导出文件放在 %APPDATA%\\Exports 目录，编码用 UTF-8";
            Assert.IsNotNull(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, t, "导出文件放在 %APPDATA%\\Exports 目录，编码用 UTF-8", replace: true));
            Assert.IsNull(TaskFailureAnalyzer.CheckAiRetry(_queue.Items, t, "导出文件放在 %APPDATA%\\Exports 目录，编码用 UTF-8", replace: true, freshContext: true));
        }

        private static int Count(string s, string part)
        {
            int n = 0;
            for (int i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        [TestMethod]
        public void Store_RoundTripsReplyIssueAndResumeNote()
        {
            var t = new QueuedTask { Id = 3, VsKey = "A", VsName = "A", Text = "t", Status = QueueStatus.Failed, Created = DateTime.Now,
                Reply = "▸ 步骤\n内容", RunIssue = RunIssue.TooLarge, ResumeNote = "【继续执行】x", FreshContext = true };
            var store = new JsonTaskStore(_data.File("tasks.json"));
            Assert.IsNull(store.Save(new[] { t }));
            var back = store.Load(new List<string>()).Single();
            Assert.AreEqual(t.Reply, back.Reply);
            Assert.AreEqual(RunIssue.TooLarge, back.RunIssue);
            Assert.IsTrue(back.FreshContext);
            Assert.IsTrue(t.Clone().FreshContext);
            Assert.AreEqual("【继续执行】x", back.ResumeNote);
            Assert.AreEqual(t.Reply, back.Clone().Reply);
        }
    }
}
