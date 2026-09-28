using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class ManualChatWhitespaceTests
    {
        [DataTestMethod]
        [DataRow("")] [DataRow("   ")] [DataRow("\t\r\n")]
        [DataRow("\u00A0\u202F\u3000")] [DataRow("\u200B\u200C\u200D\u2060\uFEFF")]
        [DataRow("\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069")]
        [DataRow(" \t\r\n\u00A0\u200B\uFEFF\u3000")]
        public void IgnorableInputIsEmpty(string text)
        {
            Assert.IsTrue(ManualChatProtection.IsEmptyInput(text));
            Assert.IsFalse(ManualChatProtection.HasDraft(text));
            Assert.AreEqual("", ManualChatProtection.NormalizeInput(text));
        }

        [DataTestMethod]
        [DataRow("中")] [DataRow("a")] [DataRow("1")] [DataRow("，。！？")]
        [DataRow("\U0001F600")] [DataRow("\u0301")] [DataRow("\u064E")]
        [DataRow("\uFE0F")] [DataRow("\0")] [DataRow("\u001B")] [DataRow("\uD800")]
        [DataRow(" \u200B文字\uFEFF ")] [DataRow(" \t！\r\n")]
        public void AnyActualCharacterRemainsProtected(string text)
        {
            Assert.IsFalse(ManualChatProtection.IsEmptyInput(text));
            Assert.IsTrue(ManualChatProtection.HasDraft(text));
        }

        [TestMethod]
        public void UnreadableIsNeverEmptyAndAttachmentsOrBusyStillBlock()
        {
            Assert.IsFalse(ManualChatProtection.IsEmptyInput(null));
            Assert.AreEqual(ManualChatObservation.Unknown, ManualChatProtection.Classify(false, false, false, false));
            Assert.AreEqual(ManualChatObservation.Draft, ManualChatProtection.Classify(false, true,
                ManualChatProtection.HasDraft("\u200B") || 1 > 0, false));
            Assert.AreEqual(ManualChatObservation.Generating, ManualChatProtection.Classify(true, true, false, true));
            Assert.AreEqual(ManualChatObservation.Idle, ManualChatProtection.Classify(false, true,
                ManualChatProtection.HasDraft("\u200B"), true));
        }
    }
}
