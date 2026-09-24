using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>只扫描用户指定目录；勾选并确认后才写入本机登记表。/ Scans only a user-selected folder; writes the local registry only after selection and confirmation.</summary>
    internal sealed class SolutionScanForm : Form
    {
        private readonly SolutionRegistry _registry;
        private readonly TextBox _folder = new TextBox();
        private readonly NumericUpDown _depth = new NumericUpDown { Minimum = 0, Maximum = SolutionDirectoryScanner.MaximumDepth, Value = SolutionDirectoryScanner.DefaultDepth };
        private readonly CheckedListBox _files = new CheckedListBox { CheckOnClick = true, HorizontalScrollbar = true };
        private readonly TextBox _status = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly FlatButton _scan, _cancel, _import, _browse;
        private CancellationTokenSource _scanCancellation;
        public List<SolutionEntry> ImportedEntries { get; private set; } = new List<SolutionEntry>();

        public SolutionScanForm(SolutionRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Text = "从目录扫描并登记 / Scan and register solutions";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            var area = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Dpi.S(980), area.Width - Dpi.S(40)), Math.Min(Dpi.S(620), area.Height - Dpi.S(60)));
            MinimumSize = new Size(Dpi.S(700), Dpi.S(430));
            Padding = new Padding(Dpi.S(16));
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            _scan = Button("扫描 / Scan", Dpi.S(125), async () => await ScanAsync());
            _cancel = Button("停止扫描 / Cancel scan", Dpi.S(185), () => _scanCancellation?.Cancel());
            _cancel.Enabled = false;
            _browse = Button("浏览目录 / Browse", Dpi.S(180), Browse);
            _import = Button("登记勾选项 / Register selected", Dpi.S(260), ImportSelected);
            _import.Primary = true;
            _import.Enabled = false;
            _folder.Dock = DockStyle.Fill;
            _folder.BackColor = Theme.Elevated;
            _folder.ForeColor = Theme.Text;
            _folder.Margin = new Padding(0, Dpi.S(4), Dpi.S(8), 0);
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = Dpi.S(44), ColumnCount = 2 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(_folder, 0, 0);
            pathRow.Controls.Add(_browse, 1, 0);
            var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            options.Controls.Add(new Label { Text = "子目录深度 / Depth", AutoSize = true, Margin = new Padding(0, Dpi.S(8), Dpi.S(6), 0) });
            _depth.Width = Dpi.S(65);
            _depth.BackColor = Theme.Elevated;
            _depth.ForeColor = Theme.Text;
            options.Controls.Add(_depth);
            options.Controls.Add(_scan);
            options.Controls.Add(_cancel);
            var hint = new Label
            {
                Dock = DockStyle.Top, Height = Dpi.S(66), ForeColor = Theme.TextSecondary,
                Text = "目录路径可输入环境变量；根目录为第 0 层。跳过生成目录及链接；扫描不自动登记。\r\nFolder accepts environment variables; root is depth 0. Build folders and links are skipped; select files to register."
            };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
            bottom.Controls.Add(Button("全选 / All", Dpi.S(110), () => CheckAll(true)));
            bottom.Controls.Add(Button("全不选 / None", Dpi.S(140), () => CheckAll(false)));
            bottom.Controls.Add(_import);
            bottom.Controls.Add(Button("关闭 / Close", Dpi.S(130), Close));
            _files.Dock = DockStyle.Fill;
            _files.BackColor = Theme.Surface;
            _files.ForeColor = Theme.Text;
            _files.ItemCheck += (s, e) =>
            {
                int count = _files.CheckedItems.Count + (e.NewValue == CheckState.Checked ? 1 : 0) - (e.CurrentValue == CheckState.Checked ? 1 : 0);
                _import.Enabled = _scanCancellation == null && count > 0;
            };
            _status.Dock = DockStyle.Bottom;
            _status.Height = Dpi.S(105);
            _status.BackColor = Theme.Elevated;
            _status.ForeColor = Theme.TextSecondary;
            _status.Text = "请输入目录后扫描；勾选的路径仅保存到 %APPDATA%\\VSManager\\solutions.json。\r\nEnter a folder and scan; selected paths are stored only in %APPDATA%\\VSManager\\solutions.json.";
            Controls.Add(_files);
            Controls.Add(_status);
            Controls.Add(bottom);
            Controls.Add(options);
            Controls.Add(pathRow);
            Controls.Add(hint);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }
        protected override void OnFormClosing(FormClosingEventArgs e) { base.OnFormClosing(e); if (!e.Cancel) _scanCancellation?.Cancel(); }

        private static FlatButton Button(string text, int width, Action action)
        {
            var button = new FlatButton { Text = text, Width = width, Height = Dpi.S(34), Margin = new Padding(0, Dpi.S(4), Dpi.S(8), Dpi.S(4)) };
            button.Click += (s, e) => action();
            return button;
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择扫描目录 / Select a folder to scan", ShowNewFolderButton = false })
                if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
        }

        private async Task ScanAsync()
        {
            string folder = _folder.Text;
            int depth = (int)_depth.Value;
            var cancellation = _scanCancellation = new CancellationTokenSource();
            _scan.Enabled = _browse.Enabled = _folder.Enabled = _depth.Enabled = false;
            _cancel.Enabled = true;
            _import.Enabled = false;
            _files.Items.Clear();
            _status.Text = "正在扫描，可停止或关闭窗口 / Scanning; you can cancel or close this window";
            try
            {
                var result = await Task.Run(() => SolutionDirectoryScanner.Scan(folder, depth, cancellation.Token));
                if (IsDisposed) return;
                _files.BeginUpdate();
                foreach (string path in result.Files) _files.Items.Add(new Candidate(path));
                _files.EndUpdate();
                _status.Text = $"找到 {result.Files.Count} 个解决方案，扫描 {result.DirectoriesVisited} 个目录。请选择需要登记的条目；同名别名自动加编号，已登记路径跳过。\r\nFound {result.Files.Count} solutions in {result.DirectoriesVisited} folders. Select entries; alias collisions are numbered and existing paths skipped.\r\n"
                    + string.Join("\r\n", result.Warnings);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!IsDisposed) _status.Text = "扫描已取消，未登记任何条目 / Scan cancelled; nothing was registered";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException || ex is NotSupportedException)
            {
                if (!IsDisposed) _status.Text = "扫描失败 / Scan failed: " + ex.Message;
            }
            finally
            {
                _scanCancellation = null;
                cancellation.Dispose();
                if (!IsDisposed)
                {
                    _scan.Enabled = _browse.Enabled = _folder.Enabled = _depth.Enabled = true;
                    _cancel.Enabled = false;
                    _import.Enabled = _files.CheckedItems.Count > 0;
                }
            }
        }

        private void CheckAll(bool check)
        {
            for (int i = 0; i < _files.Items.Count; i++) _files.SetItemChecked(i, check);
        }

        private void ImportSelected()
        {
            var paths = _files.CheckedItems.Cast<Candidate>().Select(c => c.Path).ToList();
            if (paths.Count == 0) { _status.Text = "请先勾选条目 / Select entries first"; return; }
            string error = _registry.ImportPaths(paths, out var added);
            if (error != null) { _status.Text = error; return; }
            ImportedEntries = added;
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed class Candidate
        {
            public Candidate(string path) { Path = path; }
            public string Path { get; }
            public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(Path) + " — " + Path;
        }
    }
}
