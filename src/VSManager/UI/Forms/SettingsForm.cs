using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>“属性”窗口：屏幕布局、快捷操作与各项选项。修改即时生效。</summary>
    public class SettingsForm : Form
    {
        public class Actions
        {
            public Action Layout, Activate, Rename, MoveMain, MoveTools, AllMain;
            public Func<string> WebStatus;
            public Func<List<string>> WebUrls;
            public Action WebResetToken;
            /// <summary>(key, resource, voice, text, english) → 试听结果。/ Voice preview result.</summary>
            public Func<string, string, string, string, bool, System.Threading.Tasks.Task<VoiceResult>> VoiceTest;
            /// <summary>(endpoint, model, key) → 错误信息或 null。</summary>
            public Func<string, string, string, System.Threading.Tasks.Task<string>> AgentTest;
        }

        private readonly AppSettings _s;
        private readonly ComboBox _cbMain = NewCombo(), _cbTool = NewCombo(), _cbLayout = NewCombo();
        private readonly Dictionary<ToggleSwitch, Action<bool>> _toggles = new Dictionary<ToggleSwitch, Action<bool>>();
        private bool _loading = true;
        private readonly Timer _webTimer = new Timer { Interval = 1000 };
        private readonly Timer _autoSave = new Timer { Interval = 700 };
        private Label _savedLabel;

        /// <summary>任一设置变化后触发（已写入 AppSettings）。</summary>
        public event Action Changed;

        public SettingsForm(AppSettings settings, string targetName, Actions actions)
        {
            _s = settings;
            Text = "属性";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            var wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Dpi.S(620), Math.Min(Dpi.S(820), wa.Height - Dpi.S(60)));
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background, Padding = new Padding(Dpi.S(20), Dpi.S(16), Dpi.S(20), Dpi.S(8)) };
            Theme.DarkControl(scroll);
            Shown += (s, e) => { ActiveControl = null; scroll.AutoScrollPosition = Point.Empty; };

            // ---- 屏幕与布局 ----
            _cbLayout.Items.AddRange(new object[] { "上下", "左右", "仅输出", "仅错误列表" });
            var screenGrid = NewGrid(2, 4, Dpi.S(40));
            screenGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(110)));
            screenGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            screenGrid.Controls.Add(NewLabel("主界面屏幕"), 0, 0);
            screenGrid.Controls.Add(_cbMain, 1, 0);
            screenGrid.Controls.Add(NewLabel("输出/错误栏"), 0, 1);
            screenGrid.Controls.Add(_cbTool, 1, 1);
            screenGrid.Controls.Add(NewLabel("工具窗布局"), 0, 2);
            screenGrid.Controls.Add(_cbLayout, 1, 2);
            var btnIdentify = NewButton("识别屏幕编号", () => ScreenHelper.Identify());
            btnIdentify.Dock = DockStyle.Left;
            btnIdentify.Width = Dpi.S(140);
            screenGrid.Controls.Add(btnIdentify, 1, 3);
            var screenCard = NewCard("屏幕与布局", "VS 主界面与输出/错误列表的目标屏幕", screenGrid);

            // ---- 快捷操作 ----
            var actGrid = NewGrid(2, 4, Dpi.S(42));
            actGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bool has = targetName != null;
            var btnLayout = NewButton("✦  一键布局（主界面 + 工具窗）", actions.Layout, true);
            actGrid.Controls.Add(btnLayout, 0, 0);
            actGrid.SetColumnSpan(btnLayout, 2);
            var b1 = NewButton("激活到前台", actions.Activate);
            var b2 = NewButton("重命名", actions.Rename);
            var b3 = NewButton("主界面 → 主屏", actions.MoveMain);
            var b4 = NewButton("工具窗 → 副屏", actions.MoveTools);
            actGrid.Controls.Add(b1, 0, 1);
            actGrid.Controls.Add(b2, 1, 1);
            actGrid.Controls.Add(b3, 0, 2);
            actGrid.Controls.Add(b4, 1, 2);
            foreach (var b in new[] { btnLayout, b1, b2, b3, b4 }) b.Enabled = has;
            var btnAll = NewButton("所有 VS 主界面 → 主屏", actions.AllMain);
            actGrid.Controls.Add(btnAll, 0, 3);
            actGrid.SetColumnSpan(btnAll, 2);
            var actCard = NewCard("快捷操作", has ? "作用于：" + targetName : "未选择 VS", actGrid);

            // ---- 对话 ----
            var chatCard = ToggleCard("Copilot 对话", "主界面对话框的发送与读取方式", new[]
            {
                Toggle("后台发送（不切换到 VS）", "单行消息直接写入 VS 输入框，窗口保持在本工具；多行消息需短暂切换",
                    _s.BackgroundSend, v => _s.BackgroundSend = v),
                Toggle("自动打开对话助手", "选中 VS 时若未打开 GitHub Copilot 对话窗格，则自动在该 VS 中打开",
                    _s.AutoOpenChat, v => _s.AutoOpenChat = v),
                Toggle("后台同步未选中的 VS", "定期读取其他 VS 的对话并缓存，切换时立即显示最新内容",
                    _s.BackgroundSync, v => _s.BackgroundSync = v),
                Toggle("监听 Copilot 对话状态", "检测运行中 / 空闲，并在完成时提醒", _s.MonitorCopilot, v => _s.MonitorCopilot = v),
                Toggle("对话窗格被切走时自动切回", "Copilot 停靠在文档区时，切到其他标签页会导致无法监听；离开该 VS 后自动把对话助手切回当前",
                    _s.RestoreCopilotPane, v => _s.RestoreCopilotPane = v),
                Toggle("在任务清单中显示 VS 手动对话", "可停止、打开、复制；启用「归档」时会保存并在重启后恢复，关闭归档则仅保存在内存中",
                    _s.WatchConversations, v => _s.WatchConversations = v),
                Toggle("完成时播放提示音", null, _s.Sound, v => _s.Sound = v),
                Toggle("完成时弹出通知", null, _s.Popup, v => _s.Popup = v),
            });

            // ---- 窗口 ----
            var winCard = ToggleCard("窗口与切换", "激活 VS 的行为与本工具窗口选项", new[]
            {
                Toggle("单击列表即激活 VS", "关闭时单击仅选中，双击激活", _s.ClickToActivate, v => _s.ClickToActivate = v),
                Toggle("激活时移到主屏并最大化", null, _s.ActivateMoveMain, v => _s.ActivateMoveMain = v),
                Toggle("激活时同时移动输出/错误栏", null, _s.ActivateMoveTools, v => _s.ActivateMoveTools = v),
                Toggle("关闭 / 最小化到托盘", null, _s.MinimizeToTray, v => _s.MinimizeToTray = v),
                Toggle("本工具窗口置顶", null, _s.TopMost, v => _s.TopMost = v),
            });

            var webCard = BuildWebCard(actions);
            var voiceCard = BuildVoiceCard(actions);
            var agentCard = BuildAgentCard(actions);
            var archiveCard = BuildArchiveCard();

            var cards = new[] { screenCard, actCard, agentCard, chatCard, voiceCard, webCard, archiveCard, winCard };
            for (int i = cards.Length - 1; i >= 0; i--)
            {
                cards[i].Dock = DockStyle.Top;
                scroll.Controls.Add(cards[i]);
                if (i > 0) scroll.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Dpi.S(14), BackColor = Theme.Background });
            }

            // ---- 底部 ----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(60), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(20), Dpi.S(12), Dpi.S(20), Dpi.S(12)) };
            bottom.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0); };
            var done = new FlatButton { Text = "完成", Primary = true, Dock = DockStyle.Right, Width = Dpi.S(100) };
            done.Click += (s, e) => Close();
            var path = _savedLabel = new Label
            {
                Dock = DockStyle.Fill, Text = "修改后自动保存 · " + AppSettings.FilePath, ForeColor = Theme.TextMuted, Font = Theme.Small,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, BackColor = Theme.Sidebar
            };
            AppSettings.Saved += OnSaved;
            _autoSave.Tick += (s, e) => { _autoSave.Stop(); CommitVoice(); CommitAgent(); CommitAgentQuota(); CommitArchive(); };
            bottom.Controls.Add(path);
            bottom.Controls.Add(done);

            Controls.Add(scroll);
            Controls.Add(bottom);

            LoadScreens();
            _cbLayout.SelectedItem = _s.Layout;
            if (_cbLayout.SelectedIndex < 0) _cbLayout.SelectedIndex = 0;
            _cbMain.SelectedIndexChanged += (s, e) => Commit();
            _cbTool.SelectedIndexChanged += (s, e) => Commit();
            _cbLayout.SelectedIndexChanged += (s, e) => Commit();
            // 输入过程中也自动保存，避免未离开输入框就关闭 / 结束进程导致丢失
            foreach (var box in new[] { _agentEndpoint, _agentModel, _agentKey, _agentExtra, _voiceKey, _voiceSpeaker, _voiceSpeakerEn, _archiveDays, _restoreLimit, _restoreHours }.Concat(_quotaBoxes.Values))
                if (box != null) box.TextChanged += (s, e) => { if (_loading) return; _autoSave.Stop(); _autoSave.Start(); };
            _loading = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _webTimer.Stop();
            _autoSave.Stop();
            AppSettings.Saved -= OnSaved;
            CommitPort();
            CommitVoice();
            CommitAgent();
            CommitAgentQuota();
            CommitArchive();
            _qr.Image?.Dispose();
            base.OnFormClosing(e);
        }

        private void OnSaved(string error)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(OnSaved), error); } catch { } return; }
            if (_savedLabel == null || _savedLabel.IsDisposed) return;
            _savedLabel.ForeColor = error == null ? Theme.IdleFg : Theme.Danger;
            _savedLabel.Text = error == null
                ? "✓ 已自动保存 " + DateTime.Now.ToString("HH:mm:ss") + " · " + AppSettings.FilePath
                : "⚠ 保存失败：" + error;
        }

        #region 手机网页遥控

        private TextBox _webPort;
        private Label _webStatus, _webUrlText;
        private DarkCombo _webUrls;
        private readonly PictureBox _qr = new PictureBox { SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White };
        private string _qrFor;

        private Card BuildWebCard(Actions actions)
        {
            var grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(110)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var rows = new List<int>();
            void Row(Control label, Control value, int h, bool span = false)
            {
                int r = rows.Count;
                rows.Add(h);
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
                if (span) { grid.Controls.Add(value, 0, r); grid.SetColumnSpan(value, 2); }
                else { grid.Controls.Add(label, 0, r); grid.Controls.Add(value, 1, r); }
            }

            var webToggle = Toggle("启用手机网页遥控", "手机与电脑在同一网络时，扫码即可在手机上查看对话、发送消息、调试 / 生成（无需任何账号）",
                _s.WebEnabled, v => { CommitPort(); _s.WebEnabled = v; });
            Row(null, webToggle, Dpi.S(56), true);

            Control port;
            (port, _webPort) = NewTextBox(_s.WebPort.ToString(), false);
            _webPort.Leave += (s2, e2) => CommitPort();
            var portRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            port.Dock = DockStyle.Left;
            port.Width = Dpi.S(96);
            _webStatus = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(Dpi.S(12), 0, 0, 0) };
            portRow.Controls.Add(_webStatus);
            portRow.Controls.Add(port);
            Row(NewLabel("端口"), portRow, Dpi.S(42));

            // 二维码 + 地址
            var qrRow = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, Dpi.S(6), 0, 0) };
            var qrHost = new Panel { Dock = DockStyle.Left, Width = Dpi.S(172), Padding = new Padding(Dpi.S(6)), BackColor = Color.White };
            _qr.Dock = DockStyle.Fill;
            qrHost.Controls.Add(_qr);
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Dpi.S(16), 0, 0, 0) };
            var tip = new Label { Dock = DockStyle.Top, Height = Dpi.S(26), Text = "用手机相机或微信扫一扫打开：", ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft };
            _webUrls = new DarkCombo { Dock = DockStyle.Top, Margin = Padding.Empty, DropDownWidth = Dpi.S(420) };
            _webUrls.SelectedIndexChanged += (s2, e2) => UpdateQr();
            _webUrlText = new Label { Dock = DockStyle.Top, Height = Dpi.S(40), ForeColor = Theme.TextMuted, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Dpi.S(40), Margin = Padding.Empty, Padding = new Padding(0, Dpi.S(4), 0, 0), WrapContents = false };
            var copy = new FlatButton { Text = "复制链接", Size = new Size(Dpi.S(96), Dpi.S(32)), Margin = new Padding(0, 0, Dpi.S(8), 0) };
            copy.Click += (s2, e2) =>
            {
                if (_webUrls.SelectedItem is string u) { try { Clipboard.SetText(u); copy.Text = "已复制 ✓"; } catch { } }
            };
            copy.MouseLeave += (s2, e2) => copy.Text = "复制链接";
            var reset = new FlatButton { Text = "重置密钥", Ghost = true, Size = new Size(Dpi.S(96), Dpi.S(32)) };
            reset.Click += (s2, e2) =>
            {
                if (MessageBox.Show(this, "重置后，之前扫码打开的手机页面将无法继续访问，需要重新扫码。确定重置？", "重置访问密钥",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                actions?.WebResetToken?.Invoke();
                RefreshWeb(actions, true);
            };
            btns.Controls.Add(copy);
            btns.Controls.Add(reset);
            right.Controls.Add(btns);
            right.Controls.Add(_webUrlText);
            right.Controls.Add(_webUrls);
            right.Controls.Add(tip);
            qrRow.Controls.Add(right);
            qrRow.Controls.Add(qrHost);
            Row(null, qrRow, Dpi.S(184), true);

            var help = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, Font = Theme.Small,
                Text = "首次启用时 Windows 防火墙可能弹窗，请勾选「专用网络」并允许。链接中包含访问密钥，请勿转发给他人。" +
                       "\r\n不在同一网络（如在外面用流量）时，可在电脑和手机上安装 Tailscale / ZeroTier 等组网工具后，用其分配的地址访问。"
            };
            Row(null, help, Dpi.S(70), true);

            // ---- AI Agent Skill ----
            var skillRow = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, Dpi.S(4), 0, 0) };
            var install = new FlatButton { Text = SkillInstaller.Installed ? "更新 AI Skill" : "安装 AI Skill", Dock = DockStyle.Left, Width = Dpi.S(118) };
            var skillTip = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Dpi.S(12), 0, 0, 0),
                Text = "把本工具的 API 安装为 Skill（~/.copilot、~/.claude、~/.agents 的 skills/vsmanager），" +
                       "Copilot CLI、Claude Code 等 AI Agent 即可列出 VS、派发任务并等待回复、调试 / 生成、读取错误列表。"
            };
            install.Click += (s2, e2) =>
            {
                if (!_s.WebEnabled)
                {
                    if (MessageBox.Show(this, "Skill 通过本机 API 调用 VSManager，需要启用「手机网页遥控」服务（带访问密钥保护）。现在启用？",
                            "安装 AI Skill", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    webToggle.Checked = true;
                }
                try { _s.Save(); } catch { }
                var dirs = SkillInstaller.Install(out var err);
                if (dirs.Count == 0) { MessageBox.Show(this, "安装失败：" + err, "安装 AI Skill", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                install.Text = "更新 AI Skill";
                MessageBox.Show(this, "已安装到：\r\n" + string.Join("\r\n", dirs) + (err != null ? "\r\n\r\n部分失败：" + err : "") +
                    "\r\n\r\n在 Copilot CLI / Claude Code 中直接说「用 vsmanager 把任务发给 2 号 VS」即可。", "安装 AI Skill",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            skillRow.Controls.Add(skillTip);
            skillRow.Controls.Add(install);
            Row(null, skillRow, Dpi.S(52), true);

            grid.RowCount = rows.Count + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Height = rows.Sum();

            _webTimer.Tick += (s2, e2) => RefreshWeb(actions, false);
            _webTimer.Start();
            RefreshWeb(actions, false);
            return NewCard("手机网页遥控", "同一局域网内用手机浏览器控制 VS", grid);
        }

        private void RefreshWeb(Actions actions, bool force)
        {
            string st = actions?.WebStatus?.Invoke() ?? "未启用";
            if (st == "运行中" && actions?.WebUrls != null) st = "运行中，等待手机连接";
            if (_webStatus.Text != st)
            {
                _webStatus.Text = st;
                _webStatus.ForeColor = st.StartsWith("运行中") ? Theme.IdleFg : st == "未启用" ? Theme.TextMuted : Theme.Danger;
            }
            bool on = st.StartsWith("运行中");
            var urls = on ? actions?.WebUrls?.Invoke() ?? new List<string>() : new List<string>();
            var old = _webUrls.Items.Cast<string>().ToList();
            if (force || !old.SequenceEqual(urls))
            {
                string prev = _webUrls.SelectedItem as string;
                _webUrls.Items.Clear();
                foreach (var u in urls) _webUrls.Items.Add(u);
                int keep = prev == null ? -1 : urls.FindIndex(u => HostOf(u) == HostOf(prev));
                if (urls.Count > 0) _webUrls.SelectedIndex = keep >= 0 ? keep : 0;
                UpdateQr();
            }
        }

        private static string HostOf(string url)
        {
            int i = url.IndexOf("#", StringComparison.Ordinal);
            return i >= 0 ? url.Substring(0, i) : url;
        }

        private void UpdateQr()
        {
            string url = _webUrls.SelectedItem as string;
            _webUrls.Enabled = _webUrls.Items.Count > 1;
            _webUrlText.Text = url == null ? (_s.WebEnabled ? "未找到可用的网络地址" : "启用后在这里显示二维码") :
                _webUrls.Items.Count > 1 ? "有多个网络地址，手机打不开时请换一个试试" : "手机需与电脑连接同一 Wi-Fi / 局域网";
            if (url == _qrFor) return;
            _qrFor = url;
            var oldImg = _qr.Image;
            if (url == null) _qr.Image = null;
            else
            {
                var mat = QrCode.Encode(url);
                int scale = Math.Max(2, Dpi.S(158) / (mat.GetLength(0) + 4));
                _qr.Image = QrCode.ToBitmap(mat, scale, Color.Black, Color.White, 2);
            }
            oldImg?.Dispose();
        }

        private void CommitPort()
        {
            if (_loading || _webPort == null) return;
            if (!int.TryParse(_webPort.Text.Trim(), out int p) || p < 1024 || p > 65535)
            {
                _webPort.Text = _s.WebPort.ToString();
                return;
            }
            if (p == _s.WebPort) return;
            _s.WebPort = p;
            Changed?.Invoke();
        }

        #endregion

        #region AI 总控助手

        private TextBox _agentEndpoint, _agentModel, _agentKey, _agentExtra;

        private Card BuildAgentCard(Actions actions)
        {
            var grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(110)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var rows = new List<int>();
            void Row(Control label, Control value, int h, bool span = false)
            {
                int r = rows.Count;
                rows.Add(h);
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
                if (span) { grid.Controls.Add(value, 0, r); grid.SetColumnSpan(value, 2); }
                else { grid.Controls.Add(label, 0, r); grid.Controls.Add(value, 1, r); }
            }

            Row(null, Toggle("在侧边栏显示「AI 总控助手」卡片", "点击卡片在主界面流式对话，可查看所有 VS 状态、向各 VS 的 Copilot 发布任务并等待结果、执行调试 / 生成",
                _s.AgentEnabled, v => _s.AgentEnabled = v), Dpi.S(56), true);
            Row(null, Toggle("操作前确认", "发布任务、调试 / 生成前弹窗确认，防止误操作",
                _s.AgentConfirm, v => _s.AgentConfirm = v), Dpi.S(56), true);
            Row(null, Toggle("任务完成自动跟进", "任务清单中的任务完成后，AI 助手自动汇报结果并继续后续步骤",
                _s.AgentAutoFollowUp, v => _s.AgentAutoFollowUp = v), Dpi.S(56), true);

            var preset = new DarkCombo { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
            preset.Items.AddRange(AgentPresets.All);
            preset.Items.Add("自定义（OpenAI 兼容）");
            var cur = AgentPresets.Find(_s.AgentEndpoint);
            preset.SelectedItem = (object)cur ?? preset.Items[preset.Items.Count - 1];
            Row(NewLabel("服务商"), preset, Dpi.S(40));

            Control c;
            (c, _agentEndpoint) = NewTextBox(_s.AgentEndpoint, false);
            _agentEndpoint.Leave += (s2, e2) => CommitAgent();
            Row(NewLabel("接口地址"), c, Dpi.S(42));
            (c, _agentModel) = NewTextBox(_s.AgentModel, false);
            _agentModel.Leave += (s2, e2) => CommitAgent();
            Row(NewLabel("模型"), c, Dpi.S(42));
            var models = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, Font = Theme.Small, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true };
            void SyncModels() { var p0 = preset.SelectedItem as AgentPreset; models.Text = p0?.Models != null ? "可选模型：" + p0.Models : ""; }
            SyncModels();
            Row(new Label { AutoSize = false }, models, Dpi.S(22));
            (c, _agentKey) = NewTextBox(_s.AgentApiKey, true);
            _agentKey.Leave += (s2, e2) => CommitAgent();
            Row(NewLabel("API Key"), c, Dpi.S(42));
            (c, _agentExtra) = NewTextBox(_s.AgentInstructions, false);
            _agentExtra.Leave += (s2, e2) => CommitAgent();
            Row(NewLabel("额外要求"), c, Dpi.S(42));

            // ---- AI 额度（单次上限）----
            Control QuotaBox(string name, int value, string unit, string tip)
            {
                var (host, box) = NewTextBox(value.ToString(), false);
                box.Leave += (s2, e2) => CommitAgentQuota();
                _quotaBoxes[name] = box;
                host.Dock = DockStyle.Left;
                host.Width = Dpi.S(92);
                var r = AppSettings.AgentQuotaRanges[name];
                string full = tip + $"\n范围 {r[0]} ~ {r[1]}，默认 {r[2]}" + (r[0] == 0 ? "（0 = 模型默认）" : "") + "；超出范围自动修正";
                _quotaTip.SetToolTip(box, full);
                var unitLabel = new Label { Dock = DockStyle.Left, AutoSize = true, Text = unit, ForeColor = Theme.TextMuted, Padding = new Padding(Dpi.S(6), Dpi.S(12), Dpi.S(4), 0) };
                _quotaTip.SetToolTip(unitLabel, full);
                var cell = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
                cell.Controls.Add(unitLabel);
                cell.Controls.Add(host);
                return cell;
            }
            void QuotaRow(string l1, string n1, int v1, string u1, string t1, string l2, string n2, int v2, string u2, string t2)
            {
                var pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(96)));
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                pair.Controls.Add(QuotaBox(n1, v1, u1, t1), 0, 0);
                pair.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var l2Label = NewLabel(l2);
                l2Label.Margin = new Padding(Dpi.S(12), 0, 0, 0);
                pair.Controls.Add(l2Label, 1, 0);
                pair.Controls.Add(QuotaBox(n2, v2, u2, t2), 2, 0);
                Row(NewLabel(l1), pair, Dpi.S(42));
            }
            var quotaHead = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.BottomLeft,
                Text = "AI 额度 · 单次上限（修改后立即生效；默认为原额度 ×20）"
            };
            Row(null, quotaHead, Dpi.S(30), true);
            QuotaRow("工具返回", nameof(AppSettings.AgentMaxToolText), _s.AgentMaxToolText, "字", "单次工具返回文本上限：读取对话 / 等待结果 / 错误列表 / 读取文件 / 任务清单；代码扫描为其 1.5 倍",
                "单条消息", nameof(AppSettings.AgentMaxMessageText), _s.AgentMaxMessageText, "字", "读取 VS 对话时每条消息的文本上限");
            QuotaRow("任务文本", nameof(AppSettings.AgentMaxTaskText), _s.AgentMaxTaskText, "字", "单次发布任务 / 改进需求说明的文本上限；能力名为其 1/2，职责描述为其 1/3",
                "回复长度", nameof(AppSettings.AgentMaxOutputTokens), _s.AgentMaxOutputTokens, "token", "单次回复的最大 token 数（0 = 使用模型默认值；过大可能被服务商拒绝，如 DeepSeek 最多 8192）");
            QuotaRow("历史消息", nameof(AppSettings.AgentMaxHistory), _s.AgentMaxHistory, "条", "上下文保留的历史消息条数（越多越耗 token，过多可能超出模型上下文）",
                "工具轮数", nameof(AppSettings.AgentMaxIterations), _s.AgentMaxIterations, "轮", "单次对话内最多连续调用工具的轮数");
            QuotaRow("读取行数", nameof(AppSettings.AgentMaxFileLines), _s.AgentMaxFileLines, "行", "读取文件时单次最多行数",
                "读取条数", nameof(AppSettings.AgentMaxReadCount), _s.AgentMaxReadCount, "条", "单次最多读取的对话消息条数；错误列表为其 5 倍，任务清单历史为其 2/5");

            // ---- 对话记录容量（agent-chat.jsonl）/ Chat history capacity ----
            Control HistoryBox(int value, int max, string unit, string tip, Action<int> commit)
            {
                var (host, box) = NewTextBox(value.ToString(), false);
                host.Dock = DockStyle.Left;
                host.Width = Dpi.S(92);
                _quotaTip.SetToolTip(box, tip);
                box.Leave += (s2, e2) =>
                {
                    string text = box.Text.Trim().Replace(",", "").Replace("，", "");
                    int v = long.TryParse(text, out var n) ? (int)Math.Max(0, Math.Min(max, n)) : value;
                    box.Text = v.ToString();
                    if (v == value) return;
                    value = v;
                    commit(v);
                    Changed?.Invoke();
                };
                var unitLabel = new Label { Dock = DockStyle.Left, AutoSize = true, Text = unit, ForeColor = Theme.TextMuted, Padding = new Padding(Dpi.S(6), Dpi.S(12), Dpi.S(4), 0) };
                var cell = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
                cell.Controls.Add(unitLabel);
                cell.Controls.Add(host);
                return cell;
            }
            var historyHead = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.BottomLeft,
                Text = "对话记录 / Chat history（仅存本机 agent-chat.jsonl；0 = 不限制）"
            };
            Row(null, historyHead, Dpi.S(30), true);
            {
                var pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(96)));
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                pair.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                pair.Controls.Add(HistoryBox(_s.AgentChatKeepDays, 36500, "天",
                    "对话记录保留天数，更早的记录会被裁剪（默认 " + AgentChatLog.DefaultKeepDays + "；0 = 不按天数限制）\nDays to keep; older records are trimmed (0 = no limit)",
                    v => _s.AgentChatKeepDays = v), 0, 0);
                var maxLabel = NewLabel("最多条数");
                maxLabel.Margin = new Padding(Dpi.S(12), 0, 0, 0);
                pair.Controls.Add(maxLabel, 1, 0);
                pair.Controls.Add(HistoryBox(_s.AgentChatMaxRecords, 1000000, "条",
                    "对话记录最多保留条数，超出时删除最旧的记录（默认 " + AgentChatLog.DefaultMaxRecords + "；0 = 不按条数限制）\nMax records; the oldest are removed (0 = no limit)",
                    v => _s.AgentChatMaxRecords = v), 2, 0);
                Row(NewLabel("保留天数"), pair, Dpi.S(42));
            }

            preset.SelectedIndexChanged += (s2, e2) =>
            {
                SyncModels();
                if (_loading || !(preset.SelectedItem is AgentPreset p)) return;
                _agentEndpoint.Text = p.Endpoint;
                _agentModel.Text = p.Model;
                CommitAgent();
            };

            var testRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, Dpi.S(4), 0, Dpi.S(4)) };
            var test = new FlatButton { Text = "测试连接", Dock = DockStyle.Left, Width = Dpi.S(96) };
            var status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(Dpi.S(12), 0, 0, 0) };
            test.Click += async (s2, e2) =>
            {
                if (actions?.AgentTest == null) return;
                CommitAgent();
                test.Enabled = false;
                status.ForeColor = Theme.TextSecondary;
                status.Text = "正在连接…";
                string err = await actions.AgentTest(_s.AgentEndpoint, _s.AgentModel, _s.EffectiveAgentApiKey);
                if (IsDisposed) return;
                test.Enabled = true;
                status.ForeColor = err == null ? Theme.IdleFg : Theme.Danger;
                status.Text = err ?? "✔ 连接成功，模型可用";
                new ToolTip().SetToolTip(status, status.Text);
            };
            testRow.Controls.Add(status);
            testRow.Controls.Add(test);
            Row(null, testRow, Dpi.S(42), true);

            var help = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, Font = Theme.Small,
                Text = "默认使用 DeepSeek：在 platform.deepseek.com →「API Keys」创建 Key 并粘贴到上方。也支持任意 OpenAI 兼容接口（需支持函数调用）；" +
                       "火山方舟的 Key 与豆包语音的 Key 不同。API Key 加密保存在本机；留空时读取环境变量 VSMANAGER_AGENT_API_KEY。"
            };
            Row(null, help, Dpi.S(56), true);

            grid.RowCount = rows.Count + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Height = rows.Sum();
            return NewCard("AI 总控助手", "主界面流式对话 · 管理所有 VS · 发布任务", grid);
        }

        private readonly Dictionary<string, TextBox> _quotaBoxes = new Dictionary<string, TextBox>();
        private readonly ToolTip _quotaTip = new ToolTip { AutoPopDelay = 15000 };

        /// <summary>校验并保存 AI 额度：非数字恢复原值，超出范围自动修正并回写到输入框。</summary>
        private void CommitAgentQuota()
        {
            if (_loading || _quotaBoxes.Count == 0) return;
            bool changed = false;
            foreach (var kv in _quotaBoxes)
            {
                var field = typeof(AppSettings).GetField(kv.Key);
                int old = (int)field.GetValue(_s);
                string text = kv.Value.Text.Trim().Replace(",", "").Replace("，", "");
                int v = long.TryParse(text, out var n) ? AppSettings.ClampQuota(kv.Key, (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, n))) : old;
                if (kv.Value.Text != v.ToString() && !kv.Value.Focused) kv.Value.Text = v.ToString();
                if (v == old) continue;
                field.SetValue(_s, v);
                changed = true;
            }
            if (changed) Changed?.Invoke();
        }

        private void CommitAgent()
        {
            if (_loading || _agentEndpoint == null) return;
            string ep = _agentEndpoint.Text.Trim(), model = _agentModel.Text.Trim(), key = _agentKey.Text.Trim(), extra = _agentExtra.Text.Trim();
            if (ep == (_s.AgentEndpoint ?? "") && model == (_s.AgentModel ?? "") && key == _s.AgentApiKey && extra == (_s.AgentInstructions ?? "")) return;
            _s.AgentEndpoint = ep;
            _s.AgentModel = model;
            _s.AgentApiKey = key;
            _s.AgentInstructions = extra;
            Changed?.Invoke();
        }

        #endregion

        #region 豆包语音播报

        private TextBox _voiceKey, _voiceSpeaker, _voiceSpeakerEn;
        private DarkCombo _voiceResource, _voiceLanguage;
        private Label _voiceStatus;

        private Card BuildVoiceCard(Actions actions)
        {
            var grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(128)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var rows = new List<int>();
            void Row(Control label, Control value, int h, bool span = false)
            {
                int r = rows.Count;
                rows.Add(h);
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
                if (span) { grid.Controls.Add(value, 0, r); grid.SetColumnSpan(value, 2); }
                else { grid.Controls.Add(label, 0, r); grid.Controls.Add(value, 1, r); }
            }

            Row(null, Toggle("完成时语音播报任务概述", "Copilot 完成后，从最后一条回答提炼不超过 30 字的概述，用豆包语音合成朗读",
                _s.VoiceEnabled, v => { CommitVoice(); _s.VoiceEnabled = v; }), Dpi.S(56), true);
            Row(null, Toggle("播报时先说 VS 名称", null, _s.VoiceIncludeName, v => _s.VoiceIncludeName = v), Dpi.S(36), true);
            // 语音语言：同时决定播报提示词、音色与 AI 总控助手的回复语言 / Voice language: selects prompts, voice and AI reply language
            _voiceLanguage = new DarkCombo { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty, DropDownWidth = Dpi.S(200) };
            _voiceLanguage.Items.AddRange(VoiceLanguages.DisplayNames);
            _voiceLanguage.SelectedIndex = Math.Max(0, Array.IndexOf(VoiceLanguages.Codes, VoiceLanguages.Normalize(_s.VoiceLanguage)));
            _voiceLanguage.SelectedIndexChanged += (s2, e2) =>
            {
                if (_loading) return;
                string code = VoiceLanguages.Codes[Math.Max(0, _voiceLanguage.SelectedIndex)];
                if (code == _s.VoiceLanguage) return;
                _s.VoiceLanguage = code;
                Changed?.Invoke();
            };
            new ToolTip().SetToolTip(_voiceLanguage, "语音播报与 AI 总控助手回复使用的语言，切换后立即生效\r\nLanguage for voice announcements and AI assistant replies; applies immediately");
            Row(NewLabel("语言 / Language"), _voiceLanguage, Dpi.S(40));
            Row(null, Toggle("用 AI 总控助手总结概述 / AI summary", "已配置 AI 助手模型（如 DeepSeek）时，由模型按语音语言把回答概括为 30 字（英文 30 词）内的播报语；未配置或失败时按规则提取首句。" +
                " / When an AI model is configured, it summarizes the answer in the voice language (≤ 30 words); otherwise the first sentence is used.", _s.VoiceAiSummary, v => _s.VoiceAiSummary = v), Dpi.S(56), true);
            Row(null, Toggle("回答与语音语言不一致时翻译 / Translate to voice language", "不使用 AI 总结时，用豆包机器翻译（与语音共用 API Key）把首句翻译成语音语言。" +
                " / Without AI summary, Doubao machine translation (same API key) translates the first sentence into the voice language.", _s.VoiceTranslate, v => _s.VoiceTranslate = v), Dpi.S(56), true);

            Control key;
            (key, _voiceKey) = NewTextBox(_s.VoiceApiKey, true);
            _voiceKey.Leave += (s2, e2) => CommitVoice();
            Row(NewLabel("API Key"), key, Dpi.S(42));

            _voiceResource = new DarkCombo { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty, DropDownWidth = Dpi.S(360) };
            _voiceResource.Items.AddRange(DoubaoVoice.Resources);
            _voiceResource.SelectedItem = _s.VoiceResource;
            if (_voiceResource.SelectedIndex < 0) _voiceResource.SelectedIndex = 0;
            Label spkLabel = NewLabel("音色");
            Label spkEnLabel = NewLabel("英文音色 / EN voice");
            void SyncVoiceLabel()
            {
                bool prompt = DoubaoVoice.IsPromptModel(_voiceResource.SelectedItem as string);
                spkLabel.Text = prompt ? "声音描述" : "音色 ID";
                spkEnLabel.Text = prompt ? "英文描述 / EN desc" : "英文音色 / EN voice";
            }
            _voiceResource.SelectedIndexChanged += (s2, e2) =>
            {
                string res = _voiceResource.SelectedItem as string;
                string cur = _voiceSpeaker.Text.Trim();
                // 两类模型的音色写法不同：仍是另一类的默认值或为空时自动切换
                if (cur.Length == 0 || cur == DoubaoVoice.DefaultSpeaker || cur == DoubaoVoice.DefaultVoicePrompt ||
                    DoubaoVoice.IsPromptModel(res) == System.Text.RegularExpressions.Regex.IsMatch(cur, @"^[A-Za-z0-9_.-]+$"))
                    _voiceSpeaker.Text = DoubaoVoice.DefaultVoiceFor(res);
                // 英文音色同理 / Same for the English voice
                string curEn = _voiceSpeakerEn.Text.Trim();
                if (curEn.Length == 0 || curEn == DoubaoVoice.DefaultSpeakerEn || curEn == DoubaoVoice.DefaultVoicePromptEn ||
                    DoubaoVoice.IsPromptModel(res) == System.Text.RegularExpressions.Regex.IsMatch(curEn, @"^[A-Za-z0-9_.-]+$"))
                    _voiceSpeakerEn.Text = DoubaoVoice.DefaultVoiceFor(res, true);
                SyncVoiceLabel();
                CommitVoice();
            };
            Row(NewLabel("模型 / 资源"), _voiceResource, Dpi.S(40));

            Control spk;
            (spk, _voiceSpeaker) = NewTextBox(_s.VoiceSpeaker, false);
            _voiceSpeaker.Leave += (s2, e2) => CommitVoice();
            Row(spkLabel, spk, Dpi.S(42));

            Control spkEn;
            (spkEn, _voiceSpeakerEn) = NewTextBox(string.IsNullOrWhiteSpace(_s.VoiceSpeakerEn) ? DoubaoVoice.DefaultVoiceFor(_s.VoiceResource, true) : _s.VoiceSpeakerEn, false);
            _voiceSpeakerEn.Leave += (s2, e2) => CommitVoice();
            new ToolTip().SetToolTip(_voiceSpeakerEn, "语音语言为 English 时使用；不可用时自动回退到默认音色并提示\r\nUsed when the voice language is English; falls back to the default voice with a notice if unavailable");
            SyncVoiceLabel();
            Row(spkEnLabel, spkEn, Dpi.S(42));

            var testRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, Dpi.S(4), 0, Dpi.S(4)) };
            var test = new FlatButton { Text = "▶  试听", Dock = DockStyle.Left, Width = Dpi.S(96) };
            _voiceStatus = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(Dpi.S(12), 0, 0, 0) };
            test.Click += async (s2, e2) =>
            {
                if (actions?.VoiceTest == null) return;
                CommitVoice();
                test.Enabled = false;
                _voiceStatus.ForeColor = Theme.TextSecondary;
                _voiceStatus.Text = "正在合成…";
                bool en = _s.IsEnglishVoice;
                var result = await actions.VoiceTest(_s.EffectiveVoiceApiKey, _s.VoiceResource, _s.SpeakerFor(en), Prompts.VoiceTestPhrase(en), en);
                if (IsDisposed) return;
                test.Enabled = true;
                string err = result == null ? "试听失败 / Preview failed" : result.Error;
                _voiceStatus.ForeColor = err != null ? Theme.Danger : result.Notice != null ? Theme.Warning : Theme.IdleFg;
                _voiceStatus.Text = err != null ? err : result.Notice != null ? "⚠ " + result.Notice
                    : _s.VoiceEnabled ? "✔ 播放成功" : "✔ 播放成功（注意：「完成时语音播报」未开启）";
                new ToolTip().SetToolTip(_voiceStatus, _voiceStatus.Text);
            };
            testRow.Controls.Add(_voiceStatus);
            testRow.Controls.Add(test);
            Row(null, testRow, Dpi.S(42), true);

            Row(null, Toggle("按住空格语音输入", "在输入框中按住空格（或按住「🎙 按住说话」）说话，松开后由豆包流式识别转成文字；短按仍输入空格，Esc 取消",
                _s.AsrEnabled, v => _s.AsrEnabled = v), Dpi.S(56), true);
            var asrRes = new DarkCombo { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty, DropDownWidth = Dpi.S(360) };
            asrRes.Items.AddRange(DoubaoAsr.Resources);
            asrRes.SelectedItem = _s.AsrResource;
            if (asrRes.SelectedIndex < 0) asrRes.SelectedIndex = 0;
            asrRes.SelectedIndexChanged += (s2, e2) =>
            {
                if (_loading) return;
                _s.AsrResource = asrRes.SelectedItem as string ?? DoubaoAsr.DefaultResource;
                Changed?.Invoke();
            };
            Row(NewLabel("识别资源"), asrRes, Dpi.S(40));

            var help = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, Font = Theme.Small,
                Text = "seed-audio-1.0：用一句话描述声音（如“年轻女声，语气轻快”），合成约需 10 秒。seed-tts-*：需在控制台开通「语音合成大模型」，" +
                       "填写音色 ID（如 zh_female_vv_uranus_bigtts），响应更快。语音输入与播报共用 API Key，识别资源默认 volc.seedasr.sauc.duration。API Key 加密保存在本机；留空时读取环境变量 VSMANAGER_DOUBAO_API_KEY。" +
                       "语言选 English 时使用英文提示词与英文音色，英文音色不可用时自动回退到默认音色并提示。 / With English selected, English prompts and the English voice are used; an unavailable voice falls back to the default with a notice."
            };
            Row(null, help, Dpi.S(112), true);

            grid.RowCount = rows.Count + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Height = rows.Sum();
            return NewCard("豆包语音", "完成播报 · 按住空格语音输入", grid);
        }

        private void CommitVoice()
        {
            if (_loading || _voiceKey == null) return;
            string key = _voiceKey.Text.Trim();
            string res = _voiceResource.SelectedItem as string ?? DoubaoVoice.DefaultResource;
            string spk = string.IsNullOrWhiteSpace(_voiceSpeaker.Text) ? DoubaoVoice.DefaultVoiceFor(res) : _voiceSpeaker.Text.Trim();
            string spkEn = string.IsNullOrWhiteSpace(_voiceSpeakerEn.Text) ? DoubaoVoice.DefaultVoiceFor(res, true) : _voiceSpeakerEn.Text.Trim();
            if (key == _s.VoiceApiKey && res == _s.VoiceResource && spk == _s.VoiceSpeaker && spkEn == _s.VoiceSpeakerEn) return;
            _s.VoiceApiKey = key;
            _s.VoiceResource = res;
            _s.VoiceSpeaker = spk;
            _s.VoiceSpeakerEn = spkEn;
            Changed?.Invoke();
        }

        #endregion

        #region 归档

        private TextBox _archiveRoot, _archiveDays, _restoreLimit, _restoreHours;
        private Label _archiveStatus;

        private Card BuildArchiveCard()
        {
            var grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(110)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var rows = new List<int>();
            void Row(Control label, Control value, int h, bool span = false)
            {
                int r = rows.Count;
                rows.Add(h);
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
                if (span) { grid.Controls.Add(value, 0, r); grid.SetColumnSpan(value, 2); }
                else { grid.Controls.Add(label, 0, r); grid.Controls.Add(value, 1, r); }
            }

            Row(null, Toggle("启用历史归档", "任务流水、AI 助手对话、各 VS 的 Copilot 对话与发送日志按天追加保存到归档目录（JSONL）",
                _s.ArchiveEnabled, v => { CommitArchive(false); _s.ArchiveEnabled = v; BeginInvoke((Action)UpdateArchiveStatus); }), Dpi.S(56), true);

            Control rootHost;
            (rootHost, _archiveRoot) = NewTextBox(string.IsNullOrWhiteSpace(_s.ArchiveRoot) ? Archive.DefaultRoot : _s.ArchiveRoot, false);
            _archiveRoot.Leave += (s2, e2) => CommitArchive();
            var rootRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var browse = new FlatButton { Text = "浏览…", Dock = DockStyle.Right, Width = Dpi.S(80), Margin = Padding.Empty };
            browse.Click += (s2, e2) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "选择历史归档根目录", ShowNewFolderButton = true })
                {
                    string cur = _archiveRoot.Text.Trim();
                    if (cur.Length > 0 && System.IO.Directory.Exists(cur)) dlg.SelectedPath = cur;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    _archiveRoot.Text = dlg.SelectedPath;
                    CommitArchive();
                }
            };
            rootRow.Controls.Add(rootHost);
            rootRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = Dpi.S(8) });
            rootRow.Controls.Add(browse);
            browse.Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(4));
            Row(NewLabel("归档根目录"), rootRow, Dpi.S(42));

            Control daysHost;
            (daysHost, _archiveDays) = NewTextBox(Math.Max(0, _s.ArchiveRetentionDays).ToString(), false);
            _archiveDays.Leave += (s2, e2) => CommitArchive();
            var daysRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            daysHost.Dock = DockStyle.Left;
            daysHost.Width = Dpi.S(80);
            daysRow.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "天（0 = 永久保留，不自动删除）", ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dpi.S(10), 0, 0, 0) });
            daysRow.Controls.Add(daysHost);
            Row(NewLabel("保留天数"), daysRow, Dpi.S(42));

            Control limHost, hrsHost;
            (limHost, _restoreLimit) = NewTextBox(Math.Max(0, _s.ExternalRestoreLimit).ToString(), false);
            (hrsHost, _restoreHours) = NewTextBox(Math.Max(1, _s.ExternalRestoreHours).ToString(), false);
            _restoreLimit.Leave += (s2, e2) => CommitArchive();
            _restoreHours.Leave += (s2, e2) => CommitArchive();
            var restoreRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            limHost.Dock = DockStyle.Left; limHost.Width = Dpi.S(64);
            hrsHost.Dock = DockStyle.Left; hrsHost.Width = Dpi.S(64);
            Label Lbl(string t) => new Label { Dock = DockStyle.Left, AutoSize = true, Text = t, ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Dpi.S(8), Dpi.S(12), Dpi.S(8), 0) };
            restoreRow.Controls.Add(Lbl("小时内（重启后显示在任务清单）"));
            restoreRow.Controls.Add(hrsHost);
            restoreRow.Controls.Add(Lbl("条 · 最近"));
            restoreRow.Controls.Add(limHost);
            Row(NewLabel("恢复手动对话"), restoreRow, Dpi.S(42));

            var openRow = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, Dpi.S(4), 0, 0) };
            var open = new FlatButton { Text = "打开归档目录", Dock = DockStyle.Left, Width = Dpi.S(128) };
            open.Click += (s2, e2) =>
            {
                CommitArchive();
                string dir = Archive.Root ?? (string.IsNullOrWhiteSpace(_s.ArchiveRoot) ? Archive.DefaultRoot : _s.ArchiveRoot);
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                }
                catch (Exception ex) { MessageBox.Show(this, "无法打开归档目录：" + ex.Message, "历史归档", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            _archiveStatus = new Label { Dock = DockStyle.Fill, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(Dpi.S(12), 0, 0, 0) };
            openRow.Controls.Add(_archiveStatus);
            openRow.Controls.Add(open);
            Row(null, openRow, Dpi.S(44), true);

            var help = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, Font = Theme.Small,
                Text = "tasks\\tasks-日期.jsonl 任务流水 · chat\\ai-日期.jsonl AI 助手对话 · chat\\vs-名称-日期.jsonl 各 VS 对话 · logs\\send-日期.log 发送日志。" +
                       "按天滚动，单文件超过 20 MB 自动分卷；任务清单中的手动对话也写入 vs-*.jsonl，重启后自动恢复（关闭归档则仅保存在内存）；目录不可用时改存到 " + Archive.FallbackRoot + "。"
            };
            Row(null, help, Dpi.S(56), true);

            grid.RowCount = rows.Count + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Height = rows.Sum();
            UpdateArchiveStatus();
            return NewCard("归档", "AI 对话、任务清单与各 VS 对话长期保存", grid);
        }

        private void CommitArchive() => CommitArchive(true);

        private void CommitArchive(bool raise)
        {
            if (_loading || _archiveRoot == null) return;
            string root = _archiveRoot.Text.Trim();
            if (root.Length == 0) root = Archive.DefaultRoot;
            int days = int.TryParse(_archiveDays.Text.Trim(), out var d) && d > 0 ? d : 0;
            int limit = int.TryParse(_restoreLimit.Text.Trim(), out var l) && l >= 0 ? Math.Min(l, 1000) : _s.ExternalRestoreLimit;
            int hours = int.TryParse(_restoreHours.Text.Trim(), out var h) && h > 0 ? Math.Min(h, 24 * 30) : _s.ExternalRestoreHours;
            if (root != _s.ArchiveRoot || days != _s.ArchiveRetentionDays || limit != _s.ExternalRestoreLimit || hours != _s.ExternalRestoreHours)
            {
                _s.ArchiveRoot = root;
                _s.ArchiveRetentionDays = days;
                _s.ExternalRestoreLimit = limit;
                _s.ExternalRestoreHours = hours;
                if (raise) Changed?.Invoke();
            }
            UpdateArchiveStatus();
        }

        private void UpdateArchiveStatus()
        {
            if (_archiveStatus == null) return;
            string w = Archive.Warning;
            if (!_s.ArchiveEnabled) { _archiveStatus.ForeColor = Theme.TextMuted; _archiveStatus.Text = "归档已关闭"; }
            else if (w != null) { _archiveStatus.ForeColor = Theme.Danger; _archiveStatus.Text = "⚠ " + w; }
            else { _archiveStatus.ForeColor = Theme.IdleFg; _archiveStatus.Text = "✓ 正在写入 " + (Archive.Root ?? _s.ArchiveRoot); }
            if (_archiveStatus.Text != null) _tipArchive.SetToolTip(_archiveStatus, _archiveStatus.Text);
        }

        private readonly ToolTip _tipArchive = new ToolTip();

        #endregion

        private static (Control host, TextBox box) NewTextBox(string value, bool password)
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Elevated, Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(4)), Padding = new Padding(Dpi.S(10), Dpi.S(7), Dpi.S(10), 0) };
            var box = new TextBox { Text = value ?? "", BorderStyle = BorderStyle.None, BackColor = Theme.Elevated, ForeColor = Theme.Text, Dock = DockStyle.Fill, UseSystemPasswordChar = password };
            host.Controls.Add(box);
            host.Click += (s, e) => box.Focus();
            return (host, box);
        }

        private void LoadScreens()
        {
            var screens = ScreenHelper.Ordered();
            foreach (var cb in new[] { _cbMain, _cbTool })
            {
                cb.Items.Clear();
                for (int i = 0; i < screens.Count; i++) cb.Items.Add(new ScreenItem { Screen = screens[i], Text = ScreenHelper.Label(screens[i], i) });
            }
            int mainIdx = screens.FindIndex(x => x.DeviceName == _s.MainScreen);
            if (mainIdx < 0) mainIdx = 0;
            int toolIdx = screens.FindIndex(x => x.DeviceName == _s.ToolScreen);
            if (toolIdx < 0) toolIdx = screens.Count >= 3 ? 2 : screens.Count - 1;
            if (_cbMain.Items.Count > 0) _cbMain.SelectedIndex = mainIdx;
            if (_cbTool.Items.Count > 0) _cbTool.SelectedIndex = toolIdx;
        }

        private void Commit()
        {
            if (_loading) return;
            if (_cbMain.SelectedItem is ScreenItem m) _s.MainScreen = m.Screen.DeviceName;
            if (_cbTool.SelectedItem is ScreenItem t) _s.ToolScreen = t.Screen.DeviceName;
            _s.Layout = _cbLayout.SelectedItem as string ?? "上下";
            Changed?.Invoke();
        }

        private class ScreenItem
        {
            public Screen Screen;
            public string Text;
            public override string ToString() => Text;
        }

        #region 控件工厂

        private static ComboBox NewCombo()
        {
            return new DarkCombo
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty, Cursor = Cursors.Hand, DropDownWidth = Dpi.S(360)
            };
        }

        private ToggleSwitch Toggle(string text, string desc, bool value, Action<bool> set)
        {
            var t = new ToggleSwitch { Text = text, Description = desc, Checked = value, Dock = DockStyle.Fill, Margin = Padding.Empty };
            t.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                set(t.Checked);
                Changed?.Invoke();
            };
            _toggles[t] = set;
            return t;
        }

        private static Card ToggleCard(string title, string subtitle, ToggleSwitch[] toggles)
        {
            var grid = new TableLayoutPanel { ColumnCount = 1, RowCount = toggles.Length + 1, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int h = 0;
            foreach (var t in toggles)
            {
                int rh = string.IsNullOrEmpty(t.Description) ? Dpi.S(36) : Dpi.S(52);
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rh));
                h += rh;
            }
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < toggles.Length; i++) grid.Controls.Add(toggles[i], 0, i);
            grid.Height = h;
            return NewCard(title, subtitle, grid);
        }

        private static TableLayoutPanel NewGrid(int cols, int rows, int rowHeight)
        {
            var g = new TableLayoutPanel { ColumnCount = cols, RowCount = rows + 1, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
            for (int i = 0; i < rows; i++) g.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            g.Height = rows * rowHeight;
            return g;
        }

        private static Card NewCard(string title, string subtitle, Control content)
        {
            var c = new Card(title, subtitle) { Padding = new Padding(Dpi.S(18), Dpi.S(48), Dpi.S(18), Dpi.S(12)) };
            int h = content.Height;
            c.Controls.Add(content);
            c.Height = c.Padding.Vertical + h;
            return c;
        }

        private static Label NewLabel(string t) =>
            new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 0, Dpi.S(12), 0) };

        private static FlatButton NewButton(string t, Action a, bool primary = false)
        {
            var b = new FlatButton { Text = t, Primary = primary, Dock = DockStyle.Fill, Margin = new Padding(Dpi.S(3)) };
            b.Click += (s, e) => a?.Invoke();
            return b;
        }

        #endregion
    }

    /// <summary>深色风格的单行输入对话框。</summary>
    public static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string label, string value)
        {
            using (var f = new Form
            {
                Text = title, Font = Theme.Regular, BackColor = Theme.Surface, ForeColor = Theme.Text,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Dpi.S(420), Dpi.S(150))
            })
            {
                f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
                var lbl = new Label { Text = label, AutoSize = true, ForeColor = Theme.TextSecondary, Location = new Point(Dpi.S(20), Dpi.S(18)) };
                var boxHost = new Panel { Location = new Point(Dpi.S(20), Dpi.S(44)), Size = new Size(Dpi.S(380), Dpi.S(34)), BackColor = Theme.Elevated, Padding = new Padding(Dpi.S(10), Dpi.S(7), Dpi.S(10), 0) };
                var box = new TextBox { Text = value ?? "", BorderStyle = BorderStyle.None, BackColor = Theme.Elevated, ForeColor = Theme.Text, Dock = DockStyle.Fill };
                boxHost.Controls.Add(box);
                var ok = new FlatButton { Text = "确定", Primary = true, Size = new Size(Dpi.S(90), Dpi.S(32)), Location = new Point(Dpi.S(310), Dpi.S(98)), DialogResult = DialogResult.OK };
                var cancel = new FlatButton { Text = "取消", Size = new Size(Dpi.S(90), Dpi.S(32)), Location = new Point(Dpi.S(212), Dpi.S(98)), DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { lbl, boxHost, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Shown += (s, e) => { box.Focus(); box.SelectAll(); };
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}