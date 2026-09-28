using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>
    /// 可核对的日志记录：测试项可验证性重判写入 tasks.log，发布任务时附加的「【执行方式】」写入发送日志。
    /// Checkable log records: the verifiability re-judge goes to tasks.log, and the "【执行方式】" rule appended on dispatch goes to the send log.
    /// </summary>
    [TestClass]
    public class TaskLogRecordTests
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

        private static string TasksLog()
        {
            string path = AppLog.PathOf(AppLog.TasksFile);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        [TestMethod]
        public void Parse_ReportsJudgedAndChangedCounts()
        {
            var items = TaskTestChecklist.Parse("- [ ] [AI] 编译通过\n- [ ] [人工] 重启后窗口位置与草稿恢复\n- [ ] [AI] 点击按钮后弹出窗口", out int judged, out int changed);
            Assert.AreEqual(3, judged);
            CollectionAssert.AreEqual(new[] { "ai", "ai", "user" }, items.Select(i => i.By).ToArray());
            Assert.AreEqual(2, changed);
            TaskTestChecklist.Parse("全部完成", out judged, out changed);
            Assert.AreEqual(0, judged, "通用兜底项不计 / the fallback item is not counted");
        }

        [TestMethod]
        public void Reclassify_CountsUncheckedItemsOnly()
        {
            var t = new QueuedTask
            {
                Status = QueueStatus.Unverified,
                TestItems = new[]
                {
                    new TaskTestItem { Text = "编译通过", By = "ai" },
                    new TaskTestItem { Text = "重启后窗口位置与草稿恢复", By = "user" },
                    new TaskTestItem { Text = "已勾选", By = "user", Checked = true }
                }
            };
            Assert.AreEqual(1, TaskTestChecklist.Reclassify(t, out int judged));
            Assert.AreEqual(2, judged);
        }

        [TestMethod]
        public void RejudgeLogText_UsesFixedWording()
        {
            StringAssert.Contains(TaskTestChecklist.RejudgeLogText(3, 1), "已按测试项文字重判 3 个测试项的可验证性");
            StringAssert.Contains(TaskTestChecklist.RejudgeLogText(3, 1), "Re-judged the verifiability of 3 test item(s)");
        }

        [TestMethod]
        public async Task EnteringVerification_WritesRejudgeRecordToTasksLog()
        {
            var v = _host.AddVs("A");
            var task = _queue.Add("A", "A", "feature", "AI");
            await _dispatcher.PumpAsync();
            _host.AnswerReader = t => Task.FromResult("done\r\n- [ ] [人工] 重启后窗口位置与草稿恢复\r\n- [ ] [AI] 编译通过\r\n" + TaskStateMachine.UnverifiedReceipt(t));
            await _dispatcher.FinishAsync(task, v, null);
            Assert.AreEqual(QueueStatus.Unverified, task.Status);
            string log = TasksLog();
            StringAssert.Contains(log, $"任务 #{task.Id} 进入待验证");
            StringAssert.Contains(log, "已按测试项文字重判 2 个测试项的可验证性（其中 1 项改变）");
        }

        [TestMethod]
        public async Task Completing_WithoutVerification_WritesNoRejudgeRecord()
        {
            var v = _host.AddVs("A");
            var task = _queue.Add("A", "A", "feature", "AI");
            await _dispatcher.PumpAsync();
            _host.AnswerReader = t => Task.FromResult("done\r\n" + TaskStateMachine.SuccessReceipt(t));
            await _dispatcher.FinishAsync(task, v, null);
            Assert.IsFalse(TasksLog().Contains("已按测试项文字重判"));
        }

        [TestMethod]
        public async Task Publishing_LogsAppendedExecutionRule()
        {
            _host.AddVs("A");
            var task = _queue.Add("A", "A", "feature", "AI");
            await _dispatcher.PumpAsync();
            string sent = TaskStateMachine.DispatchText(task);
            StringAssert.Contains(sent, TaskStateMachine.SuggestedPromptRule);
            var line = _host.Events.Single(e => e.Contains("已附加执行方式"));
            StringAssert.Contains(line, "【执行方式】请按自动推荐的提示词执行");
            StringAssert.Contains(line, TaskStateMachine.SuggestedPromptRule);
            StringAssert.Contains(line, $"#{task.Id} 发送正文 {sent.Length} 字");
            int publish = _host.Events.FindIndex(e => e.Contains($"发布任务 #{task.Id}"));
            Assert.IsTrue(publish >= 0 && _host.Events.IndexOf(line) == publish + 1, "紧跟发布记录 / right after the publish record");
        }

        [TestMethod]
        public void DispatchRuleLog_ReportsMissingRule()
        {
            var t = new QueuedTask { Id = 9 };
            StringAssert.Contains(TaskStateMachine.DispatchRuleLog(t, "plain"), "未附加 / not appended");
        }
    }
}
