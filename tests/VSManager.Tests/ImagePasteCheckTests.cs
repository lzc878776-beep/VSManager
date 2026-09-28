using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>逐张粘贴图片时按数量确认附件。/ Count-based attachment confirmation while pasting images one by one.</summary>
    [TestClass]
    public class ImagePasteCheckTests
    {
        [TestMethod]
        public void ExactlyOneMore_IsConfirmed_ForEveryImageInTurn()
        {
            for (int confirmed = 0; confirmed < 4; confirmed++)
                Assert.AreEqual(ImagePasteState.Confirmed, ImagePasteCheck.Evaluate(0, confirmed, confirmed + 1));
        }

        [TestMethod]
        public void NoNewAttachmentYet_IsPending_SoTheLoopMayRepaste()
        {
            // 第 3 张的粘贴被丢弃：数量仍是 2，不是异常，而是待补粘 / The 3rd paste was dropped: still 2, pending (re-paste), not unexpected
            Assert.AreEqual(ImagePasteState.Pending, ImagePasteCheck.Evaluate(0, 2, 2));
        }

        [TestMethod]
        public void VanishedOrExtraAttachments_AreUnexpected()
        {
            Assert.AreEqual(ImagePasteState.Unexpected, ImagePasteCheck.Evaluate(0, 2, 1));
            Assert.AreEqual(ImagePasteState.Unexpected, ImagePasteCheck.Evaluate(0, 2, 4));
        }

        [TestMethod]
        public void Baseline_IsCounted()
        {
            Assert.AreEqual(ImagePasteState.Confirmed, ImagePasteCheck.Evaluate(1, 0, 2));
            Assert.AreEqual(ImagePasteState.Pending, ImagePasteCheck.Evaluate(1, 0, 1));
        }
    }
}
