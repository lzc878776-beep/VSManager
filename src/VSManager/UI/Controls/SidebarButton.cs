using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VSManager
{
    internal sealed class SidebarButton : Button
    {
        private bool _hover, _pressed;
        internal bool Ghost { get; set; }

        internal SidebarButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.Regular;
            ForeColor = Theme.TextSecondary;
            BackColor = Theme.Sidebar;
            TextAlign = ContentAlignment.MiddleLeft;
            Padding = new Padding(Dpi.S(10), 0, Dpi.S(10), 0);
            Cursor = Cursors.Hand;
            UseMnemonic = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Sidebar);
            if (Width < 2 || Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (!Ghost || (Enabled && (_hover || _pressed)))
                Theme.FillRound(g, Enabled && _pressed ? Theme.RowSelected : Enabled && _hover ? (Ghost ? Theme.Elevated : Theme.RowHover) : Theme.Surface, bounds, Dpi.S(6));
            if (!Ghost || (Focused && ShowFocusCues))
                Theme.DrawRound(g, Focused && ShowFocusCues ? Theme.Accent : Theme.Border, bounds, Dpi.S(6));
            g.SmoothingMode = SmoothingMode.None;
            var textBounds = new Rectangle(Padding.Left, Padding.Top,
                Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (TextAlign == ContentAlignment.MiddleCenter) flags |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g, Text, Font, textBounds, Enabled ? (_hover ? Theme.Text : ForeColor) : Theme.TextMuted, flags);
        }
    }
}
