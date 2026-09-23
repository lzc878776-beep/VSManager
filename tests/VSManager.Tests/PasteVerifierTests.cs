using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>粘贴确认：规范化、判定与退避。/ Paste confirmation: normalization, verdicts and back-off.</summary>
    [TestClass]
    public class PasteVerifierTests
    {
        [TestMethod]
        public void Normalize_IgnoresLineBreaksWhitespaceWidthAndZeroWidth()
        {
            string a = "第一行\r\n  第二行：ＡＢＣ　１２３\u200B\uFEFF";
            string b = "第一行\n第二行:ABC 123";
            Assert.AreEqual(PasteVerifier.Normalize(b), PasteVerifier.Normalize(a));
        }

        [TestMethod]
        public void Classify_Identical_IsConfirmed()
        {
            Assert.AreEqual(PasteCheck.Confirmed, PasteVerifier.Classify("a\nb", "", "a\r\nb  "));
        }

        [TestMethod]
        public void Classify_Unreadable_WhenNull()
        {
            Assert.AreEqual(PasteCheck.Unreadable, PasteVerifier.Classify("abc", "", null));
        }

        [TestMethod]
        public void Classify_EmptyOrUnchanged_IsNotWritten()
        {
            Assert.AreEqual(PasteCheck.NotWritten, PasteVerifier.Classify("abc", "", "  "));
            // 之前的误判场景：读到的是对话中代码块的旧内容 / The original false negative: an old code block was read
            Assert.AreEqual(PasteCheck.NotWritten, PasteVerifier.Classify("new task", "git push -u origin main", "git push -u origin main"));
        }

        [TestMethod]
        public void Classify_Prefix_IsPending()
        {
            Assert.AreEqual(PasteCheck.Pending, PasteVerifier.Classify("abcdefghij", "", "abcde"));
        }

        [TestMethod]
        public void Classify_OldDraftMixedIn_IsMismatch()
        {
            Assert.AreEqual(PasteCheck.Mismatch, PasteVerifier.Classify("hello", "draft", "drafthello"));
        }

        [TestMethod]
        public void Classify_TinyDifferenceInLongText_IsNearMatch()
        {
            string want = new string('x', 100) + "middle" + new string('y', 400);
            string got = new string('x', 100) + "midle" + new string('y', 400);
            var c = PasteVerifier.Classify(want, "", got);
            Assert.AreEqual(PasteCheck.NearMatch, c);
            Assert.IsTrue(PasteVerifier.IsConfirmed(c));
        }

        [TestMethod]
        public void Classify_ShortDifferentText_IsNotNearMatch()
        {
            Assert.AreEqual(PasteCheck.Mismatch, PasteVerifier.Classify("abcdef", "", "abcxef"));
        }

        [TestMethod]
        public void NextDelay_BacksOffAndCaps()
        {
            CollectionAssert.AreEqual(new[] { 50, 100, 200, 400, 500, 500 },
                new[] { 0, 1, 2, 3, 4, 10 }.Select(PasteVerifier.NextDelayMs).ToArray());
        }

        [TestMethod]
        public void Snippet_TruncatesAndShowsLength()
        {
            string s = PasteVerifier.Snippet(new string('a', 100), 5, 5);
            StringAssert.Contains(s, "aaaaa…aaaaa");
            StringAssert.Contains(s, "100");
        }
    }
}
