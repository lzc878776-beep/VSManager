using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>一块屏幕的截图（物理像素坐标）。/ A capture of one screen (physical pixel coordinates).</summary>
    internal sealed class ScreenSnipCapture : IDisposable
    {
        public ScreenSnipCapture(Rectangle bounds, Bitmap image) { Bounds = bounds; Image = image; }
        public Rectangle Bounds { get; }
        public Bitmap Image { get; }
        public void Dispose() => Image?.Dispose();
    }

    /// <summary>
    /// 区域截图器：先冻结各屏幕画面，再在每块屏幕上显示遮罩供拖动框选；选好的区域裁剪为新图片。
    /// Region screenshot tool: freezes each screen first, then shows an overlay per screen to drag a selection; the selected
    /// area is cropped into a new image.
    /// </summary>
    internal static class ScreenSnipper
    {
        /// <summary>选区最短边（像素），更小的视为误点。/ Minimum selection side in pixels; anything smaller is treated as a stray click.</summary>
        internal const int MinSide = 3;

        private const int DwmwaCloak = 13;
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        /// <summary>由两个角点得到选区，并限制在 <paramref name="limit"/> 内。/ Selection from two corners, clipped to <paramref name="limit"/>.</summary>
        internal static Rectangle Normalize(Point a, Point b, Size limit)
        {
            int left = Clamp(Math.Min(a.X, b.X), limit.Width), right = Clamp(Math.Max(a.X, b.X), limit.Width);
            int top = Clamp(Math.Min(a.Y, b.Y), limit.Height), bottom = Clamp(Math.Max(a.Y, b.Y), limit.Height);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        internal static bool IsUsable(Rectangle r) => r.Width >= MinSide && r.Height >= MinSide;

        /// <summary>按选区裁剪出独立的新图片；选区过小时返回 null。/ Crops an independent new image; returns null for a too-small selection.</summary>
        internal static Bitmap Crop(Bitmap source, Rectangle area)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            area.Intersect(new Rectangle(Point.Empty, source.Size));
            if (!IsUsable(area)) return null;
            var result = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
                g.DrawImage(source, new Rectangle(Point.Empty, area.Size), area, GraphicsUnit.Pixel);
            return result;
        }

        /// <summary>截取所有屏幕的当前画面。/ Captures the current image of every screen.</summary>
        internal static List<ScreenSnipCapture> CaptureScreens()
        {
            var list = new List<ScreenSnipCapture>();
            try
            {
                foreach (var screen in Screen.AllScreens)
                {
                    var b = screen.Bounds;
                    if (b.Width <= 0 || b.Height <= 0) continue;
                    var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                    try
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
                    }
                    catch { bmp.Dispose(); throw; }
                    list.Add(new ScreenSnipCapture(b, bmp));
                }
                return list;
            }
            catch
            {
                foreach (var c in list) c.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 用 DWM 隐藏（cloak）或恢复窗口，不改变窗口的可见状态，截图时不把自己拍进去。返回是否成功。
        /// Cloaks or uncloaks a window via DWM without changing its visible state, so it stays out of the capture. Returns success.
        /// </summary>
        internal static bool Cloak(IntPtr hwnd, bool cloak)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int value = cloak ? 1 : 0;
                bool ok = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
                if (ok) DwmFlush();
                return ok;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>
        /// 在每块屏幕上显示框选遮罩，返回选中的图片（取消时为 null）。需在界面线程调用；调用方负责释放截图。
        /// Shows a selection overlay on every screen and returns the selected image (null when cancelled). Call on the UI thread;
        /// the caller disposes the captures.
        /// </summary>
        internal static Task<Bitmap> SelectAsync(IList<ScreenSnipCapture> captures)
        {
            var tcs = new TaskCompletionSource<Bitmap>();
            if (captures == null || captures.Count == 0) { tcs.SetResult(null); return tcs.Task; }
            var overlays = new List<SnipOverlay>();
            bool finished = false;
            void Finish(Bitmap result)
            {
                if (finished) { result?.Dispose(); return; }
                finished = true;
                foreach (var o in overlays.ToArray())
                    if (!o.IsDisposed) o.Close();
                tcs.TrySetResult(result);
            }
            foreach (var c in captures)
            {
                var overlay = new SnipOverlay(c.Bounds, c.Image);
                overlay.Completed += Finish;
                overlay.FormClosed += (s, e) => Finish(null);
                overlays.Add(overlay);
            }
            SnipOverlay active = null;
            var cursor = Cursor.Position;
            foreach (var o in overlays)
            {
                o.Show();
                if (o.ScreenBounds.Contains(cursor)) active = o;
            }
            (active ?? overlays[0]).Activate();
            return tcs.Task;
        }

        private static int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;
    }

    /// <summary>
    /// 单块屏幕的框选遮罩：显示冻结画面并调暗，拖动选区处恢复原亮度；松开鼠标完成，Esc 或右键取消。
    /// Selection overlay for one screen: shows the frozen image dimmed, with the dragged area at full brightness; releasing the
    /// mouse finishes, Esc or right-click cancels.
    /// </summary>
    internal sealed class SnipOverlay : Form
    {
        internal const string HintText = "拖动鼠标框选截图区域 · 松开完成 · Esc / 右键取消\nDrag to select an area · release to finish · Esc / right-click to cancel";
        private const int WmDpiChanged = 0x02E0;

        private readonly Bitmap _capture;
        private readonly Bitmap _dim;
        private Point _anchor;
        private bool _dragging;
        private Rectangle _selection;

        /// <summary>完成时触发：选中的图片，取消时为 null。/ Raised on finish with the selected image, or null when cancelled.</summary>
        public event Action<Bitmap> Completed;

        public SnipOverlay(Rectangle screenBounds, Bitmap capture)
        {
            _capture = capture ?? throw new ArgumentNullException(nameof(capture));
            ScreenBounds = screenBounds;
            _dim = new Bitmap(capture.Width, capture.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(_dim))
            {
                g.DrawImage(capture, new Rectangle(Point.Empty, capture.Size), new Rectangle(Point.Empty, capture.Size), GraphicsUnit.Pixel);
                using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(shade, 0, 0, capture.Width, capture.Height);
            }
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            Text = "截图 / Screenshot";
            BackColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            Bounds = screenBounds;
        }

        /// <summary>覆盖的屏幕范围（物理像素）。/ Screen area covered (physical pixels).</summary>
        internal Rectangle ScreenBounds { get; }

        /// <summary>当前选区（窗口客户区坐标，即该屏幕截图中的像素坐标）。/ Current selection in client coordinates (pixels of this screen's capture).</summary>
        internal Rectangle Selection => _selection;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = ScreenBounds;
        }

        protected override void WndProc(ref Message m)
        {
            // 遮罩必须与屏幕像素一一对应，忽略 DPI 变化时的系统建议尺寸 / The overlay must match screen pixels 1:1, so ignore the suggested DPI-change size
            if (m.Msg == WmDpiChanged) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Cancel(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) BeginSelect(e.Location);
            else if (e.Button == MouseButtons.Right)
            {
                // 正在框选时右键只放弃本次选区，否则取消截图 / Right-click drops the current selection while selecting, otherwise cancels
                if (_dragging || !_selection.IsEmpty) ResetSelection();
                else Cancel();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) MoveSelect(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _dragging) EndSelect(e.Location);
        }

        internal void BeginSelect(Point p)
        {
            _anchor = p;
            _dragging = true;
            UpdateSelection(Rectangle.Empty);
            Invalidate(HintBounds());
        }

        internal void MoveSelect(Point p)
        {
            if (_dragging) UpdateSelection(ScreenSnipper.Normalize(_anchor, p, ClientSize));
        }

        internal void EndSelect(Point p)
        {
            if (!_dragging) return;
            UpdateSelection(ScreenSnipper.Normalize(_anchor, p, ClientSize));
            _dragging = false;
            var shot = ScreenSnipper.IsUsable(_selection) ? ScreenSnipper.Crop(_capture, _selection) : null;
            if (shot == null) { ResetSelection(); return; }
            Completed?.Invoke(shot);
        }

        internal void Cancel() => Completed?.Invoke(null);

        private void ResetSelection()
        {
            _dragging = false;
            UpdateSelection(Rectangle.Empty);
            Invalidate(HintBounds());
        }

        private void UpdateSelection(Rectangle next)
        {
            var old = _selection;
            _selection = next;
            int pad = Dpi.S(40);
            var dirty = Rectangle.Union(Inflate(old, pad), Inflate(next, pad));
            if (!dirty.IsEmpty) Invalidate(dirty);
        }

        private static Rectangle Inflate(Rectangle r, int pad)
        {
            if (r.IsEmpty) return Rectangle.Empty;
            r.Inflate(pad, pad);
            return r;
        }

        private Rectangle HintBounds()
        {
            var size = TextRenderer.MeasureText(HintText, Theme.Small);
            int w = size.Width + Dpi.S(32), h = size.Height + Dpi.S(20);
            return new Rectangle((ClientSize.Width - w) / 2, Dpi.S(24), w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            g.DrawImage(_dim, clip, clip, GraphicsUnit.Pixel);
            if (!_selection.IsEmpty)
            {
                var bright = Rectangle.Intersect(_selection, clip);
                if (bright.Width > 0 && bright.Height > 0) g.DrawImage(_capture, bright, bright, GraphicsUnit.Pixel);
                using (var pen = new Pen(Theme.Accent, Math.Max(1, Dpi.S(2))))
                    g.DrawRectangle(pen, _selection.X, _selection.Y, Math.Max(0, _selection.Width - 1), Math.Max(0, _selection.Height - 1));
                string label = _selection.Width + " × " + _selection.Height;
                var size = TextRenderer.MeasureText(label, Theme.Small);
                int lw = size.Width + Dpi.S(12), lh = size.Height + Dpi.S(6);
                int ly = _selection.Top - lh - Dpi.S(4);
                if (ly < 0) ly = _selection.Top + Dpi.S(4);
                var box = new Rectangle(_selection.Left, ly, lw, lh);
                using (var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 26))) g.FillRectangle(bg, box);
                TextRenderer.DrawText(g, label, Theme.Small, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (!_dragging && _selection.IsEmpty)
            {
                var hint = HintBounds();
                if (hint.IntersectsWith(clip))
                {
                    using (var bg = new SolidBrush(Color.FromArgb(230, 20, 20, 26))) g.FillRectangle(bg, hint);
                    using (var pen = new Pen(Theme.Accent)) g.DrawRectangle(pen, hint.X, hint.Y, hint.Width - 1, hint.Height - 1);
                    TextRenderer.DrawText(g, HintText, Theme.Small, hint, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _dim.Dispose();
            base.Dispose(disposing);
        }
    }
}
