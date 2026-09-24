using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VSManager
{
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(12, 12, 15);
        public static readonly Color Sidebar = Color.FromArgb(18, 18, 22);
        public static readonly Color Surface = Color.FromArgb(23, 23, 28);
        public static readonly Color SurfaceAlt = Color.FromArgb(29, 29, 35);
        public static readonly Color Elevated = Color.FromArgb(36, 36, 43);
        public static readonly Color Border = Color.FromArgb(46, 46, 56);
        public static readonly Color Divider = Color.FromArgb(32, 32, 39);
        public static readonly Color Text = Color.FromArgb(232, 232, 238);
        public static readonly Color TextSecondary = Color.FromArgb(163, 163, 177);
        public static readonly Color TextMuted = Color.FromArgb(110, 110, 125);

        public static readonly Color Accent = Color.FromArgb(139, 92, 246);
        public static readonly Color AccentHover = Color.FromArgb(157, 116, 250);
        public static readonly Color AccentPressed = Color.FromArgb(118, 72, 226);
        public static readonly Color AccentLight = Color.FromArgb(40, 32, 66);
        public static readonly Color AccentBorder = Color.FromArgb(92, 68, 160);
        public static readonly Color AccentText = Color.FromArgb(198, 180, 255);

        public static readonly Color HeaderStart = Color.FromArgb(15, 15, 19);
        public static readonly Color HeaderEnd = Color.FromArgb(30, 22, 52);

        public static readonly Color RowHover = Color.FromArgb(28, 28, 35);
        public static readonly Color RowSelected = Color.FromArgb(37, 31, 60);

        public static readonly Color BusyFg = Color.FromArgb(252, 196, 72);
        public static readonly Color BusyBg = Color.FromArgb(56, 42, 12);
        public static readonly Color BusyDot = Color.FromArgb(245, 158, 11);
        public static readonly Color IdleFg = Color.FromArgb(74, 222, 160);
        public static readonly Color IdleBg = Color.FromArgb(13, 46, 35);
        public static readonly Color IdleDot = Color.FromArgb(16, 185, 129);
        public static readonly Color NoneFg = Color.FromArgb(150, 150, 164);
        public static readonly Color NoneBg = Color.FromArgb(36, 36, 44);
        public static readonly Color NoneDot = Color.FromArgb(96, 96, 110);
        public static readonly Color Danger = Color.FromArgb(248, 113, 113);
        public static readonly Color Success = Color.FromArgb(52, 211, 153);
        public static readonly Color Warning = Color.FromArgb(251, 191, 36);

        public const string FontName = "Microsoft YaHei UI";
        public static readonly Font Regular = new Font(FontName, 9F);
        public static readonly Font Small = new Font(FontName, 8.25F);
        public static readonly Font SemiBold = new Font(FontName, 9F, FontStyle.Bold);
        public static readonly Font CardTitle = new Font(FontName, 10F, FontStyle.Bold);
        public static readonly Font AppTitle = new Font(FontName, 12F, FontStyle.Bold);
        public static readonly Font Big = new Font(FontName, 12F, FontStyle.Bold);

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var p = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var p = RoundRect(r, radius))
            using (var pen = new Pen(c))
                g.DrawPath(pen, p);
        }

        public static void FillCircle(Graphics g, Color c, float cx, float cy, float radius)
        {
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, cx - radius, cy - radius, radius * 2, radius * 2);
        }

        /// <summary>绘制圆角胶囊标签，返回其宽度。</summary>
        public static int DrawPill(Graphics g, int x, int y, int maxWidth, string text, Color bg, Color fg, Color? dot, Color? border = null)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            var sz = TextRenderer.MeasureText(g, text, Small, Size.Empty, flags);
            int lead = dot.HasValue ? S(20) : S(9);
            int h = S(22);
            int w = Math.Min(maxWidth, sz.Width + lead + S(9));
            if (w <= lead) return 0;
            var r = new RectangleF(x, y, w, h);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillRound(g, bg, r, h / 2f);
            if (border.HasValue) DrawRound(g, border.Value, r, h / 2f);
            if (dot.HasValue) FillCircle(g, dot.Value, r.X + S(11), r.Y + h / 2f, 3.5f * Dpi.Scale);
            g.SmoothingMode = old;
            TextRenderer.DrawText(g, text, Small, new Rectangle(x + lead, y, w - lead - S(5), h), fg,
                flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            return w;
        }

        private static int S(int v) => Dpi.S(v);

        public static void Apply(ToolStrip strip)
        {
            strip.Renderer = new MenuRenderer();
            strip.Font = Regular;
            strip.BackColor = Elevated;
            strip.ForeColor = Text;
        }

        private sealed class MenuRenderer : ToolStripProfessionalRenderer
        {
            public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                Color color = e.Item is MenuGroupHeader ? TextSecondary : e.Item.Enabled ? e.TextColor : TextMuted;
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
            }
        }

        private class MenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Color.FromArgb(48, 40, 78);
            public override Color MenuItemBorder => AccentBorder;
            public override Color MenuBorder => Border;
            public override Color ToolStripDropDownBackground => Elevated;
            public override Color ImageMarginGradientBegin => Elevated;
            public override Color ImageMarginGradientMiddle => Elevated;
            public override Color ImageMarginGradientEnd => Elevated;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Elevated;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr h, string app, string idList);

        /// <summary>深色标题栏（Win10 1809+ / Win11）。</summary>
        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                int caption = ColorTranslator.ToWin32(HeaderStart);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                int text = ColorTranslator.ToWin32(Text);
                DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
            }
            catch { }
        }

        /// <summary>深色滚动条等系统控件外观。</summary>
        public static void DarkControl(Control c, string theme = "DarkMode_Explorer")
        {
            try
            {
                if (c.IsHandleCreated) SetWindowTheme(c.Handle, theme, null);
                c.HandleCreated += (s, e) => SetWindowTheme(c.Handle, theme, null);
            }
            catch { }
        }
    }

    /// <summary>暗色下拉框：自绘编辑区与下拉项。</summary>
    public class DarkCombo : ComboBox
    {
        private bool _hover;

        public DarkCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Elevated;
            ForeColor = Theme.Text;
            Font = Theme.Regular;
            ItemHeight = Dpi.S(24);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Theme.DarkControl(this, "DarkMode_CFD");
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var bg = Parent?.BackColor ?? Theme.Surface;
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.Elevated, r, Dpi.S(6));
            Theme.DrawRound(g, Focused || DroppedDown ? Theme.Accent : _hover ? Theme.AccentBorder : Theme.Border, r, Dpi.S(6));
            float ax = Width - Dpi.S(18), ay = Height / 2f;
            using (var pen = new Pen(Theme.TextSecondary, Dpi.S(3) / 2f))
                g.DrawLines(pen, new[] { new PointF(ax - Dpi.S(4), ay - Dpi.S(2)), new PointF(ax, ay + Dpi.S(2)), new PointF(ax + Dpi.S(4), ay - Dpi.S(2)) });
            g.SmoothingMode = SmoothingMode.None;
            var tr = new Rectangle(Dpi.S(10), 0, Width - Dpi.S(40), Height);
            TextRenderer.DrawText(g, SelectedItem == null ? "" : GetItemText(SelectedItem), Font, tr, Enabled ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.AccentLight : Theme.Elevated)) e.Graphics.FillRectangle(b, e.Bounds);
            var tr = new Rectangle(e.Bounds.X + Dpi.S(8), e.Bounds.Y, e.Bounds.Width - Dpi.S(8), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, tr, sel ? Color.White : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>圆角卡片容器，顶部绘制标题。</summary>
    public class Card : Panel
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }

        public Card(string title, string subtitle = null)
        {
            Title = title;
            Subtitle = subtitle;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Padding = new Padding(Dpi.S(16), Dpi.S(46), Dpi.S(16), Dpi.S(12));
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.BackColor = Theme.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Dpi.S(10);
            Theme.FillRound(g, Theme.Surface, r, radius);
            Theme.DrawRound(g, Theme.Border, r, radius);

            int x = Dpi.S(16), top = Dpi.S(14), h = Dpi.S(20);
            Theme.FillRound(g, Theme.Accent, new RectangleF(x, top + Dpi.S(3), Dpi.S(4), h - Dpi.S(6)), Dpi.S(2));
            g.SmoothingMode = SmoothingMode.None;
            var tr = new Rectangle(x + Dpi.S(12), top, Width, h);
            TextRenderer.DrawText(g, Title, Theme.CardTitle, tr, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(Subtitle))
            {
                int tw = TextRenderer.MeasureText(g, Title, Theme.CardTitle, Size.Empty, TextFormatFlags.NoPadding).Width;
                var sr = new Rectangle(tr.X + tw + Dpi.S(12), top, Width - tr.X - tw - Dpi.S(12) - SubtitleRightInset, h);
                TextRenderer.DrawText(g, Subtitle, Theme.Small, sr, Theme.TextMuted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>副标题右侧留白（为右上角按钮预留）。</summary>
        public int SubtitleRightInset { get; set; } = Dpi.S(16);
    }

    /// <summary>扁平圆角按钮，支持主按钮 / 次按钮 / 幽灵按钮样式。</summary>
    public class FlatButton : Button
    {
        private bool _hover, _down;
        public bool Primary { get; set; }
        /// <summary>无边框透明背景，仅悬停时显示底色。</summary>
        public bool Ghost { get; set; }
        public Color? Tint { get; set; }

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = Theme.Regular;
            ForeColor = Theme.Text;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Dpi.S(6);
            Color fg;
            if (!Enabled)
            {
                if (!Ghost) Theme.FillRound(g, Color.FromArgb(26, 26, 32), r, radius);
                fg = Color.FromArgb(78, 78, 90);
            }
            else if (Primary)
            {
                Theme.FillRound(g, _down ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent, r, radius);
                fg = Color.White;
            }
            else if (Ghost)
            {
                if (_hover || _down) Theme.FillRound(g, _down ? Theme.AccentLight : Theme.Elevated, r, radius);
                fg = _hover ? Theme.Text : Tint ?? Theme.TextSecondary;
            }
            else
            {
                Theme.FillRound(g, _down ? Theme.AccentLight : _hover ? Color.FromArgb(44, 44, 53) : Theme.Elevated, r, radius);
                Theme.DrawRound(g, _hover ? Theme.AccentBorder : Theme.Border, r, radius);
                fg = Tint ?? (_hover ? Color.White : Theme.Text);
            }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>开关样式的复选框：左侧文字，右侧滑块。</summary>
    public class ToggleSwitch : CheckBox
    {
        private bool _hover;
        public string Description { get; set; }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = Theme.Regular;
            ForeColor = Theme.Text;
            AutoSize = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            int sw = Dpi.S(36), sh = Dpi.S(20);
            var track = new RectangleF(Width - sw - 1, (Height - sh) / 2f, sw, sh);

            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int tw = (int)track.X - Dpi.S(10);
            if (string.IsNullOrEmpty(Description))
            {
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, tw, Height), _hover ? Color.White : Theme.Text, flags | TextFormatFlags.VerticalCenter);
            }
            else
            {
                int lh = Dpi.S(20);
                int y = (Height - lh * 2) / 2;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, y, tw, lh), _hover ? Color.White : Theme.Text, flags | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, Description, Theme.Small, new Rectangle(0, y + lh, tw, lh), Theme.TextMuted, flags | TextFormatFlags.VerticalCenter);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color trackColor = Checked ? (_hover ? Theme.AccentHover : Theme.Accent) : (_hover ? Color.FromArgb(72, 72, 84) : Color.FromArgb(58, 58, 68));
            Theme.FillRound(g, trackColor, track, sh / 2f);
            float knob = sh - Dpi.S(6);
            float kx = Checked ? track.Right - knob - Dpi.S(3) : track.X + Dpi.S(3);
            Theme.FillCircle(g, Checked ? Color.White : Color.FromArgb(200, 200, 210), kx + knob / 2, track.Y + sh / 2f, knob / 2);
        }
    }

    /// <summary>侧边栏 VS 列表：自绘卡片式条目，支持悬停与空状态提示。</summary>
    public class VsListBox : ListBox
    {
        public int HoverIndex { get; private set; } = -1;
        public string EmptyText { get; set; } = "";
        private bool _inPaint;

        public VsListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            BackColor = Theme.Sidebar;
            ForeColor = Theme.Text;
            // 全部在双缓冲的 OnPaint 中绘制，避免逐项直接绘制到屏幕造成闪烁
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Theme.DarkControl(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, e.ClipRectangle);
            if (Items.Count == 0)
            {
                if (!string.IsNullOrEmpty(EmptyText))
                {
                    var r = new Rectangle(Dpi.S(16), Dpi.S(40), ClientSize.Width - Dpi.S(32), Dpi.S(120));
                    TextRenderer.DrawText(g, EmptyText, Theme.Regular, r, Theme.TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                }
                return;
            }
            _inPaint = true;
            try
            {
                for (int i = Math.Max(0, TopIndex); i < Items.Count; i++)
                {
                    var r = GetItemRectangle(i);
                    if (r.Top >= ClientSize.Height) break;
                    if (!r.IntersectsWith(e.ClipRectangle)) continue;
                    var st = DrawItemState.None;
                    if (i == SelectedIndex) st |= DrawItemState.Selected;
                    if (Focused && i == SelectedIndex) st |= DrawItemState.Focus;
                    OnDrawItem(new DrawItemEventArgs(g, Font, r, i, st));
                }
            }
            finally { _inPaint = false; }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            // 原生 WM_DRAWITEM（选中变化等）直接画到屏幕，改为失效后由缓冲绘制
            if (!_inPaint) { if (e.Index >= 0) Invalidate(e.Bounds); return; }
            base.OnDrawItem(e);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            // 不可选中的行（如分组标题）：按移动方向跳到相邻的可选条目 / Non-selectable rows (e.g. group headers): jump to the neighbouring selectable item in the direction of travel
            if (!_adjusting && IsItemSelectable != null && SelectedIndex >= 0 && SelectedIndex < Items.Count && !IsItemSelectable(Items[SelectedIndex]))
            {
                int from = SelectedIndex, dir = from >= _lastSelected ? 1 : -1;
                int target = FindSelectable(from, dir);
                if (target < 0) target = FindSelectable(from, -dir);
                _adjusting = true;
                try { SelectedIndex = target; }
                finally { _adjusting = false; }
                return;
            }
            _lastSelected = SelectedIndex;
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }

        /// <summary>
        /// 判断条目是否可选中；为 null 时全部可选。不可选的行被点击时不改变选中项，而是触发 <see cref="InertItemClicked"/>。
        /// Whether an item can be selected; null means all can. Clicking a non-selectable row does not change the selection and
        /// raises <see cref="InertItemClicked"/> instead.
        /// </summary>
        public Func<object, bool> IsItemSelectable { get; set; }

        /// <summary>不可选的行被鼠标点击（行号、按钮）。/ A non-selectable row was clicked (index, button).</summary>
        public event Action<int, MouseButtons> InertItemClicked;

        private int _lastSelected = -1;
        private bool _adjusting;

        private int FindSelectable(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < Items.Count; i += dir)
                if (IsItemSelectable(Items[i])) return i;
            return -1;
        }

        private bool HandleInertClick(ref Message m)
        {
            if (IsItemSelectable == null) return false;
            var p = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            int idx = IndexFromPoint(p);
            if (idx < 0 || idx >= Items.Count || !GetItemRectangle(idx).Contains(p) || IsItemSelectable(Items[idx])) return false;
            if (!Focused) Focus();
            var button = m.Msg == 0x0204 || m.Msg == 0x0206 ? MouseButtons.Right : MouseButtons.Left;
            if (button == MouseButtons.Right) { _lastSelected = -1; SelectedIndex = -1; }
            InertItemClicked?.Invoke(idx, button);
            m.Result = IntPtr.Zero;
            return true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = IndexFromPoint(e.Location);
            if (idx < 0 || idx >= Items.Count || !GetItemRectangle(idx).Contains(e.Location)) idx = -1;
            SetHover(idx);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(-1);
        }

        /// <summary>滚轮每格滚动的条目数；0（默认）使用系统原生滚动。条目较高时设为 1，避免一格跳过整屏。</summary>
        public int WheelItemsPerNotch { get; set; }
        private int _wheelDelta;

        /// <summary>按滚轮增量滚动（累计触控板等高精度小增量，边界处夹住），不改变选中项。返回是否已处理。</summary>
        public bool ScrollByWheel(int delta)
        {
            if (WheelItemsPerNotch <= 0) return false;
            if (Items.Count == 0 || delta == 0) return true;
            if (Math.Sign(delta) != Math.Sign(_wheelDelta)) _wheelDelta = 0;
            _wheelDelta += delta;
            int notches = _wheelDelta / 120;
            if (notches == 0) return true;
            _wheelDelta -= notches * 120;
            int max = MaxTopIndex();
            int top = Math.Max(0, Math.Min(max, TopIndex - notches * WheelItemsPerNotch));
            if (top != TopIndex)
            {
                TopIndex = top;
                var p = PointToClient(Cursor.Position);
                int idx = ClientRectangle.Contains(p) ? IndexFromPoint(p) : -1;
                if (idx < 0 || idx >= Items.Count || !GetItemRectangle(idx).Contains(p)) idx = -1;
                SetHover(idx);
            }
            return true;
        }

        /// <summary>
        /// 最大顶部行号：从末尾累加行高，兼容固定与可变行高（分组标题比任务卡片矮）。
        /// Largest top index: accumulates row heights from the end, so it works for fixed and variable heights (group headers are
        /// shorter than task cards).
        /// </summary>
        private int MaxTopIndex()
        {
            int h = ClientSize.Height, used = 0, fit = 0;
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                int ih = Math.Max(1, DrawMode == DrawMode.OwnerDrawVariable ? GetItemHeight(i) : ItemHeight);
                if (used + ih > h) break;
                used += ih;
                fit++;
            }
            return Math.Max(0, Items.Count - Math.Max(1, fit));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // 右键也选中条目，便于上下文菜单操作
            if (e.Button == MouseButtons.Right)
            {
                int idx = IndexFromPoint(e.Location);
                if (idx >= 0 && idx != SelectedIndex) SelectedIndex = idx;
            }
            base.OnMouseDown(e);
        }

        private void SetHover(int idx)
        {
            if (idx == HoverIndex) return;
            int old = HoverIndex;
            HoverIndex = idx;
            if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old));
            if (idx >= 0 && idx < Items.Count) Invalidate(GetItemRectangle(idx));
        }

        public void InvalidateItem(object item)
        {
            int i = Items.IndexOf(item);
            if (i >= 0) Invalidate(GetItemRectangle(i));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014) { m.Result = (IntPtr)1; return; } // WM_ERASEBKGND
            // WM_LBUTTONDOWN / WM_LBUTTONDBLCLK / WM_RBUTTONDOWN / WM_RBUTTONDBLCLK 落在不可选行上 / on a non-selectable row
            if ((m.Msg == 0x0201 || m.Msg == 0x0203 || m.Msg == 0x0204 || m.Msg == 0x0206) && HandleInertClick(ref m)) return;
            if (m.Msg == 0x020A && WheelItemsPerNotch > 0)        // WM_MOUSEWHEEL
            {
                ScrollByWheel(unchecked((short)((long)m.WParam >> 16)));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
    }
}