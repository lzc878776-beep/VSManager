using System;
using System.Drawing;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class ScreenSnipperTests
    {
        [TestMethod]
        public void Normalize_AnyDragDirection_GivesClippedRectangle()
        {
            var limit = new Size(100, 80);
            Assert.AreEqual(Rectangle.FromLTRB(10, 20, 50, 60), ScreenSnipper.Normalize(new Point(50, 60), new Point(10, 20), limit));
            Assert.AreEqual(Rectangle.FromLTRB(10, 20, 50, 60), ScreenSnipper.Normalize(new Point(10, 60), new Point(50, 20), limit));
            Assert.AreEqual(Rectangle.FromLTRB(0, 0, 100, 80), ScreenSnipper.Normalize(new Point(-30, -5), new Point(500, 400), limit), "拖出屏幕时限制在屏幕内 / Dragging off-screen is clipped");
        }

        [TestMethod]
        public void Crop_ReturnsIndependentPixels_AndRejectsTinySelections()
        {
            using (var source = new Bitmap(20, 20))
            {
                using (var g = Graphics.FromImage(source)) g.Clear(Color.Blue);
                source.SetPixel(5, 6, Color.Red);
                using (var shot = ScreenSnipper.Crop(source, new Rectangle(5, 6, 4, 3)))
                {
                    Assert.AreEqual(new Size(4, 3), shot.Size);
                    Assert.AreEqual(Color.Red.ToArgb(), shot.GetPixel(0, 0).ToArgb());
                    Assert.AreEqual(Color.Blue.ToArgb(), shot.GetPixel(1, 0).ToArgb());
                    source.SetPixel(5, 6, Color.Green);
                    Assert.AreEqual(Color.Red.ToArgb(), shot.GetPixel(0, 0).ToArgb(), "裁剪结果不共享原图 / The crop does not share the source");
                }
                Assert.IsNull(ScreenSnipper.Crop(source, new Rectangle(1, 1, 2, 10)), "过小的选区视为误点 / Tiny selections are stray clicks");
                using (var edge = ScreenSnipper.Crop(source, new Rectangle(15, 15, 50, 50)))
                    Assert.AreEqual(new Size(5, 5), edge.Size, "超出部分被裁掉 / Parts outside the image are dropped");
            }
        }

        [TestMethod]
        public void Overlay_DragSelect_CompletesWithCroppedImage_ClickAndEscapeDoNot()
        {
            RunSta(() =>
            {
                using (var capture = new Bitmap(200, 100))
                {
                    using (var g = Graphics.FromImage(capture)) g.Clear(Color.Yellow);
                    capture.SetPixel(30, 40, Color.Black);
                    using (var overlay = new SnipOverlay(new Rectangle(1000, 0, 200, 100), capture))
                    {
                        int calls = 0;
                        Bitmap result = null;
                        overlay.Completed += b => { calls++; result = b; };

                        overlay.BeginSelect(new Point(30, 40));
                        overlay.EndSelect(new Point(31, 41));
                        Assert.AreEqual(0, calls, "单击不截图 / A click does not finish");
                        Assert.IsTrue(overlay.Selection.IsEmpty);

                        overlay.BeginSelect(new Point(80, 70));
                        overlay.MoveSelect(new Point(50, 50));
                        Assert.AreEqual(Rectangle.FromLTRB(50, 50, 80, 70), overlay.Selection);
                        overlay.EndSelect(new Point(30, 40));
                        Assert.AreEqual(1, calls);
                        using (result)
                        {
                            Assert.AreEqual(new Size(50, 30), result.Size);
                            Assert.AreEqual(Color.Black.ToArgb(), result.GetPixel(0, 0).ToArgb());
                        }

                        overlay.Cancel();
                        Assert.AreEqual(2, calls);
                        Assert.IsNull(result, "取消时结果为空 / Cancel yields null");
                    }
                }
            });
        }

        [TestMethod]
        [TestCategory(TestKind.Ui)]
        public void SelectAsync_CoversEveryScreen_AndCancelClosesAll()
        {
            RunSta(() =>
            {
                var captures = ScreenSnipper.CaptureScreens();
                try
                {
                    Assert.AreEqual(System.Windows.Forms.Screen.AllScreens.Length, captures.Count);
                    foreach (var c in captures) Assert.AreEqual(c.Bounds.Size, c.Image.Size, "截图与屏幕像素一致 / Capture matches screen pixels");
                    var task = ScreenSnipper.SelectAsync(captures);
                    var overlays = System.Windows.Forms.Application.OpenForms.OfType<SnipOverlay>().ToList();
                    Assert.AreEqual(captures.Count, overlays.Count, "每块屏幕一个遮罩 / One overlay per screen");
                    foreach (var o in overlays)
                    {
                        Assert.IsTrue(o.Visible && o.TopMost);
                        Assert.AreEqual(o.ScreenBounds, o.Bounds, "遮罩覆盖整块屏幕 / The overlay covers the whole screen");
                    }
                    overlays[0].Cancel();
                    Assert.IsTrue(task.IsCompleted);
                    Assert.IsNull(task.Result);
                    Assert.IsTrue(overlays.All(o => o.IsDisposed), "取消后全部关闭 / Cancelling closes every overlay");
                }
                finally { foreach (var c in captures) c.Dispose(); }
            });
        }

        private static void RunSta(Action action)
        {
            ExceptionDispatchInfo error = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            error?.Throw();
        }
    }
}
