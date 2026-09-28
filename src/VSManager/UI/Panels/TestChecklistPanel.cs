using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 任务清单旁的测试清单：列出未验证 / 待用户验证任务需要在环境中实测的项目；用户逐项勾选，全部勾选后任务转为已完成。
    /// Test checklist beside the task list: lists what must be tested in the environment for unverified / awaiting-verification tasks;
    /// the user checks items off and the task completes once all are checked.
    /// </summary>
    public sealed class TestChecklistPanel : Panel
    {
        private sealed class Row
        {
            public QueuedTask Task;
            public int Index = -1;
            public bool IsHeader => Index < 0;
            public TaskTestItem Item => IsHeader ? null : Task.TestItems[Index];
        }

        private readonly Panel _top = new Panel();
        private readonly ListBox _list = new ListBox();
        private readonly ToolTip _tips = new ThemedToolTip();
        private TaskQueue _queue;
        private Func<DateTime?> _clearedAt;
        private string _signature;
        private bool _suppressed;
        private int _pendingItems, _pendingTasks;
        private Row _menuRow;

        /// <summary>勾选 / 取消勾选一项：任务、项序号、是否勾选。/ An item was checked / unchecked: task, item index, checked.</summary>
        public event Action<QueuedTask, int, bool> ItemToggled;

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
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Theme.Sidebar;
            _list.ForeColor = Theme.Text;
            _list.DrawMode = DrawMode.OwnerDrawVariable;
            _list.IntegralHeight = false;
            _list.SelectionMode = SelectionMode.None;
            _list.AccessibleName = "测试清单 / Test checklist";
            _list.MeasureItem += MeasureRow;
            _list.DrawItem += DrawRow;
            _list.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || !(RowAt(e.Location) is Row row) || row.IsHeader) return;
                ItemToggled?.Invoke(row.Task, row.Index, !row.Item.Checked);
            };
            _list.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && RowAt(e.Location) is Row row && row.IsHeader) ActionRequested?.Invoke(row.Task, "open");
            };
            _list.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) _menuRow = RowAt(e.Location); };
            _list.MouseMove += (s, e) =>
            {
                var row = RowAt(e.Location);
                string tip = row == null ? ""
                    : row.IsHeader ? "#" + row.Task.Id + " " + TextUtil.Clip(row.Task.Text, 300) + "\r\n双击查看该 VS 的对话，右键更多操作 / Double-click to open the chat; right-click for more"
                    : row.Item.Text + "\r\n点击勾选 / 取消勾选；全部勾选后任务即完成 / Click to check / uncheck; the task completes once all are checked";
                _list.Cursor = row != null && !row.IsHeader ? Cursors.Hand : Cursors.Default;
                if (_tips.GetToolTip(_list) != tip) _tips.SetToolTip(_list, tip);
            };
            _list.Resize += (s, e) => Rebuild(true);
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

        public void Reload() => Rebuild(false);

        private void Rebuild(bool force)
        {
            if (_queue == null) return;
            var tasks = ListedTasks(_queue.Items, _clearedAt?.Invoke());
            foreach (var t in tasks) TaskTestChecklist.Ensure(t);
            string signature = string.Join("\n", tasks.Select(t => t.Id + "|" + t.Status + "|" + t.Title + "|" + t.VsName + "|"
                + string.Join("\u0001", t.TestItems.Select(i => (i.Checked ? "1" : "0") + i.Text))));
            _pendingTasks = tasks.Count;
            _pendingItems = tasks.Sum(TaskTestChecklist.Remaining);
            if (force || signature != _signature)
            {
                _signature = signature;
                int top = _list.Items.Count > 0 ? _list.TopIndex : 0;
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var t in tasks)
                {
                    _list.Items.Add(new Row { Task = t });
                    for (int i = 0; i < t.TestItems.Length; i++) _list.Items.Add(new Row { Task = t, Index = i });
                }
                if (_list.Items.Count > 0) _list.TopIndex = Math.Min(top, _list.Items.Count - 1);
                _list.EndUpdate();
            }
            _top.Invalidate();
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            bool show = !_suppressed && _pendingTasks > 0;
            if (Visible != show) Visible = show;
        }

        private Row RowAt(Point p)
        {
            int i = _list.IndexFromPoint(p);
            return i >= 0 && i < _list.Items.Count && _list.GetItemRectangle(i).Contains(p) ? _list.Items[i] as Row : null;
        }

        private int TextWidth => Math.Max(Dpi.S(60), _list.ClientSize.Width - Dpi.S(52));
        private const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

        private void MeasureRow(object sender, MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count || !(_list.Items[e.Index] is Row row)) return;
            if (row.IsHeader) { e.ItemHeight = Dpi.S(52); return; }
            int h = TextRenderer.MeasureText(e.Graphics, row.Item.Text, Theme.Regular, new Size(TextWidth, 0), WrapFlags).Height;
            e.ItemHeight = Math.Min(255, Math.Max(Dpi.S(30), h + Dpi.S(12)));
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Theme.Sidebar)) g.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= _list.Items.Count || !(_list.Items[e.Index] is Row row)) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            if (row.IsHeader)
            {
                var t = row.Task;
                bool unverified = t.Status == QueueStatus.Unverified;
                int x = e.Bounds.X + Dpi.S(12), right = e.Bounds.Right - Dpi.S(10);
                if (e.Index > 0)
                    using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, x, e.Bounds.Y + Dpi.S(2), right, e.Bounds.Y + Dpi.S(2));
                string pill = unverified ? "未验证" : "待验证";
                var pillFont = Theme.Small;
                int pw = TextRenderer.MeasureText(g, pill, pillFont, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(14);
                var pr = new RectangleF(right - pw, e.Bounds.Y + Dpi.S(10), pw, Dpi.S(18));
                Theme.FillRound(g, unverified ? Theme.UnverifiedBg : Theme.NoneBg, pr, pr.Height / 2);
                TextRenderer.DrawText(g, pill, pillFont, Rectangle.Round(pr), unverified ? Theme.UnverifiedFg : Theme.Warning,
                    TextFormatFlags.HorizontalCenter | flags);
                string title = "#" + t.Id + "  " + (string.IsNullOrEmpty(t.Title) ? TextUtil.Clip(t.Text.Replace("\r", " ").Replace("\n", " "), 40) : t.Title);
                TextRenderer.DrawText(g, title, Theme.SemiBold, new Rectangle(x, e.Bounds.Y + Dpi.S(8), Math.Max(0, (int)pr.X - x - Dpi.S(6)), Dpi.S(22)), Theme.Text, flags);
                int left = TaskTestChecklist.Remaining(t);
                string sub = t.VsName + " · " + (left == 0 ? "已全部勾选 / All checked" : $"剩 {left}/{t.TestItems.Length} 项 / {left} left");
                TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(x, e.Bounds.Y + Dpi.S(30), Math.Max(0, right - x), Dpi.S(18)), Theme.TextMuted, flags);
                return;
            }
            var item = row.Item;
            int box = Dpi.S(16);
            var br = new RectangleF(e.Bounds.X + Dpi.S(20), e.Bounds.Y + Dpi.S(7), box, box);
            if (item.Checked)
            {
                Theme.FillRound(g, Theme.Accent, br, Dpi.S(4));
                using (var pen = new Pen(Color.White, Math.Max(1.5f, Dpi.S(2))))
                    g.DrawLines(pen, new[]
                    {
                        new PointF(br.X + box * 0.22f, br.Y + box * 0.52f),
                        new PointF(br.X + box * 0.42f, br.Y + box * 0.72f),
                        new PointF(br.X + box * 0.78f, br.Y + box * 0.30f)
                    });
            }
            else Theme.DrawRound(g, Theme.TextMuted, br, Dpi.S(4));
            var tr = new Rectangle((int)br.Right + Dpi.S(10), e.Bounds.Y + Dpi.S(6), TextWidth, e.Bounds.Height - Dpi.S(8));
            using (var font = item.Checked ? new Font(Theme.Regular, FontStyle.Strikeout) : null)
                TextRenderer.DrawText(g, item.Text, font ?? Theme.Regular, tr, item.Checked ? Theme.TextMuted : Theme.Text, WrapFlags | TextFormatFlags.EndEllipsis);
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
            string sub = $"{_pendingTasks} 个任务 · {_pendingItems} 项待测，全部勾选即完成 / {_pendingItems} to test; check all to complete";
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
            var verify = m.Items.Add("全部通过，标记为已完成 / All passed — mark done", null, (s, e) => Do("verify"));
            var supplement = m.Items.Add("测试未通过，补充信息后重试… / Failed — retry with info…", null, (s, e) => Do("supplement"));
            m.Items.Add(new ToolStripSeparator());
            var open = m.Items.Add("查看该 VS 的对话 / Open the chat", null, (s, e) => Do("open"));
            m.Opening += (s, e) =>
            {
                var t = _menuRow?.Task;
                if (t == null) { e.Cancel = true; return; }
                verify.Enabled = TaskTestChecklist.Pending(t);
                supplement.Enabled = TaskStateMachine.IsHoldOutcome(t) && t.SupplementCount < TaskStateMachine.MaxSupplements;
                open.Enabled = true;
            };
            return m;
        }

        private void Do(string action)
        {
            var t = _menuRow?.Task;
            if (t != null) ActionRequested?.Invoke(t, action);
        }
    }
}
