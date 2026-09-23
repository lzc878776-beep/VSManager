using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 「发布到 GitHub」窗口：编辑发布配置、执行敏感信息自检与发布，并显示日志。
    /// "Publish to GitHub" window: edit publish settings, run the sensitive-content scan and publish, and show the log.
    /// </summary>
    public class PublishForm : Form
    {
        private readonly AppSettings _s;
        private readonly TextBox _path, _owner, _repo, _branch, _author, _email, _token, _message, _log;
        private readonly DarkCombo _visibility = new DarkCombo();
        private readonly Label _tokenState, _status;
        private readonly FlatButton _btnPublish, _btnScan, _btnOpen, _btnClose;
        private bool _running;
        private string _url;

        public PublishForm(AppSettings settings)
        {
            _s = settings;
            Text = "发布到 GitHub / Publish to GitHub";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            var wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Dpi.S(760), wa.Width - Dpi.S(40)), Math.Min(Dpi.S(860), wa.Height - Dpi.S(60)));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 仓库配置 / Repository ----
            var grid = NewGrid(10);
            _path = AddRow(grid, 0, "本地目录", _s.PublishRepoPath, false, NewButton("浏览…", Browse));
            _owner = AddRow(grid, 1, "所有者 Owner", _s.PublishOwner, false, null);
            _repo = AddRow(grid, 2, "仓库名", _s.PublishRepoName, false, null);
            _visibility.Items.AddRange(new object[] { "public", "private" });
            _visibility.SelectedItem = string.Equals(_s.PublishVisibility, "private", StringComparison.OrdinalIgnoreCase) ? "private" : "public";
            _visibility.Dock = DockStyle.Fill;
            _visibility.Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(4));
            grid.Controls.Add(NewLabel("可见性"), 0, 3);
            grid.Controls.Add(_visibility, 1, 3);
            grid.SetColumnSpan(_visibility, 2);
            _branch = AddRow(grid, 4, "默认分支", _s.PublishBranch, false, null);
            _author = AddRow(grid, 5, "提交作者", _s.PublishAuthorName, false, null);
            _email = AddRow(grid, 6, "提交邮箱", _s.PublishAuthorEmail, false, null);
            _token = AddRow(grid, 7, "GitHub Token", "", true, NewButton("清除", ClearToken));
            _tokenState = new Label { Dock = DockStyle.Fill, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            grid.Controls.Add(_tokenState, 1, 8);
            grid.SetColumnSpan(_tokenState, 2);
            var hint = new Label
            {
                Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.TopLeft,
                Text = "目录留空＝自动查找 VSManager.csproj；Owner 留空＝Token 对应的用户；邮箱留空＝GitHub noreply 地址。\n" +
                       "Empty folder = auto-detect; empty owner = token user; empty e-mail = GitHub noreply address."
            };
            grid.Controls.Add(hint, 0, 9);
            grid.SetColumnSpan(hint, 3);
            grid.RowStyles[9] = new RowStyle(SizeType.Absolute, Dpi.S(44));
            grid.Height = Dpi.S(38) * 9 + Dpi.S(44);
            var repoCard = NewCard("仓库配置 / Repository", "保存在 settings.json；Token 加密保存", grid);

            // ---- 提交信息 / Commit message ----
            _message = NewMultiline(GitHubPublisher.DefaultCommitMessage().Replace("\n", "\r\n"), false);
            var msgHost = Wrap(_message, Dpi.S(92));
            var msgCard = NewCard("提交信息 / Commit message", "请保持中英双语，不要包含个人信息", msgHost);

            // ---- 日志 / Log ----
            _status = new Label { Dock = DockStyle.Top, Height = Dpi.S(28), Font = Theme.SemiBold, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Theme.TextSecondary, Text = "就绪 / Ready" };
            _log = NewMultiline("", true);
            _log.Font = new Font("Consolas", 9F);
            var logHost = Wrap(_log, 0);
            logHost.Dock = DockStyle.Fill;
            var logCard = new Card("发布日志 / Log", "日志文件：%APPDATA%\\VSManager\\logs\\publish-*.log") { Dock = DockStyle.Fill };
            logCard.Controls.Add(logHost);
            logCard.Controls.Add(_status);

            // ---- 底部 / Bottom bar ----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(60), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(20), Dpi.S(12), Dpi.S(20), Dpi.S(12)) };
            bottom.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0); };
            _btnPublish = new FlatButton { Text = "🚀  发布到 GitHub", Primary = true, Dock = DockStyle.Right, Width = Dpi.S(150) };
            _btnPublish.Click += (s, e) => Start(false);
            _btnOpen = new FlatButton { Text = "打开仓库", Dock = DockStyle.Right, Width = Dpi.S(96), Enabled = false };
            _btnOpen.Click += (s, e) => { if (_url != null) try { Process.Start(_url); } catch { } };
            _btnClose = new FlatButton { Text = "关闭", Dock = DockStyle.Right, Width = Dpi.S(84) };
            _btnClose.Click += (s, e) => Close();
            _btnScan = new FlatButton { Text = "仅自检", Dock = DockStyle.Left, Width = Dpi.S(96) };
            _btnScan.Click += (s, e) => Start(true);
            var btnTerms = new FlatButton { Text = "编辑自检词表", Dock = DockStyle.Left, Width = Dpi.S(120) };
            btnTerms.Click += (s, e) => EditTerms();
            var tips = new ToolTip();
            tips.SetToolTip(_btnScan, "只扫描待提交文件中的敏感信息，不修改仓库\nScan the files to be committed without changing the repository");
            tips.SetToolTip(btnTerms, "内部项目名、客户名等（每行一个），保存在本机，不会提交\nInternal/customer names (one per line), stored locally and never committed");
            tips.SetToolTip(_btnPublish, "git init → .gitignore → 自检 → 提交 → 创建/关联远程 → push");
            bottom.Controls.Add(Spacer(DockStyle.Left));
            bottom.Controls.Add(btnTerms);
            bottom.Controls.Add(Spacer(DockStyle.Left));
            bottom.Controls.Add(_btnScan);
            bottom.Controls.Add(_btnClose);
            bottom.Controls.Add(Spacer(DockStyle.Right));
            bottom.Controls.Add(_btnOpen);
            bottom.Controls.Add(Spacer(DockStyle.Right));
            bottom.Controls.Add(_btnPublish);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(Dpi.S(20), Dpi.S(16), Dpi.S(20), Dpi.S(12)) };
            repoCard.Dock = DockStyle.Top;
            msgCard.Dock = DockStyle.Top;
            body.Controls.Add(logCard);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Dpi.S(12), BackColor = Theme.Background });
            body.Controls.Add(msgCard);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Dpi.S(12), BackColor = Theme.Background });
            body.Controls.Add(repoCard);
            Controls.Add(body);
            Controls.Add(bottom);

            UpdateTokenState();
            string resolved = GitHubPublisher.ResolveRepoPath(_s.PublishRepoPath);
            AppendLog("本地目录 / Folder: " + (resolved.Length > 0 ? resolved : "（未找到，请设置 / not found, please set it）"));
            FormClosing += (s, e) =>
            {
                if (_running) { e.Cancel = true; SetStatus("正在发布，请等待完成 / Publishing, please wait", Theme.Warning); return; }
                SaveConfig();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        #region 控件工厂 / Control helpers

        private static TableLayoutPanel NewGrid(int rows)
        {
            var g = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = rows, BackColor = Theme.Surface };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(118)));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(84)));
            for (int i = 0; i < rows; i++) g.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(38)));
            return g;
        }

        private static Label NewLabel(string text) =>
            new Label { Text = text, Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

        private static FlatButton NewButton(string text, Action click)
        {
            var b = new FlatButton { Text = text, Dock = DockStyle.Fill, Margin = new Padding(Dpi.S(8), Dpi.S(4), 0, Dpi.S(4)) };
            b.Click += (s, e) => click();
            return b;
        }

        private static TextBox AddRow(TableLayoutPanel grid, int row, string label, string value, bool password, Control extra)
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Elevated, Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(4)), Padding = new Padding(Dpi.S(10), Dpi.S(6), Dpi.S(10), 0) };
            var box = new TextBox { Text = value ?? "", BorderStyle = BorderStyle.None, BackColor = Theme.Elevated, ForeColor = Theme.Text, Dock = DockStyle.Fill, UseSystemPasswordChar = password };
            host.Controls.Add(box);
            host.Click += (s, e) => box.Focus();
            grid.Controls.Add(NewLabel(label), 0, row);
            grid.Controls.Add(host, 1, row);
            if (extra != null) grid.Controls.Add(extra, 2, row);
            else grid.SetColumnSpan(host, 2);
            return box;
        }

        private static TextBox NewMultiline(string text, bool readOnly)
        {
            var box = new TextBox
            {
                Text = text, Multiline = true, ReadOnly = readOnly, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                BorderStyle = BorderStyle.None, BackColor = Theme.Elevated, ForeColor = Theme.Text, Dock = DockStyle.Fill
            };
            Theme.DarkControl(box);
            return box;
        }

        private static Panel Wrap(Control c, int height)
        {
            var host = new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Elevated, Padding = new Padding(Dpi.S(10), Dpi.S(8), Dpi.S(4), Dpi.S(8)) };
            host.Controls.Add(c);
            return host;
        }

        private static Card NewCard(string title, string subtitle, Control content)
        {
            var card = new Card(title, subtitle);
            card.Controls.Add(content);
            card.Height = content.Height + card.Padding.Vertical;
            return card;
        }

        private static Panel Spacer(DockStyle dock) => new Panel { Dock = dock, Width = Dpi.S(8), BackColor = Theme.Sidebar };

        #endregion

        private void Browse()
        {
            using (var d = new FolderBrowserDialog { Description = "选择要发布的本地仓库目录 / Choose the local repository folder", ShowNewFolderButton = false })
            {
                string cur = GitHubPublisher.ResolveRepoPath(_path.Text);
                if (Directory.Exists(cur)) d.SelectedPath = cur;
                if (d.ShowDialog(this) == DialogResult.OK) _path.Text = d.SelectedPath;
            }
        }

        private void ClearToken()
        {
            _s.GitHubToken = null;
            _token.Text = "";
            _s.Save();
            UpdateTokenState();
        }

        private void UpdateTokenState()
        {
            if (_token.Text.Trim().Length > 0) { _tokenState.ForeColor = Theme.AccentText; _tokenState.Text = "将保存新的 Token（加密）/ New token will be saved (encrypted)"; }
            else if (!string.IsNullOrEmpty(_s.GitHubTokenProtected) && _s.GitHubToken.Length > 0) { _tokenState.ForeColor = Theme.IdleFg; _tokenState.Text = "✓ 已保存 Token（留空保持不变）/ Token saved (leave empty to keep)"; }
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppSettings.GitHubTokenEnvVar))) { _tokenState.ForeColor = Theme.IdleFg; _tokenState.Text = "✓ 使用环境变量 / Using " + AppSettings.GitHubTokenEnvVar; }
            else { _tokenState.ForeColor = Theme.Warning; _tokenState.Text = "未配置：填写 Token 或设置环境变量 " + AppSettings.GitHubTokenEnvVar; }
        }

        private void EditTerms()
        {
            try
            {
                PublishScanner.LoadTerms();
                Process.Start("notepad.exe", "\"" + PublishScanner.TermsFile + "\"");
            }
            catch (Exception ex) { SetStatus("无法打开词表 / Cannot open the term list: " + ex.Message, Theme.Danger); }
        }

        private void SaveConfig()
        {
            _s.PublishRepoPath = _path.Text.Trim();
            _s.PublishOwner = _owner.Text.Trim();
            _s.PublishRepoName = _repo.Text.Trim();
            _s.PublishVisibility = (_visibility.SelectedItem as string) ?? "public";
            _s.PublishBranch = _branch.Text.Trim();
            _s.PublishAuthorName = _author.Text.Trim();
            _s.PublishAuthorEmail = _email.Text.Trim();
            if (_token.Text.Trim().Length > 0)
            {
                _s.GitHubToken = _token.Text.Trim();
                _token.Text = "";
            }
            _s.Save();
            UpdateTokenState();
        }

        private PublishOptions BuildOptions()
        {
            var o = new PublishOptions
            {
                RepoPath = GitHubPublisher.ResolveRepoPath(_s.PublishRepoPath),
                Owner = _s.PublishOwner,
                RepoName = _s.PublishRepoName,
                Private = string.Equals(_s.PublishVisibility, "private", StringComparison.OrdinalIgnoreCase),
                Branch = _s.PublishBranch,
                AuthorName = _s.PublishAuthorName,
                AuthorEmail = _s.PublishAuthorEmail,
                CommitMessage = _message.Text,
                Token = _s.EffectiveGitHubToken
            };
            foreach (var k in new[] { _s.EffectiveAgentApiKey, _s.EffectiveVoiceApiKey, _s.WebToken, o.Token })
                if (!string.IsNullOrWhiteSpace(k)) o.KnownSecrets.Add(k);
            return o;
        }

        private async void Start(bool scanOnly)
        {
            if (_running) return;
            SaveConfig();
            var o = BuildOptions();
            _running = true;
            _url = null;
            foreach (var b in new[] { _btnPublish, _btnScan, _btnClose, _btnOpen }) b.Enabled = false;
            _log.Clear();
            SetStatus(scanOnly ? "正在自检… / Scanning…" : "正在发布… / Publishing…", Theme.AccentText);
            PublishResult r;
            try
            {
                var p = new GitHubPublisher(o, AppendLogSafe, ConfirmFindings);
                r = await Task.Run(() => scanOnly ? p.ScanOnly() : p.Run());
            }
            catch (Exception ex) { r = new PublishResult { Error = ex.Message }; }
            _running = false;
            if (IsDisposed) return;
            foreach (var b in new[] { _btnPublish, _btnScan, _btnClose }) b.Enabled = true;
            if (scanOnly)
            {
                if (r.Error != null) SetStatus("✗ " + r.Error, Theme.Danger);
                else if (r.Findings.Count == 0) SetStatus("✓ 自检通过，未发现敏感信息 / Scan passed", Theme.Success);
                else SetStatus($"⚠ 发现 {r.Findings.Count} 处疑似敏感信息，见日志 / {r.Findings.Count} hit(s), see log", Theme.Warning);
            }
            else if (r.Ok)
            {
                _url = r.Url;
                _btnOpen.Enabled = true;
                SetStatus("✓ 发布成功 / Published: " + r.Url, Theme.Success);
            }
            else SetStatus((r.Aborted ? "⏹ " : "✗ ") + r.Error + (string.IsNullOrEmpty(r.Hint) ? "" : "  —  " + r.Hint), r.Aborted ? Theme.Warning : Theme.Danger);
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        private void AppendLog(string line)
        {
            if (IsDisposed) return;
            _log.AppendText((_log.TextLength > 0 ? "\r\n" : "") + line);
        }

        private void AppendLogSafe(string line)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) BeginInvoke(new Action(() => AppendLog(line)));
                else AppendLog(line);
            }
            catch { }
        }

        /// <summary>自检命中时暂停发布，由用户确认是否继续（默认取消）。/ Pauses on hits and asks the user whether to continue (defaults to cancel).</summary>
        private bool ConfirmFindings(List<PublishFinding> findings)
        {
            if (IsDisposed) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<bool>(() => ShowFindings(this, findings)));
            return ShowFindings(this, findings);
        }

        public static bool ShowFindings(IWin32Window owner, List<PublishFinding> findings)
        {
            using (var f = new Form
            {
                Text = "敏感信息自检 / Sensitive-content scan", Font = Theme.Regular, BackColor = Theme.Surface, ForeColor = Theme.Text,
                FormBorderStyle = FormBorderStyle.Sizable, MinimizeBox = false, ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Dpi.S(760), Dpi.S(460))
            })
            {
                f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
                var head = new Label
                {
                    Dock = DockStyle.Top, Height = Dpi.S(58), ForeColor = Theme.Warning, Padding = new Padding(Dpi.S(16), Dpi.S(10), Dpi.S(16), 0),
                    Text = $"发现 {findings.Count} 处疑似敏感信息，发布已暂停。请逐条确认后再决定是否继续。\n" +
                           $"{findings.Count} possible sensitive item(s) found. Publishing is paused — review them before continuing."
                };
                var list = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None,
                    BackColor = Theme.Elevated, ForeColor = Theme.Text, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F),
                    Text = string.Join("\r\n", findings.Select(x => x.ToString()))
                };
                Theme.DarkControl(list);
                var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Dpi.S(16), 0, Dpi.S(16), Dpi.S(8)), BackColor = Theme.Surface };
                host.Controls.Add(list);
                var bar = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(56), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(16), Dpi.S(11), Dpi.S(16), Dpi.S(11)) };
                var cancel = new FlatButton { Text = "取消发布 / Cancel", Primary = true, Dock = DockStyle.Right, Width = Dpi.S(150), DialogResult = DialogResult.Cancel };
                var go = new FlatButton { Text = "我已确认，继续发布 / Continue", Dock = DockStyle.Right, Width = Dpi.S(220), Tint = Theme.Danger, DialogResult = DialogResult.Yes };
                var copy = new FlatButton { Text = "复制列表 / Copy", Dock = DockStyle.Left, Width = Dpi.S(130) };
                copy.Click += (s, e) => { try { Clipboard.SetText(list.Text); } catch { } };
                bar.Controls.Add(go);
                bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = Dpi.S(8), BackColor = Theme.Sidebar });
                bar.Controls.Add(cancel);
                bar.Controls.Add(copy);
                f.Controls.Add(host);
                f.Controls.Add(head);
                f.Controls.Add(bar);
                f.CancelButton = cancel;
                f.Shown += (s, e) => list.Select(0, 0);
                return f.ShowDialog(owner) == DialogResult.Yes;
            }
        }
    }
}
