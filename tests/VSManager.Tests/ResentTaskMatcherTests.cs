using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>「失败任务重新发布后隐藏原条目」判定规则的测试。/ Tests for identifying republished failed tasks.</summary>
    [TestClass]
    public class ResentTaskMatcherTests
    {
        private const string Body = "请修复任务清单中失败条目重复显示的问题，并补充单元测试";

        private static QueuedTask T(int id, string status, string text, string vsKey = null, string target = null) => new QueuedTask
        {
            Id = id, Status = status, Text = text, VsKey = vsKey ?? SolutionMatcherTests.P(@"work\App\App.slnx"), VsName = "App",
            Target = target, Created = new DateTime(2026, 1, 1).AddMinutes(id)
        };

        [TestMethod]
        public void Fingerprint_IgnoresWhitespaceAndLineBreaks()
        {
            string a = "  第一行\r\n\r\n  第二行  \t尾部\r\n";
            string b = "第一行\n第二行 尾部";
            string c = "第一行\r第二行\u200B 尾部   ";
            Assert.AreEqual(ResentTaskMatcher.Fingerprint(b), ResentTaskMatcher.Fingerprint(a));
            Assert.AreEqual(ResentTaskMatcher.Fingerprint(b), ResentTaskMatcher.Fingerprint(c));
            Assert.AreNotEqual(ResentTaskMatcher.Fingerprint("第一行 第二行"), ResentTaskMatcher.Fingerprint("第一行第二行"));
            Assert.AreNotEqual(ResentTaskMatcher.Fingerprint("Fix A"), ResentTaskMatcher.Fingerprint("fix a"));
            Assert.AreEqual("", ResentTaskMatcher.Fingerprint(" \r\n "));
            Assert.AreEqual(32, ResentTaskMatcher.Fingerprint(Body).Length);
        }

        [TestMethod]
        public void Find_HidesFailedWithSameContentAndTarget()
        {
            var old1 = T(25, QueueStatus.Failed, Body + "\r\n");
            var old2 = T(26, QueueStatus.Failed, "  " + Body);
            var done = T(24, QueueStatus.Done, Body);
            var cancelled = T(23, QueueStatus.Cancelled, Body);
            var other = T(22, QueueStatus.Failed, "另一个完全不同的任务内容，用于确认不会误删");
            var neu = T(27, QueueStatus.Waiting, Body);
            var m = ResentTaskMatcher.Find(new[] { cancelled, other, done, old1, old2, neu }, neu);
            CollectionAssert.AreEquivalent(new[] { old1, old2 }, m.Hide);
            Assert.AreEqual(0, m.Kept.Count);
        }

        [TestMethod]
        public void Find_KeepsWhenTargetDiffers()
        {
            var old = T(25, QueueStatus.Failed, Body, SolutionMatcherTests.P(@"work\Other\Other.sln"));
            var neu = T(27, QueueStatus.Waiting, Body);
            var m = ResentTaskMatcher.Find(new[] { old, neu }, neu);
            Assert.AreEqual(0, m.Hide.Count);
            Assert.AreEqual(1, m.Kept.Count);
            StringAssert.Contains(m.Kept[0].Reason, "目标 VS 不同");
        }

        [TestMethod]
        public void Find_KeepsShortTextUnlessReferenced()
        {
            var old = T(25, QueueStatus.Failed, "继续");
            var neu = T(27, QueueStatus.Waiting, "继续");
            var m = ResentTaskMatcher.Find(new[] { old, neu }, neu);
            Assert.AreEqual(0, m.Hide.Count);
            StringAssert.Contains(m.Kept[0].Reason, "过短");

            var byRef = T(28, QueueStatus.Waiting, "重发 #25：继续");
            m = ResentTaskMatcher.Find(new[] { old, neu, byRef }, byRef);
            Assert.AreEqual(25, m.ReferencedId);
            CollectionAssert.AreEqual(new[] { old }, m.Hide);
        }

        [TestMethod]
        public void Find_IgnoresNewerAndSelf()
        {
            var neu = T(27, QueueStatus.Failed, Body);
            var later = T(30, QueueStatus.Failed, Body);
            var m = ResentTaskMatcher.Find(new[] { neu, later }, neu);
            Assert.AreEqual(0, m.Hide.Count);
            Assert.AreEqual(0, ResentTaskMatcher.Find(null, neu).Hide.Count);
            Assert.AreEqual(0, ResentTaskMatcher.Find(new[] { neu }, null).Hide.Count);
        }

        [TestMethod]
        public void Find_MatchesParkedTaskByPathOrAlias()
        {
            var old = T(25, QueueStatus.Failed, Body, SolutionMatcherTests.P(@"work\App\App.slnx").ToUpperInvariant());
            var neu = T(27, QueueStatus.WaitingVs, Body, SolutionMatcherTests.P(@"work\App\App.slnx"), "订单项目");
            Assert.AreEqual(1, ResentTaskMatcher.Find(new[] { old, neu }, neu).Hide.Count);

            var oldAlias = T(26, QueueStatus.Failed, Body, "title:App", "订单项目");
            Assert.IsTrue(ResentTaskMatcher.SameTarget(oldAlias, neu));
            Assert.IsFalse(ResentTaskMatcher.SameTarget(T(1, QueueStatus.Failed, Body, "title:A"), T(2, QueueStatus.Failed, Body, "title:B")));
        }

        [TestMethod]
        public void StripMarker_RecognizesResendMarkers()
        {
            Assert.AreEqual(Body, ResentTaskMatcher.StripMarker("重发 #26：" + Body, out int? id));
            Assert.AreEqual(26, id);
            Assert.AreEqual(Body, ResentTaskMatcher.StripMarker(Body + "（重试 @31）", out id));
            Assert.AreEqual(31, id);
            Assert.AreEqual(Body, ResentTaskMatcher.StripMarker("(Resend task #7) " + Body, out id));
            Assert.AreEqual(7, id);
            Assert.AreEqual("修复 #26 的问题", ResentTaskMatcher.StripMarker("修复 #26 的问题", out id));
            Assert.IsNull(id);
        }

        [TestMethod]
        public void HideList_AddRemoveAndStatusScope()
        {
            var marks = new List<HiddenTaskMark>();
            var failed = T(25, QueueStatus.Failed, Body);
            TaskHideList.Add(marks, 25, 27, DateTime.Now);
            TaskHideList.Add(marks, 25, 28, DateTime.Now);
            TaskHideList.Add(marks, 26, 28, DateTime.Now);
            Assert.AreEqual(2, marks.Count);
            Assert.IsTrue(TaskHideList.IsHidden(marks, failed));
            Assert.AreEqual(28, TaskHideList.ReplacedBy(marks, 25));
            CollectionAssert.AreEqual(new[] { 25, 26 }, TaskHideList.Replaced(marks, 28));

            // 失败条目被手动重新排队后不再隐藏 / Once the failed entry is requeued manually it is no longer hidden
            failed.Status = QueueStatus.Waiting;
            Assert.IsFalse(TaskHideList.IsHidden(marks, failed));

            Assert.IsTrue(TaskHideList.Remove(marks, 25));
            Assert.IsFalse(TaskHideList.Remove(marks, 25));
            Assert.IsNull(TaskHideList.ReplacedBy(marks, 25));
            Assert.IsFalse(TaskHideList.IsHidden(null, failed));

            for (int i = 0; i < TaskHideList.MaxMarks + 5; i++) TaskHideList.Add(marks, 1000 + i, 1, DateTime.Now);
            Assert.AreEqual(TaskHideList.MaxMarks, marks.Count);
        }
    }
}
