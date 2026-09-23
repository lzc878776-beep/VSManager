using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 解决方案登记窗口：增删改查登记表（%APPDATA%\VSManager\solutions.json），并支持从已打开的 VS 一键登记。
    /// Solution registry window: add / edit / delete entries of %APPDATA%\VSManager\solutions.json and register a solution
    /// from an open VS with one click.
    /// </summary>
    public sealed class SolutionsForm : Form
    {
        /// <summary>已打开 VS 的信息（编号从 1 开始）。/ An open VS (number starts at 1).</summary>
        public sealed class OpenVs
        {
            public int Number;
            public string Name, SolutionPath;
        }

        private readonly SolutionRegistry _reg;
        private readonly Func<List<OpenVs>> _openVs;
        private readonly Func<SolutionEntry, string> _state;
        private readonly Action<SolutionEntry> _open;
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _alias, _path, _synonyms, _desc, _defaultVs;
        private readonly Label _status = new Label();
        private string _editing;

        public SolutionsForm(SolutionRegistry registry, Func<List<OpenVs>> openVs, Func<SolutionEntry, string> state, Action<SolutionEntry> open)
        {
            _reg = registry;
            _openVs = openVs;
            _state = state;
            _open = open;
            Text = "解决方案登记 / Solution registry";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            var wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Dpi.S(860), wa.Width - Dpi.S(40)), Math.Min(Dpi.S(560), wa.Height - Dpi.S(60)));
            MinimumSize = new Size(Dpi.S(700), Dpi.S(460));
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 左侧列表 / List on the left ----
            _list.Dock = DockStyle.Fill;
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Theme.Surface;
            _list.ForeColor = Theme.Text;
            _list.IntegralHeight = false;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = Dpi.S(46);
            _list.DrawItem += DrawEntry;
            _list.SelectedIndexChanged += (s, e) => ShowEntry(_list.SelectedItem as SolutionEntry);
            var left = new Panel { Dock = DockStyle.Left, Width = Dpi.S(320), Padding = new Padding(0, 0, Dpi.S(12), 0), BackColor = Theme.Background };
            left.Controls.Add(_list);

            // ---- 右侧编辑区 / Editor on the right ----
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(130)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(90)));
            int row = 0;
            TextBox AddRow(string label, string tip, Control extra = null)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(40)));
                var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.TextSecondary };
                var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Elevated, Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(4)), Padding = new Padding(Dpi.S(10), Dpi.S(7), Dpi.S(10), 0) };
                var box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Elevated, ForeColor = Theme.Text, Dock = DockStyle.Fill };
                host.Controls.Add(box);
                grid.Controls.Add(lbl, 0, row);
                grid.Controls.Add(host, 1, row);
                if (extra != null) grid.Controls.Add(extra, 2, row); else grid.SetColumnSpan(host, 2);
                if (tip != null) new ToolTip().SetToolTip(box, tip);
                row++;
                return box;
            }
            _alias = AddRow("别名 / Alias", "口语名称，如「订单项目」/ Spoken name, e.g. \"Order project\"");
            _path = AddRow("路径 / Path", "解决方案完整路径（.sln / .slnx）/ Full solution path (.sln / .slnx)", Button("浏览… / Browse…", Browse, Dpi.S(90)));
            _synonyms = AddRow("同义词 / Synonyms", "逗号或顿号分隔，如：订单、下单、order / Separated by commas, e.g. order, ordering");
            _desc = AddRow("说明 / Description", "可选 / Optional");
            _defaultVs = AddRow("默认 VS 编号 / Default VS #", "可选，0 表示不设置；仅在无法读取该 VS 的解决方案路径时用于判断是否已打开 / Optional, 0 = none; only used to tell whether it is open when that VS's solution path cannot be read");
            grid.RowCount = row + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Height = Dpi.S(40) * row;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Dpi.S(92), FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(0, Dpi.S(8), 0, 0), BackColor = Theme.Background };
            buttons.Controls.Add(Button("新增 / New", NewEntry, Dpi.S(120)));
            buttons.Controls.Add(Button("保存此条 / Save entry", SaveEntry, Dpi.S(160), true));
            buttons.Controls.Add(Button("删除 / Delete", DeleteEntry, Dpi.S(120)));
            var fromVs = Button("从已打开的 VS 登记 / From open VS…", null, Dpi.S(260));
            fromVs.Click += (s, e) => ShowOpenVsMenu(fromVs);
            buttons.Controls.Add(fromVs);
            buttons.Controls.Add(Button("打开此解决方案 / Open", OpenSelected, Dpi.S(180)));

            var hint = new Label
            {
                Dock = DockStyle.Top, Height = Dpi.S(64), Font = Theme.Small, ForeColor = Theme.TextMuted,
                Text = "匹配规则：别名 / 同义词 / 文件名完全一致优先，其次忽略「项目、解决方案、工程」等后缀、包含、字符顺序与相似度；全角半角、大小写、空格与标点不影响匹配。\r\n" +
                       "Matching: exact alias / synonym / file name first, then ignoring suffixes such as \"project\" / \"solution\", containment, characters in order and similarity; width, case, spaces and punctuation are ignored."
            };

            _status.Dock = DockStyle.Top;
            _status.Height = Dpi.S(26);
            _status.Font = Theme.Small;
            _status.ForeColor = Theme.TextSecondary;
            _status.AutoEllipsis = true;
            _status.Text = "保存位置 / Stored in：%APPDATA%\\VSManager\\solutions.json";

            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
            right.Controls.Add(_status);
            right.Controls.Add(hint);
            right.Controls.Add(buttons);
            right.Controls.Add(grid);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Dpi.S(20), Dpi.S(16), Dpi.S(20), Dpi.S(12)), BackColor = Theme.Background };
            body.Controls.Add(right);
            body.Controls.Add(left);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(60), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(20), Dpi.S(12), Dpi.S(20), Dpi.S(12)) };
            var close = new FlatButton { Text = "关闭 / Close", Primary = true, Dock = DockStyle.Right, Width = Dpi.S(120) };
            close.Click += (s, e) => Close();
            bottom.Controls.Add(close);

            Controls.Add(body);
            Controls.Add(bottom);
            Reload(null);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        private static FlatButton Button(string text, Action a, int width, bool primary = false)
        {
            var b = new FlatButton { Text = text, Primary = primary, Width = width, Height = Dpi.S(34), Margin = new Padding(0, 0, Dpi.S(8), Dpi.S(8)) };
            if (a != null) b.Click += (s, e) => a();
            return b;
        }

        private void DrawEntry(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count || !(_list.Items[e.Index] is SolutionEntry item)) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.RowSelected : Theme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            string state = _state?.Invoke(item) ?? "";
            var r1 = new Rectangle(e.Bounds.X + Dpi.S(10), e.Bounds.Y + Dpi.S(6), e.Bounds.Width - Dpi.S(20), Dpi.S(18));
            TextRenderer.DrawText(e.Graphics, item.Alias + (state.Length > 0 ? "   " + state : ""), Theme.SemiBold, r1, Theme.Text, flags);
            var r2 = new Rectangle(r1.X, r1.Bottom + Dpi.S(2), r1.Width, Dpi.S(16));
            TextRenderer.DrawText(e.Graphics, item.FileName + (item.Synonyms.Count > 0 ? " · " + item.SynonymText : ""), Theme.Small, r2, Theme.TextMuted, flags);
        }

        private void Reload(string select)
        {
            var items = _reg.Items;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in items) _list.Items.Add(e);
            _list.EndUpdate();
            int idx = select == null ? -1 : items.FindIndex(x => string.Equals(x.Alias, select, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _list.SelectedIndex = idx;
            else if (_list.Items.Count > 0 && select == null) _list.SelectedIndex = 0;
            else ShowEntry(null);
        }

        private void ShowEntry(SolutionEntry e)
        {
            _editing = e?.Alias;
            _alias.Text = e?.Alias ?? "";
            _path.Text = e?.Path ?? "";
            _synonyms.Text = e?.SynonymText ?? "";
            _desc.Text = e?.Description ?? "";
            _defaultVs.Text = (e?.DefaultVs ?? 0).ToString();
        }

        private void NewEntry()
        {
            _list.ClearSelected();
            ShowEntry(null);
            _alias.Focus();
            SetStatus("填写后点「保存此条」/ Fill in and click \"Save entry\"", false);
        }

        private void SaveEntry()
        {
            string alias = _alias.Text.Trim(), path = _path.Text.Trim().Trim('"');
            if (alias.Length == 0) { SetStatus("请填写别名 / Alias is required", true); return; }
            if (path.Length == 0) { SetStatus("请填写解决方案路径 / Solution path is required", true); return; }
            if (!System.IO.File.Exists(Environment.ExpandEnvironmentVariables(path)))
                SetStatus("提示：该路径的文件目前不存在 / Note: the file does not exist right now", true);
            int.TryParse(_defaultVs.Text.Trim(), out int dv);
            var entry = new SolutionEntry
            {
                Alias = alias, Path = path, Description = _desc.Text.Trim(),
                Synonyms = SolutionEntry.ParseSynonyms(_synonyms.Text), DefaultVs = Math.Max(0, dv)
            };
            var items = _reg.Items;
            // 改名时替换原条目 / Renaming replaces the original entry
            if (_editing != null) items.RemoveAll(x => string.Equals(x.Alias, _editing, StringComparison.OrdinalIgnoreCase));
            if (items.Any(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("别名「" + alias + "」已存在 / Alias already exists", true);
                return;
            }
            items.Add(entry);
            string err = _reg.Replace(items);
            if (err != null) { SetStatus(err, true); return; }
            Reload(alias);
            SetStatus("已保存「" + alias + "」/ Saved", false);
        }

        private void DeleteEntry()
        {
            if (!(_list.SelectedItem is SolutionEntry e)) { SetStatus("请先选择一条 / Select an entry first", true); return; }
            if (MessageBox.Show(this, "删除登记「" + e.Alias + "」？（不会删除解决方案文件）\n\nDelete the entry \"" + e.Alias + "\"? (The solution file is not deleted.)",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var items = _reg.Items;
            items.RemoveAll(x => string.Equals(x.Alias, e.Alias, StringComparison.OrdinalIgnoreCase));
            string err = _reg.Replace(items);
            if (err != null) { SetStatus(err, true); return; }
            Reload(null);
            SetStatus("已删除「" + e.Alias + "」/ Deleted", false);
        }

        private void ShowOpenVsMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            Theme.Apply(menu);
            var open = (_openVs?.Invoke() ?? new List<OpenVs>()).Where(v => !string.IsNullOrEmpty(v.SolutionPath)).ToList();
            if (open.Count == 0) menu.Items.Add(new ToolStripMenuItem("（没有可登记的已打开解决方案 / No open solution to register）") { Enabled = false });
            foreach (var v in open)
            {
                var x = v;
                bool known = _reg.FindByPath(x.SolutionPath) != null;
                var item = new ToolStripMenuItem("#" + x.Number + " " + x.Name + (known ? "（已登记 / registered）" : "") + "  —  " + System.IO.Path.GetFileName(x.SolutionPath));
                item.Click += (s, e) => RegisterFromVs(x);
                menu.Items.Add(item);
            }
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        private void RegisterFromVs(OpenVs v)
        {
            var existing = _reg.FindByPath(v.SolutionPath);
            if (existing != null)
            {
                Reload(existing.Alias);
                SetStatus("该解决方案已登记为「" + existing.Alias + "」/ Already registered", false);
                return;
            }
            string alias = v.Name;
            var items = _reg.Items;
            for (int i = 2; items.Any(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase)); i++) alias = v.Name + " " + i;
            var entry = new SolutionEntry { Alias = alias, Path = v.SolutionPath };
            string fileName = entry.FileName;
            if (!string.Equals(fileName, alias, StringComparison.OrdinalIgnoreCase)) entry.Synonyms.Add(fileName);
            items.Add(entry);
            string err = _reg.Replace(items);
            if (err != null) { SetStatus(err, true); return; }
            Reload(alias);
            SetStatus("已登记「" + alias + "」，可补充同义词后保存 / Registered; add synonyms and save if needed", false);
        }

        private void OpenSelected()
        {
            if (!(_list.SelectedItem is SolutionEntry e)) { SetStatus("请先选择一条 / Select an entry first", true); return; }
            _open?.Invoke(e);
            SetStatus("正在打开「" + e.Alias + "」… / Opening…", false);
        }

        private void Browse()
        {
            using (var d = new OpenFileDialog { Filter = "解决方案 / Solutions (*.sln;*.slnx)|*.sln;*.slnx|所有文件 / All files (*.*)|*.*", CheckFileExists = true })
            {
                try { if (_path.Text.Length > 0) d.InitialDirectory = System.IO.Path.GetDirectoryName(_path.Text.Trim().Trim('"')); } catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _path.Text = d.FileName;
                if (_alias.Text.Trim().Length == 0) _alias.Text = System.IO.Path.GetFileNameWithoutExtension(d.FileName);
            }
        }

        private void SetStatus(string text, bool warn)
        {
            _status.ForeColor = warn ? Theme.Warning : Theme.IdleFg;
            _status.Text = text;
        }
    }
}
