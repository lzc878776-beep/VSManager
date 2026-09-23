using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 「对话记录」窗口：查看 AI 助手的全部历史对话（agent-chat.jsonl），支持关键词搜索、按日期筛选、正序 / 倒序。
    /// 只读取本机文件，不修改记录。
    /// Chat history window: shows the full AI assistant history (agent-chat.jsonl) with keyword search, date filter and
    /// oldest/newest-first order. Read-only; it never modifies the records.
    /// </summary>
    public class AgentHistoryForm : Form
    {
        /// <summary>单次最多渲染的条数（超出时显示最新的部分并提示缩小范围）。/ Max records rendered at once.</summary>
        public const int MaxShown = 1000;

        private readonly TranscriptView _view = new TranscriptView { Dock = DockStyle.Fill, AssistantLabel = "AI 助手" };
        private readonly TextBox _search;
        private readonly DarkCombo _date = new DarkCombo();
        private readonly DarkCombo _order = new DarkCombo();
        private readonly FlatButton _btnSteps, _btnRefresh, _btnFolder;
        private readonly Label _count = new Label();
        private readonly Timer _debounce = new Timer { Interval = 300 };
        private List<AgentChatRecord> _all = new List<AgentChatRecord>();
        private bool _showSteps;
        private bool _loading;
        private int _loadVersion;

        private sealed class DateFilter
        {
            public string Text;
            public DateTime? From, To;
            public override string ToString() => Text;
        }

        public AgentHistoryForm()
        {
            Text = "对话记录 / Chat history";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Dpi.S(1000), wa.Width - Dpi.S(40)), Math.Min(Dpi.S(780), wa.Height - Dpi.S(40)));
            MinimumSize = new Size(Math.Min(Dpi.S(760), wa.Width), Math.Min(Dpi.S(420), wa.Height));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 顶部工具栏 / Toolbar ----
            var bar = new Panel { Dock = DockStyle.Top, Height = Dpi.S(56), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(16), Dpi.S(11), Dpi.S(16), Dpi.S(11)) };
            bar.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1); };

            var searchHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Elevated, Padding = new Padding(Dpi.S(10), Dpi.S(8), Dpi.S(10), 0) };
            _search = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Elevated, ForeColor = Theme.Text, Dock = DockStyle.Fill };
            searchHost.Controls.Add(_search);
            searchHost.Click += (s, e) => _search.Focus();
            _search.HandleCreated += (s, e) => SendMessage(_search.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, "🔍 搜索关键词 / 任务 #编号（空格分隔多个词）");
            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Apply(true); };

            _date.Dock = DockStyle.Right;
            _date.Width = Dpi.S(190);
            _date.DropDownWidth = Dpi.S(220);
            _date.SelectedIndexChanged += (s, e) => { if (!_loading) Apply(true); };

            _order.Dock = DockStyle.Right;
            _order.Width = Dpi.S(150);
            _order.Items.AddRange(new object[] { "倒序（新 → 旧）", "正序（旧 → 新）" });
            _order.SelectedIndex = 0;
            _order.SelectedIndexChanged += (s, e) => { if (!_loading) Apply(true); };

            _btnSteps = new FlatButton { Text = "显示步骤", Ghost = true, Dock = DockStyle.Right, Width = Dpi.S(92) };
            _btnSteps.Click += (s, e) =>
            {
                _showSteps = !_showSteps;
                _btnSteps.Text = _showSteps ? "隐藏步骤" : "显示步骤";
                Apply(false);
            };
            _btnRefresh = new FlatButton { Text = "↻ 刷新", Ghost = true, Dock = DockStyle.Right, Width = Dpi.S(80) };
            _btnRefresh.Click += (s, e) => Reload();
            _btnFolder = new FlatButton { Text = "📂", Ghost = true, Dock = DockStyle.Right, Width = Dpi.S(40) };
            _btnFolder.Click += (s, e) => OpenFolder();

            var tips = new ToolTip();
            tips.SetToolTip(_search, "按关键词筛选（不区分大小写；多个词需同时出现），也可输入「#12」查找关联任务\nFilter by keywords (case-insensitive, all words must match), or \"#12\" for a task");
            tips.SetToolTip(_date, "按日期筛选 / Filter by date");
            tips.SetToolTip(_order, "排序方式 / Sort order");
            tips.SetToolTip(_btnSteps, "显示 / 隐藏 AI 的工具调用步骤与通知详情\nShow / hide tool-call steps and notice details");
            tips.SetToolTip(_btnRefresh, "重新读取对话记录文件 / Reload the history file");
            tips.SetToolTip(_btnFolder, "打开对话记录所在目录（仅本机）\nOpen the folder that contains the history file (local only)");

            bar.Controls.Add(searchHost);
            bar.Controls.Add(Gap());
            bar.Controls.Add(_date);
            bar.Controls.Add(Gap());
            bar.Controls.Add(_order);
            bar.Controls.Add(Gap());
            bar.Controls.Add(_btnSteps);
            bar.Controls.Add(_btnRefresh);
            bar.Controls.Add(_btnFolder);

            // ---- 底部状态栏 / Status bar ----
            var foot = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(30), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(16), 0, Dpi.S(16), 0) };
            foot.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, foot.Width, 0); };
            _count.Dock = DockStyle.Fill;
            _count.ForeColor = Theme.TextMuted;
            _count.Font = Theme.Small;
            _count.TextAlign = ContentAlignment.MiddleLeft;
            _count.AutoEllipsis = true;
            _count.UseMnemonic = false;
            var where = new Label
            {
                Dock = DockStyle.Right, AutoSize = true, ForeColor = Theme.TextMuted, Font = Theme.Small, UseMnemonic = false,
                Padding = new Padding(0, Dpi.S(8), 0, 0), Text = "仅保存在本机 %APPDATA%\\VSManager\\" + AgentChatLog.FileName
            };
            foot.Controls.Add(_count);
            foot.Controls.Add(where);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(Dpi.S(4), 0, 0, 0) };
            body.Controls.Add(_view);

            Controls.Add(body);
            Controls.Add(foot);
            Controls.Add(bar);

            AgentChatLog.Appended += OnAppended;
            Reload();
        }

        private static Control Gap() => new Panel { Dock = DockStyle.Right, Width = Dpi.S(8), BackColor = Theme.Sidebar };

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; return; }
            if (e.KeyCode == Keys.F5) { Reload(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            AgentChatLog.Appended -= OnAppended;
            _debounce.Dispose();
            base.OnFormClosed(e);
        }

        /// <summary>有新记录写入时增量刷新（保持滚动位置）。/ Refreshes in place when a record is appended.</summary>
        private void OnAppended()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) Reload(false); })); } catch { }
        }

        /// <summary>重新读取文件（后台线程）。/ Reloads the file on a background thread.</summary>
        public void Reload() => Reload(true);

        private async void Reload(bool reset)
        {
            int version = ++_loadVersion;
            _count.Text = "正在读取… / Loading…";
            List<AgentChatRecord> list;
            try { list = await Task.Run(() => AgentChatLog.ReadAll()); }
            catch (Exception ex) { list = new List<AgentChatRecord>(); _count.Text = "读取失败：" + ex.Message; }
            if (IsDisposed || version != _loadVersion) return;
            _all = list;
            RebuildDates();
            Apply(reset);
        }

        private void RebuildDates()
        {
            _loading = true;
            try
            {
                string selected = (_date.SelectedItem as DateFilter)?.Text;
                var today = DateTime.Today;
                _date.Items.Clear();
                _date.Items.Add(new DateFilter { Text = "全部日期 / All dates" });
                _date.Items.Add(new DateFilter { Text = "今天 / Today", From = today });
                _date.Items.Add(new DateFilter { Text = "最近 7 天 / 7 days", From = today.AddDays(-6) });
                _date.Items.Add(new DateFilter { Text = "最近 30 天 / 30 days", From = today.AddDays(-29) });
                foreach (var g in _all.GroupBy(r => r.Time.Date).OrderByDescending(g => g.Key))
                    _date.Items.Add(new DateFilter { Text = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "（" + g.Count() + " 条）", From = g.Key, To = g.Key.AddDays(1) });
                int idx = 0;
                for (int i = 0; i < _date.Items.Count; i++)
                    if (((DateFilter)_date.Items[i]).Text == selected) { idx = i; break; }
                // 按天筛选的条数会变化：按日期前缀匹配 / Per-day items change their count; match by date prefix
                if (idx == 0 && selected != null && selected.Length >= 10 && char.IsDigit(selected[0]))
                    for (int i = 0; i < _date.Items.Count; i++)
                        if (((DateFilter)_date.Items[i]).Text.StartsWith(selected.Substring(0, 10), StringComparison.Ordinal)) { idx = i; break; }
                _date.SelectedIndex = idx;
            }
            finally { _loading = false; }
        }

        /// <summary>按当前条件筛选并渲染。/ Filters and renders with the current criteria.</summary>
        private void Apply(bool reset)
        {
            var filter = _date.SelectedItem as DateFilter;
            var words = (_search.Text ?? "").Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries);
            var matched = _all.Where(r =>
                (filter?.From == null || r.Time >= filter.From.Value) &&
                (filter?.To == null || r.Time < filter.To.Value) &&
                Matches(r, words)).ToList();
            int total = matched.Count;
            if (matched.Count > MaxShown) matched = matched.Skip(matched.Count - MaxShown).ToList();
            bool newestFirst = _order.SelectedIndex != 1;
            if (newestFirst) matched.Reverse();

            var t = new ChatTranscript { PaneFound = true, Title = "对话记录" };
            foreach (var r in matched) t.Messages.Add(ToMessage(r));
            _view.ScrollTopOnReset = newestFirst;
            if (reset) _view.ResetView();
            if (t.Messages.Count == 0)
                _view.SetEmpty(_all.Count == 0
                    ? "还没有 AI 助手对话记录\nNo AI assistant chat history yet"
                    : "没有符合条件的记录\nNo records match the current filter");
            else
                _view.Render(t, true);

            string text = _all.Count == 0 ? "共 0 条" :
                total == _all.Count ? "共 " + _all.Count + " 条" : "匹配 " + total + " / 共 " + _all.Count + " 条";
            if (total > MaxShown) text += "，仅显示最新 " + MaxShown + " 条，请用搜索或日期缩小范围";
            if (AgentChatLog.LastError != null) text += "  ⚠ " + AgentChatLog.LastError;
            _count.Text = text;
        }

        private static bool Matches(AgentChatRecord r, string[] words)
        {
            if (words.Length == 0) return true;
            string hay = string.Join("\n", new[] { r.Text, r.Detail, r.Error }.Concat(r.Steps).Where(x => !string.IsNullOrEmpty(x)))
                         + "\n" + string.Join(" ", r.Tasks.Select(id => "#" + id));
            foreach (var w in words)
            {
                string word = w;
                // 「#12」只匹配任务编号，避免命中 #120 / "#12" matches the task id exactly
                if (word.Length > 1 && word[0] == '#' && int.TryParse(word.Substring(1), out int id))
                {
                    if (!r.Tasks.Contains(id)) return false;
                    continue;
                }
                if (hay.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        private ChatMessage ToMessage(AgentChatRecord r)
        {
            var m = new ChatMessage { Role = r.IsUser ? ChatRole.User : ChatRole.Assistant };
            string kind = r.Role == AgentChatLog.RoleNotice ? " · 📋 通知" : "";
            string tasks = r.Tasks.Count > 0 ? " · 任务 " + string.Join(", ", r.Tasks.Select(id => "#" + id)) : "";
            m.Parts.Add(new ChatPart { IsStep = true, Text = "🕒 " + r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + kind + tasks });
            if (!string.IsNullOrWhiteSpace(r.Text)) m.Parts.Add(new ChatPart { Text = r.Text });
            if (_showSteps)
            {
                if (!string.IsNullOrWhiteSpace(r.Detail)) m.Parts.Add(new ChatPart { IsStep = true, Text = "↳ " + r.Detail });
                foreach (var s in r.Steps) m.Parts.Add(new ChatPart { IsStep = true, Text = s });
            }
            if (!string.IsNullOrWhiteSpace(r.Error) && !(_showSteps && r.Steps.Any(s => s.Contains(r.Error))))
                m.Parts.Add(new ChatPart { IsStep = true, Text = "⚠ " + r.Error });
            return m;
        }

        private static void OpenFolder()
        {
            try
            {
                string path = AgentChatLog.FilePath;
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
            }
            catch { }
        }
    }
}
