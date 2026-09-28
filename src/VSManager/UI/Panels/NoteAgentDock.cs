using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>笔记助手面板的停靠位置。/ Dock position of the note assistant panel.</summary>
    internal enum NoteDockPosition { Right, Left, Bottom, Float }

    /// <summary>
    /// 笔记本界面的分屏容器：主内容（笔记本）与可拖拽的助手面板（默认各占一半）。
    /// 拖动面板标题栏可停靠到右侧 / 左侧（侧边栏停靠）或底部，拖到中间或窗口外则变为浮动窗口；浮动窗口拖回边缘即可重新停靠。
    /// Split host of the notebook page: main content (the notebook) and a draggable assistant panel (half and half by default).
    /// Drag the panel's title bar to dock it right / left (side dock) or at the bottom; drop it in the middle or outside to float;
    /// drag the floating window back to an edge to dock it again.
    /// </summary>
    internal sealed class NoteAgentDock : Panel
    {
        private const int MinPercent = 15, MaxPercent = 85;

        private readonly Control _content;
        private readonly SplitContainer _split = new SplitContainer();
        private readonly Panel _frame = new Panel();
        private readonly Panel _grip = new Panel();
        private readonly FlatButton _btnLeft, _btnRight, _btnBottom, _btnFloat;
        private readonly ToolTip _tips = new ThemedToolTip();
        private readonly string _title;
        private Form _float;
        private DockPreview _preview;
        private bool _applying;

        // 标题栏拖拽状态 / Title bar drag state
        private Point _pressAt;
        private bool _pressed, _dragging;
        private Point _floatOffset;
        // 浮动窗口原生拖动（系统标题栏）/ Native move of the floating window (system caption)
        private bool _nativeMoving;
        private Size _nativeStartSize;

        /// <summary>当前停靠位置。/ Current dock position.</summary>
        public NoteDockPosition Position { get; private set; } = NoteDockPosition.Right;

        /// <summary>最近一次停靠（非浮动）的位置，关闭浮动窗口时回到这里。/ The last docked (non-floating) position; closing the floating window returns here.</summary>
        public NoteDockPosition LastDocked { get; private set; } = NoteDockPosition.Right;

        /// <summary>停靠时助手面板所占百分比。/ Share of the docked assistant panel, in percent.</summary>
        public int Percent { get; private set; } = 50;

        /// <summary>浮动窗口的屏幕位置与大小。/ Screen bounds of the floating window.</summary>
        public Rectangle FloatBounds { get; private set; }

        /// <summary>用户改变位置、比例或浮动窗口大小后触发（用于保存设置）。/ Raised after the user changes the position, share or floating bounds (to save settings).</summary>
        public event Action LayoutChanged;

        private bool _agentVisible = true;

        /// <summary>是否显示助手面板；隐藏时主内容占满（例如在设置中关闭了 AI 助手）。/ Whether the assistant panel shows; when hidden the content fills the area (e.g. the AI assistant is disabled in settings).</summary>
        public bool AgentVisible
        {
            get => _agentVisible;
            set
            {
                if (_agentVisible == value) return;
                _agentVisible = value;
                Apply(Position, false);
            }
        }

        public NoteAgentDock(Control content, Control panel, string title)
        {
            _content = content;
            _title = title;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            _split.Dock = DockStyle.Fill;
            _split.BackColor = Theme.Divider;
            _split.SplitterWidth = Dpi.S(5);
            _split.Panel1.BackColor = Theme.Background;
            _split.Panel2.BackColor = Theme.Background;
            _split.SplitterMoved += (s, e) =>
            {
                if (_applying || Position == NoteDockPosition.Float) return;
                int total = Total();
                if (total <= 0) return;
                int agent = Position == NoteDockPosition.Left ? _split.SplitterDistance : total - _split.SplitterDistance - _split.SplitterWidth;
                Percent = Clamp((int)Math.Round(agent * 100.0 / total));
                LayoutChanged?.Invoke();
            };

            _grip.Dock = DockStyle.Top;
            _grip.Height = Dpi.S(30);
            _grip.BackColor = Theme.Sidebar;
            _grip.Cursor = Cursors.SizeAll;
            _grip.AccessibleName = "拖动以停靠笔记助手 / Drag to dock the note assistant";
            _grip.Paint += Grip_Paint;
            _grip.MouseDown += Grip_MouseDown;
            _grip.MouseMove += Grip_MouseMove;
            _grip.MouseUp += Grip_MouseUp;
            _grip.MouseCaptureChanged += (s, e) => { if (_dragging && !_grip.Capture) EndDrag(Control.MouseButtons == MouseButtons.None); };
            _btnFloat = GripButton("⧉", "浮动 / Float", NoteDockPosition.Float);
            _btnBottom = GripButton("⬓", "停靠到底部 / Dock at the bottom", NoteDockPosition.Bottom);
            _btnRight = GripButton("◨", "停靠到右侧 / Dock on the right", NoteDockPosition.Right);
            _btnLeft = GripButton("◧", "停靠到左侧 / Dock on the left", NoteDockPosition.Left);

            panel.Dock = DockStyle.Fill;
            _frame.Dock = DockStyle.Fill;
            _frame.BackColor = Theme.Background;
            _frame.Controls.Add(panel);
            _frame.Controls.Add(_grip);

            content.Dock = DockStyle.Fill;
            Controls.Add(_split);
            Resize += (s, e) => ApplySplit();
            VisibleChanged += (s, e) => SyncFloatVisibility();
            Disposed += (s, e) => { _tips.Dispose(); _preview?.Dispose(); _float?.Dispose(); };
            Apply(NoteDockPosition.Right, false);
        }

        /// <summary>按保存的设置恢复布局（不触发 LayoutChanged）。/ Restores the saved layout (does not raise LayoutChanged).</summary>
        public void Restore(string position, int percent, string floatBounds)
        {
            Percent = percent <= 0 ? 50 : Clamp(percent);
            FloatBounds = ParseBounds(floatBounds);
            Apply(ParsePosition(position), false);
        }

        /// <summary>停靠位置的设置字符串。/ Setting string of a dock position.</summary>
        public static string Format(NoteDockPosition p) => p.ToString().ToLowerInvariant();

        public static NoteDockPosition ParsePosition(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "left": return NoteDockPosition.Left;
                case "bottom": return NoteDockPosition.Bottom;
                case "float": return NoteDockPosition.Float;
                default: return NoteDockPosition.Right;
            }
        }

        public static string FormatBounds(Rectangle r) =>
            r.Width <= 0 || r.Height <= 0 ? "" : string.Join(",", r.X, r.Y, r.Width, r.Height);

        public static Rectangle ParseBounds(string s)
        {
            var parts = (s ?? "").Split(',');
            if (parts.Length != 4) return Rectangle.Empty;
            var v = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return Rectangle.Empty;
            return v[2] > 0 && v[3] > 0 ? new Rectangle(v[0], v[1], v[2], v[3]) : Rectangle.Empty;
        }

        /// <summary>
        /// 根据拖拽时鼠标所在位置判断停靠目标：宿主左 / 右 20% 为侧边停靠，底部 25% 为底部停靠，其余（含窗口外）为浮动。
        /// Picks the dock target from the cursor while dragging: the left / right 20 % of the host dock to that side, the bottom 25 % docks at the bottom, anything else (including outside) floats.
        /// </summary>
        public static NoteDockPosition HitTest(Rectangle host, Point screen)
        {
            if (host.Width <= 0 || host.Height <= 0 || !host.Contains(screen)) return NoteDockPosition.Float;
            double x = (screen.X - host.Left) / (double)host.Width, y = (screen.Y - host.Top) / (double)host.Height;
            if (x < 0.2) return NoteDockPosition.Left;
            if (x > 0.8) return NoteDockPosition.Right;
            if (y > 0.75) return NoteDockPosition.Bottom;
            return NoteDockPosition.Float;
        }

        /// <summary>停靠到指定位置后助手面板在宿主中的区域（用于拖拽预览）。/ Area the docked panel would take in the host (for the drag preview).</summary>
        public static Rectangle DockArea(Rectangle host, NoteDockPosition p, int percent)
        {
            int w = host.Width * percent / 100, h = host.Height * percent / 100;
            switch (p)
            {
                case NoteDockPosition.Left: return new Rectangle(host.Left, host.Top, w, host.Height);
                case NoteDockPosition.Right: return new Rectangle(host.Right - w, host.Top, w, host.Height);
                case NoteDockPosition.Bottom: return new Rectangle(host.Left, host.Bottom - h, host.Width, h);
                default: return Rectangle.Empty;
            }
        }

        private static int Clamp(int percent) => Math.Max(MinPercent, Math.Min(MaxPercent, percent));

        private int Total() => _split.Orientation == Orientation.Vertical ? _split.Width : _split.Height;

        private FlatButton GripButton(string text, string tip, NoteDockPosition target)
        {
            var b = new FlatButton { Text = text, Ghost = true, Dock = DockStyle.Right, Width = Dpi.S(30), AccessibleName = tip };
            b.Click += (s, e) => MoveTo(target);
            _tips.SetToolTip(b, tip);
            _grip.Controls.Add(b);
            return b;
        }

        /// <summary>用户操作：移动到指定位置并通知保存。/ User action: move to the position and notify for saving.</summary>
        public void MoveTo(NoteDockPosition target)
        {
            if (target == Position) return;
            Apply(target, true);
            LayoutChanged?.Invoke();
        }

        private void Apply(NoteDockPosition target, bool focusFloat)
        {
            _applying = true;
            SuspendLayout();
            try
            {
                if (target != NoteDockPosition.Float) LastDocked = target;
                Position = target;
                if (!_agentVisible)
                {
                    _frame.Parent?.Controls.Remove(_frame);
                    _split.Panel1.Controls.Add(_content);
                    _split.Panel1Collapsed = false;
                    _split.Panel2Collapsed = true;
                    if (_float != null && _float.Visible) { RememberFloat(); _float.Hide(); }
                }
                else if (target == NoteDockPosition.Float)
                {
                    _split.Panel1.Controls.Add(_content);
                    _split.Panel2Collapsed = true;
                    var form = EnsureFloat();
                    if (!form.Controls.Contains(_frame)) form.Controls.Add(_frame);
                    SyncFloatVisibility();
                    if (focusFloat && form.Visible) form.Activate();
                }
                else
                {
                    bool left = target == NoteDockPosition.Left;
                    _split.Orientation = target == NoteDockPosition.Bottom ? Orientation.Horizontal : Orientation.Vertical;
                    (left ? _split.Panel2 : _split.Panel1).Controls.Add(_content);
                    (left ? _split.Panel1 : _split.Panel2).Controls.Add(_frame);
                    _split.Panel1Collapsed = false;
                    _split.Panel2Collapsed = false;
                    if (_float != null && _float.Visible) { RememberFloat(); _float.Hide(); }
                }
                UpdateButtons();
            }
            finally
            {
                ResumeLayout(true);
                _applying = false;
            }
            ApplySplit();
        }

        private void ApplySplit()
        {
            if (Position == NoteDockPosition.Float || _split.Panel1Collapsed || _split.Panel2Collapsed) return;
            int total = Total();
            int min = Dpi.S(160);
            if (total < min * 2 + _split.SplitterWidth) return;
            int agent = total * Percent / 100;
            int distance = Position == NoteDockPosition.Left ? agent : total - agent - _split.SplitterWidth;
            distance = Math.Max(min, Math.Min(total - min - _split.SplitterWidth, distance));
            if (_split.SplitterDistance == distance) return;
            _applying = true;
            try { _split.SplitterDistance = distance; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentOutOfRangeException) { }
            finally { _applying = false; }
        }

        private void UpdateButtons()
        {
            foreach (var b in new[] { (_btnLeft, NoteDockPosition.Left), (_btnRight, NoteDockPosition.Right), (_btnBottom, NoteDockPosition.Bottom), (_btnFloat, NoteDockPosition.Float) })
                b.Item1.Enabled = b.Item2 != Position;
        }

        private Form EnsureFloat()
        {
            if (_float != null && !_float.IsDisposed) return _float;
            _float = new Form
            {
                Text = _title,
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                BackColor = Theme.Background,
                ForeColor = Theme.Text,
                Font = Theme.Regular,
                MinimumSize = new Size(Dpi.S(320), Dpi.S(360)),
                KeyPreview = false
            };
            var owner = FindForm();
            var bounds = FloatBounds;
            if (bounds.Width <= 0 || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            {
                var host = RectangleToScreen(ClientRectangle);
                var size = new Size(Math.Max(Dpi.S(420), host.Width / 3), Math.Max(Dpi.S(520), host.Height * 2 / 3));
                bounds = new Rectangle(host.Right - size.Width - Dpi.S(24), host.Top + Dpi.S(24), size.Width, size.Height);
            }
            _float.Bounds = bounds;
            if (owner != null) _float.Owner = owner;
            // 关闭浮动窗口 = 停靠回上一次的位置 / Closing the floating window docks it back to the last position
            _float.FormClosing += (s, e) =>
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                MoveTo(LastDocked);
            };
            _float.ResizeBegin += (s, e) => { _nativeMoving = true; _nativeStartSize = _float.Size; };
            _float.Move += (s, e) => { if (_nativeMoving && _float.Size == _nativeStartSize) ShowPreview(HitTest(RectangleToScreen(ClientRectangle), Cursor.Position)); };
            _float.ResizeEnd += (s, e) =>
            {
                bool moved = _float.Size == _nativeStartSize;
                _nativeMoving = false;
                HidePreview();
                RememberFloat();
                var target = moved ? HitTest(RectangleToScreen(ClientRectangle), Cursor.Position) : NoteDockPosition.Float;
                if (target != NoteDockPosition.Float) MoveTo(target);
                else LayoutChanged?.Invoke();
            };
            return _float;
        }

        private void RememberFloat()
        {
            if (_float != null && !_float.IsDisposed && _float.WindowState == FormWindowState.Normal) FloatBounds = _float.Bounds;
        }

        /// <summary>浮动窗口只在笔记本页面可见时显示。/ The floating window shows only while the notebook page is visible.</summary>
        private void SyncFloatVisibility()
        {
            if (_float == null || _float.IsDisposed) return;
            bool show = _agentVisible && Position == NoteDockPosition.Float && Visible && FindForm()?.Visible == true;
            if (show && !_float.Visible) _float.Show(FindForm());
            else if (!show && _float.Visible) { RememberFloat(); _float.Hide(); }
        }

        #region 标题栏拖拽 / Title bar dragging

        private void Grip_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _pressed = true;
            _dragging = false;
            _pressAt = Cursor.Position;
        }

        private void Grip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_pressed || (Control.MouseButtons & MouseButtons.Left) == 0) return;
            var pt = Cursor.Position;
            if (!_dragging)
            {
                var drag = SystemInformation.DragSize;
                if (Math.Abs(pt.X - _pressAt.X) < drag.Width && Math.Abs(pt.Y - _pressAt.Y) < drag.Height) return;
                _dragging = true;
                if (Position == NoteDockPosition.Float && _float != null) _floatOffset = new Point(_pressAt.X - _float.Left, _pressAt.Y - _float.Top);
            }
            if (Position == NoteDockPosition.Float && _float != null)
                _float.Location = new Point(pt.X - _floatOffset.X, pt.Y - _floatOffset.Y);
            var target = HitTest(RectangleToScreen(ClientRectangle), pt);
            ShowPreview(target == Position ? NoteDockPosition.Float : target, target == NoteDockPosition.Float && Position != NoteDockPosition.Float ? pt : (Point?)null);
        }

        private void Grip_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            EndDrag(true);
        }

        private void EndDrag(bool commit)
        {
            bool wasDragging = _dragging;
            _pressed = false;
            _dragging = false;
            HidePreview();
            if (!commit || !wasDragging) return;
            var pt = Cursor.Position;
            var target = HitTest(RectangleToScreen(ClientRectangle), pt);
            if (Position == NoteDockPosition.Float)
            {
                RememberFloat();
                if (target != NoteDockPosition.Float) MoveTo(target);
                else LayoutChanged?.Invoke();
                return;
            }
            if (target == Position) return;
            if (target == NoteDockPosition.Float)
            {
                // 从停靠拖出：浮动窗口出现在松开鼠标的位置 / Dragged out of the dock: the floating window appears where the mouse is released
                var size = FloatBounds.Width > 0 ? FloatBounds.Size : new Size(Math.Max(Dpi.S(420), Width / 3), Math.Max(Dpi.S(520), Height * 2 / 3));
                FloatBounds = new Rectangle(pt.X - size.Width / 2, pt.Y - Dpi.S(12), size.Width, size.Height);
                if (_float != null && !_float.IsDisposed) _float.Bounds = FloatBounds;
            }
            MoveTo(target);
        }

        private void ShowPreview(NoteDockPosition target, Point? floatAt = null)
        {
            var host = RectangleToScreen(ClientRectangle);
            Rectangle area;
            if (target != NoteDockPosition.Float) area = DockArea(host, target, Percent);
            else if (floatAt.HasValue)
            {
                var size = FloatBounds.Width > 0 ? FloatBounds.Size : new Size(Math.Max(Dpi.S(420), Width / 3), Math.Max(Dpi.S(520), Height * 2 / 3));
                area = new Rectangle(floatAt.Value.X - size.Width / 2, floatAt.Value.Y - Dpi.S(12), size.Width, size.Height);
            }
            else { HidePreview(); return; }
            if (_preview == null || _preview.IsDisposed) _preview = new DockPreview();
            _preview.ShowAt(area, FindForm());
        }

        private void HidePreview()
        {
            if (_preview != null && !_preview.IsDisposed && _preview.Visible) _preview.Hide();
        }

        #endregion

        private void Grip_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Sidebar);
            using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, 0, _grip.Height - 1, _grip.Width, _grip.Height - 1);
            int buttons = 0;
            foreach (Control c in _grip.Controls) buttons += c.Width;
            var rect = new Rectangle(Dpi.S(10), 0, Math.Max(0, _grip.Width - buttons - Dpi.S(16)), _grip.Height);
            TextRenderer.DrawText(g, "⠿  " + _title + " · 拖动标题栏停靠 / Drag to dock", Theme.Small, rect, Theme.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        /// <summary>拖拽时显示的半透明停靠预览，不抢焦点、不拦截鼠标。/ Translucent dock preview shown while dragging; never takes focus or the mouse.</summary>
        private sealed class DockPreview : Form
        {
            public DockPreview()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Theme.Accent;
                Opacity = 0.28;
                TopMost = true;
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
                    return cp;
                }
            }

            public void ShowAt(Rectangle area, Form owner)
            {
                Bounds = area;
                if (!Visible)
                {
                    if (owner != null && Owner == null) Owner = owner;
                    Show();
                }
            }
        }
    }
}
