using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>任务层项目隔离：题目补全项目名与项目摘要注入。/ Task-level project isolation: project-prefixed titles and injected project summaries.</summary>
    [TestClass]
    public class TaskProjectContextTests
    {
        [TestMethod]
        public void ProjectName_PrefersSolutionName()
        {
            Assert.AreEqual("Orders", TaskProjectContext.ProjectName(@"C:\Work\Orders\Orders.sln", "订单 · 线程"));
            Assert.AreEqual("Shop", TaskProjectContext.ProjectName(@"C:\Work\Shop\Shop.slnx", null));
            Assert.AreEqual("Demo", TaskProjectContext.ProjectName("title:Demo", "Other"));
            Assert.AreEqual("订单项目", TaskProjectContext.ProjectName("inst:1:2", "订单项目 · 对话标题"));
            Assert.AreEqual("App", TaskProjectContext.ProjectName("A", "App #2"));
            Assert.IsNull(TaskProjectContext.ProjectName(null, null));
        }

        [TestMethod]
        public void TitleWithProject_PrefixesOnlyWhenMissing()
        {
            Assert.AreEqual("VSManager · 优化创建界面并加暗色模式", TaskProjectContext.TitleWithProject("VSManager", "优化创建界面并加暗色模式"));
            Assert.AreEqual("修复 vsmanager 启动", TaskProjectContext.TitleWithProject("VSManager", "修复 vsmanager 启动"));
            Assert.AreEqual("整理代码", TaskProjectContext.TitleWithProject(null, "整理代码"));
            Assert.AreEqual("Orders · 修复登录按钮", TaskProjectContext.TitleWithProject("Orders", null, "【需求】修复登录按钮。其他说明"));
            Assert.IsNull(TaskProjectContext.TitleWithProject("Orders", null, "  "));
            string longTitle = TaskProjectContext.TitleWithProject(new string('P', 24), new string('题', 30));
            Assert.AreEqual(24 + TaskProjectContext.Separator.Length + TaskTitle.MaxLength, longTitle.Length);
            Assert.IsTrue(longTitle.Length <= TaskTitle.MaxStoredLength);
        }

        [TestMethod]
        public void Summary_ListsLatestThreeFinishedTasksOfSameVs()
        {
            var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
            QueuedTask T(int id, string key, string status, int minutes, string result = null, string reason = null) => new QueuedTask
            {
                Id = id, VsKey = key, VsName = key, Text = "任务" + id, Title = "P · 事项" + id, Status = status,
                Created = t0, Finished = QueueStatus.Active(status) ? (DateTime?)null : t0.AddMinutes(minutes), Result = result, FailureReason = reason,
            };
            var tasks = new[]
            {
                T(1, "A", QueueStatus.Done, 1, "旧结果"),
                T(2, "A", QueueStatus.Failed, 2, reason: "编译失败"),
                T(3, "B", QueueStatus.Done, 3, "B 项目结论"),
                T(4, "A", QueueStatus.Unverified, 4, "待用户测试"),
                T(5, "A", QueueStatus.Running, 5),
                T(6, "A", QueueStatus.Done, 6, "当前任务"),
                T(7, "A", QueueStatus.Cancelled, 7),
            };
            string s = TaskProjectContext.Summary("P", "负责订单 / Orders", tasks, "a", excludeId: 6);
            StringAssert.StartsWith(s, "[项目上下文 / Project context] 项目 / Project：P；职责 / Role：负责订单 / Orders");
            StringAssert.Contains(s, "#7 P · 事项7｜已取消");
            StringAssert.Contains(s, "#4 P · 事项4｜待验证 / awaiting verification｜待用户测试");
            StringAssert.Contains(s, "#2 P · 事项2｜失败 / failed｜编译失败");
            Assert.IsFalse(s.Contains("#1 "), "只取最近 3 条 / latest three only");
            Assert.IsFalse(s.Contains("B 项目结论"), "不混入其他项目 / no other project");
            Assert.IsFalse(s.Contains("当前任务"), "排除当前任务 / current task excluded");
            Assert.IsFalse(s.Contains("#5 "), "未结束任务不计入 / active tasks skipped");
            Assert.IsTrue(s.IndexOf("#7", StringComparison.Ordinal) < s.IndexOf("#4", StringComparison.Ordinal));

            string empty = TaskProjectContext.Summary("Q", null, tasks, "Q");
            StringAssert.Contains(empty, "职责 / Role：未设置 / not set");
            StringAssert.Contains(empty, "最近任务 / Recent tasks：无 / none");
        }

        [TestMethod]
        public async Task Dispatcher_AppendsProjectContextAndTitleToNotifications()
        {
            using (new TempDataFolder())
            {
                var clock = new FakeClock();
                var queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), clock.Func);
                var host = new FakeDispatchHost { ProjectContextReader = t => "[项目上下文 / Project context] 项目 / Project：A #" + t.Id };
                var dispatcher = new TaskDispatcher(queue, host, clock.Func);
                dispatcher.Start();
                var v = host.AddVs("A");
                var ok = queue.Add("A", "A", "ok", "AI");
                ok.Title = "A · 完成事项";
                await dispatcher.PumpAsync();
                await dispatcher.FinishAsync(ok, v, null);
                Assert.AreEqual(QueueStatus.Done, ok.Status);
                StringAssert.Contains(host.Notices.Last(), "「A · 完成事项」");
                StringAssert.EndsWith(host.NoticeBodies.Last(), "\n[项目上下文 / Project context] 项目 / Project：A #" + ok.Id);

                var bad = queue.Add("A", "A", "bad", "AI");
                dispatcher.Fail(bad, "boom");
                StringAssert.EndsWith(host.NoticeBodies.Last(), "项目 / Project：A #" + bad.Id);

                host.ProjectContextReader = t => throw new InvalidOperationException("x");
                var third = queue.Add("A", "A", "third", "AI");
                dispatcher.Fail(third, "boom");
                Assert.IsFalse(host.NoticeBodies.Last().Contains("项目上下文"), "摘要出错不影响通知 / summary errors do not break notices");
            }
        }
    }
}
