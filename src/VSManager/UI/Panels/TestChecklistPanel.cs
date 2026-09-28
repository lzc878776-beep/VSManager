using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 任务清单旁的测试清单：每个待验证任务对应一个测试条目（列出需要在环境中实测的内容），勾选后任务转为已完成。
    /// Test checklist beside the task list: each task awaiting verification has one test entry (listing what must be tested in
    /// the environment); checking it marks the task done.
    /// </summary>
    public sealed class TestChecklistPanel : Panel
    {
        private readonly Panel _top = new Panel();
        private readonly ChecklistView _list = new ChecklistView();
        private readonly ToolTip _tips = new ThemedToolTip();
        private TaskQueue _queue;
        private Func<DateTime?> _clearedAt;
        private string _signature;
        private bool _suppressed;
        private int _pendingTasks;

        /// <summary>用户勾选了任务的测试条目（测试通过）。/ The user checked a task's test entry (tests passed).</summary>
        public event Action<QueuedTask> TaskChecked;

        /// <summary>请求对任务执行操作：verify / supplement / open。/ Requests a task action: verify / supplement / open.</summary>
        public event Action<QueuedTask, string> ActionRequested;

        public TestChecklistPanel()
        {
            Dock = DockStyle.Right;
            Width = Dpi.S(280);
            BackColor = Theme.Sidebar;
            DoubleBuffered = true;
            Padding = new Padding(1, 0, 0, 0);
            Visible = false;

            _top.Dock = DockStyle.Top;
            _top.Height = Dpi.S(58);
            _top.BackColor = Theme.Sidebar;
            _top.Paint += Top_Paint;
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_top, true, null);

            _list.Dock = DockStyle.Fill;
            _list.AccessibleName = "测试清单 / Test checklist";
            _list.CheckClicked += t => TaskChecked?.Invoke(t);
            _list.HeaderDoubleClicked += t => ActionRequested?.Invoke(t, "open");
            _list.HoverChanged += (t, onCheck) =>
            {
                string tip = t == null ? ""
                    : onCheck ? ChecklistView.ContentText(t) + "\r\n点击勾选：测试通过，任务标记为已完成 / Click to check: tests passed, the task is marked done"
                    : "#" + t.Id + " " + TextUtil.Clip(t.Text, 300) + "\r\n双击查看该 VS 的对话，右键更多操作 / Double-click to open the chat; right-click for more";
                if (_tips.GetToolTip(_list) != tip) _tips.SetToolTip(_list, tip);
            };
            _list.ContextMenuStrip = BuildMenu();

            Controls.Add(_list);
            Controls.Add(_top);
            Disposed += (s, e) =>
            {
                if (_queue != null) _queue.Changed -= Reload;
                _tips.Dispose();
                _list.ContextMenuStrip?.Dispose();
            };
        }

        /// <summary>clearedAt：「清除已完成」的时间点，之前完成的待验证任务不再列出。/ clearedAt: time of "Clear completed"; tasks finished before it are not listed.</summary>
        public void Bind(TaskQueue queue, Func<DateTime?> clearedAt = null)
        {
            if (_queue != null) _queue.Changed -= Reload;
            _queue = queue;
            _clearedAt = clearedAt;
            _queue.Changed += Reload;
            Reload();
        }

        /// <summary>任务清单收起时一并隐藏。/ Hidden while the task list is collapsed.</summary>
        public void SetSuppressed(bool suppressed)
        {
            _suppressed = suppressed;
            UpdateVisibility();
        }

        /// <summary>等待测试并在清单中列出的任务。/ Tasks awaiting testing that are listed.</summary>
        internal static List<QueuedTask> ListedTasks(IEnumerable<QueuedTask> tasks, DateTime? clearedAt) =>
            tasks.Where(t => TaskTestChecklist.Pending(t) && (t.TestItems != null || t.Status == QueueStatus.Unverified)
                    && !TaskPanel.IsCleared(t, clearedAt))
                .OrderByDescending(t => t.Finished ?? t.Created).ThenByDescending(t => t.Id).ToList();

        public void Reload()
        {
            if (_queue == null) return;
            var tasks = ListedTasks(_queue.Items, _clearedAt?.Invoke());
            foreach (var t in tasks) TaskTestChecklist.Ensure(t);
            string signature = string.Join("\n", tasks.Select(t => t.Id + "|" + t.Status + "|" + t.Title + "|" + t.VsName + "|"
                + string.Join("\u0001", t.TestItems.Select(i => (i.Checked ? "1" : "0") + i.Text))));
            _pendingTasks = tasks.Count;
            // 只替换内容，滚动位置由列表自己保持 / Only the content changes; the list keeps its own scroll position
            if (signature != _signature)
            {
                _signature = signature;
                _list.SetTasks(tasks);
            }
            _top.Invalidate();
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            bool show = !_suppressed && _pendingTasks > 0;
            if (Visible != show) Visible = show;
        }

        private void Top_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Sidebar);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, 0, 0, _top.Height);
            using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, 0, _top.Height - 1, _top.Width, _top.Height - 1);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            int x = Dpi.S(12), w = Math.Max(0, _top.Width - Dpi.S(22));
            TextRenderer.DrawText(g, "🧪 测试清单 / Test checklist", Theme.CardTitle, new Rectangle(x, Dpi.S(8), w, Dpi.S(22)), Theme.Text, flags);
            string sub = $"{_pendingTasks} 个任务待测，勾选即完成 / {_pendingTasks} to test; check to complete";
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(x, Dpi.S(32), w, Dpi.S(18)), Theme.TextMuted, flags);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, 0, Height);
        }

        private ContextMenuStrip BuildMenu()
        {
            var m = new GroupedContextMenuStrip();
            var verify = m.Items.Add("测试通过，标记为已完成 / Passed — mark done", null, (s, e) => Do("verify"));
            var supplement = m.Items.Add("测试未通过，补充信息后重试… / Failed — retry with info…", null, (s, e) => Do("supplement"));
            m.Items.Add(new ToolStripSeparator());
            var open = m.Items.Add("查看该 VS 的对话 / Open the chat", null, (s, e) => Do("open"));
            m.Opening += (s, e) =>
            {
                var t = _list.MenuTask;
                if (t == null) { e.Cancel = true; return; }
                verify.Enabled = TaskTestChecklist.Pending(t);
                supplement.Enabled = TaskStateMachine.IsHoldOutcome(t);
                open.Enabled = true;
            };
            return m;
        }

        private void Do(string action)
        {
            var t = _list.MenuTask;
            if (t != null) ActionRequested?.Invoke(t, action);
        }

        /// <summary>
        /// 自绘的测试条目列表：按像素滚动，内容变化时保持滚动位置，滚动条样式与主对话栏一致（细条、圆角滑块）。
        /// Owner-drawn list of test entries: pixel scrolling, keeps its scroll position when the content changes, and uses the
        /// same scrollbar style as the main chat (thin bar, rounded thumb).
        /// </summary>
        private sealed class ChecklistView : Control
        {
            private const TextFormatFlags LineFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            private const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

            private List<QueuedTask> _tasks = new List<QueuedTask>();
            private int[] _tops = new int[0], _heights = new int[0], _textHeights = new int[0];
            private int _content, _scroll;
            private bool _dragging;
            private int _dragY, _dragScroll;
            private int _hover = -1;
            private bool _hoverCheck;

            public event Action<QueuedTask> CheckClicked;
            public event Action<QueuedTask> HeaderDoubleClicked;
            /// <summary>悬停的任务与是否在测试条目上（null 表示离开）。/ Hovered task and whether it is over the test entry (null when leaving).</summary>
            public event Action<QueuedTask, bool> HoverChanged;
            /// <summary>右键菜单对应的任务。/ Task under the context menu.</summary>
            public QueuedTask MenuTask { get; private set; }

            public ChecklistView()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Sidebar;
                ForeColor = Theme.Text;
                Font = Theme.Regular;
            }

            private static int HeaderHeight => Dpi.S(52);
            private static int BarWidth => Dpi.S(7);
            private static int TextLeft => Dpi.S(46);
            private int TextWidth => Math.Max(Dpi.S(60), ClientSize.Width - TextLeft - Dpi.S(10) - BarWidth);
            private int MaxScroll => Math.Max(0, _content - ClientSize.Height);

            /// <summary>测试条目的文字：单项直接显示，多项逐行列出（不再是多个勾选项）。/ Text of the test entry: a single item as is, several as lines (no longer separate checkboxes).</summary>
            internal static string ContentText(QueuedTask t)
            {
                var items = t.TestItems ?? new TaskTestItem[0];
                if (items.Length == 1) return items[0].Text;
                return string.Join("\n", items.Where(i => i != null).Select(i => (i.Checked ? "✓ " : "• ") + i.Text));
            }

            /// <summary>
            /// 替换条目：首个可见条目仍在时保持它在视图中的位置，否则保持像素位置，不跳回顶端。
            /// Replaces the entries: keeps the first visible entry at the same place when it remains, otherwise keeps the pixel offset;
            /// never jumps back to the top.
            /// </summary>
            public void SetTasks(List<QueuedTask> tasks)
            {
                int anchorId = -1, anchorOffset = 0;
                int first = IndexAt(0);
                if (first >= 0) { anchorId = _tasks[first].Id; anchorOffset = _tops[first] - _scroll; }
                _tasks = tasks ?? new List<QueuedTask>();
                Relayout();
                int again = _tasks.FindIndex(t => t.Id == anchorId);
                if (again >= 0) _scroll = _tops[again] - anchorOffset;
                SetScroll(_scroll);
                UpdateHover(PointToClient(MousePosition));
                Invalidate();
            }

            private void Relayout()
            {
                int n = _tasks.Count;
                _tops = new int[n]; _heights = new int[n]; _textHeights = new int[n];
                int y = 0;
                using (var g = CreateGraphics())
                    for (int i = 0; i < n; i++)
                    {
                        int h = TextRenderer.MeasureText(g, ContentText(_tasks[i]), Theme.Regular, new Size(TextWidth, 0), WrapFlags).Height;
                        _textHeights[i] = Math.Min(Dpi.S(360), Math.Max(Dpi.S(18), h));
                        _tops[i] = y;
                        _heights[i] = HeaderHeight + _textHeights[i] + Dpi.S(14);
                        y += _heights[i];
                    }
                _content = y;
            }

            private void SetScroll(int value)
            {
                int v = Math.Max(0, Math.Min(MaxScroll, value));
                if (v == _scroll) return;
                _scroll = v;
                Invalidate();
            }

            private int IndexAt(int y)
            {
                int cy = y + _scroll;
                for (int i = 0; i < _tasks.Count; i++)
                    if (cy >= _tops[i] && cy < _tops[i] + _heights[i]) return i;
                return -1;
            }

            private Rectangle ThumbRect()
            {
                int h = ClientSize.Height;
                if (_content <= h || h <= 0) return Rectangle.Empty;
                int th = Math.Max(Dpi.S(24), (int)((long)h * h / _content));
                int ty = MaxScroll == 0 ? 0 : (int)((long)(h - th) * _scroll / MaxScroll);
                return new Rectangle(ClientSize.Width - BarWidth, ty, BarWidth, th);
            }

            private bool OnBar(Point p) => _content > ClientSize.Height && p.X >= ClientSize.Width - BarWidth - Dpi.S(2);

            private bool OnEntry(int index, Point p) => index >= 0 && p.Y + _scroll >= _tops[index] + HeaderHeight;

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                Relayout();
                SetScroll(_scroll);
                Invalidate();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                SetScroll(_scroll - e.Delta * Dpi.S(48) / 120);
                UpdateHover(e.Location);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Right) { int i = IndexAt(e.Y); MenuTask = i >= 0 ? _tasks[i] : null; return; }
                if (e.Button != MouseButtons.Left || !OnBar(e.Location)) return;
                var thumb = ThumbRect();
                if (thumb.Contains(e.X, e.Y) || (e.Y >= thumb.Top && e.Y < thumb.Bottom))
                {
                    _dragging = true; _dragY = e.Y; _dragScroll = _scroll; Capture = true;
                }
                else SetScroll(_scroll + (e.Y < thumb.Top ? -1 : 1) * Math.Max(Dpi.S(40), ClientSize.Height - Dpi.S(40)));
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_dragging)
                {
                    int track = ClientSize.Height - ThumbRect().Height;
                    if (track > 0) SetScroll(_dragScroll + (int)((long)(e.Y - _dragY) * MaxScroll / track));
                    return;
                }
                UpdateHover(e.Location);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (_dragging) { _dragging = false; Capture = false; }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                SetHover(-1, false);
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button != MouseButtons.Left || OnBar(e.Location)) return;
                int i = IndexAt(e.Y);
                if (OnEntry(i, e.Location)) CheckClicked?.Invoke(_tasks[i]);
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                if (e.Button != MouseButtons.Left || OnBar(e.Location)) return;
                int i = IndexAt(e.Y);
                if (i >= 0 && !OnEntry(i, e.Location)) HeaderDoubleClicked?.Invoke(_tasks[i]);
            }

            private void UpdateHover(Point p)
            {
                if (!ClientRectangle.Contains(p) || OnBar(p)) { SetHover(-1, false); Cursor = Cursors.Default; return; }
                int i = IndexAt(p.Y);
                bool onCheck = OnEntry(i, p);
                Cursor = onCheck ? Cursors.Hand : Cursors.Default;
                SetHover(i, onCheck);
            }

            private void SetHover(int index, bool onCheck)
            {
                if (index == _hover && onCheck == _hoverCheck) return;
                _hover = index; _hoverCheck = onCheck;
                Invalidate();
                HoverChanged?.Invoke(index >= 0 && index < _tasks.Count ? _tasks[index] : null, onCheck);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Sidebar);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                for (int i = 0; i < _tasks.Count; i++)
                {
                    var r = new Rectangle(0, _tops[i] - _scroll, ClientSize.Width - BarWidth, _heights[i]);
                    if (r.Bottom < e.ClipRectangle.Top || r.Top > e.ClipRectangle.Bottom) continue;
                    DrawEntry(g, i, r);
                }
                var thumb = ThumbRect();
                // 与主对话栏滚动条一致：细条、圆角、边框色滑块 / Same as the main chat scrollbar: thin, rounded, border-colored thumb
                if (!thumb.IsEmpty) Theme.FillRound(g, Theme.Border, thumb, Dpi.S(4));
            }

            private void DrawEntry(Graphics g, int i, Rectangle r)
            {
                var t = _tasks[i];
                bool unverified = t.Status == QueueStatus.Unverified;
                int x = r.X + Dpi.S(12), right = r.Right - Dpi.S(8);
                if (i > 0)
                    using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, x, r.Y + Dpi.S(2), right, r.Y + Dpi.S(2));
                string pill = "待验证";
                int pw = TextRenderer.MeasureText(g, pill, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(14);
                var pr = new RectangleF(right - pw, r.Y + Dpi.S(10), pw, Dpi.S(18));
                Theme.FillRound(g, unverified ? Theme.UnverifiedBg : Theme.NoneBg, pr, pr.Height / 2);
                TextRenderer.DrawText(g, pill, Theme.Small, Rectangle.Round(pr), unverified ? Theme.UnverifiedFg : Theme.Warning,
                    TextFormatFlags.HorizontalCenter | LineFlags);
                string title = "#" + t.Id + "  " + (string.IsNullOrEmpty(t.Title) ? TextUtil.Clip(t.Text.Replace("\r", " ").Replace("\n", " "), 40) : t.Title);
                TextRenderer.DrawText(g, title, Theme.SemiBold, new Rectangle(x, r.Y + Dpi.S(8), Math.Max(0, (int)pr.X - x - Dpi.S(6)), Dpi.S(22)), Theme.Text, LineFlags);
                TextRenderer.DrawText(g, t.VsName + " · 勾选即标记为已完成 / Check to mark done", Theme.Small,
                    new Rectangle(x, r.Y + Dpi.S(30), Math.Max(0, right - x), Dpi.S(18)), Theme.TextMuted, LineFlags);

                int top = r.Y + HeaderHeight;
                bool hot = i == _hover && _hoverCheck;
                if (hot) Theme.FillRound(g, Theme.Elevated, new RectangleF(Dpi.S(8), top - Dpi.S(4), r.Width - Dpi.S(14), _textHeights[i] + Dpi.S(10)), Dpi.S(6));
                int box = Dpi.S(16);
                var br = new RectangleF(Dpi.S(20), top, box, box);
                Theme.DrawRound(g, hot ? Theme.Accent : Theme.TextMuted, br, Dpi.S(4));
                var tr = new Rectangle(TextLeft, top, TextWidth, _textHeights[i]);
                TextRenderer.DrawText(g, ContentText(t), Theme.Regular, tr, Theme.Text, WrapFlags | TextFormatFlags.EndEllipsis);
            }
        }
    }
}