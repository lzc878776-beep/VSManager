using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>主窗口右侧的任务清单：显示排队 / 执行中 / 已完成的任务，可折叠。</summary>
    public sealed class TaskPanel : Panel
    {
        private readonly Panel _top = new Panel();
        private readonly VsListBox _list = new VsListBox();
        private readonly FlatButton _btnClear = new FlatButton { Text = "清除已完成", Ghost = true };
        private readonly FlatButton _btnHistory = new FlatButton { Text = "历史", Ghost = true };
        private Func<DateTime?> _clearedAt;
        private Func<IList<HiddenTaskMark>> _hiddenMarks;
        private bool _showHistory;
        private int _hiddenCount;
        private const string DefaultEmptyText = "暂无任务\r\n\r\nAI 助手发布任务时，若目标 VS 正忙\r\n会在这里排队，空闲后自动发布\r\n完成后自动通知 AI 助手";
        private readonly FlatButton _btnCollapse = new FlatButton { Text = "»", Ghost = true };
        private readonly Timer _tick = new Timer { Interval = 1000 };
        private readonly ToolTip _tips = new ToolTip();
        private TaskQueue _queue;
        private bool _collapsed;
        private bool _loadWarningSeen;
        private string _tip;

        /// <summary>请求对任务执行操作：dispatch / cancel / retry / remove / clear / unclear / unhide / open。/ Requests a task action.</summary>
        public event Action<QueuedTask, string> ActionRequested;
        public event Action<bool> CollapsedChanged;

        public TaskPanel()
        {
            Dock = DockStyle.Right;
            BackColor = Theme.Sidebar;
            DoubleBuffered = true;
            Padding = new Padding(1, 0, 0, 0);

            _top.Dock = DockStyle.Top;
            _top.Height = Dpi.S(52);
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
            _top.Controls.Add(_btnClear);
            _top.Controls.Add(_btnCollapse);
            _top.Resize += (s, e) => LayoutTop();

            _list.Dock = DockStyle.Fill;
            _list.ItemHeight = Dpi.S(108);
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
                int i = _list.IndexFromPoint(e.Location);
                if (i >= 0) _list.SelectedIndex = i;
            };
            _list.ContextMenuStrip = BuildMenu();
            _list.Resize += (s, e) => _list.Invalidate();
            // 条目较高，滚轮每格滚动 1 条；焦点在其他控件时，指针位于任务清单上的滚轮也转给任务清单
            _list.WheelItemsPerNotch = 1;
            _wheel = new WheelForwarder(_list);
            Application.AddMessageFilter(_wheel);

            Controls.Add(_list);
            Controls.Add(_top);

            _tick.Tick += (s, e) =>
            {
                if ((_queue != null && _queue.Items.Any(t => t.Status == QueueStatus.Running || t.Status == QueueStatus.Sending))
                    || (_externals?.Invoke().Any(c => c.Generating) ?? false)) _list.Invalidate();
            };
            _tick.Start();
            Disposed += (s, e) => { Application.RemoveMessageFilter(_wheel); _tick.Dispose(); _tips.Dispose(); };
        }

        private readonly WheelForwarder _wheel;

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

        /// <summary>对“VS 手动对话”条目执行操作：stop / open / copy / remove。</summary>
        public event Action<ExternalChat, string> ExternalActionRequested;

        private Func<IReadOnlyList<ExternalChat>> _externals;

        /// <summary>
        /// clearedAt：「清除已完成」的时间点，此前完成的条目只在界面隐藏；hiddenMarks：因「已重新排队」而隐藏的失败条目。
        /// clearedAt: time of "Clear completed" (items completed before it are hidden in the UI only); hiddenMarks: failed entries hidden as "requeued".
        /// </summary>
        public void Bind(TaskQueue queue, Func<IReadOnlyList<ExternalChat>> externals = null, Func<DateTime?> clearedAt = null, Func<IList<HiddenTaskMark>> hiddenMarks = null)
        {
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
            _btnHistory.Visible = !collapsed && (_hiddenCount > 0 || _showHistory);
            _btnCollapse.Text = collapsed ? "«" : "»";
            _tips.SetToolTip(_btnCollapse, collapsed ? "展开任务清单" : "收起任务清单");
            LayoutTop();
            _top.Height = collapsed ? Height : Dpi.S(52);
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
            int y = Dpi.S(12);
            if (_collapsed) { _btnCollapse.Location = new Point((_top.Width - _btnCollapse.Width) / 2, y); return; }
            _btnCollapse.Location = new Point(_top.Width - _btnCollapse.Width - Dpi.S(10), y);
            _btnClear.Location = new Point(_btnCollapse.Left - _btnClear.Width - Dpi.S(4), y);
            _btnHistory.Location = new Point(_btnClear.Left - _btnHistory.Width - Dpi.S(4), y);
        }

        /// <summary>标题副文字可用的右边界（避开按钮）。</summary>
        private int SubRight => _btnHistory.Visible ? _btnHistory.Left : _btnClear.Left;

        private void Reload()
        {
            if (_queue == null) return;
            var selected = _list.SelectedItem;
            var ext = _externals?.Invoke() ?? (IReadOnlyList<ExternalChat>)new ExternalChat[0];
            // 「清除已完成」只影响显示：按清除时间点过滤，重启后保持；点「历史」可临时显示被隐藏的条目
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
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Items.AddRange(items);
            if (selected != null && items.Contains(selected)) _list.SelectedItem = selected;
            _list.EndUpdate();
            _btnClear.Enabled = items.Any(x => x is QueuedTask t ? t.Status == QueueStatus.Done && !IsCleared(t, cleared)
                : x is ExternalChat c && !c.Generating && !c.Stopped && !c.Interrupted && !IsCleared(c, cleared));
            _top.Invalidate();
        }

        private object ItemAt(Point p)
        {
            int i = _list.IndexFromPoint(p);
            return i >= 0 && i < _list.Items.Count && _list.GetItemRectangle(i).Contains(p) ? _list.Items[i] : null;
        }

        private ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            Theme.Apply(m);
            var open = m.Items.Add("查看该 VS 的对话", null, (s, e) => Do("open"));
            var dispatch = m.Items.Add("立即尝试发布", null, (s, e) => Do("dispatch"));
            var retry = m.Items.Add("重新排队", null, (s, e) => Do("retry"));
            var cancel = m.Items.Add("取消任务", null, (s, e) => Do("cancel"));
            var stop = m.Items.Add("■ 停止生成", null, (s, e) => Do("stop"));
            m.Items.Add(new ToolStripSeparator());
            var copy = m.Items.Add("复制任务内容", null, (s, e) => Do("copy"));
            var remove = m.Items.Add("从清单中删除", null, (s, e) => Do("remove"));
            var clear = m.Items.Add("清除已完成（仅界面）", null, (s, e) => ActionRequested?.Invoke(null, "clear"));
            var history = new ToolStripMenuItem("显示已清除的历史", null, (s, e) => { _showHistory = !_showHistory; Reload(); });
            m.Items.Add(history);
            var unclear = m.Items.Add("撤销清除（恢复显示全部历史）", null, (s, e) => { _showHistory = false; ActionRequested?.Invoke(null, "unclear"); });
            var unhide = m.Items.Add("恢复显示该失败条目 / Show this failed entry again", null, (s, e) => Do("unhide"));
            m.Opening += (s, e) =>
            {
                clear.Enabled = _btnClear.Enabled;
                history.Checked = _showHistory;
                history.Enabled = _hiddenCount > 0;
                unclear.Enabled = _hiddenCount > 0;
                var c = _list.SelectedItem as ExternalChat;
                bool isChat = c != null;
                dispatch.Visible = retry.Visible = cancel.Visible = !isChat;
                stop.Visible = isChat;
                unhide.Visible = !isChat && _list.SelectedItem is QueuedTask ht && IsResentHidden(ht);
                if (isChat)
                {
                    stop.Enabled = c.Generating;
                    open.Enabled = copy.Enabled = remove.Enabled = true;
                    open.Text = "打开该 VS 并定位对话";
                    copy.Text = "复制提问与回答";
                    remove.Text = "从清单中移除";
                    return;
                }
                open.Text = "查看该 VS 的对话";
                copy.Text = "复制任务内容";
                remove.Text = "从清单中删除";
                var t = _list.SelectedItem as QueuedTask;
                bool has = t != null;
                open.Enabled = copy.Enabled = has;
                dispatch.Enabled = has && (t.Status == QueueStatus.Waiting || t.Status == QueueStatus.WaitingVs);
                retry.Enabled = has && (t.Status == QueueStatus.Failed || t.Status == QueueStatus.Cancelled);
                cancel.Enabled = has && (t.Status == QueueStatus.Waiting || t.Status == QueueStatus.WaitingVs || t.Status == QueueStatus.Running);
                cancel.Text = has && t.Status == QueueStatus.Running ? "停止跟踪（不停止 Copilot）" : "取消任务";
                remove.Enabled = has && t.Status != QueueStatus.Sending;
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
                && TaskStateMachine.BlockingTask(_queue.Items, t)?.Status == QueueStatus.Failed) ?? 0;
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
            Color subColor = running > 0 || chatting > 0 ? Theme.BusyFg : waiting + parked > 0 ? Theme.AccentText : Theme.TextMuted;
            string tip = null;
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
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(Dpi.S(16), Dpi.S(31), Math.Max(0, SubRight - Dpi.S(20)), Dpi.S(16)), subColor,
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

        private void List_DrawItem(object sender, DrawItemEventArgs e)
        {
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
            string src = "#" + t.Id + (t.FromAgent ? " · AI" : "");
            TextRenderer.DrawText(g, src, Theme.Small, new Rectangle(x + pw + Dpi.S(8), y + Dpi.S(3), Math.Max(0, right - tsz.Width - x - pw - Dpi.S(16)), Dpi.S(16)),
                Theme.TextMuted, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            y += Dpi.S(28);
            string vsLine = t.Status == QueueStatus.WaitingVs ? "→ " + (t.Target ?? t.VsName) + "（等待打开 / waiting to open）" : "→ " + t.VsName;
            TextRenderer.DrawText(g, vsLine, Theme.SemiBold, new Rectangle(x, y, right - x, Dpi.S(18)), active ? Theme.AccentText : Theme.TextSecondary, flags | TextFormatFlags.SingleLine);
            y += Dpi.S(20);
            string body = OneLine(t.Text);
            string tail = t.Status == QueueStatus.Done && !string.IsNullOrEmpty(t.Result) ? "↳ " + OneLine(t.Result)
                : t.Status == QueueStatus.Failed && !string.IsNullOrEmpty(t.Error) ? "⚠ " + OneLine(t.Error)
                : t.Status == QueueStatus.WaitingVs ? "⏳ 「" + (t.Target ?? t.VsName) + "」打开后自动推送 / pushed once it opens" : null;
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
                    t.Status == QueueStatus.Failed ? Theme.Danger : Theme.TextMuted, flags | TextFormatFlags.SingleLine);
        }

        private void StatusLook(QueuedTask t, out string text, out Color fg, out Color bg, out Color dot)
        {
            switch (t.Status)
            {
                case QueueStatus.Waiting:
                    text = TaskStateMachine.StatusText(t, DateTime.Now, _queue.Items); fg = Theme.AccentText; bg = Theme.AccentLight; dot = Theme.Accent; break;
                case QueueStatus.WaitingVs:
                    text = "待打开 VS"; fg = Theme.Warning; bg = Theme.NoneBg; dot = Theme.Warning; break;
                case QueueStatus.Sending:
                    text = "发送中…"; fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; break;
                case QueueStatus.Running:
                    text = "执行中 · " + Dur(DateTime.Now - (t.Started ?? DateTime.Now)); fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; break;
                case QueueStatus.Done:
                    text = "✓ 已完成" + (t.Started.HasValue && t.Finished.HasValue ? " · " + Dur(t.Finished.Value - t.Started.Value) : "");
                    fg = Theme.IdleFg; bg = Theme.IdleBg; dot = Theme.IdleDot; break;
                case QueueStatus.Failed:
                    text = "失败"; fg = Theme.Danger; bg = Color.FromArgb(60, 22, 26); dot = Theme.Danger; break;
                default:
                    text = "已取消"; fg = Theme.NoneFg; bg = Theme.NoneBg; dot = Theme.NoneDot; break;
            }
        }

        private static string Dur(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{Math.Max(0, t.Seconds)}s";

        private static string OneLine(string s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim(), @"\s+", " ");
    }
}
