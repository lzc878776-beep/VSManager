using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>主窗口右侧的任务清单：显示排队 / 执行中 / 已完成的任务，可折叠。</summary>
    public sealed partial class TaskPanel : Panel
    {
        private readonly Panel _top = new Panel();
        private readonly TaskDragListBox _list = new TaskDragListBox();
        private readonly FlatButton _btnClear = new FlatButton { Text = "清除已完成", Ghost = true };
        private readonly FlatButton _btnHistory = new FlatButton { Text = "历史", Ghost = true };
        private readonly FlatButton _btnStart = new FlatButton { Text = "▶ 开始流程 / Start" };
        private readonly ReleaseLevelSlider _levelSlider = new ReleaseLevelSlider { BackColor = Theme.Sidebar };
        /// <summary>用户在顶栏滑块上切换接续等级。/ Raised when the user switches the continuation level on the header slider.</summary>
        public event Action<ReleaseLevel> ReleaseLevelChanged;
        private static int TopHeight => Dpi.S(140);
        private bool _workflowStarted;
        public Func<QueuedTask, bool> CanRunTask { get; set; }
        public Func<QueuedTask, string> TaskStartText { get; set; }
        internal bool IsTaskEligible(QueuedTask task) => CanRunTask?.Invoke(task) ?? _workflowStarted;
        internal string EligibilityText(QueuedTask task) => TaskStartText?.Invoke(task)
            ?? (IsTaskEligible(task) ? "按编号调度 / Dispatch in ID order" : TaskDispatcher.WaitingForStart);
        private Func<DateTime?> _clearedAt;
        private Func<IList<HiddenTaskMark>> _hiddenMarks;
        private bool _showHistory;
        private int _hiddenCount;
        private const string DefaultEmptyText = "暂无任务 / No tasks\r\n\r\nAI 与用户文本任务一律在此排队\r\nAI 默认自动，手动任务等待 Start\r\nAI and manual tasks queue in ID order\r\nAI starts automatically by default; manual tasks await Start";
        private readonly FlatButton _btnCollapse = new FlatButton { Text = "»", Ghost = true };
        private readonly FlatButton _btnView = new FlatButton { Text = "▤", Ghost = true };
        private bool _groupByVs = true;
        private string _groupSort = TaskGrouping.SortByActivity;
        private readonly HashSet<string> _collapsedGroups = new HashSet<string>(StringComparer.Ordinal);
        private List<string> _groupKeys = new List<string>();
        private TaskGroupHeader _menuHeader;
        private string _menuGroupKey;
        private static int HeaderHeight => Dpi.S(48);
        private static int CardHeight => Dpi.S(108);
        private readonly Timer _tick = new Timer { Interval = 1000 };
        private readonly ToolTip _tips = new ThemedToolTip();
        // 任务条目的提示：指针在同一条目上停留 ItemTipDelay 毫秒后才显示，移到其他条目时重新计时。
        // Tips for task entries: shown only after the pointer rests on the same entry for ItemTipDelay ms; moving to another entry restarts the wait.
        internal const int ItemTipDelay = 1200;
        private readonly ThemedToolTip _itemTips = new ThemedToolTip();
        private readonly Timer _itemTipTimer = new Timer { Interval = ItemTipDelay };
        private object _hoverKey;
        private TaskQueue _queue;
        private bool _collapsed;
        private bool _loadWarningSeen;
        private string _tip;

        /// <summary>请求对任务执行操作：start / dispatch / cancel / retry / remove / clear / unclear / unhide / open。/ Requests a task action.</summary>
        public event Action<QueuedTask, string> ActionRequested;
        public event Action<bool> CollapsedChanged;

        public TaskPanel()
        {
            Dock = DockStyle.Right;
            BackColor = Theme.Sidebar;
            DoubleBuffered = true;
            Padding = new Padding(1, 0, 0, 0);

            _top.Dock = DockStyle.Top;
            _top.Height = TopHeight;
            _top.BackColor = Theme.Sidebar;
            _top.Paint += Top_Paint;
            _top.MouseClick += (s, e) =>
            {
                if (_collapsed) SetCollapsed(false, true);
                else if (_queue?.SaveError == null && _queue?.LoadWarning != null && !_loadWarningSeen) { _loadWarningSeen = true; _top.Invalidate(); }
            };
            _btnClear.Font = Theme.Small;
            _btnClear.Size = new Size(Dpi.S(82), Dpi.S(28));
            _btnClear.Click += (s, e) => ActionRequested?.Invoke(null, "clear");
            _tips.SetToolTip(_btnClear, "从界面隐藏已完成的任务与对话（失败 / 已取消的保留）\r\n历史记录仍保存在 tasks.json 与归档中，可点「历史」查看");
            _btnHistory.Font = Theme.Small;
            _btnHistory.Size = new Size(Dpi.S(48), Dpi.S(28));
            _btnHistory.Visible = false;
            _btnHistory.Click += (s, e) => { _showHistory = !_showHistory; Reload(); };
            _top.Controls.Add(_btnHistory);
            _btnCollapse.Font = new Font(Theme.FontName, 11F);
            _btnCollapse.Size = new Size(Dpi.S(30), Dpi.S(28));
            _btnCollapse.Click += (s, e) => SetCollapsed(!_collapsed, true);
            _btnView.Font = new Font(Theme.FontName, 10F);
            _btnView.Size = new Size(Dpi.S(30), Dpi.S(28));
            _btnView.Click += (s, e) => SetGroupByVs(!_groupByVs);
            _top.Controls.Add(_btnView);
            UpdateViewButton();
            _top.Controls.Add(_btnClear);
            _top.Controls.Add(_btnCollapse);
            _btnStart.Font = Theme.SemiBold;
            _btnStart.Primary = true;
            _btnStart.DisabledTint = Theme.Success;
            _btnStart.Click += (s, e) => ActionRequested?.Invoke(null, "start");
            _tips.SetToolTip(_btnStart, TaskDispatcher.WaitingForStart);
            _top.Controls.Add(_btnStart);
            _levelSlider.ValueChanged += () =>
            {
                _tips.SetToolTip(_levelSlider, LevelTip(_levelSlider.Value));
                ReleaseLevelChanged?.Invoke(_levelSlider.Value);
            };
            _tips.SetToolTip(_levelSlider, LevelTip(_levelSlider.Value));
            _top.Controls.Add(_levelSlider);
            _top.Resize += (s, e) => LayoutTop();

            _list.Dock = DockStyle.Fill;
            // 分组标题比任务卡片矮，使用可变行高 / Group headers are shorter than task cards, so rows have variable heights
            _list.DrawMode = DrawMode.OwnerDrawVariable;
            _list.ItemHeight = CardHeight;
            _list.MeasureItem += (s, e) =>
                e.ItemHeight = e.Index >= 0 && e.Index < _list.Items.Count && _list.Items[e.Index] is TaskGroupHeader ? HeaderHeight : CardHeight;
            _list.IsItemSelectable = item => !(item is TaskGroupHeader);
            _list.InertItemClicked += (i, button) =>
            {
                if (!(i >= 0 && i < _list.Items.Count && _list.Items[i] is TaskGroupHeader h)) return;
                if (button == MouseButtons.Right) _menuHeader = h;
            };
            ConfigureDrag();
            _list.EmptyText = DefaultEmptyText;
            _list.DrawItem += List_DrawItem;
            _list.MouseDoubleClick += (s, e) =>
            {
                var item = ItemAt(e.Location);
                if (item is QueuedTask t) ActionRequested?.Invoke(t, "open");
                else if (item is ExternalChat c) ExternalActionRequested?.Invoke(c, "open");
            };
            _list.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                _menuHeader = null;
                int i = _list.IndexFromPoint(e.Location);
                if (i >= 0) _list.SelectedIndex = i;
            };
            _list.ContextMenuStrip = BuildMenu();
            _list.Resize += (s, e) => _list.Invalidate();
            _list.MouseMove += (s, e) =>
            {
                object key = HoverKey(ItemAt(e.Location));
                if (Equals(key, _hoverKey)) return;
                ResetItemTip(key);
            };
            _list.MouseLeave += (s, e) => ResetItemTip(null);
            _list.MouseDown += (s, e) => ResetItemTip(null);
            _list.MouseWheel += (s, e) => ResetItemTip(null);
            _itemTipTimer.Tick += (s, e) => ShowItemTip();
            // 条目较高，滚轮每格滚动 1 条；焦点在其他控件时，指针位于任务清单上的滚轮也转给任务清单
            _list.WheelItemsPerNotch = 1;
            _wheel = new WheelForwarder(_list);
            Application.AddMessageFilter(_wheel);

            InitDetail();
            Controls.Add(_list);
            Controls.Add(_detail);
            Controls.Add(_top);

            _tick.Tick += (s, e) =>
            {
                if ((_queue != null && _queue.Items.Any(t => t.Status == QueueStatus.Running || t.Status == QueueStatus.Sending))
                    || (_externals?.Invoke().Any(c => c.Generating) ?? false)) _list.Invalidate();
            };
            _tick.Start();
            Disposed += (s, e) =>
            {
                Application.RemoveMessageFilter(_wheel);
                if (_queue != null) _queue.Changed -= Reload;
                _tick.Dispose();
                _tips.Dispose();
                _itemTipTimer.Dispose();
                _itemTips.Dispose();
                _list.ContextMenuStrip?.Dispose();
            };
        }

        private readonly WheelForwarder _wheel;

        /// <summary>按任务编号识别悬停条目，列表刷新后仍视为同一条。/ Identifies the hovered entry by task id so it stays the same across list reloads.</summary>
        private static object HoverKey(object item) => item is QueuedTask t ? (object)("task:" + t.Id) : item;

        private void ResetItemTip(object key)
        {
            _hoverKey = key;
            _itemTipTimer.Stop();
            _itemTips.HideText(_list);
            if (key != null) _itemTipTimer.Start();
        }

        private void ShowItemTip()
        {
            _itemTipTimer.Stop();
            if (IsDisposed || !_list.IsHandleCreated) return;
            var point = _list.PointToClient(Cursor.Position);
            var item = ItemAt(point);
            if (item == null || !Equals(HoverKey(item), _hoverKey)) return;
            string text = ItemTipText(item);
            if (string.IsNullOrWhiteSpace(text)) return;
            _itemTips.ShowText(text, _list, new Point(point.X + Dpi.S(14), point.Y + Dpi.S(18)));
        }

        /// <summary>条目提示：任务以内容 / 完成情况为主，分组标题说明折叠与拖拽。/ Entry tip: tasks focus on content / outcome; group headers explain collapsing and dragging.</summary>
        private string ItemTipText(object item)
        {
            if (item is TaskGroupHeader)
                return "点击折叠 / 展开，拖拽调整整组显示位置 / Click to collapse / expand; drag to move the whole group\n" + DisplayOrderNotice;
            if (item is QueuedTask task)
            {
                string extra = null;
                if (QueueStatus.Active(task.Status))
                    extra = string.Join("\n", new[] { task.PredecessorNotice, EligibilityText(task) }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return TaskTooltip.Build(task, TaskStateMachine.StatusText(task, DateTime.Now), extra);
            }
            return "拖拽调整显示位置，右键切换排序 / Drag to reorder display; right-click for sorting\n" + DisplayOrderNotice;
        }

        /// <summary>把发给其他控件（如有焦点的输入框）、但指针正位于任务清单上的滚轮消息转给任务清单；其他区域的滚轮不受影响。</summary>
        private sealed class WheelForwarder : IMessageFilter
        {
            private readonly VsListBox _target;
            public WheelForwarder(VsListBox target) { _target = target; }

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern IntPtr WindowFromPoint(Point p);

            public bool PreFilterMessage(ref Message m)
            {
                const int WM_MOUSEWHEEL = 0x020A;
                if (m.Msg != WM_MOUSEWHEEL || _target.IsDisposed || !_target.IsHandleCreated || !_target.Visible || m.HWnd == _target.Handle) return false;
                var p = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                if (WindowFromPoint(p) != _target.Handle) return false;   // 指针不在任务清单上，或被其他窗口遮挡
                return _target.ScrollByWheel(unchecked((short)((long)m.WParam >> 16)));
            }
        }

        public int ExpandedWidth { get; set; } = Dpi.S(300);

        public bool Collapsed => _collapsed;

        public void SetWorkflowStarted(bool started)
        {
            _workflowStarted = started;
            _btnStart.Text = started ? "✓ 已启动 / Started" : "▶ 开始流程 / Start";
            _btnStart.Enabled = !started;
            _tips.SetToolTip(_btnStart, started ? "本次会话已启动；下次启动需重新手动开始 / Started for this session only" : TaskDispatcher.WaitingForStart);
            _top.Invalidate();
            _list.Invalidate();
        }

        /// <summary>是否按目标 VS 分组显示。/ Whether the list is grouped by target VS.</summary>
        public bool GroupByVs => _groupByVs;
        /// <summary>分组排序方式。/ Group sort mode.</summary>
        public string GroupSort => _groupSort;
        /// <summary>已折叠的分组键。/ Keys of collapsed groups.</summary>
        public List<string> CollapsedGroups => _collapsedGroups.OrderBy(k => k, StringComparer.Ordinal).ToList();
        /// <summary>显示方式、排序或折叠状态变化（由主窗口持久化）。/ View mode, sort or collapsed state changed (persisted by the main window).</summary>
        public event Action ViewOptionsChanged;
        /// <summary>当前已打开的 VS（按左侧列表编号）。/ Currently open VS instances (numbered as in the list on the left).</summary>
        public Func<IReadOnlyList<TaskGroupVs>> VsProvider { get; set; }

        /// <summary>应用已保存的显示偏好（不触发 <see cref="ViewOptionsChanged"/>）。/ Applies saved view preferences (does not raise <see cref="ViewOptionsChanged"/>).</summary>
        public void SetViewOptions(bool groupByVs, string sort, IEnumerable<string> collapsedGroups,
            bool manualOrder = false, IEnumerable<string> itemOrder = null, IEnumerable<string> groupOrder = null)
        {
            _groupByVs = groupByVs;
            _groupSort = TaskGrouping.NormalizeSort(sort);
            _manualOrder = manualOrder;
            _itemOrder = TaskDisplayOrder.Normalize(itemOrder);
            _groupOrder = TaskDisplayOrder.Normalize(groupOrder, true);
            _collapsedGroups.Clear();
            foreach (var k in TaskGrouping.NormalizeCollapsed(collapsedGroups)) _collapsedGroups.Add(k);
            UpdateViewButton();
            Reload();
        }

        internal void SetGroupByVs(bool on)
        {
            if (_groupByVs == on) return;
            _groupByVs = on;
            UpdateViewButton();
            Reload();
            ViewOptionsChanged?.Invoke();
        }

        internal void SetGroupSort(string sort)
        {
            sort = TaskGrouping.NormalizeSort(sort);
            if (_groupSort == sort) return;
            _groupSort = sort;
            if (sort == TaskGrouping.SortManual && _groupOrder.Count == 0)
                _groupOrder = new List<string>(_groupKeys);
            UpdateViewButton();
            Reload();
            ViewOptionsChanged?.Invoke();
        }

        /// <summary>折叠或展开一个分组。/ Collapses or expands one group.</summary>
        internal void ToggleGroup(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!_collapsedGroups.Remove(key))
            {
                if (_collapsedGroups.Count >= TaskGrouping.MaxCollapsed) return;
                _collapsedGroups.Add(key);
            }
            Reload();
            ViewOptionsChanged?.Invoke();
        }

        private void SetAllCollapsed(bool collapsed)
        {
            bool changed = false;
            foreach (var k in _groupKeys)
                changed |= collapsed ? _collapsedGroups.Count < TaskGrouping.MaxCollapsed && _collapsedGroups.Add(k) : _collapsedGroups.Remove(k);
            if (!changed) return;
            Reload();
            ViewOptionsChanged?.Invoke();
        }

        private void UpdateViewButton()
        {
            _btnView.Text = _groupByVs ? "▤" : "≡";
            _tips.SetToolTip(_btnView, (_groupByVs
                ? "当前：按 VS 分组（点击切换为平铺列表）\r\nCurrent: grouped by VS (click for a flat list)"
                : "当前：平铺列表（点击切换为按 VS 分组）\r\nCurrent: flat list (click to group by VS)") + "\r\n" + DisplayOrderNotice);
        }

        /// <summary>对“VS 手动对话”条目执行操作：stop / open / copy / remove。</summary>
        public event Action<ExternalChat, string> ExternalActionRequested;

        private Func<IReadOnlyList<ExternalChat>> _externals;

        /// <summary>
        /// clearedAt：「清除已完成」的时间点，此前完成的条目只在界面隐藏；hiddenMarks：因「已重新排队」而隐藏的失败条目。
        /// clearedAt: time of "Clear completed" (items completed before it are hidden in the UI only); hiddenMarks: failed entries hidden as "requeued".
        /// </summary>
        public void Bind(TaskQueue queue, Func<IReadOnlyList<ExternalChat>> externals = null, Func<DateTime?> clearedAt = null, Func<IList<HiddenTaskMark>> hiddenMarks = null)
        {
            if (_queue != null) _queue.Changed -= Reload;
            _queue = queue;
            _externals = externals;
            _clearedAt = clearedAt;
            _hiddenMarks = hiddenMarks;
            _queue.Changed += Reload;
            Reload();
        }

        /// <summary>该任务是否已被「清除已完成」从界面隐藏：只针对清除时已完成的任务（重新排队后再次完成的会重新显示）。</summary>
        public static bool IsCleared(QueuedTask t, DateTime? clearedAt) =>
            clearedAt.HasValue && t != null && t.Status == QueueStatus.Done && (t.Finished ?? t.Created) <= clearedAt.Value;

        /// <summary>该失败任务是否因「已重新排队」而在界面隐藏。/ Whether this failed task is hidden in the UI as "requeued".</summary>
        private bool IsResentHidden(QueuedTask t) => TaskHideList.IsHidden(_hiddenMarks?.Invoke(), t);

        /// <summary>该手动对话是否已被隐藏：只针对清除时已完成（非生成中 / 已停止 / 中断）的对话。</summary>
        public static bool IsCleared(ExternalChat c, DateTime? clearedAt) =>
            clearedAt.HasValue && c != null && !c.Generating && !c.Stopped && !c.Interrupted && (c.Finished ?? c.Started) <= clearedAt.Value;

        /// <summary>外部对话条目变化后调用。</summary>
        public void RefreshItems() => Reload();

        public void SetCollapsed(bool collapsed, bool raise = false)
        {
            _collapsed = collapsed;
            Width = collapsed ? Dpi.S(44) : ExpandedWidth;
            _list.Visible = !collapsed;
            _btnClear.Visible = !collapsed;
            _btnView.Visible = !collapsed;
            _btnStart.Visible = !collapsed;
            _levelSlider.Visible = !collapsed;
            UpdateDetail();
            _btnHistory.Visible = !collapsed && (_hiddenCount > 0 || _showHistory);
            _btnCollapse.Text = collapsed ? "«" : "»";
            _tips.SetToolTip(_btnCollapse, collapsed ? "展开任务清单" : "收起任务清单");
            LayoutTop();
            _top.Height = collapsed ? Height : TopHeight;
            _top.Invalidate();
            if (raise) CollapsedChanged?.Invoke(collapsed);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_collapsed && _top.Height != Height) _top.Height = Height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, 0, Height);
        }

        private void LayoutTop()
        {
            int y = Dpi.S(6);
            if (_collapsed) { _btnCollapse.Location = new Point((_top.Width - _btnCollapse.Width) / 2, y); return; }
            _btnCollapse.Location = new Point(_top.Width - _btnCollapse.Width - Dpi.S(10), y);
            _btnView.Location = new Point(_btnCollapse.Left - _btnView.Width - Dpi.S(4), y);
            _btnClear.Location = new Point(_top.Width - _btnClear.Width - Dpi.S(10), Dpi.S(52));
            _btnHistory.Location = new Point(_btnClear.Left - _btnHistory.Width - Dpi.S(4), Dpi.S(52));
            _btnStart.SetBounds(Dpi.S(10), Dpi.S(52), Math.Max(0, _btnHistory.Left - Dpi.S(14)), Dpi.S(28));
            _levelSlider.SetBounds(Dpi.S(6), Dpi.S(86), Math.Max(0, _top.Width - Dpi.S(12)), Dpi.S(48));
        }

        /// <summary>同步顶栏滑块显示的接续等级（不触发 ReleaseLevelChanged）。/ Syncs the header slider (does not raise ReleaseLevelChanged).</summary>
        public void SetReleaseLevel(ReleaseLevel level)
        {
            _levelSlider.SetValueSilently(level);
            _tips.SetToolTip(_levelSlider, LevelTip(level));
            _top.Invalidate();
        }

        private static string LevelTip(ReleaseLevel level) =>
            ReleaseLevels.Describe(level) + "\r\n被阻塞时在任务上右键「补充信息后重试」或「放行后续任务」；拖动或点击切换（←/→ 键也可）"
            + "\r\nWhen blocked, right-click the task for \"Retry with info\" or \"Release successors\"; drag or click to switch (←/→ keys work too)";

        /// <summary>标题副文字使用独立一行。/ Subtitle has its own row.</summary>
        private int SubRight => _top.Width - Dpi.S(10);
        private string _selectionAnchor;

        private void Reload()
        {
            if (_queue == null) return;
            string selectedKey = TaskDisplayOrder.KeyOf(_list.SelectedItem) ?? _selectionAnchor;
            int top = _list.Items.Count > 0 ? _list.TopIndex : 0;
            string topKey = _list.Items.Count > 0 ? TaskDisplayOrder.KeyOf(_list.Items[top]) : null;
            var ext = _externals?.Invoke() ?? (IReadOnlyList<ExternalChat>)new ExternalChat[0];
            _knownItemKeys = _queue.Items.Cast<object>().Concat(ext).Select(TaskDisplayOrder.KeyOf).Where(k => k != null).ToList();
            _selectionAnchor = selectedKey != null && _knownItemKeys.Contains(selectedKey) ? selectedKey : null;
            // 清除与失败隐藏只影响显示，保留排序键。/ Clearing and hiding failures affect display only; retain ordering keys.
            DateTime? cleared = _clearedAt?.Invoke();
            var finishedTasks = _queue.Items.Where(t => !QueueStatus.Active(t.Status)).ToList();
            var finishedChats = ext.Where(c => !c.Generating).ToList();
            _hiddenCount = finishedTasks.Count(t => IsCleared(t, cleared) || IsResentHidden(t)) + finishedChats.Count(c => IsCleared(c, cleared));
            if (!_showHistory)
            {
                finishedTasks.RemoveAll(t => IsCleared(t, cleared) || IsResentHidden(t));
                finishedChats.RemoveAll(c => IsCleared(c, cleared));
            }
            // 生成中的对话、执行中与排队的任务在前；已结束的（任务与对话混合）按完成时间倒序
            var items = ext.Where(c => c.Generating).OrderByDescending(c => c.Started).Cast<object>()
                .Concat(_queue.Items.Where(t => QueueStatus.Active(t.Status))
                    .OrderBy(t => t.Status == QueueStatus.WaitingVs ? 2 : t.Status == QueueStatus.Waiting ? 1 : 0).ThenBy(t => t.Order).ThenBy(t => t.Id))
                .Concat(finishedTasks.Select(t => (Item: (object)t, At: t.Finished ?? t.Created))
                    .Concat(finishedChats.Select(c => (Item: (object)c, At: c.Finished ?? c.Started)))
                    .OrderByDescending(x => x.At).Select(x => x.Item))
                .ToArray();
            _list.EmptyText = _hiddenCount > 0 && !_showHistory
                ? "已隐藏 " + _hiddenCount + " 条记录（已完成 / 已重新排队的失败任务）\r\n" + _hiddenCount + " item(s) hidden (completed / requeued failed)\r\n\r\n历史仍保留在 tasks.json 与归档中\r\nHistory stays in tasks.json and the archive\r\n点击上方「历史」查看 / Click History to view"
                : DefaultEmptyText;
            if (_hiddenCount == 0) _showHistory = false;
            _btnHistory.Visible = !_collapsed && (_hiddenCount > 0 || _showHistory);
            _btnHistory.Text = _showHistory ? "收起" : "历史";
            _tips.SetToolTip(_btnHistory, _showHistory
                ? "再次隐藏已清除的 " + _hiddenCount + " 条历史记录"
                : "显示已清除的 " + _hiddenCount + " 条历史记录（完整保存在 tasks.json 与归档中）");
            if (_manualOrder) items = TaskDisplayOrder.Apply(items, _itemOrder, TaskDisplayOrder.KeyOf).ToArray();
            _displayItems = items;
            _list.BeginUpdate();
            // 按 VS 分组只影响显示：组内保持上面的排序规则 / Grouping by VS is display-only: items keep the ordering above within each group
            object[] shown = items;
            if (_groupByVs && items.Length > 0)
            {
                IReadOnlyList<TaskGroupVs> open = null;
                try { open = VsProvider?.Invoke(); } catch (Exception ex) when (!(ex is OutOfMemoryException)) { open = null; }
                shown = TaskGrouping.Build(items, open, _groupSort, _collapsedGroups, _groupOrder).ToArray();
            }
            _groupKeys = shown.OfType<TaskGroupHeader>().Select(h => h.Key).ToList();
            _list.Items.Clear();
            _list.Items.AddRange(shown);
            if (selectedKey != null) _list.SelectedItem = shown.FirstOrDefault(x => !(x is TaskGroupHeader) && TaskDisplayOrder.KeyOf(x) == selectedKey);
            // 以顶部条目的身份锚定滚动，条目消失时才退回原行号。/ Anchor scrolling by identity; fall back to the row index only if it disappeared.
            int anchor = topKey == null ? -1 : Array.FindIndex(shown, x => TaskDisplayOrder.KeyOf(x) == topKey);
            if (shown.Length > 0) _list.TopIndex = anchor >= 0 ? anchor : Math.Min(top, shown.Length - 1);
            _list.ClearFeedback();
            _list.EndUpdate();
            _btnClear.Enabled = items.Any(x => x is QueuedTask t ? t.Status == QueueStatus.Done && !IsCleared(t, cleared)
                : x is ExternalChat c && !c.Generating && !c.Stopped && !c.Interrupted && !IsCleared(c, cleared));
            _top.Invalidate();
            UpdateDetail();
        }

        private object ItemAt(Point p)
        {
            int i = _list.IndexFromPoint(p);
            return i >= 0 && i < _list.Items.Count && _list.GetItemRectangle(i).Contains(p) ? _list.Items[i] : null;
        }

        private ContextMenuStrip BuildMenu()
        {
            var m = new GroupedContextMenuStrip();
// 只保留关键操作，不适用于当前状态的条目直接隐藏；清除与历史使用标题栏按钮。
// Key actions only; items that do not apply to the current state are hidden; clearing and history live in the header buttons.
var dispatch = m.Items.Add("重新检查并推送 / Recheck and send", null, (s, e) => Do("dispatch"));
var retry = m.Items.Add("手动重新排队 / Requeue manually", null, (s, e) => Do("retry"));
var supplement = m.Items.Add("补充信息后重试… / Retry with info…", null, (s, e) => Do("supplement"));
var release = m.Items.Add("放行后续任务 / Release successors", null, (s, e) => Do("release"));
var verify = m.Items.Add("标记为已验证 / Mark as verified", null, (s, e) => Do("verify"));
            var cancel = m.Items.Add("取消任务", null, (s, e) => Do("cancel"));
            var stop = m.Items.Add("■ 停止生成", null, (s, e) => Do("stop"));
            var actionSeparator = new ToolStripSeparator();
            m.Items.Add(actionSeparator);
            var open = m.Items.Add("查看该 VS 的对话", null, (s, e) => Do("open"));
            var copy = m.Items.Add("复制任务内容", null, (s, e) => Do("copy"));
            var attachments = m.Items.Add("查看附件 / View attachments", null, (s, e) => Do("attachments"));
            var unhide = m.Items.Add("恢复显示该失败条目 / Show this failed entry again", null, (s, e) => Do("unhide"));
            var removeSeparator = new ToolStripSeparator();
            m.Items.Add(removeSeparator);
            var remove = m.Items.Add("从清单中删除", null, (s, e) => Do("remove"));
            var resetOrder = m.Items.Add("恢复默认排序 / Reset order", null, (s, e) => ResetDisplayOrder());
            // 右键分组标题时才显示 / Shown only when a group header is right-clicked
            var toggleGroup = m.Items.Add("折叠该分组 / Collapse group", null, (s, e) => ToggleGroup(_menuGroupKey));
            var expandAll = m.Items.Add("全部展开 / Expand all", null, (s, e) => SetAllCollapsed(false));
            var collapseAll = m.Items.Add("全部折叠 / Collapse all", null, (s, e) => SetAllCollapsed(true));
            var flatList = m.Items.Add("切换为平铺列表 / Switch to flat list", null, (s, e) => SetGroupByVs(false));
            var groupItems = new[] { toggleGroup, expandAll, collapseAll, flatList };
            var taskItems = new[] { dispatch, retry, supplement, release, verify, cancel, stop, actionSeparator, open, copy, attachments, unhide, removeSeparator, remove };
            m.Opening += (s, e) =>
            {
                var header = _groupByVs ? _menuHeader : null;
                _menuHeader = null;
                _menuGroupKey = header?.Key;
                foreach (var gi in groupItems) gi.Visible = header != null;
                foreach (var ti in taskItems) ti.Visible = header == null;
                resetOrder.Visible = _manualOrder || _groupSort == TaskGrouping.SortManual;
                if (header != null)
                {
                    toggleGroup.Text = header.Collapsed ? "展开该分组 / Expand group" : "折叠该分组 / Collapse group";
                    expandAll.Visible = _groupKeys.Any(k => _collapsedGroups.Contains(k));
                    collapseAll.Visible = _groupKeys.Any(k => !_collapsedGroups.Contains(k));
                    return;
                }
                var c = _list.SelectedItem as ExternalChat;
if (c != null)
{
    dispatch.Visible = retry.Visible = supplement.Visible = release.Visible = verify.Visible = cancel.Visible = attachments.Visible = unhide.Visible = false;
                    stop.Visible = stop.Enabled = c.Generating;
                    open.Enabled = copy.Enabled = remove.Enabled = true;
                    open.Text = "打开该 VS 并定位对话";
                    copy.Text = "复制提问与回答";
                    remove.Text = "从清单中移除";
                    return;
                }
                stop.Visible = false;
                open.Text = "查看该 VS 的对话";
                copy.Text = "复制任务内容";
                remove.Text = "从清单中删除";
                var t = _list.SelectedItem as QueuedTask;
                bool has = t != null;
                open.Visible = copy.Visible = open.Enabled = copy.Enabled = has;
                bool withFiles = has && t.HasAttachments;
                attachments.Visible = attachments.Enabled = withFiles;
                if (withFiles) attachments.Text = $"查看附件（{t.Attachments.Length}）/ View attachments";
                unhide.Visible = has && IsResentHidden(t);
                dispatch.Visible = dispatch.Enabled = has && (t.Status == QueueStatus.Waiting || t.Status == QueueStatus.WaitingVs);
                retry.Visible = retry.Enabled = has && (t.Status == QueueStatus.Failed || t.Status == QueueStatus.Cancelled || t.Status == QueueStatus.Unverified);
                verify.Visible = verify.Enabled = has && TaskTestChecklist.Pending(t);
                supplement.Visible = has && TaskStateMachine.IsHoldOutcome(t);
                supplement.Enabled = supplement.Visible && t.SupplementCount < TaskStateMachine.MaxSupplements;
                supplement.Text = has && t.SupplementCount > 0
                    ? $"补充信息后重试…（{t.SupplementCount}/{TaskStateMachine.MaxSupplements}）/ Retry with info…"
                    : "补充信息后重试… / Retry with info…";
                // 仅当前等级下会阻塞后续的结果才需要放行 / Release only matters for outcomes that block at the current level
                release.Visible = has && TaskStateMachine.IsHoldOutcome(t) && _queue != null && ReleaseLevels.Blocks(_queue.ReleaseLevel, t);
                release.Enabled = release.Visible && !t.Released;
                release.Text = has && t.Released ? "已放行 / Released" : "放行后续任务 / Release successors";
                cancel.Visible = cancel.Enabled = has && (t.Status == QueueStatus.Waiting || t.Status == QueueStatus.WaitingVs || t.Status == QueueStatus.Running);
                cancel.Text = has && t.Status == QueueStatus.Running ? "停止跟踪（不停止 Copilot）" : "取消任务";
                remove.Visible = has;
                remove.Enabled = has && t.Status != QueueStatus.Sending;
            };
            m.Opening += (s, e) =>
            {
                if (!m.Items.Cast<ToolStripItem>().Any(i => i.Available && !(i is ToolStripSeparator))) e.Cancel = true;
            };
            return m;
        }

        private void Do(string action)
        {
            if (_list.SelectedItem is ExternalChat c) { ExternalActionRequested?.Invoke(c, action); return; }
            if (!(_list.SelectedItem is QueuedTask t)) return;
            if (action == "copy") { try { Clipboard.SetText(t.Text); } catch { } return; }
            ActionRequested?.Invoke(t, action);
        }

        private void Top_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Sidebar);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int waiting = _queue?.Items.Count(t => t.Status == QueueStatus.Waiting) ?? 0;
            int paused = _queue?.Items.Count(t => t.Status == QueueStatus.Waiting
                && TaskStateMachine.PausedText(TaskStateMachine.BlockingTask(_queue.Items, t, _queue.ReleaseLevel)) != null) ?? 0;
            int parked = _queue?.Items.Count(t => t.Status == QueueStatus.WaitingVs) ?? 0;
            int running = _queue?.Items.Count(t => t.Status == QueueStatus.Running || t.Status == QueueStatus.Sending) ?? 0;
            int chatting = _externals?.Invoke().Count(c => c.Generating) ?? 0;
            if (_collapsed)
            {
                int y = Dpi.S(56);
                var sf = new StringFormat(StringFormatFlags.DirectionVertical);
                using (var b = new SolidBrush(Theme.TextSecondary)) g.DrawString("任务清单", Theme.SemiBold, b, (_top.Width - Dpi.S(16)) / 2f, y, sf);
                int n = waiting + parked + running + chatting;
                bool saveFailed = _queue?.SaveError != null;
                _tip = "";
                _tips.SetToolTip(_top, saveFailed ? "任务清单保存失败：" + _queue.SaveError + "\r\n内存中的任务不会丢失，每 10 秒自动重试。" : "");
                if (n > 0 || saveFailed)
                {
                    float cx = _top.Width / 2f, cy = y + Dpi.S(92);
                    Theme.FillCircle(g, saveFailed ? Theme.Danger : running > 0 ? Theme.BusyDot : Theme.Accent, cx, cy, Dpi.S(10));
                    TextRenderer.DrawText(g, saveFailed ? "!" : n.ToString(), Theme.Small, new Rectangle((int)cx - Dpi.S(10), (int)cy - Dpi.S(10), Dpi.S(20), Dpi.S(20)), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                return;
            }
            TextRenderer.DrawText(g, "任务清单", Theme.SemiBold, new Point(Dpi.S(16), Dpi.S(12)), Theme.Text, TextFormatFlags.NoPadding);
            string sub = running + waiting == 0 ? "空闲" : $"执行 {running} · 排队 {waiting - paused}";
            if (paused > 0) sub += $" · 暂停 {paused}";
            if (parked > 0) sub = (running + waiting == 0 ? "" : sub + " · ") + $"待打开 {parked}";
            if (chatting > 0) sub = (running + waiting + parked == 0 ? "" : sub + " · ") + $"对话 {chatting}";
            int eligible = _queue?.Items.Count(t => QueueStatus.Active(t.Status) && IsTaskEligible(t)) ?? 0;
            int awaitingStart = _queue?.Items.Count(t => QueueStatus.Active(t.Status) && !IsTaskEligible(t)) ?? 0;
            bool waitingForStart = !_workflowStarted && eligible == 0;
            if (!_workflowStarted) sub = $"可调度 {eligible} · 待开始 {awaitingStart} / Eligible · Awaiting Start";
            Color subColor = waitingForStart ? Theme.Warning : running > 0 || chatting > 0 ? Theme.BusyFg : waiting + parked > 0 ? Theme.AccentText : Theme.TextMuted;
            string tip = awaitingStart == 0 ? null : TaskDispatcher.WaitingForStart;
            if (_queue?.SaveError != null)
            {
                // 保存失败：清单仍在内存中，提示用户并自动重试
                sub = "⚠ 保存失败，自动重试中";
                subColor = Theme.Danger;
                tip = "任务清单保存失败：" + _queue.SaveError + "\r\n内存中的任务不会丢失，每 10 秒自动重试。\r\n日志：" + TaskQueue.LogPath;
            }
            else if (_queue?.LoadWarning != null && !_loadWarningSeen)
            {
                sub = "⚠ 任务记录已修复，悬停查看";
                subColor = Theme.Warning;
                tip = _queue.LoadWarning + "\r\n日志：" + TaskQueue.LogPath + "\r\n（点击此处关闭提示）";
            }
            if (_tip != tip) { _tip = tip; _tips.SetToolTip(_top, tip ?? ""); }
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(Dpi.S(16), Dpi.S(34), Math.Max(0, SubRight - Dpi.S(20)), Dpi.S(16)), subColor,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, 0, _top.Height - 1, _top.Width, _top.Height - 1);
        }

        private static readonly Color ChatFg = Color.FromArgb(94, 234, 212), ChatBg = Color.FromArgb(17, 52, 50), ChatDot = Color.FromArgb(45, 212, 191);

        /// <summary>“VS 手动对话”卡片：左侧青色色条 + 来源标签，与本工具发布的任务区分。</summary>
        private void DrawChat(Graphics g, DrawItemEventArgs e, ExternalChat c)
        {
            var r = new Rectangle(e.Bounds.X + Dpi.S(10), e.Bounds.Y + Dpi.S(5), e.Bounds.Width - Dpi.S(20), e.Bounds.Height - Dpi.S(10));
            bool sel = (e.State & DrawItemState.Selected) != 0, hover = _list.HoverIndex == e.Index;
            Theme.FillRound(g, sel ? Theme.RowSelected : hover ? Theme.RowHover : Theme.Surface, r, Dpi.S(10));
            Theme.DrawRound(g, sel ? Theme.AccentBorder : Theme.Border, r, Dpi.S(10));
            Theme.FillRound(g, c.Generating ? ChatDot : Color.FromArgb(40, 110, 104), new Rectangle(r.X + Dpi.S(4), r.Y + Dpi.S(12), Dpi.S(3), r.Height - Dpi.S(24)), Dpi.S(1));

            Color fg, bg, dot;
            string st;
            if (c.Generating) { st = "生成中 · " + Dur(DateTime.Now - c.Started); fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; }
            else if (c.Stopped) { st = "已停止"; fg = Theme.NoneFg; bg = Theme.NoneBg; dot = Theme.NoneDot; }
            else if (c.Interrupted) { st = "已中断"; fg = Theme.NoneFg; bg = Theme.NoneBg; dot = Theme.NoneDot; }
            else { st = "✓ 已完成" + (c.Finished.HasValue ? " · " + Dur(c.Finished.Value - c.Started) : ""); fg = Theme.IdleFg; bg = Theme.IdleBg; dot = Theme.IdleDot; }
            int x = r.X + Dpi.S(14), y = r.Y + Dpi.S(10), right = r.Right - Dpi.S(12);
            var when = c.Finished ?? c.Started;
            string time = when.Date == DateTime.Today ? when.ToString("HH:mm") : when.ToString("MM-dd HH:mm");
            var tsz = TextRenderer.MeasureText(g, time, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, time, Theme.Small, new Point(right - tsz.Width, y + Dpi.S(3)), Theme.TextMuted, TextFormatFlags.NoPadding);
            int pw = Theme.DrawPill(g, x, y, Math.Max(0, right - x - tsz.Width - Dpi.S(8)), st, bg, fg, dot);
            int avail = right - tsz.Width - x - pw - Dpi.S(16);
            if (avail > Dpi.S(40)) Theme.DrawPill(g, x + pw + Dpi.S(6), y, avail, c.Restored && c.Pid == 0 ? "历史对话" : "手动对话", ChatBg, ChatFg, null);

            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            y += Dpi.S(28);
            TextRenderer.DrawText(g, "◎ " + c.VsName, Theme.SemiBold, new Rectangle(x, y, right - x, Dpi.S(18)), c.Generating ? ChatFg : Theme.TextSecondary, flags | TextFormatFlags.SingleLine);
            y += Dpi.S(20);
            TextRenderer.DrawText(g, "问：" + OneLine(c.Question), Theme.Small, new Rectangle(x, y, right - x, Dpi.S(17)), c.Generating ? Theme.Text : Theme.TextSecondary, flags | TextFormatFlags.SingleLine);
            string ans = string.IsNullOrWhiteSpace(c.Answer) ? (c.Generating ? "Copilot 正在思考…" : "（未读取到回答）") : OneLine(c.Answer);
            TextRenderer.DrawText(g, "答：" + ans, Theme.Small, new Rectangle(x, y + Dpi.S(18), right - x, Dpi.S(17)), Theme.TextMuted, flags | TextFormatFlags.SingleLine);
        }

        private void DrawHeader(Graphics g, DrawItemEventArgs e, TaskGroupHeader h)
        {
            var b = e.Bounds;
            // 第一组上方不画分割线，只有一个分组时也就没有多余线条 / No divider above the first group, so a single group has no stray line
            if (e.Index > 0)
                using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, b.X + Dpi.S(10), b.Y + Dpi.S(3), b.Right - Dpi.S(10), b.Y + Dpi.S(3));
            var r = new Rectangle(b.X + Dpi.S(8), b.Y + Dpi.S(7), b.Width - Dpi.S(16), b.Height - Dpi.S(9));
            if (_list.HoverIndex == e.Index) Theme.FillRound(g, Theme.RowHover, r, Dpi.S(8));
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int x = r.X + Dpi.S(6), right = r.Right - Dpi.S(8);
            TextRenderer.DrawText(g, h.Collapsed ? "▸" : "▾", Theme.Small, new Rectangle(x, r.Y + Dpi.S(5), Dpi.S(12), Dpi.S(16)), Theme.TextMuted, TextFormatFlags.NoPadding);
            x += Dpi.S(14);
            if (h.HasRunning)
            {
                Theme.FillCircle(g, Theme.BusyDot, right - Dpi.S(4), r.Y + Dpi.S(12), 3.5f * Dpi.Scale);
                right -= Dpi.S(14);
            }
            Color titleColor = h.IsWaitingOpen ? Theme.Warning : h.IsOpen ? Theme.Text : Theme.TextSecondary;
            TextRenderer.DrawText(g, h.Title, Theme.SemiBold, new Rectangle(x, r.Y + Dpi.S(4), Math.Max(0, right - x), Dpi.S(18)), titleColor, flags);
            TextRenderer.DrawText(g, h.StatsText, Theme.Small, new Rectangle(x, r.Y + Dpi.S(22), Math.Max(0, r.Right - Dpi.S(8) - x), Dpi.S(16)),
                h.HasRunning ? Theme.BusyFg : Theme.TextMuted, flags);
        }

        private void List_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index >= 0 && e.Index < _list.Items.Count && _list.Items[e.Index] is TaskGroupHeader header)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                DrawHeader(e.Graphics, e, header);
                return;
            }
            if (e.Index >= 0 && e.Index < _list.Items.Count && _list.Items[e.Index] is ExternalChat chat)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                DrawChat(e.Graphics, e, chat);
                return;
            }
            if (e.Index < 0 || e.Index >= _list.Items.Count || !(_list.Items[e.Index] is QueuedTask t)) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(e.Bounds.X + Dpi.S(10), e.Bounds.Y + Dpi.S(5), e.Bounds.Width - Dpi.S(20), e.Bounds.Height - Dpi.S(10));
            bool sel = (e.State & DrawItemState.Selected) != 0, hover = _list.HoverIndex == e.Index;
            Theme.FillRound(g, sel ? Theme.RowSelected : hover ? Theme.RowHover : Theme.Surface, r, Dpi.S(10));
            Theme.DrawRound(g, sel ? Theme.AccentBorder : Theme.Border, r, Dpi.S(10));
            bool active = QueueStatus.Active(t.Status);

            StatusLook(t, out string st, out Color fg, out Color bg, out Color dot);
            int x = r.X + Dpi.S(12), y = r.Y + Dpi.S(10), right = r.Right - Dpi.S(12);
            string time = (t.Finished ?? t.Started ?? t.Created).ToString("HH:mm");
            var tsz = TextRenderer.MeasureText(g, time, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, time, Theme.Small, new Point(right - tsz.Width, y + Dpi.S(3)), Theme.TextMuted, TextFormatFlags.NoPadding);
            int pw = Theme.DrawPill(g, x, y, Math.Max(0, right - x - tsz.Width - Dpi.S(8)), st, bg, fg, dot);
            string src = "#" + t.Id + (t.FromAgent ? " · AI" : "") + (t.HasAttachments ? " · 📎 " + t.Attachments.Length : "");
            TextRenderer.DrawText(g, src, Theme.Small, new Rectangle(x + pw + Dpi.S(8), y + Dpi.S(3), Math.Max(0, right - tsz.Width - x - pw - Dpi.S(16)), Dpi.S(16)),
                Theme.TextMuted, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            y += Dpi.S(28);
            string vsLine = t.Status == QueueStatus.WaitingVs ? "→ " + (t.Target ?? t.VsName) + "（等待打开 / waiting to open）" : "→ " + t.VsName;
            TextRenderer.DrawText(g, vsLine, Theme.SemiBold, new Rectangle(x, y, right - x, Dpi.S(18)), active ? Theme.AccentText : Theme.TextSecondary, flags | TextFormatFlags.SingleLine);
            y += Dpi.S(20);
            string body = OneLine(t.Text);
            // 待确认 / 未验证 / 失败显示记录的待处理内容或失败原因，点击条目可在下方查看全文 / Pending / unverified / failed show the recorded note; click for the full text below
            string tail = TaskHoldNote.IsPending(t) && !string.IsNullOrEmpty(t.PendingNote) ? "⚑ 待处理：" + OneLine(t.PendingNote)
                : QueueStatus.Delivered(t.Status) && !string.IsNullOrEmpty(t.Result) ? "↳ " + OneLine(t.Result)
                : t.Status == QueueStatus.Failed && !string.IsNullOrEmpty(t.FailureReason) ? "⚠ " + OneLine(t.FailureReason)
                : t.Status == QueueStatus.Failed && !string.IsNullOrEmpty(t.Error) ? "⚠ " + OneLine(t.Error)
                : QueueStatus.Active(t.Status) ? (t.ManualChatWaitReason ?? EligibilityText(t))
                : t.Status == QueueStatus.WaitingVs ? "⏳ 「" + (t.Target ?? t.VsName) + "」打开后自动推送 / pushed once it opens" : null;
            if (!string.IsNullOrEmpty(t.PredecessorNotice)) tail = t.PredecessorNotice + (tail == null ? "" : " · " + tail);
            if (IsResentHidden(t))
            {
                // 历史模式下显示的已隐藏失败条目：注明被哪条任务取代 / A hidden failed entry shown in history mode: say which task superseded it
                int? by = TaskHideList.ReplacedBy(_hiddenMarks?.Invoke(), t.Id);
                tail = "⤴ 已重新排队为 #" + by + "，已隐藏 / requeued as #" + by + ", hidden" + (tail == null ? "" : " · " + tail);
            }
            int bodyH = tail == null ? Dpi.S(34) : Dpi.S(17);
            TextRenderer.DrawText(g, body, Theme.Small, new Rectangle(x, y, right - x, bodyH), active ? Theme.Text : Theme.TextSecondary, flags | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            if (tail != null)
                TextRenderer.DrawText(g, tail, Theme.Small, new Rectangle(x, y + Dpi.S(18), right - x, Dpi.S(17)),
                    t.Status == QueueStatus.Failed ? Theme.Danger : TaskHoldNote.IsPending(t) ? Theme.Warning : Theme.TextMuted, flags | TextFormatFlags.SingleLine);
        }

        private void StatusLook(QueuedTask t, out string text, out Color fg, out Color bg, out Color dot)
        {
            switch (t.Status)
            {
                case QueueStatus.Waiting:
                    text = t.ManualChatWaitReason == null ? TaskStateMachine.StatusText(t, DateTime.Now, _queue.Items, _queue.ReleaseLevel)
                        : "等待对话 / Wait chat";
                    fg = Theme.AccentText; bg = Theme.AccentLight; dot = Theme.Accent; break;
                case QueueStatus.WaitingVs:
                    text = "待打开 VS"; fg = Theme.Warning; bg = Theme.NoneBg; dot = Theme.Warning; break;
                case QueueStatus.Sending:
                    text = "发送中…"; fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; break;
                case QueueStatus.Running:
                    text = "执行中 · " + Dur(DateTime.Now - (t.Started ?? DateTime.Now)); fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; break;
                case QueueStatus.Done:
    text = (t.NeedsUser ? "✓ 待验证" : "✓ 已完成") + (t.Released ? " · 已放行" : "") + (t.Started.HasValue && t.Finished.HasValue ? " · " + Dur(t.Finished.Value - t.Started.Value) : "");
    fg = t.NeedsUser ? Theme.Warning : Theme.IdleFg; bg = Theme.IdleBg; dot = t.NeedsUser ? Theme.Warning : Theme.IdleDot; break;
case QueueStatus.Unverified:
                    text = "◐ 未验证" + (t.Started.HasValue && t.Finished.HasValue ? " · " + Dur(t.Finished.Value - t.Started.Value) : "");
                    fg = Theme.UnverifiedFg; bg = Theme.UnverifiedBg; dot = Theme.UnverifiedDot; break;
                case QueueStatus.Failed:
                    text = t.Released ? "失败 · 已放行" : "失败"; fg = Theme.Danger; bg = Color.FromArgb(60, 22, 26); dot = Theme.Danger; break;
                default:
                    text = "已取消"; fg = Theme.NoneFg; bg = Theme.NoneBg; dot = Theme.NoneDot; break;
            }
        }

        private static string Dur(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{Math.Max(0, t.Seconds)}s";

        private static string OneLine(string s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim(), @"\s+", " ");
    }
}
