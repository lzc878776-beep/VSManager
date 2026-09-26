using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 放行等级三刻度滑块（已完成 / 待验证 / 失败）：点击、拖动或方向键切换，吸附到刻度。
    /// Three-stop release level slider (Completed / Awaiting verification / Failed): click, drag or arrow keys; snaps to stops.
    /// </summary>
    public sealed class ReleaseLevelSlider : Control
    {
        private ReleaseLevel _value = ReleaseLevels.Default;
        private bool _dragging, _hover;
        private ReleaseLevel _dragStart;

        /// <summary>用户操作改变等级后触发（拖动时松开鼠标才触发）。/ Raised after the user changes the level (on mouse release while dragging).</summary>
        public event Action ValueChanged;

        public ReleaseLevelSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Background;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.Slider;
            AccessibleName = "放行等级 / Release level";
            UpdateAccessibleValue();
        }

        public ReleaseLevel Value
        {
            get => _value;
            set { if (SetValueSilently(value)) ValueChanged?.Invoke(); }
        }

        /// <summary>只更新显示，不触发 ValueChanged；返回是否有变化。/ Updates the display without raising ValueChanged; returns whether it changed.</summary>
        public bool SetValueSilently(ReleaseLevel value)
        {
            value = ReleaseLevels.Clamp((int)value);
            if (value == _value) return false;
            _value = value;
            UpdateAccessibleValue();
            Invalidate();
            return true;
        }

        private void UpdateAccessibleValue() =>
            AccessibleDescription = ReleaseLevels.ShortName(_value) + " / " + ReleaseLevels.ShortNameEn(_value);

        private int Pad => Dpi.S(26);
        private int TrackY => Dpi.S(24);

        private int TickX(int index) => Pad + (Width - 2 * Pad) * index / 2;

        private ReleaseLevel LevelAt(int x)
        {
            float step = Math.Max(1, Width - 2 * Pad) / 2f;
            return ReleaseLevels.Clamp((int)Math.Round((x - Pad) / step));
        }

        private static Color LevelColor(ReleaseLevel level) =>
            level == ReleaseLevel.Completed ? Theme.Success : level == ReleaseLevel.NeedsUser ? Theme.Warning : Theme.Danger;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

            TextRenderer.DrawText(g, "放行等级", Theme.Small, new Rectangle(0, 0, Width, Dpi.S(16)), Theme.TextMuted,
                flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int y = TrackY, left = TickX(0), right = TickX(2), cur = TickX((int)_value);
            Color color = LevelColor(_value);
            Theme.FillRound(g, Theme.Border, new RectangleF(left, y - Dpi.S(2), right - left, Dpi.S(4)), Dpi.S(2));
            if (cur > left)
                Theme.FillRound(g, Color.FromArgb(160, color), new RectangleF(left, y - Dpi.S(2), cur - left, Dpi.S(4)), Dpi.S(2));

            foreach (var level in ReleaseLevels.All)
            {
                int i = (int)level, x = TickX(i);
                bool on = level == _value;
                if (!on) Theme.FillCircle(g, i < (int)_value ? Color.FromArgb(200, color) : Theme.TextMuted, x, y, Dpi.S(3));
                var box = new Rectangle(x - Dpi.S(34), y + Dpi.S(8), Dpi.S(68), Dpi.S(16));
                TextRenderer.DrawText(g, ReleaseLevels.ShortName(level), on ? Theme.SemiBold : Theme.Small, box,
                    on ? color : Theme.TextSecondary, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            float r = Dpi.S(_dragging || _hover ? 7 : 6);
            if (Focused && ShowFocusCues) Theme.FillCircle(g, Color.FromArgb(70, color), cur, y, r + Dpi.S(3));
            Theme.FillCircle(g, color, cur, y, r);
            Theme.FillCircle(g, Theme.Background, cur, y, r - Dpi.S(3));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _dragging = true;
            _dragStart = _value;
            Capture = true;
            SetValueSilently(LevelAt(e.X));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetValueSilently(LevelAt(e.X));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            Invalidate();
            if (_value != _dragStart) ValueChanged?.Invoke();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_dragging && !Capture)
            {
                // 拖动被中断时按当前位置提交 / Commit the current stop when a drag is interrupted
                _dragging = false;
                Invalidate();
                if (_value != _dragStart) ValueChanged?.Invoke();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int v = (int)_value;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: v--; break;
                case Keys.Right: case Keys.Up: v++; break;
                case Keys.Home: v = 0; break;
                case Keys.End: v = 2; break;
                default: return;
            }
            e.Handled = true;
            Value = ReleaseLevels.Clamp(v);
        }
    }
}
