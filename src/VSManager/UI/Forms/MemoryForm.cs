using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 「内存」面板：按进程分组展示 VSManager、各 VS 实例及其子进程、共享 VS 组件的工作集与私有字节，提供温和清理与超阈值自动策略。
    /// 只修剪工作集 / 触发 GC，从不结束任何进程。
    /// Memory panel: working set and private bytes of VSManager, each VS instance with its child processes and shared VS
    /// components, grouped by owner, with gentle cleanup and a threshold policy. It only trims working sets / runs GC and never
    /// terminates any process.
    /// </summary>
    public class MemoryForm : Form
    {
        private readonly AppSettings _settings;
        private readonly Func<IList<VsRef>> _refs;
        private readonly Action<string> _status;
        private readonly MemList _list = new MemList();
        private readonly Panel _summary, _columns;
        private readonly ToggleSwitch _autoRefresh = new ToggleSwitch();
        private readonly FlatButton _btnRefresh, _btnSelf, _btnAllVs;
        private readonly Label _time = new Label();
        private readonly TextBox _log = new TextBox();
        private readonly ToggleSwitch _policy = new ToggleSwitch();
        private readonly TextBox _threshold = new TextBox();
        private readonly DarkCombo _mode = new DarkCombo();
        private readonly Timer _timer = new Timer { Interval = 5000 };
        private readonly ToolTip _tips = new ToolTip();
        private readonly HashSet<string> _cleaning = new HashSet<string>();
        private MemSnapshot _snap;
        private bool _refreshing, _loading;

        /// <summary>列表中的一行：分组标题（P 为 null）或进程。/ A list row: group header (P == null) or a process.</summary>
        private sealed class Row
        {
            public MemGroup G;
            public MemProc P;
        }

        public MemoryForm(AppSettings settings, Func<IList<VsRef>> refs, Action<string> status)
        {
            _settings = settings;
            _refs = refs;
            _status = status;
            Text = "内存 / Memory";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Dpi.S(1120), wa.Width - Dpi.S(40)), Math.Min(Dpi.S(800), wa.Height - Dpi.S(40)));
            MinimumSize = new Size(Math.Min(Dpi.S(860), wa.Width), Math.Min(Dpi.S(520), wa.Height));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 顶部工具栏 / Toolbar ----
            var bar = new Panel { Dock = DockStyle.Top, Height = Dpi.S(56), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(16), Dpi.S(11), Dpi.S(16), Dpi.S(11)) };
            bar.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            _time.Dock = DockStyle.Fill;
            _time.ForeColor = Theme.TextMuted;
            _time.TextAlign = ContentAlignment.MiddleLeft;
            _time.AutoEllipsis = true;
            _time.UseMnemonic = false;
            _time.Text = "正在测量… / Measuring…";
            _autoRefresh.Text = "自动刷新 5 秒 / Auto 5 s";
            _autoRefresh.Dock = DockStyle.Right;
            _autoRefresh.Width = Dpi.S(210);
            _autoRefresh.Checked = true;
            _autoRefresh.CheckedChanged += (s, e) => _timer.Enabled = _autoRefresh.Checked;
            _btnRefresh = new FlatButton { Text = "↻ 刷新 / Refresh", Ghost = true, Dock = DockStyle.Right, Width = Dpi.S(130) };
            _btnRefresh.Click += async (s, e) => await RefreshAsync();
            _btnSelf = new FlatButton { Text = "🧹 清理 VSManager", Dock = DockStyle.Right, Width = Dpi.S(160) };
            _btnSelf.Click += async (s, e) => await CleanAsync(MemGroupKind.Self.ToString());
            _btnAllVs = new FlatButton { Text = "🧹 温和清理全部 VS / Clean all VS", Primary = true, Dock = DockStyle.Right, Width = Dpi.S(250) };
            _btnAllVs.Click += async (s, e) => await CleanAllVsAsync();
            _tips.SetToolTip(_autoRefresh, "每 5 秒重新测量一次 / Re-measure every 5 seconds");
            _tips.SetToolTip(_btnRefresh, "立即重新测量（F5）/ Measure now (F5)");
            _tips.SetToolTip(_btnSelf, "对 VSManager 本体做完整 GC 并修剪工作集（含其 WebView2 进程）\nFull GC of VSManager and trim its working set (including its WebView2 processes)");
            _tips.SetToolTip(_btnAllVs, "依次对每个 VS 实例温和清理：尝试 VS 内部 GC + 修剪可安全清理进程的工作集；不结束任何进程、不影响未保存内容\nGently clean every VS: try an in-VS GC + trim safe processes; nothing is terminated and unsaved work is untouched");
            bar.Controls.Add(_time);
            bar.Controls.Add(_autoRefresh);
            bar.Controls.Add(Gap(bar.BackColor));
            bar.Controls.Add(_btnRefresh);
            bar.Controls.Add(Gap(bar.BackColor));
            bar.Controls.Add(_btnSelf);
            bar.Controls.Add(Gap(bar.BackColor));
            bar.Controls.Add(_btnAllVs);

            // ---- 汇总 / Summary tiles ----
            _summary = new DoubleBufferedPanel { Dock = DockStyle.Top, Height = Dpi.S(84), BackColor = Theme.Background };
            _summary.Paint += Summary_Paint;
            _summary.Resize += (s, e) => _summary.Invalidate();

            // ---- 列标题 / Column header ----
            _columns = new DoubleBufferedPanel { Dock = DockStyle.Top, Height = Dpi.S(30), BackColor = Theme.Sidebar };
            _columns.Paint += Columns_Paint;
            _columns.Resize += (s, e) => _columns.Invalidate();

            _list.Dock = DockStyle.Fill;
            _list.ItemHeight = Dpi.S(30);
            _list.EmptyText = "正在测量… / Measuring…";
            _list.DrawItem += List_DrawItem;
            _list.MouseClick += List_MouseClick;
            _list.MouseMove += List_MouseMove;
            _list.Resize += (s, e) => _list.Invalidate();

            // ---- 自动策略 / Threshold policy ----
            var policy = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(52), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(16), Dpi.S(10), Dpi.S(16), Dpi.S(10)) };
            policy.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, policy.Width, 0); };
            _policy.Text = "超阈值自动处理 / Auto on threshold";
            _policy.Dock = DockStyle.Left;
            _policy.Width = Dpi.S(290);
            var thLabel = new Label { Text = "阈值 / Threshold", Dock = DockStyle.Left, AutoSize = true, ForeColor = Theme.TextSecondary, Padding = new Padding(Dpi.S(12), Dpi.S(7), Dpi.S(6), 0), UseMnemonic = false };
            var thHost = new Panel { Dock = DockStyle.Left, Width = Dpi.S(90), BackColor = Theme.Elevated, Padding = new Padding(Dpi.S(8), Dpi.S(7), Dpi.S(8), 0) };
            _threshold.BorderStyle = BorderStyle.None;
            _threshold.BackColor = Theme.Elevated;
            _threshold.ForeColor = Theme.Text;
            _threshold.Dock = DockStyle.Fill;
            thHost.Controls.Add(_threshold);
            var mbLabel = new Label { Text = "MB", Dock = DockStyle.Left, AutoSize = true, ForeColor = Theme.TextMuted, Padding = new Padding(Dpi.S(6), Dpi.S(7), Dpi.S(12), 0) };
            _mode.Dock = DockStyle.Left;
            _mode.Width = Dpi.S(250);
            _mode.Items.AddRange(new object[] { "仅提示 / Notify only", "自动温和清理 / Auto gentle clean" });
            var hint = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, Font = Theme.Small, AutoEllipsis = true, UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dpi.S(12), 0, 0, 0),
                Text = "按 VS 及其子进程工作集合计判断，每分钟检查，同一 VS 30 分钟内最多一次；调试 / 生成 / Copilot 运行中只提示 · Per VS tree working set, checked every minute, once per 30 min; busy VS is only notified"
            };
            _tips.SetToolTip(_policy, "默认关闭；开启后超过阈值时提示或自动温和清理（保存到 settings.json）\nOff by default; when on, notify or gently clean above the threshold (saved to settings.json)");
            _tips.SetToolTip(_threshold, "单个 VS 实例（含子进程）的工作集阈值，范围 " + VsMemory.MinThresholdMB + "–" + VsMemory.MaxThresholdMB + " MB\nWorking-set threshold of one VS instance (with children)");
            _tips.SetToolTip(hint, hint.Text);
            policy.Controls.Add(hint);
            policy.Controls.Add(_mode);
            policy.Controls.Add(mbLabel);
            policy.Controls.Add(thHost);
            policy.Controls.Add(thLabel);
            policy.Controls.Add(_policy);

            _loading = true;
            _policy.Checked = _settings.VsMemoryAutoEnabled;
            _threshold.Text = VsMemory.ClampThreshold(_settings.VsMemoryThresholdMB).ToString();
            _mode.SelectedIndex = _settings.VsMemoryAutoClean ? 1 : 0;
            _loading = false;
            _policy.CheckedChanged += (s, e) => SavePolicy();
            _mode.SelectedIndexChanged += (s, e) => SavePolicy();
            _threshold.Leave += (s, e) => SavePolicy();
            _threshold.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { SavePolicy(); e.SuppressKeyPress = true; } };

            // ---- 清理记录 / Cleanup log ----
            var logHost = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(120), BackColor = Theme.Surface, Padding = new Padding(Dpi.S(16), Dpi.S(8), Dpi.S(8), Dpi.S(8)) };
            logHost.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, logHost.Width, 0); };
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Theme.Surface;
            _log.ForeColor = Theme.TextSecondary;
            _log.Font = Theme.Small;
            _log.Dock = DockStyle.Fill;
            _log.Text = "清理记录（同时写入 %APPDATA%\\VSManager\\logs\\memory.log）/ Cleanup log (also written to memory.log)";
            Theme.DarkControl(_log);
            logHost.Controls.Add(_log);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
            body.Controls.Add(_list);
            body.Controls.Add(_columns);

            Controls.Add(body);
            Controls.Add(_summary);
            Controls.Add(logHost);
            Controls.Add(policy);
            Controls.Add(bar);

            _timer.Tick += async (s, e) => { if (WindowState != FormWindowState.Minimized) await RefreshAsync(); };
            _timer.Start();
        }

        private static Control Gap(Color back) => new Panel { Dock = DockStyle.Right, Width = Dpi.S(8), BackColor = back };

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await RefreshAsync();
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); return; }
            if (e.KeyCode == Keys.F5) { e.Handled = true; await RefreshAsync(); return; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            _tips.Dispose();
            base.OnFormClosed(e);
        }

        /// <summary>保存自动策略到 settings.json。/ Saves the threshold policy to settings.json.</summary>
        private void SavePolicy()
        {
            if (_loading) return;
            int mb = int.TryParse(_threshold.Text.Trim(), out var n) ? VsMemory.ClampThreshold(n) : VsMemory.ClampThreshold(_settings.VsMemoryThresholdMB);
            _threshold.Text = mb.ToString();
            bool changed = _settings.VsMemoryAutoEnabled != _policy.Checked || _settings.VsMemoryThresholdMB != mb || _settings.VsMemoryAutoClean != (_mode.SelectedIndex == 1);
            if (!changed) return;
            _settings.VsMemoryAutoEnabled = _policy.Checked;
            _settings.VsMemoryThresholdMB = mb;
            _settings.VsMemoryAutoClean = _mode.SelectedIndex == 1;
            bool ok = _settings.Save();
            string msg = !_settings.VsMemoryAutoEnabled ? "内存自动策略已关闭 / Memory policy off"
                : "内存自动策略：超过 " + mb + " MB " + (_settings.VsMemoryAutoClean ? "自动温和清理 / auto clean" : "仅提示 / notify only");
            AppendLog(ok ? msg : msg + "（保存失败 / save failed）");
        }

        /// <summary>重新测量并刷新列表（保持滚动位置与选中项）。/ Re-measures and refreshes (keeps scroll and selection).</summary>
        public async Task RefreshAsync()
        {
            if (_refreshing || IsDisposed) return;
            _refreshing = true;
            try
            {
                var refs = _refs();
                var snap = await Task.Run(() => VsMemory.Take(refs));
                if (IsDisposed) return;
                ApplySnapshot(snap);
            }
            catch (Exception ex) { _time.Text = "测量失败 / Measure failed: " + ex.Message; }
            finally { _refreshing = false; }
        }

        private void ApplySnapshot(MemSnapshot snap)
        {
            _snap = snap;
            var rows = new List<Row>();
            foreach (var g in snap.Groups)
            {
                rows.Add(new Row { G = g });
                foreach (var p in g.Procs) rows.Add(new Row { G = g, P = p });
            }
            bool same = rows.Count == _list.Items.Count;
            for (int i = 0; same && i < rows.Count; i++)
            {
                var old = (Row)_list.Items[i];
                same = old.G.Key == rows[i].G.Key && (old.P?.Pid ?? 0) == (rows[i].P?.Pid ?? 0);
            }
            if (same)
            {
                // 结构未变：原地更新数值，滚动位置与选中项不动 / Same structure: update in place, scroll and selection untouched
                for (int i = 0; i < rows.Count; i++)
                {
                    var old = (Row)_list.Items[i];
                    old.G = rows[i].G;
                    old.P = rows[i].P;
                }
            }
            else
            {
                var sel = _list.SelectedItem as Row;
                int top = _list.TopIndex;
                _list.BeginUpdate();
                try
                {
                    _list.Items.Clear();
                    _list.Items.AddRange(rows.Cast<object>().ToArray());
                    if (sel != null)
                        for (int i = 0; i < rows.Count; i++)
                            if (rows[i].G.Key == sel.G.Key && (rows[i].P?.Pid ?? 0) == (sel.P?.Pid ?? 0)) { _list.SelectedIndex = i; break; }
                }
                finally { _list.EndUpdate(); }
                if (rows.Count > 0) _list.TopIndex = Math.Min(top, rows.Count - 1);
            }
            _list.EmptyText = "没有找到 VS 进程 / No VS processes";
            int procs = snap.Groups.Sum(g => g.Procs.Count);
            _time.Text = "测量 / At " + snap.At.ToString("HH:mm:ss") + " · " + procs + " 个进程 / procs";
            _btnAllVs.Enabled = snap.Groups.Any(g => g.Kind == MemGroupKind.Vs && g.CanClean);
            _summary.Invalidate();
            _list.Invalidate();
        }

        // ---- 清理 / Cleanup ----

        private async Task CleanAsync(string key)
        {
            if (_cleaning.Contains(key)) return;
            var g = _snap?.Find(key);
            if (g != null && g.Kind == MemGroupKind.Vs)
            {
                var vs = _refs().FirstOrDefault(x => x.Vs != null && x.Vs.Pid == g.RootPid)?.Vs;
                if (vs != null && (vs.DebugMode == 2 || vs.DebugMode == 3 || vs.Building || vs.Copilot == CopilotState.Busy))
                    AppendLog(g.Owner + " 正在调试 / 生成 / Copilot 运行中，修剪后短时间内可能稍慢 / busy: may be slightly slower for a moment");
            }
            _cleaning.Add(key);
            UpdateButtons();
            _list.Invalidate();
            try
            {
                AppendLog("开始清理 / Cleaning " + (g?.Owner ?? key) + " …");
                var r = await VsMemory.CleanAsync(key, _refs());
                string line = "🧹 " + r.Summary();
                AppendLog(line);
                _status?.Invoke(line);
            }
            catch (Exception ex) { AppendLog("清理失败 / Clean failed: " + ex.Message); }
            finally
            {
                _cleaning.Remove(key);
                UpdateButtons();
            }
            await RefreshAsync();
        }

        private async Task CleanAllVsAsync()
        {
            if (_snap == null) await RefreshAsync();
            if (_snap == null) return;
            foreach (var key in _snap.Groups.Where(g => g.Kind == MemGroupKind.Vs && g.CanClean).Select(g => g.Key).ToList())
            {
                if (IsDisposed) return;
                await CleanAsync(key);
            }
        }

        private void UpdateButtons()
        {
            if (IsDisposed) return;
            _btnSelf.Enabled = !_cleaning.Contains(MemGroupKind.Self.ToString());
            _btnSelf.Text = _btnSelf.Enabled ? "🧹 清理 VSManager" : "清理中… / Cleaning…";
            _btnAllVs.Enabled = !_cleaning.Any(k => k.StartsWith("vs:")) && (_snap?.Groups.Any(g => g.Kind == MemGroupKind.Vs && g.CanClean) ?? false);
        }

        private void AppendLog(string text)
        {
            if (IsDisposed) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            _log.AppendText((_log.TextLength > 0 ? "\r\n" : "") + line);
            // 界面只保留最近的记录，完整记录在 memory.log / Keep only recent lines on screen; the full log is in memory.log
            if (_log.Lines.Length > 300) _log.Lines = _log.Lines.Skip(_log.Lines.Length - 200).ToArray();
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        // ---- 绘制 / Painting ----

        private static readonly float[] ColStops = { 0f, 0.30f, 0.37f, 0.55f, 0.65f, 0.75f, 1f };

        private int ColX(int i, int width) => Dpi.S(16) + (int)((width - Dpi.S(32)) * ColStops[i]);

        private void Columns_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = _columns.ClientSize.Width;
            string[] names = { "进程 / Process", "PID", "所属 VS / Owner", "工作集 / WS", "私有字节 / Private", "清理 / Cleanup" };
            for (int i = 0; i < names.Length; i++)
            {
                var r = new Rectangle(ColX(i, w), 0, ColX(i + 1, w) - ColX(i, w) - Dpi.S(8), _columns.Height);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                if (i == 3 || i == 4) flags |= TextFormatFlags.Right;
                TextRenderer.DrawText(g, names[i], Theme.Small, r, Theme.TextMuted, flags);
            }
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, _columns.Height - 1, w, _columns.Height - 1);
        }

        private void Summary_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var s = _snap;
            int w = _summary.ClientSize.Width, pad = Dpi.S(16), gap = Dpi.S(10);
            int tileW = (w - pad * 2 - gap * 3) / 4, tileH = _summary.Height - Dpi.S(20);
            long totalWs = s?.TotalWorkingSet ?? 0, totalPriv = s?.TotalPrivate ?? 0;
            var vs = s?.Groups.Where(x => x.Kind == MemGroupKind.Vs).ToList() ?? new List<MemGroup>();
            var self = s?.Groups.FirstOrDefault(x => x.Kind == MemGroupKind.Self);
            var shared = s?.Groups.FirstOrDefault(x => x.Kind == MemGroupKind.Shared);
            long vsWs = vs.Sum(x => x.WorkingSet), vsPriv = vs.Sum(x => x.Private);
            var top = vs.OrderByDescending(x => x.WorkingSet).FirstOrDefault();
            var tiles = new[]
            {
                Tuple.Create("VS 实例合计 / VS total（" + vs.Count + "）", s == null ? "…" : VsMemory.Mb(vsWs) + "  ·  " + VsMemory.Percent(vsWs, totalWs),
                    s == null ? "" : "私有 / Private " + VsMemory.Mb(vsPriv) + " · " + VsMemory.Percent(vsPriv, totalPriv), Theme.BusyDot),
                Tuple.Create("VSManager 本体 / itself", self == null ? "…" : VsMemory.Mb(self.WorkingSet) + "  ·  " + VsMemory.Percent(self.WorkingSet, totalWs),
                    self == null ? "" : "私有 / Private " + VsMemory.Mb(self.Private) + " · " + self.Procs.Count + " 个进程 / procs", Theme.Accent),
                Tuple.Create("最大实例 / Largest VS", top == null ? "—" : VsMemory.Mb(top.WorkingSet),
                    top == null ? (shared == null ? "" : "共享组件 / Shared " + VsMemory.Mb(shared.WorkingSet)) : top.Owner + (shared == null ? "" : " · 共享 / Shared " + VsMemory.Mb(shared.WorkingSet)), Theme.Warning),
                Tuple.Create("系统内存 / System RAM", s == null || s.PhysTotal == 0 ? "…" : VsMemory.Mb(s.PhysTotal - s.PhysAvail) + " / " + VsMemory.Mb(s.PhysTotal),
                    s == null || s.PhysTotal == 0 ? "" : "以上合计占 / listed " + VsMemory.Percent(totalWs, s.PhysTotal), Theme.IdleDot),
            };
            for (int i = 0; i < tiles.Length; i++)
            {
                var r = new RectangleF(pad + i * (tileW + gap), Dpi.S(12), tileW, tileH);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Theme.FillRound(g, Theme.Surface, r, Dpi.S(8));
                Theme.DrawRound(g, Theme.Border, r, Dpi.S(8));
                Theme.FillCircle(g, tiles[i].Item4, r.X + Dpi.S(14), r.Y + Dpi.S(15), Dpi.S(3));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                int x = (int)r.X + Dpi.S(24), tw = (int)r.Width - Dpi.S(32);
                TextRenderer.DrawText(g, tiles[i].Item1, Theme.Small, new Rectangle(x, (int)r.Y + Dpi.S(7), tw, Dpi.S(16)), Theme.TextMuted, flags);
                TextRenderer.DrawText(g, tiles[i].Item2, Theme.Big, new Rectangle((int)r.X + Dpi.S(12), (int)r.Y + Dpi.S(24), tw + Dpi.S(12), Dpi.S(22)), Theme.Text, flags);
                TextRenderer.DrawText(g, tiles[i].Item3, Theme.Small, new Rectangle((int)r.X + Dpi.S(12), (int)r.Y + Dpi.S(47), tw + Dpi.S(12), Dpi.S(16)), Theme.TextSecondary, flags);
            }
        }

        private Rectangle ButtonRect(Rectangle bounds)
        {
            int x = ColX(5, bounds.Width);
            int w = Math.Min(Dpi.S(190), ColX(6, bounds.Width) - x);
            return new Rectangle(x, bounds.Y + Dpi.S(4), w, bounds.Height - Dpi.S(8));
        }

        private void List_DrawItem(object sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;
            var row = (Row)_list.Items[e.Index];
            var b = e.Bounds;
            int w = b.Width;
            bool hover = e.Index == _list.HoverIndex;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            Rectangle Cell(int i) => new Rectangle(ColX(i, w), b.Y, ColX(i + 1, w) - ColX(i, w) - Dpi.S(8), b.Height);

            if (row.P == null)
            {
                var gr = row.G;
                using (var br = new SolidBrush(selected ? Theme.RowSelected : Theme.SurfaceAlt)) g.FillRectangle(br, b);
                using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, b.X, b.Bottom - 1, b.Right, b.Bottom - 1);
                Color dot = gr.Kind == MemGroupKind.Self ? Theme.Accent : gr.Kind == MemGroupKind.Shared ? Theme.NoneDot : Theme.BusyDot;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Theme.FillCircle(g, dot, ColX(0, w) + Dpi.S(4), b.Y + b.Height / 2f, Dpi.S(4));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                long total = _snap?.TotalWorkingSet ?? 0;
                string title = gr.Owner + "   ·   " + gr.Procs.Count + " 个进程 / procs   ·   占 / share " + VsMemory.Percent(gr.WorkingSet, total);
                var tr = new Rectangle(ColX(0, w) + Dpi.S(16), b.Y, ColX(3, w) - ColX(0, w) - Dpi.S(24), b.Height);
                TextRenderer.DrawText(g, title, Theme.SemiBold, tr, Theme.Text, flags);
                TextRenderer.DrawText(g, VsMemory.Mb(gr.WorkingSet), Theme.SemiBold, Cell(3), Theme.Text, flags | TextFormatFlags.Right);
                TextRenderer.DrawText(g, VsMemory.Mb(gr.Private), Theme.SemiBold, Cell(4), Theme.Text, flags | TextFormatFlags.Right);
                if (gr.CanClean)
                {
                    bool busy = _cleaning.Contains(gr.Key);
                    var br2 = ButtonRect(b);
                    bool hot = hover && br2.Contains(_list.PointToClient(Cursor.Position));
                    string text = busy ? "清理中… / Cleaning…" : gr.Kind == MemGroupKind.Self ? "🧹 清理 VSManager" : "🧹 温和清理 / Clean";
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Theme.FillRound(g, busy ? Theme.Elevated : hot ? Theme.AccentHover : Theme.Accent, br2, Dpi.S(6));
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    TextRenderer.DrawText(g, text, Theme.Small, br2, busy ? Theme.TextMuted : Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }
                else TextRenderer.DrawText(g, "— 无可清理进程 / nothing to clean", Theme.Small, Cell(5), Theme.TextMuted, flags);
                return;
            }

            var p = row.P;
            using (var br = new SolidBrush(selected ? Theme.RowSelected : hover ? Theme.RowHover : Theme.Background)) g.FillRectangle(br, b);
            using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, b.X, b.Bottom - 1, b.Right, b.Bottom - 1);
            var nameCell = Cell(0);
            nameCell.X += Dpi.S(16);
            nameCell.Width -= Dpi.S(16);
            string name = p.Name;
            var nsz = TextRenderer.MeasureText(g, name, p.Root ? Theme.SemiBold : Theme.Regular, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, name, p.Root ? Theme.SemiBold : Theme.Regular, nameCell, Theme.Text, flags);
            var roleCell = new Rectangle(nameCell.X + nsz.Width + Dpi.S(12), b.Y, nameCell.Right - nameCell.X - nsz.Width - Dpi.S(12), b.Height);
            if (roleCell.Width > Dpi.S(30)) TextRenderer.DrawText(g, p.Role, Theme.Small, roleCell, Theme.TextMuted, flags);
            TextRenderer.DrawText(g, p.Pid.ToString(), Theme.Regular, Cell(1), Theme.TextSecondary, flags);
            TextRenderer.DrawText(g, row.G.Owner, Theme.Regular, Cell(2), Theme.TextSecondary, flags);
            TextRenderer.DrawText(g, p.AccessDenied ? "—" : VsMemory.Mb(p.WorkingSet), Theme.Regular, Cell(3), Theme.Text, flags | TextFormatFlags.Right);
            TextRenderer.DrawText(g, p.AccessDenied ? "—" : VsMemory.Mb(p.Private), Theme.Regular, Cell(4), Theme.TextSecondary, flags | TextFormatFlags.Right);
            string mark = p.Trimmable ? "✔ 可安全清理 / Safe" : "✕ 不建议 / Not advised";
            var mc = Cell(5);
            var msz = TextRenderer.MeasureText(g, mark, Theme.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, mark, Theme.Small, mc, p.Trimmable ? Theme.Success : Theme.TextMuted, flags);
            var why = new Rectangle(mc.X + msz.Width + Dpi.S(10), b.Y, mc.Width - msz.Width - Dpi.S(10), b.Height);
            string reason = p.Reason;
            int cut = reason.IndexOf(" / ", StringComparison.Ordinal);
            if (cut > 0) reason = reason.Substring(0, cut);
            if (reason.StartsWith("不建议：", StringComparison.Ordinal)) reason = reason.Substring(4);
            if (why.Width > Dpi.S(30)) TextRenderer.DrawText(g, reason, Theme.Small, why, Theme.TextMuted, flags);
        }

        private Row RowAt(Point pt)
        {
            int i = _list.IndexFromPoint(pt);
            return i >= 0 && i < _list.Items.Count && _list.GetItemRectangle(i).Contains(pt) ? (Row)_list.Items[i] : null;
        }

        private async void List_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var row = RowAt(e.Location);
            if (row == null || row.P != null || !row.G.CanClean) return;
            var rect = ButtonRect(_list.GetItemRectangle(_list.IndexFromPoint(e.Location)));
            if (rect.Contains(e.Location)) await CleanAsync(row.G.Key);
        }

        private Row _tipRow;

        private void List_MouseMove(object sender, MouseEventArgs e)
        {
            var row = RowAt(e.Location);
            bool onButton = row != null && row.P == null && row.G.CanClean && ButtonRect(_list.GetItemRectangle(_list.IndexFromPoint(e.Location))).Contains(e.Location);
            _list.Cursor = onButton ? Cursors.Hand : Cursors.Default;
            _list.Invalidate();
            if (row == _tipRow) return;
            _tipRow = row;
            string tip = null;
            if (row?.P != null) tip = row.P.Name + "  (PID " + row.P.Pid + ")\n" + row.P.Role + "\n" + row.P.Reason;
            else if (row != null)
                tip = row.G.Kind == MemGroupKind.Vs
                    ? "温和清理：尝试 VS 内部 GC + 修剪可安全清理进程的工作集；不结束进程、不影响未保存内容\nGentle clean: try an in-VS GC + trim safe processes; nothing is terminated, unsaved work is untouched"
                    : row.G.Kind == MemGroupKind.Self ? "VSManager 本体：完整 GC + 修剪工作集\nVSManager: full GC + working-set trim"
                    : "无归属的共享 VS 组件（如编译服务器），只修剪工作集\nOrphaned shared VS components (e.g. compiler server); working-set trim only";
            _tips.SetToolTip(_list, tip);
        }

        /// <summary>自绘内存列表（复用侧边栏列表的深色双缓冲绘制与悬停）。/ Owner-drawn memory list reusing the sidebar list painting.</summary>
        private sealed class MemList : VsListBox
        {
            public MemList() { BackColor = Theme.Background; }
        }

        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }
    }
}
