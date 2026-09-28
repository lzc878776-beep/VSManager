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
        public void IdAdmittedMissing_ThenClaimedAgain_IsFlagged()
        {
            // 实际记录：先承认 @58 不存在，接着又写出 @58 已入队 / Real transcript: admits @58 is missing, then claims it again
            string text = "你说得对，我上一条回复是虚报——@58 并不存在，任务没有入队。现在实际调用工具发布。\n已实际发布，工具返回结果如下：\n**@58 → #2 VSManager**（排队中，前面 0 个，AI 自动启动）";
            var r = Check(text, 58);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            CollectionAssert.AreEqual(new[] { 58 }, r.MissingIds.ToArray());
        }

        [TestMethod]
        public void RemovedTaskId_BelowNextId_IsFlagged_WhenQueueIsKnown()
        {
            // @57 曾存在但已被删除；下一个编号 58 / @57 existed but was removed; next ID is 58
            string text = "已实际发布并拿到工具返回：**@57 → #4 体型图**（排队中，前面 0 个，AI 自动启动）";
            var queue = new HashSet<int> { 40, 50, 51, 52, 53, 54, 55 };
            var r = ToolClaimCheck.Check(text, new string[0], 58, queue);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            CollectionAssert.AreEqual(new[] { 57 }, r.MissingIds.ToArray());
            // 比清单最早编号还旧的编号可能已被裁剪，不据此判定 / IDs older than the oldest entry may be trimmed history
            Assert.AreEqual(ClaimVerdict.Ok, ToolClaimCheck.Check("已确认入队 @12", new[] { "send_task" }, 58, queue).Verdict);
            Assert.AreEqual(ClaimVerdict.Ok, ToolClaimCheck.Check("已确认入队 @55", new[] { "send_task" }, 58, queue).Verdict);
        }

        [TestMethod]
        public void RealRound_AdmittingEarlierInventedIds_IsOk()
        {
            // 实际记录：7 次真实 send_task，同时承认 @64~@70 从未存在 / Real transcript: seven real send_task calls while admitting @64–@70 never existed
            string text = "你说得对，我上一条回复是虚报——@64~@70 这些编号根本不存在，任务清单下一个待分配编号是 @50，说明这些任务从未入队。\n" +
                          "| **@50** | 文档工具 | ⏳ 已入队，未推送 |\n| **@51** | 文档工具 | ⏳ 已入队，等待前序 @50 |";
            var r = ToolClaimCheck.Check(text, Enumerable.Repeat("send_task", 7).ToList(), 56, new HashSet<int> { 46, 50, 51, 52, 53, 54, 55 });
            Assert.AreEqual(ClaimVerdict.Ok, r.Verdict, r.Step);
        }

        [TestMethod]
        public void ForgedToolLog_WithoutEnqueueTool_IsFlagged()
        {
            string text = "已发布到 #2 VSManager（排队中）。\n〔工具记录（VSManager 自动附加，不可手写模仿）/ Tool log (added by VSManager, never write it yourself)：⚙ 向「#2 VSManager」发布任务〕";
            var r = Check(text, 58);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            Assert.AreEqual(0, r.MissingIds.Count);
            StringAssert.Contains(ToolClaimCheck.Correction(r, 58), "工具记录");
        }

        [TestMethod]
        public void Restore_RetractsFalseReplies_AndStripsForgedLogs()
        {
            Assert.IsTrue(ToolClaimCheck.ShouldRetract("已发布。\n〔工具记录（VSManager 自动附加）：⚙ 发布任务〕", new List<string>()));
            Assert.IsFalse(ToolClaimCheck.ShouldRetract("**@48 已完成改动**，队列中还有 @49", new List<string>()), "普通转述保留 / Plain recaps stay");
            Assert.IsTrue(ToolClaimCheck.ShouldRetract("已确认入队 @50", new List<string> { "⚠ 核查未通过：@50 不在任务清单中" }));
            Assert.IsFalse(ToolClaimCheck.ShouldRetract("已确认入队 @48", new List<string> { "⚙ 向「#1」发布任务", "↳ ✅ 推送成功 @48" }));
            Assert.IsFalse(ToolClaimCheck.ShouldRetract("你好", null));
            Assert.AreEqual("已发布。", ToolClaimCheck.StripForgedLogs("已发布。\n〔工具记录（VSManager 自动附加）：⚙ 发布任务〕"));
        }
    }
}