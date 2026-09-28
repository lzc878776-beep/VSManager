using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>工具真实性核查：识别未调用工具却声称入队 / 核实的回复。/ Tool-truthfulness check: spots replies claiming enqueue / verification without tools.</summary>
    [TestClass]
    public class ToolClaimCheckTests
    {
        private static ClaimCheckResult Check(string text, int next, params string[] tools) => ToolClaimCheck.Check(text, tools.ToList(), next);

        [TestMethod]
        public void FabricatedIds_WithoutSendTask_AreFlagged()
        {
            var r = Check("按 6 批依次发布。已确认入队 6 个任务：| **@50** | 规范 | | **@51** | 目录 |", 50);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            CollectionAssert.AreEqual(new[] { 50, 51 }, r.MissingIds.ToArray());
            StringAssert.Contains(r.Step, "@50");
        }

        [TestMethod]
        public void FakeVerification_OfMissingIds_IsFlagged()
        {
            var r = Check("我核实了任务清单，**这 7 个任务确实存在**：@57 排队中，@58 排队中", 50);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            CollectionAssert.AreEqual(new[] { 57, 58 }, r.MissingIds.ToArray());
        }

        [TestMethod]
        public void HashTaskIds_BeyondVsRange_AreChecked_ButVsNumbersAreNot()
        {
            var r = Check("我核实了任务清单，这 7 个任务确实存在：\n| #57 | 文档工具 | 执行中 |\n| #58 | 文档工具 | 排队中 |", 50);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            CollectionAssert.AreEqual(new[] { 57, 58 }, r.MissingIds.ToArray());
            // 「#1」是 VS 编号 / "#1" is a VS number
            Assert.AreEqual(ClaimVerdict.Ok, Check("已确认入队：@2 → #3 VSManager", 3, "send_task").Verdict);
        }

        [TestMethod]
        public void HonestAdmission_OfMissingTask_IsNotFlagged()
        {
            string text = "我查了任务清单，**#47 确实不存在**——清单里最新的编号是 #46。\n" +
                          "**原因**：我上一轮回复里说「已发布到 #1 VSManager（@47）」，但那次发布**实际没有成功**，这是我的失误。";
            Assert.AreEqual(ClaimVerdict.Ok, Check(text, 47, "list_tasks").Verdict);
        }

        [TestMethod]
        public void AdmittingOldIds_DoesNotHideNewFabricatedOnes()
        {
            var r = Check("我核实了，这 7 条确实存在：\n| #57 | 排队中 |\n另外，之前 #47、#48 确实是我虚报的（声称已发布但实际未入队）。", 50);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            CollectionAssert.AreEqual(new[] { 57 }, r.MissingIds.ToArray());
        }

        [TestMethod]
        public void RealEnqueue_WithExistingId_IsOk()
        {
            Assert.AreEqual(ClaimVerdict.Ok, Check("已确认入队：**@49 → #2 VSManager**（排队中）", 50, "send_task").Verdict);
        }

        [TestMethod]
        public void IdsBeyondTheQueue_AreFlaggedEvenWithOneSendTask()
        {
            // 只调用了一次 send_task（@50），却声称 @50–@52 / One real call (@50) but claims @50–@52
            var r = Check("已确认入队 @50、@51、@52", 51, "send_task");
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            CollectionAssert.AreEqual(new[] { 51, 52 }, r.MissingIds.ToArray());
        }

        [TestMethod]
        public void EnqueueClaim_WithoutToolOrIds_IsUnconfirmed()
        {
            var r = Check("已发布到 #1 文档工具，排队中。", 50);
            Assert.AreEqual(ClaimVerdict.Unconfirmed, r.Verdict);
            StringAssert.Contains(r.Step, "send_task");
        }

        [TestMethod]
        public void VerifyClaim_WithoutListTasks_IsUnconfirmed()
        {
            Assert.AreEqual(ClaimVerdict.Unconfirmed, Check("我查一下任务清单的实际状态。一切正常。", 50).Verdict);
            Assert.AreEqual(ClaimVerdict.Ok, Check("我查一下任务清单的实际状态。一切正常。", 50, "list_tasks").Verdict);
        }

        [TestMethod]
        public void CompletionReports_AndGitPush_AreNotClaims()
        {
            Assert.AreEqual(ClaimVerdict.Ok, Check("**@49 已完成**（用时 21m39s，已提交推送 `791f052`）", 50).Verdict);
            Assert.AreEqual(ClaimVerdict.Ok, Check("改动已推送到 origin/master。", 50).Verdict);
            Assert.AreEqual(ClaimVerdict.Ok, Check("", 50).Verdict);
        }

        [TestMethod]
        public void UnknownNextId_SkipsIdCheck()
        {
            Assert.AreEqual(ClaimVerdict.Ok, Check("已确认入队 @99", 0, "send_task").Verdict);
        }

        [TestMethod]
        public void Correction_NamesIdsAndDemandsToolCalls()
        {
            var r = Check("已确认入队 @50", 50);
            string c = ToolClaimCheck.Correction(r, 50);
            Assert.IsTrue(c.StartsWith(ToolClaimCheck.Marker));
            StringAssert.Contains(c, "@50");
            StringAssert.Contains(c, "send_task");
        }

        [TestMethod]
        public void HistoryAnnotation_ShowsRealToolLog_OrMarksUnbackedClaims()
        {
            string real = ToolClaimCheck.HistoryAnnotation("已确认入队 @48", new List<string> { "⚙ 向「#1 VSManager」发布任务：修复", "↳ ✅ 推送成功 @48", "💬 其他" });
            StringAssert.Contains(real, "发布任务");
            StringAssert.Contains(real, "推送成功 @48");
            Assert.IsFalse(real.Contains("其他"));

            string fake = ToolClaimCheck.HistoryAnnotation("已确认入队 @50", new List<string>());
            StringAssert.Contains(fake, "没有调用任何工具");

            Assert.AreEqual("", ToolClaimCheck.HistoryAnnotation("你好", null));
        }
    }
}