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

        [TestMethod]
        public void CollapsedImageChips_AreCountedThroughSizeOfSet()
        {
            // 「添加引用」+「活动文档」+ 2 张图片：UI 自动化只显示 1 个图片项，SizeOfSet=4
            // Add-reference + active document + 2 images: UIA shows a single image item, SizeOfSet=4
            Assert.AreEqual(2, ImagePasteCheck.CountAttachments(1, 2, 4));
            Assert.AreEqual(ImagePasteState.Confirmed, ImagePasteCheck.Evaluate(0, 1, ImagePasteCheck.CountAttachments(1, 2, 4)));
            Assert.AreEqual(0, ImagePasteCheck.CountAttachments(0, 2, 2));
        }

        [TestMethod]
        public void MissingOrInconsistentSizeOfSet_FallsBackToVisibleCount()
        {
            Assert.AreEqual(1, ImagePasteCheck.CountAttachments(1, 2, null));
            Assert.AreEqual(3, ImagePasteCheck.CountAttachments(3, 1, 2));
            Assert.AreEqual(0, ImagePasteCheck.CountAttachments(0, 0, null));
        }
    }
}
