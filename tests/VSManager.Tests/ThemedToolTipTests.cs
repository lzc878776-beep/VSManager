using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class ThemedToolTipTests
    {
        [TestMethod]
        public void Parse_SplitsBilingualLabelsTitleAndSections()
        {
            var blocks = ThemedToolTip.Parse("拖拽调整显示位置 / Drag to reorder display\r\n条目：默认显示 / Entries: default display\r\n\r\n排队中\r\nWaiting in queue");
            Assert.AreEqual(3, blocks.Count);
            Assert.IsTrue(blocks[0].Title);
            Assert.AreEqual("拖拽调整显示位置", blocks[0].Primary);
            Assert.AreEqual("Drag to reorder display", blocks[0].Secondary);
            Assert.AreEqual("条目：", blocks[1].Label);
            Assert.AreEqual("默认显示", blocks[1].Primary);
            Assert.AreEqual("Entries: default display", blocks[1].Secondary);
            Assert.IsTrue(blocks[2].GapBefore);
            Assert.AreEqual("Waiting in queue", blocks[2].Secondary, "紧随的英文行并入 / Following English line joins");
        }

        [TestMethod]
        public void SplitBilingual_SkipsSlashesInsideChinese()
        {
            ThemedToolTip.SplitBilingual("点击折叠 / 展开 / Click to collapse / expand", out string zh, out string en);
            Assert.AreEqual("点击折叠 / 展开", zh);
            Assert.AreEqual("Click to collapse / expand", en);
            ThemedToolTip.SplitBilingual("Only English / text", out zh, out en);
            Assert.IsNull(en);
        }

        [TestMethod]
        public void Layout_LongTextWrapsNearFixedAspectRatio_AndDelayIsSet()
        {
            string line = string.Concat(Enumerable.Repeat("实际执行仍按编号及前序规则，", 30)) + " / " + string.Concat(Enumerable.Repeat("Execution still follows IDs and predecessors. ", 12));
            var size = ThemedToolTip.Layout(ThemedToolTip.Parse(line));
            double ratio = (double)size.Width / size.Height;
            Assert.IsTrue(ratio > 1.2 && ratio < 4.5, "ratio " + ratio + " size " + size);
            using (var tip = new ThemedToolTip())
            {
                Assert.IsTrue(tip.OwnerDraw);
                Assert.AreEqual(ThemedToolTip.HoverDelay, tip.InitialDelay);
            }
        }
    }
}