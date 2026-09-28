using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class TaskTooltipTests
    {
        [TestMethod]
        public void Waiting_ShowsTaskContent()
        {
            var t = new QueuedTask { Id = 7, Title = "修复闪烁", Text = "修复输入框闪烁的问题", Status = QueueStatus.Waiting };
            string tip = TaskTooltip.Build(t, "排队中", "等待开始 / Awaiting start");
            StringAssert.Contains(tip, "#7 修复闪烁");
            StringAssert.Contains(tip, "任务内容 / Task");
            StringAssert.Contains(tip, "修复输入框闪烁的问题");
            StringAssert.Contains(tip, "等待开始");
            Assert.IsFalse(tip.Contains("完成情况"));
        }

        [TestMethod]
        public void Done_ShowsOutcome_WithoutReceiptOrTaskText()
        {
            var t = new QueuedTask { Id = 3, Text = "长长的任务原文", Status = QueueStatus.Done,
                Result = "已修复侧栏闪烁。\n\n[VSManager:0123456789abcdef0123456789abcdef:SUCCESS]" };
            string tip = TaskTooltip.Build(t, "已完成");
            StringAssert.Contains(tip, "完成情况 / Outcome");
            StringAssert.Contains(tip, "已修复侧栏闪烁。");
            Assert.IsFalse(tip.Contains("[VSManager:"));
            Assert.IsFalse(tip.Contains("任务内容"));
        }

        [TestMethod]
        public void Unverified_SplitsDoneAndUnverifiedItems()
        {
            var t = new QueuedTask { Id = 5, Text = "x", Status = QueueStatus.Unverified,
                Result = "已实现延迟提示。\n\n未验证：\n- 悬停 1 秒后出现\n- 移开后消失",
                TestItems = new[] { new TaskTestItem { Text = "悬停 1 秒后出现", Checked = true }, new TaskTestItem { Text = "移开后消失" } } };
            string tip = TaskTooltip.Build(t, "未验证");
            StringAssert.Contains(tip, "已完成的内容 / Done");
            StringAssert.Contains(tip, "已实现延迟提示。");
            StringAssert.Contains(tip, "未验证的内容（剩 1 项）");
            StringAssert.Contains(tip, "☑ 悬停 1 秒后出现");
            StringAssert.Contains(tip, "☐ 移开后消失");
            int done = tip.IndexOf("已完成的内容");
            Assert.AreEqual(-1, tip.IndexOf("- 移开后消失"), "列表不重复出现在已完成部分 / The list is not repeated in the done section");
            Assert.IsTrue(done > 0);
        }

        [TestMethod]
        public void DoneAwaitingUser_IsTreatedAsUnverified()
        {
            var t = new QueuedTask { Id = 9, Text = "x", Status = QueueStatus.Done, NeedsUser = true, Result = "改好了\n- [ ] 重启后检查" };
            string tip = TaskTooltip.Build(t, "待验证");
            StringAssert.Contains(tip, "改好了");
            StringAssert.Contains(tip, "☐ 重启后检查");
        }

        [TestMethod]
        public void Failed_ShowsReasonFirst()
        {
            var t = new QueuedTask { Id = 2, Text = "任务", Status = QueueStatus.Failed, Error = "编译失败" };
            string tip = TaskTooltip.Build(t, "失败");
            Assert.IsTrue(tip.IndexOf("编译失败") < tip.IndexOf("任务内容"));
        }

        [TestMethod]
        public void LongContent_IsClipped()
        {
            var t = new QueuedTask { Id = 1, Text = new string('a', 5000), Status = QueueStatus.Waiting };
            Assert.IsTrue(TaskTooltip.Build(t, "").Length < TaskTooltip.MaxSection + 200);
        }
    }
}
