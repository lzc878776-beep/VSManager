using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class MentionLineBreakTests
    {
        private const string Token = "@[#1 My Demo App|39eb61]";

        [TestMethod]
        public void NoBreakInsideConfirmedToken()
        {
            string s = "修复 " + Token + " 按钮";
            int start = s.IndexOf(Token, System.StringComparison.Ordinal);
            for (int p = start + 1; p < start + Token.Length; p++)
            {
                Assert.IsFalse(MentionLineBreak.CanBreakBefore(s, p), "p=" + p);
                Assert.IsTrue(MentionLineBreak.InsideToken(s, p), "p=" + p);
            }
            Assert.IsTrue(MentionLineBreak.CanBreakBefore(s, start));
            Assert.IsTrue(MentionLineBreak.CanBreakBefore(s, start + Token.Length + 1));
            Assert.AreEqual(0, MentionLineBreak.Evaluate(s, start + 3, MentionLineBreak.WbIsDelimiter));
            Assert.AreEqual(start + Token.Length + 1, MentionLineBreak.Evaluate(s, start, MentionLineBreak.WbRight));
            Assert.AreEqual(start, MentionLineBreak.Evaluate(s, start + 10, MentionLineBreak.WbLeft));
        }

        [TestMethod]
        public void TokenDirectlyAfterChinese_CanWrapAsWhole()
        {
            string s = "标签" + Token + "没了";
            Assert.IsTrue(MentionLineBreak.CanBreakBefore(s, 2));
            Assert.IsTrue(MentionLineBreak.CanBreakBefore(s, 2 + Token.Length));
        }

        [TestMethod]
        public void OrdinaryTextKeepsWordAndCjkBreaks()
        {
            Assert.IsTrue(MentionLineBreak.CanBreakBefore("hello world", 6));
            Assert.IsFalse(MentionLineBreak.CanBreakBefore("hello world", 3));
            Assert.IsFalse(MentionLineBreak.CanBreakBefore("hello world", 5));
            Assert.IsTrue(MentionLineBreak.CanBreakBefore("中文字", 1));
            Assert.IsFalse(MentionLineBreak.CanBreakBefore("中文，好", 2));
            Assert.AreEqual(1, MentionLineBreak.Evaluate("a b", 1, MentionLineBreak.WbIsDelimiter));
            // 未确认或被修改的片段按普通文字处理。/ Unconfirmed or edited fragments are ordinary text.
            Assert.IsTrue(MentionLineBreak.CanBreakBefore("@[#1 x y|zz]", 8 - 3));
        }
    }
}
