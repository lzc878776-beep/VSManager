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
        public void EnqueueClaim_WithoutToolOrIds_IsFabricated()
        {
            // 用户轮里没有调用入队工具却声称刚发布：直接要求更正 / A fresh claim without an enqueue tool in a user round: demand a correction
            var r = Check("已发布到 #1 文档工具，排队中。", 50);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            Assert.AreEqual(ClaimReason.NoEnqueueTool, r.Reason);
            StringAssert.Contains(ToolClaimCheck.Correction(r, 50), "send_task");
        }

        [TestMethod]
        public void WiderEnqueuePhrasings_WithoutTool_AreFabricated()
        {
            string[] claims =
            {
                "好的，已经把这个任务加入任务清单了。",
                "已排队，等 VS 空闲后自动发送。",
                "任务已创建，稍后会自动推送。",
                "已发送给「Sap2000」的 Copilot。",
                "已同时推送到两个 VS 的 Copilot",
                "已经入队。",
                "已加入队列",
                "已重新排队。",
                "The task has been queued.",
                "I sent it to Copilot on Sap2000.",
                "Added it to the task list.",
            };
            foreach (var c in claims)
            {
                var r = Check(c, 50);
                Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict, c);
                Assert.AreEqual(ClaimReason.NoEnqueueTool, r.Reason, c);
            }
        }

        [TestMethod]
        public void NonClaims_AreNotFlagged()
        {
            string[] texts =
            {
                "改动已推送到 origin/master。",
                "代码已提交到仓库并推送。",
                "需要的话，我可以把它加入任务清单。",
                "**@49 已完成**，Copilot 已提交推送。",
                "Pushed to origin/master.",
                "要我发布这个任务吗？",
            };
            foreach (var t in texts) Assert.AreEqual(ClaimVerdict.Ok, Check(t, 50).Verdict, t);
        }

        [TestMethod]
        public void CopiedToolBoilerplate_DoesNotExemptTheClaim()
        {
            // 照抄 send_task 返回的格式：其中「未推送」「失败后通知」不是承认失败 / Copied send_task text: "not pushed" / "notify on failure" are not admissions
            string copied = "⏳ 已加入任务清单 @50，尚未推送到 Copilot；原因 / Queued, not yet pushed: 等待前序任务；送达或失败后会另行通知 / you will be notified on delivery or failure";
            var r = Check(copied, 50);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            CollectionAssert.AreEqual(new[] { 50 }, r.MissingIds.ToArray());
            var r2 = ToolClaimCheck.Check(copied.Replace("@50", "@49"), new string[0], 50, new HashSet<int> { 49 });
            Assert.AreEqual(ClaimVerdict.Fabricated, r2.Verdict);
            Assert.AreEqual(ClaimReason.NoEnqueueTool, r2.Reason);
        }

        [TestMethod]
        public void SuccessClaim_AfterToolRefusal_IsFabricated()
        {
            var refused = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("send_task", "❌ 未推送 / Not pushed: 「Cadmium」同时在多个 VS 中打开，请指定实例"),
            };
            var r = ToolClaimCheck.Check("已加入任务清单，排队中，完成后通知你。", new[] { "send_task" }, 50, null, refused);
            Assert.AreEqual(ClaimVerdict.Fabricated, r.Verdict);
            Assert.AreEqual(ClaimReason.EnqueueRejected, r.Reason);
            StringAssert.Contains(r.Step, "未推送");
            StringAssert.Contains(ToolClaimCheck.Correction(r, 50), "❌");

            var denied = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("send_task", "用户拒绝了该操作。") };
            Assert.AreEqual(ClaimReason.EnqueueRejected, ToolClaimCheck.Check("已推送给 Copilot。", new[] { "send_task" }, 50, null, denied).Reason);

            // 如实转述拒绝原因不算虚报 / Honestly relaying the refusal is fine
            Assert.AreEqual(ClaimVerdict.Ok, ToolClaimCheck.Check("任务未推送：同一解决方案在多个 VS 中打开，请指定实例。", new[] { "send_task" }, 50, null, refused).Verdict);

            var ok = new List<KeyValuePair<string, string>>
            {
                refused[0],
                new KeyValuePair<string, string>("send_task", "已加入任务清单：@50「Sap2000」（排队中，前面 0 个）；按编号调度"),
            };
            Assert.AreEqual(ClaimVerdict.Ok, ToolClaimCheck.Check("已加入任务清单 @50，排队中。", new[] { "send_task", "send_task" }, 51, null, ok).Verdict);
        }

        [TestMethod]
        public void EnqueueSucceeded_RecognizesResults()
        {
            Assert.IsTrue(ToolClaimCheck.EnqueueSucceeded("⏳ 已加入任务清单 @5，尚未推送到 Copilot"));
            Assert.IsTrue(ToolClaimCheck.EnqueueSucceeded("✅ 推送成功：任务 @5 已送达「A」的 Copilot"));
            Assert.IsTrue(ToolClaimCheck.EnqueueSucceeded("已原样重新排队任务 #5（直接重试第 1/2 次）"));
            Assert.IsTrue(ToolClaimCheck.EnqueueSucceeded("已为任务 #5 插入补充信息并在原条目重新排队"));
            Assert.IsFalse(ToolClaimCheck.EnqueueSucceeded("❌ 未推送：未能确认任务已加入任务清单"));
            Assert.IsFalse(ToolClaimCheck.EnqueueSucceeded("任务 #5 暂时不能重新排队（任务清单未开始或正在处理）"));
            Assert.IsFalse(ToolClaimCheck.EnqueueSucceeded("任务内容为空"));
            Assert.IsFalse(ToolClaimCheck.EnqueueSucceeded(null));
        }

        [TestMethod]
        public void NoticeRounds_AreCheckedToo()
        {
            var queue = new HashSet<int> { 12 };
            // 通知轮转述已有编号：仅提醒 / Notice round recapping an existing ID: warn only
            var recap = ToolClaimCheck.Check("@12 已加入任务清单，排队中。", new string[0], 13, queue, null, userRound: false);
            Assert.AreEqual(ClaimVerdict.Unconfirmed, recap.Verdict);
            Assert.AreEqual(ClaimReason.RecapUnconfirmed, recap.Reason);
            // 通知轮里不带编号声称刚推送：更正 / Notice round claiming a fresh push without any ID: correct it
            var fresh = ToolClaimCheck.Check("已把修复任务推送给 Sap2000 的 Copilot。", new string[0], 13, queue, null, userRound: false);
            Assert.AreEqual(ClaimVerdict.Fabricated, fresh.Verdict);
            Assert.AreEqual(ClaimReason.NoEnqueueTool, fresh.Reason);
            // 通知轮里的虚构编号照样更正 / Invented IDs in notice rounds are corrected as before
            Assert.AreEqual(ClaimReason.MissingIds, ToolClaimCheck.Check("已加入任务清单 @13", new string[0], 13, queue, null, userRound: false).Reason);
            // 用户轮：带现有编号的「刚入队」也要更正；明确是回顾的只提醒 / User round: a fresh claim with an existing ID is corrected; explicit recaps only warn
            Assert.AreEqual(ClaimVerdict.Fabricated, ToolClaimCheck.Check("@12 已加入任务清单，排队中。", new string[0], 13, queue).Verdict);
            Assert.AreEqual(ClaimVerdict.Unconfirmed, ToolClaimCheck.Check("之前已入队的 @12 还在排队中。", new string[0], 13, queue).Verdict);
        }

        [TestMethod]
        public void QuotedUiText_ChecklistsAndInputRecaps_AreNotFreshClaims()
        {
            // 完成汇报引用界面文字与测试清单 / Completion reports citing UI text and checklists
            string report = "修复后只有确认送达时才显示「✅ 推送成功」。\n- [ ] 目标 VS 忙碌时发布任务：显示「⏳ 已加入任务清单，尚未推送」";
            Assert.AreEqual(ClaimVerdict.Ok, ToolClaimCheck.Check(report, new string[0], 50, null, null, userRound: false).Verdict);
            // 转述本轮通知里的原文 / Recapping wording from this round's notice
            string notice = "任务 #48 已完成。Copilot 回复：- **✅ 推送成功**：只有确认已送达时才提示";
            Assert.AreEqual(ClaimVerdict.Ok, ToolClaimCheck.Check("- **✅ 推送成功**：只有确认已送达时才提示", new string[0], 50, null, null, false, notice).Verdict);
        }

        [TestMethod]
        public void StatusOfExistingTasks_AfterListTasks_IsOk()
        {
            var queue = new HashSet<int> { 60, 61 };
            Assert.AreEqual(ClaimVerdict.Ok, ToolClaimCheck.Check("我核实了清单，**@61 已经入队**，当前状态是「发送中」", new[] { "list_tasks" }, 62, queue).Verdict);
            // 没有编号的「已推送」即使查过清单也要更正 / An ID-less "pushed" claim is still corrected after list_tasks
            Assert.AreEqual(ClaimVerdict.Fabricated, ToolClaimCheck.Check("已推送给 Sap2000 的 Copilot。", new[] { "list_tasks" }, 62, queue).Verdict);
        }

        [TestMethod]
        public void Notice_DescribesEachReason()
        {
            StringAssert.Contains(ToolClaimCheck.Notice(Check("已排队。", 50)), "没有调用入队工具");
            StringAssert.Contains(ToolClaimCheck.Notice(Check("已确认入队 @50", 50)), "@50");
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