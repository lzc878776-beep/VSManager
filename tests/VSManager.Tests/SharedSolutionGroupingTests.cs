using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>
    /// 同一解决方案被多个 VS 打开时的任务清单分组，以及带图发送在粘贴前丢失焦点时的结果分类。
    /// Task-list grouping when one solution is open in several VS, and the result of image sends that lose focus before pasting.
    /// </summary>
    [TestClass]
    public class SharedSolutionGroupingTests
    {
        private const string Sln = "%USERPROFILE%\\source\\Example\\Example.sln";
        private const string Other = "%USERPROFILE%\\source\\Tool\\Tool.slnx";

        private static readonly TaskGroupVs[] Open =
        {
            new TaskGroupVs { Key = Sln, Name = "Docs", Number = 1, InstanceKey = "inst:100:1", Pid = 100 },
            new TaskGroupVs { Key = Other, Name = "Tool", Number = 2, InstanceKey = "inst:200:2", Pid = 200 },
            new TaskGroupVs { Key = Sln, Name = "Calc", Number = 5, InstanceKey = "inst:300:3", Pid = 300 },
        };

        private static QueuedTask Task(int id, string name, string pinned, string status = QueueStatus.Done) =>
            new QueuedTask { Id = id, VsKey = Sln, VsName = name, TargetInstanceKey = pinned, Status = status, Created = new DateTime(2025, 1, 1, 9, id, 0), Text = "task " + id };

        private static string GroupTitleOf(System.Collections.Generic.List<object> shown, object item)
        {
            string title = null;
            foreach (var row in shown)
            {
                if (row is TaskGroupHeader h) title = h.Title;
                else if (ReferenceEquals(row, item)) return title;
            }
            return null;
        }

        [TestMethod]
        public void PinnedTask_GroupsUnderItsOwnInstance_NotTheFirstVsOfTheSolution()
        {
            var docs = Task(1, "Docs", "inst:100:1");
            var calc = Task(2, "Calc", "inst:300:3", QueueStatus.Failed);
            var shown = TaskGrouping.Build(new object[] { docs, calc }, Open, TaskGrouping.SortByNumber, null);
            Assert.AreEqual("@1 Docs", GroupTitleOf(shown, docs));
            Assert.AreEqual("@5 Calc", GroupTitleOf(shown, calc));
            var calcHeader = shown.OfType<TaskGroupHeader>().Single(h => h.Number == 5);
            Assert.AreEqual(1, calcHeader.Failed);
        }

        [TestMethod]
        public void UnpinnedTask_UsesAUniqueNameMatch_AndUnknownOnesStayApart()
        {
            var named = Task(1, "Calc", null);
            var gone = Task(2, "Closed window", "inst:999:9");
            var shown = TaskGrouping.Build(new object[] { named, gone }, Open, TaskGrouping.SortByNumber, null);
            Assert.AreEqual("@5 Calc", GroupTitleOf(shown, named));
            StringAssert.StartsWith(GroupTitleOf(shown, gone), "Closed window（未打开");
        }

        [TestMethod]
        public void ExplicitMentionTarget_WinsOverName()
        {
            var t = Task(1, "Docs", null);
            t.ExplicitInstanceKey = "inst:300:3";
            var shown = TaskGrouping.Build(new object[] { t }, Open, TaskGrouping.SortByNumber, null);
            Assert.AreEqual("@5 Calc", GroupTitleOf(shown, t));
        }

        [TestMethod]
        public void ManualChat_GroupsByProcess()
        {
            var chat = new ExternalChat { Id = 1, Pid = 300, VsKey = Sln, VsName = "Docs", Question = "q", Started = new DateTime(2025, 1, 1) };
            var shown = TaskGrouping.Build(new object[] { chat }, Open, TaskGrouping.SortByNumber, null);
            Assert.AreEqual("@5 Calc", GroupTitleOf(shown, chat));
        }

        [TestMethod]
        public void SingleInstanceSolution_KeepsTheSolutionKey()
        {
            var t = new QueuedTask { Id = 1, VsKey = Other, VsName = "Tool", TargetInstanceKey = "inst:200:2", Status = QueueStatus.Done, Created = new DateTime(2025, 1, 1), Text = "x" };
            var shown = TaskGrouping.Build(new object[] { t }, Open, TaskGrouping.SortByNumber, null);
            var header = shown.OfType<TaskGroupHeader>().Single();
            Assert.AreEqual(TaskGrouping.NormalizeKey(Other), header.Key);
            Assert.AreEqual("@2 Tool", header.Title);
        }

        [TestMethod]
        public void ImageFocusLostBeforePaste_QueuedRetriesLater_DirectKeepsTextFallback()
        {
            string queued = CopilotChat.PrePasteFocusResult("输入焦点已改变，未发送图片", true);
            Assert.IsTrue(SendRetryPolicy.IsUserBusy(queued));
            Assert.AreEqual(SendDecision.Retry, SendRetryPolicy.Decide(1, queued));
            Assert.IsFalse(MainForm.IsPreSubmitImageFailure(queued));

            string direct = CopilotChat.PrePasteFocusResult("无法激活该 VS 窗口，发送已取消 / Could not activate this VS window; send cancelled", false);
            Assert.IsTrue(MainForm.IsPreSubmitImageFailure(direct));

            string clip = SendRetryPolicy.ClipboardBusyPrefix + "busy";
            Assert.AreEqual(clip, CopilotChat.PrePasteFocusResult(clip, true));
        }
    }
}
