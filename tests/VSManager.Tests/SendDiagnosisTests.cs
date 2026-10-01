using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class SendDiagnosisTests
    {
        [TestInitialize]
        public void Init() => SendDiagnosis.Clear();

        [TestMethod]
        public void Delivered_AllDeliveryStagesOk()
        {
            var s = SendDiagnosis.Evaluate(SendStage.Confirm, "已发送", SendStageState.Ok);
            Assert.IsTrue(s.All(x => x == SendStageState.Ok));
            var r = SendDiagnosis.Add(1, "A", SendStage.Confirm, "已发送", SendStageState.Ok, null, 10, null);
            Assert.IsNull(r.ProblemStage);
            Assert.IsNull(r.Hint);
            Assert.AreEqual("已送达 / Delivered", SendDiagnosis.Verdict(r));
        }

        [TestMethod]
        public void DeliveredButFocusFailed_PointsToFocus()
        {
            var r = SendDiagnosis.Add(1, "A", SendStage.Confirm, "已发送", SendStageState.Failed, "VS 仍在前台", 10, null);
            Assert.AreEqual(SendStage.Focus, r.ProblemStage);
            Assert.IsFalse(r.Uncertain);
            StringAssert.Contains(SendDiagnosis.Verdict(r), "未能切回");
        }

        [TestMethod]
        public void FailureAtLocate_EarlierOkLaterNotReached()
        {
            var s = SendDiagnosis.Evaluate(SendStage.Locate, "未找到 Copilot 对话窗格", null);
            Assert.AreEqual(SendStageState.Ok, s[(int)SendStage.Queue]);
            Assert.AreEqual(SendStageState.Failed, s[(int)SendStage.Locate]);
            Assert.AreEqual(SendStageState.NotReached, s[(int)SendStage.Fill]);
            Assert.AreEqual(SendStageState.NotReached, s[(int)SendStage.Focus]);
        }

        [TestMethod]
        public void BlockedResult_IsWaitingNotFailed()
        {
            var s = SendDiagnosis.Evaluate(SendStage.Locate, SendRetryPolicy.UserBusyPrefix + "键盘", null);
            Assert.AreEqual(SendStageState.Waiting, s[(int)SendStage.Locate]);
            var r = SendDiagnosis.Add(1, "A", SendStage.Queue, ManualChatProtection.WaitPrefix + "busy", null, null, 0, null);
            Assert.AreEqual(SendStage.Queue, r.ProblemStage);
            StringAssert.Contains(r.Hint, "无需重发");
        }

        [TestMethod]
        public void UncertainResults_AreFlagged()
        {
            // 已进入确认步骤但未确认 / Reached confirm without confirmation
            Assert.AreEqual(SendStageState.Uncertain,
                SendDiagnosis.Evaluate(SendStage.Confirm, "输入框已清空，但未在对话中确认到新消息，可能未送达", null)[(int)SendStage.Confirm]);
            // 提交后异常 / Exception after submission
            Assert.AreEqual(SendStageState.Uncertain, SendDiagnosis.Evaluate(SendStage.Submit, "发送失败：COM 异常", null)[(int)SendStage.Submit]);
            // 图片发送已点击但未确认 / Image send clicked but unconfirmed
            Assert.AreEqual(SendStageState.Uncertain,
                SendDiagnosis.Evaluate(SendStage.Submit, "已点击发送，但尚未确认成功。请在 VS 中查看，确认前不要重复发送", null)[(int)SendStage.Submit]);
            // 带待核实前缀，即使同时是等待类结果 / Verify prefix wins over waiting results
            Assert.AreEqual(SendStageState.Uncertain,
                SendDiagnosis.Evaluate(SendStage.Fill, ManualChatProtection.UncertainPrefix + SendRetryPolicy.UserBusyPrefix + "x", null)[(int)SendStage.Fill]);
            // 提交前失败不是不确定 / Failure before submission is not uncertain
            Assert.AreEqual(SendStageState.Failed, SendDiagnosis.Evaluate(SendStage.Fill, "发送失败：剪贴板", null)[(int)SendStage.Fill]);

            var r = SendDiagnosis.Add(1, "A", SendStage.Confirm, "可能未送达", SendStageState.Ok, null, 0, null);
            Assert.IsTrue(r.Uncertain);
            StringAssert.Contains(SendDiagnosis.Verdict(r), "禁止盲目重发");
        }

        [TestMethod]
        public void StageLine_ShowsMarks()
        {
            var r = SendDiagnosis.Add(1, "A", SendStage.Fill, "内容写入失败", SendStageState.Ok, null, 0, null);
            Assert.AreEqual("排队✓ → 定位✓ → 填入✗ → 提交○ → 确认○ → 焦点✓", r.StageLine);
            StringAssert.Contains(r.Describe(), "填入 / Fill — 失败 / failed");
        }

        [TestMethod]
        public void AnnotateAndUpdateFocus_OnlyTouchNewRecordsOfThatVs()
        {
            SendDiagnosis.Add(1, "old", SendStage.Queue, "x", null, null, 0, null);
            long seq = SendDiagnosis.LastSeq;
            var mine = SendDiagnosis.Add(1, "pid", SendStage.Confirm, "已发送", SendStageState.Ok, null, 0, null);
            var other = SendDiagnosis.Add(2, "other", SendStage.Confirm, "已发送", SendStageState.Ok, null, 0, null);
            SendDiagnosis.Annotate(1, seq, "VS-A", 42);
            Assert.AreEqual("VS-A", mine.VsName);
            Assert.AreEqual(42, mine.TaskId);
            Assert.IsNull(other.TaskId);
            Assert.AreEqual("old", SendDiagnosis.Recent().Last().VsName);
            Assert.AreSame(mine, SendDiagnosis.LatestForTask(42));

            SendDiagnosis.UpdateFocus(1, seq, false, "延迟");
            Assert.AreEqual(SendStageState.Failed, mine.States[(int)SendStage.Focus]);
            SendDiagnosis.UpdateFocus(1, seq, true, "已切回");
            Assert.AreEqual(SendStageState.Ok, mine.States[(int)SendStage.Focus]);
            StringAssert.Contains(mine.FocusDetail, "延迟");
            Assert.AreEqual(SendStageState.Ok, other.States[(int)SendStage.Focus]);
        }

        [TestMethod]
        public void RingBuffer_KeepsLatest()
        {
            for (int i = 0; i < SendDiagnosis.MaxRecords + 5; i++) SendDiagnosis.Add(1, "A", SendStage.Queue, "x" + i, null, null, 0, null);
            var recent = SendDiagnosis.Recent();
            Assert.AreEqual(SendDiagnosis.MaxRecords, recent.Count);
            Assert.AreEqual("x" + (SendDiagnosis.MaxRecords + 4), recent[0].Result);
        }

        [TestMethod]
        public void UncertainFailure_RefusesAiRecoveryRetry()
        {
            var t = new QueuedTask
            {
                Id = 7, Status = QueueStatus.Failed, FailureKind = FailureKind.Delivery,
                Error = ManualChatProtection.UncertainPrefix + "发送失败：x",
            };
            Assert.IsTrue(SendDiagnosis.IsUncertainFailure(t));
            StringAssert.Contains(TaskFailureAnalyzer.CheckRecoveryRetry(t), "已拒绝");
            StringAssert.Contains(TaskFailureAnalyzer.RecoveryNote(t), "送达不确定");

            t.Error = "未找到 Copilot 对话窗格";
            Assert.IsFalse(SendDiagnosis.IsUncertainFailure(t));
            Assert.IsNull(TaskFailureAnalyzer.CheckRecoveryRetry(t));
        }

        [TestMethod]
        public void Report_FiltersByTaskAndWarnsOnUncertain()
        {
            Assert.AreEqual("暂无发送诊断记录 / No send diagnoses yet", SendDiagnosisReport.Format(SendDiagnosis.Recent(), 0, 5));
            SendDiagnosis.Add(1, "A", SendStage.Confirm, "可能未送达", null, null, 0, new[] { "+ step" });
            long seq = SendDiagnosis.LastSeq - 1;
            SendDiagnosis.Annotate(1, seq, null, 9);
            string report = SendDiagnosisReport.Format(SendDiagnosis.Recent(), 9, 5);
            StringAssert.Contains(report, "任务 / Task #9");
            StringAssert.Contains(report, "禁止重发");
            StringAssert.Contains(report, "+ step");
            StringAssert.Contains(SendDiagnosisReport.Format(SendDiagnosis.Recent(), 3, 5), "没有任务 #3");
        }
    }
}
