using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>Copilot 输入框定位纯逻辑的测试。/ Tests for the pure Copilot input locating logic.</summary>
    [TestClass]
    public class InputLocatorTests
    {
        private static InputCandidate Input(string name = "询问 Copilot", double bottom = 1000, int order = 0) => new InputCandidate
        {
            Name = name, AutomationId = "WpfTextView", ClassName = "WpfTextView",
            Enabled = true, KeyboardFocusable = true, HasTextPattern = true, Bottom = bottom, Order = order
        };

        [TestMethod]
        public void Editable_ExcludesNonInputs()
        {
            Assert.IsTrue(Input().Editable);
            var c = Input(); c.InConversation = true; Assert.IsFalse(c.Editable);
            c = Input(); c.Enabled = false; Assert.IsFalse(c.Editable);
            c = Input(); c.KeyboardFocusable = false; Assert.IsFalse(c.Editable);
            c = Input(); c.ReadOnly = true; Assert.IsFalse(c.Editable);
            c = Input(); c.ReadOnly = false; Assert.IsTrue(c.Editable);
            c = Input(); c.HasTextPattern = false; Assert.IsFalse(c.Editable);
            c = Input(); c.Error = "ElementNotAvailableException"; Assert.IsTrue(c.Stale); Assert.IsFalse(c.Editable);
        }

        [TestMethod]
        public void PickLowest_PrefersBottomAndSkipsNonEditable()
        {
            var code = Input("代码块", 1500, 0); code.InConversation = true;
            var upper = Input("上方", 800, 1);
            var lower = Input("输入框", 1200, 2);
            var unknown = Input("未知", double.NaN, 3);
            Assert.AreSame(lower, InputLocator.PickLowest(new[] { code, upper, lower, unknown }));
            Assert.AreSame(unknown, InputLocator.PickLowest(new[] { unknown }));

            var tieA = Input("A", 900, 4); var tieB = Input("B", 900, 5);
            Assert.AreSame(tieB, InputLocator.PickLowest(new[] { tieB, tieA }));
            Assert.IsNull(InputLocator.PickLowest(new[] { code }));
            Assert.IsNull(InputLocator.PickLowest(null));
        }

        [TestMethod]
        public void PickFirst_UsesDocumentOrder()
        {
            var ro = Input("只读", 0, 0); ro.ReadOnly = true;
            var a = Input("A", 0, 2); var b = Input("B", 0, 1);
            Assert.AreSame(b, InputLocator.PickFirst(new[] { ro, a, b }));
            Assert.IsNull(InputLocator.PickFirst(new InputCandidate[0]));
        }

        [TestMethod]
        public void Classify_DistinguishesReasons()
        {
            Assert.AreEqual(LocateOutcome.PaneNotFound, InputLocator.Classify(false, new[] { Input() }));
            Assert.AreEqual(LocateOutcome.Found, InputLocator.Classify(true, new[] { Input() }));
            Assert.AreEqual(LocateOutcome.InputNotFound, InputLocator.Classify(true, null));

            var inList = Input(); inList.InConversation = true;
            var stale = Input(); stale.Error = "x";
            var hidden = Input(); hidden.ReadOnly = true; hidden.Offscreen = true;
            var title = Input(); title.AutomationId = "chatTitleEditor"; title.KeyboardFocusable = false;
            var picker = Input(); picker.AutomationId = "PART_EditableTextBox"; picker.KeyboardFocusable = false;
            Assert.AreEqual(LocateOutcome.InputNotFound, InputLocator.Classify(true, new[] { inList, stale, hidden, title, picker }));

            var disabled = Input(); disabled.Enabled = false;
            Assert.AreEqual(LocateOutcome.InputReadOnly, InputLocator.Classify(true, new[] { inList, disabled }));
        }

        [TestMethod]
        public void NextDelayMs_BacksOffAndCaps()
        {
            CollectionAssert.AreEqual(new[] { 150, 300, 600, 1000, 1000 }, Enumerable.Range(0, 5).Select(InputLocator.NextDelayMs).ToArray());
        }

        [TestMethod]
        public void FailureMessage_DistinctAndBilingual()
        {
            var msgs = new List<string>();
            foreach (var o in new[] { LocateOutcome.PaneNotFound, LocateOutcome.InputNotFound, LocateOutcome.InputReadOnly })
            {
                var m = InputLocator.FailureMessage(o, 2);
                StringAssert.Contains(m, "已尝试 2 次");
                StringAssert.Contains(m, "tried 2 time(s)");
                StringAssert.Contains(m, " / ");
                Assert.IsFalse(m.StartsWith("已发送"), "不得被视为发送成功 / must not look like success");
                msgs.Add(m);
            }
            Assert.AreEqual(3, msgs.Distinct().Count());
            StringAssert.Contains(msgs[0], "对话窗格");
            StringAssert.Contains(msgs[1], "未找到 Copilot 输入框");
            StringAssert.Contains(msgs[2], "不可编辑");
        }
    }
}
