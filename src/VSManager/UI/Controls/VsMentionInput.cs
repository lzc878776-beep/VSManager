using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>两个桌面输入框共用的不抢焦点候选层。/ Shared non-activating suggestion layer for both desktop inputs.</summary>
    public sealed class VsMentionInput : IDisposable
    {
        private sealed class Popup : Form
        {
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; }
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
                base.WndProc(ref m);
            }
        }
        private sealed class SuggestionList : ListBox
        {
            protected override void WndProc(ref Message m)
            {
                // 点击仅确认候选，不让原生列表框抢走输入焦点。/ Click confirms only; prevent the native list from taking input focus.
                if (m.Msg == 0x0201)
                {
                    int point = m.LParam.ToInt32();
                    OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, (short)point, (short)(point >> 16), 0));
                    return;
                }
                base.WndProc(ref m);
            }
        }
        /// <summary>在原生绘制后把已确认令牌覆盖为气泡；文本本身不变。/ Paints confirmed tokens as chips after native painting; the text itself is unchanged.</summary>
        private sealed class PaintHook : NativeWindow
        {
            private readonly VsMentionInput _owner;
            internal PaintHook(VsMentionInput owner) { _owner = owner; }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x000F && _owner.PaintBuffered(m.HWnd)) return;
                base.WndProc(ref m);
                if (m.Msg == 0x000F) _owner.PaintChips();
                else if (m.Msg == 0x0115 || m.Msg == 0x020A) _owner._input.Invalidate();
            }
        }
        private readonly TextBoxBase _input;
        private readonly VsMentionSession _session;
        private readonly Func<VsMentionTarget[]> _targets;
        private readonly Popup _popup;
        private readonly ListBox _list;
        private readonly Timer _refresh = new Timer { Interval = 500 };
        private readonly ToolTip _tips = new ThemedToolTip();
        private readonly PaintHook _hook;
        private Form _owner;
        private readonly List<Control> _ancestors = new List<Control>();
        private int _start = -1, _end;
        private bool _selecting, _disposed;
        private Keys _consumed;
        public bool IsOpen => _popup.Visible;
        public int CandidateCount => _list.Items.OfType<VsMentionTarget>().Count();

        public VsMentionInput(TextBoxBase input, VsMentionSession session, Func<VsMentionTarget[]> targets)
        {
            _input = input; _session = session; _targets = targets;
            _tips.SetToolTip(input, "输入 @ 选择目标 VS；↑↓ 选择，Enter 确认，Esc 取消；确认后再次 Enter 入队\r\nType @ to choose a target VS; arrows select, Enter confirms, Esc dismisses; Enter again queues the task");
            _popup = new Popup { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                BackColor = Theme.Border, Padding = new Padding(1), AccessibleName = "VS 提及候选 / VS mention candidates" };
            _list = new SuggestionList { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = input.Font,
                BackColor = Theme.Elevated, ForeColor = Theme.Text, IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Dpi.S(34),
                AccessibleName = "编号与名称，上下选择，回车确认 / Number and name, arrows to select, Enter to confirm" };
            _list.DrawItem += DrawCandidate;
            _list.SelectedIndexChanged += (s, e) => _list.Invalidate();
            _popup.Controls.Add(_list);
            _list.MouseDown += OnMouseDown;
            input.TextChanged += OnTextChanged;
            input.KeyUp += OnKeyUp;
            input.MouseUp += OnCaretMoved;
            input.LostFocus += OnDismiss;
            input.VisibleChanged += OnDismiss;
            input.ParentChanged += OnDismiss;
            input.Resize += OnDismiss;
            input.Disposed += OnInputDisposed;
            _refresh.Tick += OnRefresh;
            _hook = new PaintHook(this);
            _wordBreak = WordBreak;
            if (input.IsHandleCreated) { _hook.AssignHandle(input.Handle); InstallWordBreak(); }
            input.HandleCreated += OnHandleCreated;
            input.HandleDestroyed += OnHandleDestroyed;
            input.MouseMove += OnMouseMoveInput;
            HookAncestors();
        }

        private void OnHandleCreated(object sender, EventArgs e) { if (_hook.Handle == IntPtr.Zero) _hook.AssignHandle(_input.Handle); InstallWordBreak(); }

        /// <summary>自定义换行：确认的令牌不被自动换行拆开，气泡始终完整显示。/ Custom word breaking: word wrap never splits a confirmed token, so its chip always renders whole.</summary>
        private delegate int EditWordBreakProc(IntPtr lpch, int ichCurrent, int cch, int code);
        private readonly EditWordBreakProc _wordBreak;
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendWordBreak(IntPtr hwnd, int msg, IntPtr wParam, EditWordBreakProc lParam);
        private const int EM_SETWORDBREAKPROC = 0x00D0;

        private void InstallWordBreak()
        {
            if (_disposed || !_input.IsHandleCreated) return;
            SendWordBreak(_input.Handle, EM_SETWORDBREAKPROC, IntPtr.Zero, _wordBreak);
        }

        private static int WordBreak(IntPtr lpch, int ichCurrent, int cch, int code)
        {
            try
            {
                if (lpch == IntPtr.Zero || cch <= 0) return code == MentionLineBreak.WbIsDelimiter ? 0 : Math.Max(0, Math.Min(ichCurrent, cch));
                // 只读取光标附近的窗口，长文本也不会拖慢排版。/ Read only a window around the position so long text stays fast.
                int ich = Math.Max(0, Math.Min(ichCurrent, cch));
                int from = Math.Max(0, ich - 256), to = Math.Min(cch, ich + 512);
                string s = System.Runtime.InteropServices.Marshal.PtrToStringUni(IntPtr.Add(lpch, from * 2), to - from);
                int r = MentionLineBreak.Evaluate(s, ich - from, code);
                return code == MentionLineBreak.WbIsDelimiter ? r : r + from;
            }
            catch { return code == MentionLineBreak.WbIsDelimiter ? 0 : ichCurrent; }
        }
        private void OnHandleDestroyed(object sender, EventArgs e) => _hook.ReleaseHandle();
        private void OnMouseMoveInput(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _input.Invalidate(); }

        private (int Start, int Length, string Label)[] CurrentChips() => _session.Chips(_input.Text).ToArray();

        private void PaintChips()
        {
            if (_disposed || !_input.IsHandleCreated) return;
            var chips = CurrentChips();
            if (chips.Length == 0) return;
            using (var g = Graphics.FromHwnd(_input.Handle)) DrawChips(g, chips);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc; public bool fErase; public int left, top, right, bottom; public bool fRestore, fIncUpdate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 有气泡时在内存位图中先让原生编辑框绘制（WM_PRINTCLIENT），再叠加气泡，最后一次性贴到屏幕，避免原文与气泡交替出现造成频闪。
        /// When chips exist, let the native edit paint into an off-screen bitmap (WM_PRINTCLIENT), overlay the chips, then blit once, so raw text and chips never alternate on screen.
        /// </summary>
        private bool PaintBuffered(IntPtr hwnd)
        {
            if (_disposed || !_input.IsHandleCreated) return false;
            var chips = CurrentChips();
            var size = _input.ClientSize;
            if (chips.Length == 0 || size.Width <= 0 || size.Height <= 0) return false;
            BeginPaint(hwnd, out var ps);
            try
            {
                using (var bmp = new Bitmap(size.Width, size.Height))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(_input.BackColor);
                    IntPtr mem = g.GetHdc();
                    try { SendMessage(hwnd, 0x0318, mem, (IntPtr)(0x04 | 0x08)); }
                    finally { g.ReleaseHdc(mem); }
                    DrawChips(g, chips);
                    using (var screen = Graphics.FromHdc(ps.hdc)) screen.DrawImageUnscaled(bmp, 0, 0);
                }
            }
            finally { EndPaint(hwnd, ref ps); }
            return true;
        }

        private void DrawChips(Graphics g, (int Start, int Length, string Label)[] chips)
        {
            string text = _input.Text;
            int selStart = _input.SelectionStart, selEnd = selStart + _input.SelectionLength;
            foreach (var chip in chips)
            {
                // 令牌被自动换行拆开时逐行绘制，标签放在最宽的一段，不再露出原始令牌。
                // When word wrap splits a token, draw each line's segment and put the label in the widest one, so the raw token never shows.
                var segments = ChipSegments(g, text, chip.Start, chip.Length);
                if (segments.Count == 0) continue;
                int labelAt = 0;
                for (int i = 1; i < segments.Count; i++) if (segments[i].Width > segments[labelAt].Width) labelAt = i;
                bool selected = selEnd > selStart && selStart < chip.Start + chip.Length && selEnd > chip.Start;
                for (int i = 0; i < segments.Count; i++)
                {
                    var cover = segments[i];
                    if (cover.Width <= 0 || !_input.ClientRectangle.IntersectsWith(cover)) continue;
                    using (var b = new SolidBrush(_input.BackColor)) g.FillRectangle(b, cover);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var pill = new RectangleF(cover.X + 0.5f, cover.Y + 0.5f, cover.Width - 2f, cover.Height - 1f);
                    Theme.FillRound(g, selected ? Theme.Accent : Theme.AccentLight, pill, pill.Height / 2);
                    Theme.DrawRound(g, selected ? Theme.AccentHover : Theme.AccentBorder, pill, pill.Height / 2);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    if (i == labelAt)
                        TextRenderer.DrawText(g, chip.Label, _input.Font, Rectangle.Round(pill), selected ? Color.White : Theme.AccentText,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }
        }

        /// <summary>令牌在每个可视行上占据的矩形。/ Rectangles the token occupies on each visual line.</summary>
        private List<Rectangle> ChipSegments(Graphics g, string text, int start, int length)
        {
            var result = new List<Rectangle>();
            int lineH = _input.Font.Height, end = Math.Min(text.Length, start + length);
            int i = start;
            while (i < end)
            {
                var first = _input.GetPositionFromCharIndex(i);
                int j = i + 1;
                while (j < end && _input.GetPositionFromCharIndex(j).Y == first.Y) j++;
                int lastIndex = j - 1;
                var last = _input.GetPositionFromCharIndex(lastIndex);
                int right = last.X + TextRenderer.MeasureText(g, text[lastIndex].ToString(), _input.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                if (j < text.Length)
                {
                    var next = _input.GetPositionFromCharIndex(j);
                    if (next.Y == first.Y && next.X > first.X) right = next.X;
                }
                result.Add(new Rectangle(first.X, first.Y, Math.Max(0, right - first.X), lineH));
                i = j;
            }
            return result;
        }

        /// <summary>光标不停在气泡内部；向左移动时贴到起点，其余贴到末尾。/ Keeps the caret out of chips: snaps to the start when moving left, otherwise to the end.</summary>
        private void SnapCaret(bool toStart)
        {
            if (_input.SelectionLength != 0) { RepaintChips(); return; }
            int caret = _input.SelectionStart;
            foreach (var chip in CurrentChips())
                if (caret > chip.Start && caret < chip.Start + chip.Length)
                {
                    _input.Select(toStart ? chip.Start : chip.Start + chip.Length, 0);
                    break;
                }
            RepaintChips();
        }

        private int _chipCount;

        /// <summary>
        /// 原生编辑框已自行重绘改动的区域，这里只在其上覆盖气泡，不使整个输入框失效，避免输入时频闪。
        /// The native edit control has already redrawn what changed; only overlay the chips instead of invalidating the whole box, so typing does not flicker.
        /// </summary>
        private void RepaintChips()
        {
            int count = CurrentChips().Length;
            if (count > 0 || _chipCount > 0) PaintChips();
            _chipCount = count;
        }

        private void OnInputDisposed(object sender, EventArgs e) => Dispose();
        private void OnTextChanged(object sender, EventArgs e) { RepaintChips(); if (!_selecting) Refresh(); }
        private void OnCaretMoved(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _input.SelectionLength == 0)
            {
                int caret = _input.SelectionStart;
                var chip = CurrentChips().FirstOrDefault(c => caret > c.Start && caret < c.Start + c.Length);
                if (chip.Length > 0) SnapCaret(caret - chip.Start < chip.Start + chip.Length - caret);
            }
            RepaintChips();
            Refresh();
        }
        private void OnRefresh(object sender, EventArgs e) { if (IsOpen) Refresh(); }
        private void OnDismiss(object sender, EventArgs e) => Dismiss();
        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (_consumed == e.KeyCode) { e.Handled = true; _consumed = Keys.None; return; }
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down
                || e.KeyCode == Keys.Home || e.KeyCode == Keys.End || e.KeyCode == Keys.ShiftKey)
                SnapCaret(e.KeyCode == Keys.Left || e.KeyCode == Keys.Up || e.KeyCode == Keys.Home);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Home || e.KeyCode == Keys.End) Refresh();
        }
        public void Refresh()
        {
            if (_disposed || _selecting || !_input.Focused || !_input.Visible) { Dismiss(); return; }
            HookAncestors();
            string text = _input.Text;
            int caret = _input.SelectionStart;
            int start = VsMentionSession.Starts(text).Where(i => i < caret).DefaultIfEmpty(-1).Last();
            if (start < 0 || _input.SelectionLength != 0) { Dismiss(); return; }
            string query = text.Substring(start + 1, caret - start - 1);
            if (query.IndexOfAny(new[] { '[', ']', '\r', '\n', ' ', '\t' }) >= 0) { Dismiss(); return; }
            string selected = (_list.SelectedItem as VsMentionTarget)?.InstanceKey;
            var matches = VsMentionSession.Filter(_targets(), query);
            if (matches.Length == 0) { _list.Items.Clear(); Dismiss(); return; }
            _start = start; _end = caret;
            // 候选未变化时不重建列表，避免定时刷新导致弹层频闪。/ Skip rebuilding unchanged candidates so periodic refreshes do not make the popup flicker.
            if (!SameCandidates(_list.Items.OfType<VsMentionTarget>().ToArray(), matches))
            {
                _list.BeginUpdate();
                _list.Items.Clear();
                _list.Items.AddRange(matches.Cast<object>().ToArray());
                _list.EndUpdate();
            }
            int index = Math.Max(0, Array.FindIndex(matches, t => t.InstanceKey == selected));
            if (_list.SelectedIndex != index) _list.SelectedIndex = index;
            var owner = _input.FindForm();
            if (_owner != owner)
            {
                UnhookOwner();
                _owner = owner;
                if (_owner != null) { _owner.Resize += OnDismiss; _owner.Move += OnDismiss; _owner.Deactivate += OnDismiss; }
            }
            var point = _input.PointToScreen(_input.GetPositionFromCharIndex(caret));
            var screen = Screen.FromPoint(point).WorkingArea;
            int width = Math.Min(Dpi.S(420), screen.Width);
            int height = Math.Min(Dpi.S(34) * 8, _list.ItemHeight * Math.Min(8, _list.Items.Count) + Dpi.S(8));
            var bounds = new Rectangle(Math.Max(screen.Left, Math.Min(point.X, screen.Right - width)),
                Math.Max(screen.Top, Math.Min(point.Y - height, screen.Bottom - height)), width, height);
            if (_popup.Bounds != bounds) _popup.Bounds = bounds;
            if (!IsOpen && owner != null) _popup.Show(owner);
            _refresh.Start();
        }
        private static bool SameCandidates(VsMentionTarget[] shown, VsMentionTarget[] next)
        {
            if (shown.Length != next.Length) return false;
            for (int i = 0; i < shown.Length; i++)
                if (shown[i].InstanceKey != next[i].InstanceKey || shown[i].SolutionPath != next[i].SolutionPath || shown[i].Name != next[i].Name
                    || shown[i].Number != next[i].Number || shown[i].Note != next[i].Note) return false;
            return true;
        }
        public bool HandleKeyDown(KeyEventArgs e)
        {
            if (_consumed == e.KeyCode && e.Modifiers == Keys.None)
            {
                e.Handled = e.SuppressKeyPress = true;
                return true;
            }
            if (!IsOpen && e.Modifiers == Keys.None && (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
                && _input.SelectionLength == 0 && !_input.ReadOnly)
            {
                int caret = _input.SelectionStart;
                bool back = e.KeyCode == Keys.Back;
                var chip = CurrentChips().FirstOrDefault(c => back
                    ? caret > c.Start && caret <= c.Start + c.Length
                    : caret >= c.Start && caret < c.Start + c.Length);
                if (chip.Length > 0)
                {
                    // 气泡整体删除，避免留下无法路由的半个令牌。/ Delete the chip as a whole so no unroutable half-token remains.
                    e.Handled = e.SuppressKeyPress = true;
                    _input.Select(chip.Start, chip.Length);
                    _input.SelectedText = "";
                    return true;
                }
            }
            if (!IsOpen || e.Modifiers != Keys.None) return false;
            if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down && e.KeyCode != Keys.Enter && e.KeyCode != Keys.Escape) return false;
            e.Handled = e.SuppressKeyPress = true;
            _consumed = e.KeyCode;
            if (e.KeyCode == Keys.Escape) Dismiss();
            else if (e.KeyCode == Keys.Enter) Confirm();
            else _list.SelectedIndex = (_list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : _list.Items.Count - 1)) % _list.Items.Count;
            return true;
        }
        private void DrawCandidate(object sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Theme.Elevated)) g.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= _list.Items.Count || !(_list.Items[e.Index] is VsMentionTarget t)) return;
            bool selected = e.Index == _list.SelectedIndex;
            var row = new RectangleF(e.Bounds.X + Dpi.S(4), e.Bounds.Y + Dpi.S(2), e.Bounds.Width - Dpi.S(8), e.Bounds.Height - Dpi.S(4));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (selected) Theme.FillRound(g, Theme.RowSelected, row, Dpi.S(6));
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            string number = "#" + t.Number;
            int badgeW = TextRenderer.MeasureText(g, number, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(12);
            var badge = new RectangleF(row.X + Dpi.S(8), row.Y + (row.Height - Dpi.S(20)) / 2, badgeW, Dpi.S(20));
            Theme.FillRound(g, selected ? Theme.Accent : Theme.AccentLight, badge, Dpi.S(10));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            TextRenderer.DrawText(g, number, Theme.Small, Rectangle.Round(badge), selected ? Color.White : Theme.AccentText,
                flags | TextFormatFlags.HorizontalCenter);
            int x = (int)badge.Right + Dpi.S(10), right = (int)row.Right - Dpi.S(10);
            int nameW = Math.Min(Math.Max(0, right - x), TextRenderer.MeasureText(g, t.Name, Theme.SemiBold, Size.Empty, TextFormatFlags.NoPadding).Width + 2);
            TextRenderer.DrawText(g, t.Name, Theme.SemiBold, new Rectangle(x, (int)row.Y, nameW, (int)row.Height), selected ? Color.White : Theme.Text, flags);
            if (t.Note.Length > 0 && x + nameW + Dpi.S(40) < right)
                TextRenderer.DrawText(g, t.Note, Theme.Small, new Rectangle(x + nameW + Dpi.S(10), (int)row.Y, right - x - nameW - Dpi.S(10), (int)row.Height), Theme.TextMuted, flags);
        }
        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            int index = _list.IndexFromPoint(e.Location);
            if (e.Button != MouseButtons.Left || index < 0) return;
            _list.SelectedIndex = index;
            Confirm();
        }
        public void Confirm()
        {
            if (!IsOpen || !(_list.SelectedItem is VsMentionTarget target)) return;
            _selecting = true;
            try
            {
                _input.Select(_start, _end - _start);
                _input.SelectedText = _session.Select(target) + " ";
                Dismiss();
            }
            finally { _selecting = false; }
        }
        public void Dismiss() { if (_disposed) return; _refresh.Stop(); _popup.Hide(); }
        private void HookAncestors()
        {
            var parents = new List<Control>();
            for (var parent = _input.Parent; parent != null; parent = parent.Parent) parents.Add(parent);
            if (_ancestors.SequenceEqual(parents)) return;
            UnhookAncestors();
            _ancestors.AddRange(parents);
            foreach (var parent in _ancestors) { parent.VisibleChanged += OnDismiss; parent.Resize += OnDismiss; parent.ParentChanged += OnDismiss; }
        }
        private void UnhookAncestors()
        {
            foreach (var parent in _ancestors) { parent.VisibleChanged -= OnDismiss; parent.Resize -= OnDismiss; parent.ParentChanged -= OnDismiss; }
            _ancestors.Clear();
        }
        private void UnhookOwner()
        {
            if (_owner == null) return;
            _owner.Resize -= OnDismiss; _owner.Move -= OnDismiss; _owner.Deactivate -= OnDismiss;
        }
        public void Dispose()
        {
            if (_disposed) return;
            Dismiss(); _disposed = true;
            UnhookOwner();
            UnhookAncestors();
            _input.TextChanged -= OnTextChanged; _input.KeyUp -= OnKeyUp; _input.MouseUp -= OnCaretMoved;
            _input.LostFocus -= OnDismiss; _input.VisibleChanged -= OnDismiss; _input.Resize -= OnDismiss;
            _input.ParentChanged -= OnDismiss;
            _input.HandleCreated -= OnHandleCreated; _input.HandleDestroyed -= OnHandleDestroyed; _input.MouseMove -= OnMouseMoveInput;
            if (_input.IsHandleCreated && !_input.IsDisposed) SendMessage(_input.Handle, EM_SETWORDBREAKPROC, IntPtr.Zero, IntPtr.Zero);
            _hook.ReleaseHandle();
            _input.Disposed -= OnInputDisposed;
            _refresh.Dispose(); _popup.Dispose(); _tips.Dispose();
        }
    }
}
