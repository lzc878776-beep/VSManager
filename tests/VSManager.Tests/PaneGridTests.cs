using System.Drawing;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>一键布局窗格计算的测试。/ Tests for the one-click layout pane math.</summary>
    [TestClass]
    public class PaneGridTests
    {
        private static readonly Rectangle Area = new Rectangle(1920, 0, 2560, 1400);

        [TestMethod]
        public void Horizontal_SplitsWidthEvenly()
        {
            var r = PaneGrid.Compute(Area, 4, 360, 300, PaneArrangement.Horizontal);
            Assert.AreEqual(4, r.Columns);
            Assert.AreEqual(1, r.Rows);
            Assert.AreEqual(4, r.Cells.Count);
            Assert.IsTrue(r.Cells.All(c => c.Width == 640 && c.Height == 1400 && c.Y == 0));
            Assert.AreEqual(Area.X, r.Cells[0].X);
            Assert.AreEqual(Area.Right, r.Cells[3].Right);
            for (int i = 1; i < 4; i++) Assert.AreEqual(r.Cells[i - 1].Right, r.Cells[i].X);
        }

        [TestMethod]
        public void Horizontal_WrapsWhenBelowMinWidth()
        {
            // 2560 / 360 = 7 列上限；9 个窗格 → 2 行，每行 5 / 4 个 / max 7 columns; 9 panes -> 2 rows of 5 and 4
            var r = PaneGrid.Compute(Area, 9, 360, 300, PaneArrangement.Horizontal);
            Assert.AreEqual(2, r.Rows);
            Assert.AreEqual(5, r.Columns);
            Assert.AreEqual(9, r.Cells.Count);
            Assert.IsTrue(r.Cells.All(c => c.Width >= 360));
            Assert.AreEqual(5, r.Cells.Count(c => c.Y == 0));
            Assert.AreEqual(4, r.Cells.Count(c => c.Y == 700));
            Assert.AreEqual(Area.Right, r.Cells.Last().Right);
            Assert.IsFalse(r.Cramped);
        }

        [TestMethod]
        public void Grid_IsRoughlySquare()
        {
            var r = PaneGrid.Compute(Area, 4, 360, 300, PaneArrangement.Grid);
            Assert.AreEqual(2, r.Columns);
            Assert.AreEqual(2, r.Rows);
            Assert.IsTrue(r.Cells.All(c => c.Width == 1280 && c.Height == 700));
        }

        [TestMethod]
        public void SinglePane_FillsArea_AndEmptyInputIsSafe()
        {
            var r = PaneGrid.Compute(Area, 1, 360, 300, PaneArrangement.Horizontal);
            Assert.AreEqual(Area, r.Cells.Single());
            Assert.AreEqual(0, PaneGrid.Compute(Area, 0, 360, 300, PaneArrangement.Horizontal).Cells.Count);
            Assert.AreEqual(0, PaneGrid.Compute(Rectangle.Empty, 3, 360, 300, PaneArrangement.Horizontal).Cells.Count);
        }

        [TestMethod]
        public void NarrowScreen_MarksCramped()
        {
            var r = PaneGrid.Compute(new Rectangle(0, 0, 800, 600), 6, 360, 300, PaneArrangement.Horizontal);
            Assert.AreEqual(2, r.Columns);
            Assert.AreEqual(3, r.Rows);
            Assert.IsTrue(r.Cramped);
            Assert.AreEqual(6, r.Cells.Count);
        }

        [TestMethod]
        public void PickScreenIndex_AutoPrefersSecondScreen()
        {
            Assert.AreEqual(1, PaneGrid.PickScreenIndex(0, 3));
            Assert.AreEqual(0, PaneGrid.PickScreenIndex(0, 1));
            Assert.AreEqual(2, PaneGrid.PickScreenIndex(3, 3));
            Assert.AreEqual(-1, PaneGrid.PickScreenIndex(4, 3));
            Assert.AreEqual(-1, PaneGrid.PickScreenIndex(0, 0));
        }

        [TestMethod]
        public void ParseArrangement_AcceptsChineseAndEnglish()
        {
            Assert.AreEqual(PaneArrangement.Horizontal, PaneGrid.ParseArrangement(null));
            Assert.AreEqual(PaneArrangement.Horizontal, PaneGrid.ParseArrangement("横向均布"));
            Assert.AreEqual(PaneArrangement.Grid, PaneGrid.ParseArrangement("Grid"));
            Assert.IsNull(PaneGrid.ParseArrangement("diagonal"));
        }
    }
}
