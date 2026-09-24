using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class CopilotPaneModeTests
    {
        [TestMethod]
        public void Classify_NotFoundAndHidden()
        {
            Assert.AreEqual(CopilotPaneMode.NotFound, CopilotPaneModes.Classify(false, false, null, null, null));
            Assert.AreEqual(CopilotPaneMode.Hidden, CopilotPaneModes.Classify(true, true, false, true, true));
        }

        [TestMethod]
        public void Classify_HistoryMode_BackVisibleListAndInputOffscreen()
        {
            // 与 UIA 探查一致：历史记录模式下「返回」可见，对话列表与输入框不可见 / Matches the UIA probe of history mode
            Assert.AreEqual(CopilotPaneMode.History, CopilotPaneModes.Classify(true, false, true, false, false));
            Assert.AreEqual(CopilotPaneMode.History, CopilotPaneModes.Classify(true, false, true, true, false));
            Assert.AreEqual(CopilotPaneMode.History, CopilotPaneModes.Classify(true, false, true, false, null));
        }

        [TestMethod]
        public void Classify_Conversation_AndNoInput()
        {
            Assert.AreEqual(CopilotPaneMode.Conversation, CopilotPaneModes.Classify(true, false, false, true, true));
            Assert.AreEqual(CopilotPaneMode.Conversation, CopilotPaneModes.Classify(true, false, null, true, true));
            Assert.AreEqual(CopilotPaneMode.Conversation, CopilotPaneModes.Classify(true, false, true, true, true));
            Assert.AreEqual(CopilotPaneMode.NoInput, CopilotPaneModes.Classify(true, false, false, true, false));
            Assert.AreEqual(CopilotPaneMode.NoInput, CopilotPaneModes.Classify(true, false, null, null, null));
        }

        [TestMethod]
        public void Result_Messages_AreBilingualWithActionableHint()
        {
            var ok = new CopilotPaneOpenResult { Ok = true, LeftHistory = true, Final = CopilotPaneMode.Conversation };
            StringAssert.StartsWith(ok.MessageZh("示例 VS"), "已打开「示例 VS」的对话助手");
            StringAssert.Contains(ok.MessageEn("示例 VS"), "opened");
            StringAssert.Contains(ok.Message("示例 VS"), " / ");

            var fail = new CopilotPaneOpenResult { Final = CopilotPaneMode.History };
            StringAssert.Contains(fail.MessageZh(null), "未能打开对话助手，请手动打开");
            StringAssert.Contains(fail.MessageEn(null), "open it manually");
            StringAssert.Contains(fail.Message(null), CopilotPaneModes.Describe(CopilotPaneMode.History));

            var blocked = new CopilotPaneOpenResult { Blocked = "弹窗 / dialog" };
            StringAssert.Contains(blocked.MessageZh(null), "弹窗");
        }

        [TestMethod]
        public void Result_Diagnostics_ContainsSteps()
        {
            var r = new CopilotPaneOpenResult { Candidates = 2, ElapsedMs = 42 };
            r.Step("step-a");
            string d = r.Diagnostics();
            StringAssert.Contains(d, "step-a");
            StringAssert.Contains(d, "candidates=2");
            StringAssert.Contains(d, "42ms");
        }

        [TestMethod]
        public void Settings_AutoOpenCopilotPane_DefaultsOn()
        {
            Assert.IsTrue(new AppSettings().AutoOpenCopilotPane);
        }
    }
}
