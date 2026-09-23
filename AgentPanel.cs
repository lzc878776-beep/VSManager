using System;
using System.Drawing;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>主界面 AI 总控助手：与 VS 对话界面同样的标题栏 / 快捷栏 / 居中对话记录 / 输入区。</summary>
    public class AgentPanel : Panel
    {
        private static readonly Color InputBg = Color.FromArgb(24, 24, 30);
        private static readonly Font BadgeFont = new Font(Theme.FontName, 13F);

        private static readonly (string Text, string Prompt)[] QuickPrompts =
        {
            ("📋 各 VS 状态", "各个 VS 现在都在做什么？简要列出状态和最近的任务。"),
            ("🏷 整理 VS 职责", "用 scan_vs_code 扫描每个 VS 的代码结构，结合最近的 Copilot 对话，为每个 VS 写一句长期稳定的职责描述并用 set_vs_note 记录，方便之后按描述自动分派任务。最后用表格列出结果。"),
            ("🛠 检查编译错误", "检查所有 VS 的错误列表，汇总有编译错误的项目。"),
            ("⚙ 生成空闲的 VS", "给所有空闲的 VS 生成解决方案，完成后汇总结果。"),
            ("⇲ 工具窗模式", "把所有 VS 的 Copilot 对话窗格切换为工具窗口模式。"),
        };

        private readonly Panel _header = new Panel();
        private readonly Panel _toolbarRow = new Panel();
        private readonly FlowLayoutPanel _toolbar = new FlowLayoutPanel();
        private readonly Panel _transcriptHost = new Panel();
        private readonly TranscriptView _transcript = new TranscriptView { AssistantLabel = "AI 助手" };
        private readonly Panel _inputArea = new Panel();
        private readonly Label _inputStatus = new Label();
        private readonly Panel _inputBox = new Panel();
        private readonly TextBox _input = new TextBox();
        private readonly Label _placeholder = new Label();
        private readonly FlatButton _btnSend, _btnStop, _btnClear, _btnSettings;
        private readonly Timer _renderTimer = new Timer { Interval = 60 };
        private readonly Timer _pulse = new Timer { Interval = 400 };
        private readonly ToolTip _tips = new ToolTip();
        private AgentService _agent;
        private int _dots;

        public event Action SettingsRequested;

        public AgentPanel()
        {
            BackColor = Theme.Background;
            DoubleBuffered = true;

            // ---- 标题栏 ----
            _header.Dock = DockStyle.Top;
            _header.Height = Dpi.S(64);
            _header.BackColor = Theme.Background;
            SetDoubleBuffered(_header);
            _header.Paint += Header_Paint;
            _header.Resize += (s, e) => { LayoutHeaderButtons(); _header.Invalidate(); };
            _btnSettings = HeaderButton("⚙  模型设置", () => SettingsRequested?.Invoke());
            _btnClear = HeaderButton("＋  新对话", () => { _agent?.Clear(); _input.Focus(); });
            _tips.SetToolTip(_btnSettings, "服务商 / 模型 / API Key（属性 → AI 总控助手）");
            _tips.SetToolTip(_btnClear, "清空上下文，开始新对话");
            _header.Controls.AddRange(new Control[] { _btnSettings, _btnClear });

            // ---- 快捷指令 ----
            _toolbarRow.Dock = DockStyle.Top;
            _toolbarRow.Height = Dpi.S(46);
            _toolbarRow.BackColor = Theme.Background;
            _toolbarRow.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Divider)) e.Graphics.DrawLine(pen, 0, _toolbarRow.Height - 1, _toolbarRow.Width, _toolbarRow.Height - 1);
            };
            _toolbar.Dock = DockStyle.Fill;
            _toolbar.WrapContents = false;
            _toolbar.AutoScroll = false;
            _toolbar.BackColor = Theme.Background;
            _toolbar.Padding = new Padding(0, Dpi.S(6), 0, 0);
            foreach (var q in QuickPrompts)
            {
                var b = new FlatButton { Text = q.Text, Ghost = true, Height = Dpi.S(30), Margin = new Padding(Dpi.S(6), 0, 0, 0) };
                b.Width = TextRenderer.MeasureText(q.Text, b.Font).Width + Dpi.S(26);
                string prompt = q.Prompt;
                b.Click += (s, e) => Send(prompt);
                _tips.SetToolTip(b, prompt);
                _toolbar.Controls.Add(b);
            }
            _toolbarRow.Controls.Add(_toolbar);

            // ---- 对话记录 ----
            _transcriptHost.Dock = DockStyle.Fill;
            _transcriptHost.BackColor = Theme.Background;
            _transcript.Dock = DockStyle.Fill;
            _transcriptHost.Controls.Add(_transcript);

            // ---- 输入区 ----
            _inputArea.Dock = DockStyle.Bottom;
            _inputArea.Height = Dpi.S(184);
            _inputArea.BackColor = Theme.Background;
            _inputStatus.Dock = DockStyle.Top;
            _inputStatus.Height = Dpi.S(28);
            _inputStatus.Font = Theme.Small;
            _inputStatus.ForeColor = Theme.TextSecondary;
            _inputStatus.TextAlign = ContentAlignment.MiddleLeft;
            _inputStatus.AutoEllipsis = true;
            _inputStatus.UseMnemonic = false;

            _inputBox.Dock = DockStyle.Fill;
            _inputBox.BackColor = Theme.Background;
            _inputBox.Padding = new Padding(Dpi.S(14), Dpi.S(12), Dpi.S(10), Dpi.S(8));
            SetDoubleBuffered(_inputBox);
            _inputBox.Paint += InputBox_Paint;
            _inputBox.Resize += (s, e) => _inputBox.Invalidate();
            _inputBox.Click += (s, e) => _input.Focus();

            _input.Multiline = true;
            _input.AcceptsReturn = true;
            _input.BorderStyle = BorderStyle.None;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.Dock = DockStyle.Fill;
            _input.Font = new Font(Theme.FontName, 10F);
            _input.BackColor = InputBg;
            _input.ForeColor = Theme.Text;
            _input.AccessibleName = "AI 助手输入框";
            _input.KeyDown += Input_KeyDown;
            _input.TextChanged += (s, e) => UpdateUi();
            _input.GotFocus += (s, e) => { UpdateUi(); _inputBox.Invalidate(); };
            _input.LostFocus += (s, e) => { UpdateUi(); _inputBox.Invalidate(); };
            Theme.DarkControl(_input);

            _placeholder.AutoSize = true;
            _placeholder.ForeColor = Theme.TextMuted;
            _placeholder.BackColor = InputBg;
            _placeholder.Font = _input.Font;
            _placeholder.Location = new Point(Dpi.S(14), Dpi.S(12));
            _placeholder.Cursor = Cursors.IBeam;
            _placeholder.Click += (s, e) => _input.Focus();

            var actions = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(38), BackColor = InputBg, Padding = new Padding(0, Dpi.S(4), 0, 0) };
            _btnSend = new FlatButton { Text = "发送  ➤", Primary = true, Dock = DockStyle.Right, Width = Dpi.S(92) };
            _btnSend.Click += (s, e) => Send(_input.Text);
            _btnStop = new FlatButton { Text = "■  停止", Tint = Theme.Danger, Dock = DockStyle.Right, Width = Dpi.S(80) };
            _btnStop.Click += (s, e) => _agent?.Stop();
            var gap = new Panel { Dock = DockStyle.Right, Width = Dpi.S(8), BackColor = InputBg };
            var hint = new Label
            {
                Dock = DockStyle.Fill, Text = "Enter 发送 · Shift+Enter 换行 · Esc 停止", ForeColor = Theme.TextMuted, BackColor = InputBg,
                Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false
            };
            actions.Controls.Add(hint);
            actions.Controls.Add(_btnStop);
            actions.Controls.Add(gap);
            actions.Controls.Add(_btnSend);

            _inputBox.Controls.Add(_placeholder);
            _inputBox.Controls.Add(_input);
            _inputBox.Controls.Add(actions);
            _placeholder.BringToFront();
            _inputArea.Controls.Add(_inputBox);
            _inputArea.Controls.Add(_inputStatus);

            Controls.Add(_transcriptHost);
            Controls.Add(_inputArea);
            Controls.Add(_toolbarRow);
            Controls.Add(_header);

            _renderTimer.Tick += (s, e) => { _renderTimer.Stop(); RenderNow(); };
            _pulse.Tick += (s, e) => { _dots = (_dots + 1) % 4; UpdateStatus(); };
            Disposed += (s, e) => { _renderTimer.Dispose(); _pulse.Dispose(); _tips.Dispose(); };
            Resize += (s, e) => LayoutContent();
            LayoutContent();
            UpdateUi();
        }

        public void Bind(AgentService agent)
        {
            _agent = agent;
            _agent.Changed += () => { if (!_renderTimer.Enabled) _renderTimer.Start(); UpdateUi(); };
            RenderNow();
            UpdateUi();
        }

        /// <summary>设置变化后刷新模型名与空状态提示。</summary>
        public void RefreshConfig()
        {
            RenderNow();
            UpdateUi();
        }

        public void FocusInput()
        {
            if (Visible && _input.CanFocus) _input.Focus();
        }

        private FlatButton HeaderButton(string text, Action a)
        {
            var b = new FlatButton { Text = text, Height = Dpi.S(32) };
            b.Width = TextRenderer.MeasureText(text, b.Font).Width + Dpi.S(28);
            b.Click += (s, e) => a();
            return b;
        }

        private void LayoutContent()
        {
            int side = Math.Max(Dpi.S(28), (Width - Dpi.S(1000)) / 2);
            _transcriptHost.Padding = new Padding(side, Dpi.S(12), side - Dpi.S(8) < 0 ? 0 : side - Dpi.S(8), Dpi.S(4));
            _inputArea.Padding = new Padding(side, Dpi.S(8), side, Dpi.S(18));
            _toolbarRow.Padding = new Padding(Dpi.S(22), 0, Dpi.S(28), 0);
            LayoutHeaderButtons();
            _header.Invalidate();
        }

        private void LayoutHeaderButtons()
        {
            int y = (_header.Height - _btnSettings.Height) / 2, right = _header.Width - Dpi.S(28);
            _btnSettings.Location = new Point(right - _btnSettings.Width, y);
            _btnClear.Location = new Point(_btnSettings.Left - Dpi.S(8) - _btnClear.Width, y);
        }

        private void RenderNow()
        {
            if (_agent == null) return;
            if (_agent.Transcript.Messages.Count == 0)
            {
                _transcript.SetEmpty(_agent.Configured
                    ? "我是 AI 总控助手，可以统一管理所有 VS：\n\n· 各个 VS 现在都在做什么？\n· 让 2 号 VS 修复编译错误，完成后告诉我\n· 给所有空闲的 VS 生成解决方案"
                    : "尚未配置 AI 模型\n\n点击右上角「⚙ 模型设置」，选择 DeepSeek 并填写 API Key 后即可使用\n（在 platform.deepseek.com 创建 Key）");
                return;
            }
            _transcript.Render(_agent.Transcript, true);
        }

        private void Send(string text)
        {
            if (_agent == null) return;
            text = (text ?? "").Trim();
            if (_agent.Running || text.Length == 0) return;
            if (!_agent.Configured) { SettingsRequested?.Invoke(); return; }
            if (text == _input.Text.Trim()) _input.Clear();
            _ = _agent.RunAsync(text);
            FocusInput();
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control && !e.Alt)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Send(_input.Text);
            }
            else if (e.KeyCode == Keys.Escape && _agent?.Running == true)
            {
                e.SuppressKeyPress = true;
                _agent.Stop();
            }
        }

        private void UpdateUi()
        {
            bool running = _agent?.Running == true;
            _placeholder.Visible = _input.TextLength == 0 && !_input.Focused;
            _placeholder.Text = _agent?.Configured == false ? "尚未配置模型，点击右上角「⚙ 模型设置」…" : "让 AI 查看所有 VS 状态、发布任务、调试与生成…";
            _btnSend.Enabled = !running && _input.Text.Trim().Length > 0;
            _btnStop.Enabled = running;
            _btnClear.Enabled = _agent != null && (running || _agent.Transcript.Messages.Count > 0);
            foreach (Control c in _toolbar.Controls) c.Enabled = !running;
            if (running && !_pulse.Enabled) { _dots = 0; _pulse.Start(); }
            else if (!running && _pulse.Enabled) _pulse.Stop();
            UpdateStatus();
            _transcript.SetActivity(running ? (_agent.Activity.Length > 0 ? _agent.Activity : "思考中…") : null, null);
            _header.Invalidate();
        }

        private void UpdateStatus()
        {
            string text;
            Color color;
            if (_agent?.Running == true)
            {
                text = "● " + (_agent.Activity.Length > 0 ? _agent.Activity.TrimEnd('…') : "思考中") + new string('.', _dots + 1) + " · 可编辑草稿，Esc 停止";
                color = Theme.BusyFg;
            }
            else if (_agent != null && !_agent.Configured) { text = "⚠ 未配置模型 · 点击右上角「⚙ 模型设置」"; color = Theme.Warning; }
            else
            {
                text = (_input.Focused ? "正在输入" : _input.TextLength > 0 ? "草稿待发送" : "等待输入") + " · " + _input.TextLength + " 字";
                color = _input.Focused ? Theme.AccentText : Theme.TextSecondary;
            }
            if (_inputStatus.Text != text) _inputStatus.Text = text;
            if (_inputStatus.ForeColor != color) _inputStatus.ForeColor = color;
        }

        private void Header_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int x = Dpi.S(28), right = _btnClear.Left - Dpi.S(16);

            var badge = new RectangleF(x, Dpi.S(14), Dpi.S(36), Dpi.S(36));
            using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(badge, Theme.Accent, Color.FromArgb(59, 130, 246), 45f))
            using (var p = Theme.RoundRect(badge, Dpi.S(10)))
                g.FillPath(br, p);
            TextRenderer.DrawText(g, "✦", BadgeFont, Rectangle.Round(badge), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += Dpi.S(48);

            const string title = "AI 总控助手";
            var tsz = TextRenderer.MeasureText(g, title, Theme.Big, Size.Empty, flags);
            TextRenderer.DrawText(g, title, Theme.Big, new Rectangle(x, Dpi.S(10), Math.Min(tsz.Width, Math.Max(0, right - x)), Dpi.S(26)), Theme.Text, flags | TextFormatFlags.VerticalCenter);
            string st; Color fg, bg, dot;
            if (_agent == null || !_agent.Configured) { st = "未配置"; fg = Theme.Warning; bg = Theme.NoneBg; dot = Theme.Warning; }
            else if (_agent.Running) { st = "思考中"; fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; }
            else { st = "空闲"; fg = Theme.IdleFg; bg = Theme.IdleBg; dot = Theme.IdleDot; }
            int px = x + tsz.Width + Dpi.S(12);
            if (right - px > Dpi.S(40)) Theme.DrawPill(g, px, Dpi.S(12), Math.Min(Dpi.S(160), right - px), st, bg, fg, dot);

            string sub = _agent == null ? "" : _agent.Configured
                ? _agent.ProviderName + " · " + _agent.ModelName + " · 统一管理所有 VS、发布任务"
                : "选择服务商并填写 API Key 后即可使用";
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(x, Dpi.S(38), Math.Max(0, right - x), Dpi.S(18)), Theme.TextMuted, flags | TextFormatFlags.VerticalCenter);
        }

        private void InputBox_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, _inputBox.Width - 1.5f, _inputBox.Height - 1.5f);
            Theme.FillRound(g, InputBg, r, Dpi.S(12));
            using (var p = Theme.RoundRect(r, Dpi.S(12)))
            using (var pen = new Pen(_input.Focused ? Theme.AccentBorder : Theme.Border, _input.Focused ? 1.5f : 1f))
                g.DrawPath(pen, p);
        }

        private static void SetDoubleBuffered(Control c) =>
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(c, true, null);
    }

    /// <summary>侧边栏顶部的「AI 总控助手」入口卡片，样式与 VS 列表项一致。</summary>
    public class AgentCard : Control
    {
        private static readonly Font BadgeFont = new Font(Theme.FontName, 11F);
        private AgentService _agent;
        private bool _hover, _selected;

        public AgentCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
            Cursor = Cursors.Hand;
            Height = Dpi.S(84);
            AccessibleName = "AI 总控助手";
            AccessibleRole = AccessibleRole.PushButton;
        }

        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; Invalidate(); }
        }

        public void Bind(AgentService agent)
        {
            _agent = agent;
            _agent.Changed += Invalidate;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Sidebar);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var card = new RectangleF(Dpi.S(10), Dpi.S(10), Width - Dpi.S(20), Height - Dpi.S(14));
            if (_selected)
            {
                Theme.FillRound(g, Theme.RowSelected, card, Dpi.S(10));
                Theme.DrawRound(g, Color.FromArgb(70, 139, 92, 246), card, Dpi.S(10));
                Theme.FillRound(g, Theme.Accent, new RectangleF(card.X + Dpi.S(1), card.Y + Dpi.S(14), Dpi.S(3), card.Height - Dpi.S(28)), Dpi.S(2));
            }
            else
            {
                Theme.FillRound(g, _hover ? Theme.RowHover : Theme.Surface, card, Dpi.S(10));
                Theme.DrawRound(g, Theme.Divider, card, Dpi.S(10));
            }

            int x0 = (int)card.X + Dpi.S(14), right = (int)card.Right - Dpi.S(12);
            var badge = new RectangleF(x0, card.Y + (card.Height - Dpi.S(36)) / 2, Dpi.S(36), Dpi.S(36));
            using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(badge, Theme.Accent, Color.FromArgb(59, 130, 246), 45f))
            using (var p = Theme.RoundRect(badge, Dpi.S(10)))
                g.FillPath(br, p);
            TextRenderer.DrawText(g, "✦", BadgeFont, Rectangle.Round(badge), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            int tx = (int)badge.Right + Dpi.S(12), ty = (int)card.Y + Dpi.S(10);
            string st; Color fg, bg, dot;
            if (_agent == null || !_agent.Configured) { st = "未配置"; fg = Theme.Warning; bg = Theme.NoneBg; dot = Theme.Warning; }
            else if (_agent.Running) { st = "思考中"; fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; }
            else { st = "空闲"; fg = Theme.IdleFg; bg = Theme.IdleBg; dot = Theme.IdleDot; }
            var pw = TextRenderer.MeasureText(g, st, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(30);
            TextRenderer.DrawText(g, "AI 总控助手", Theme.SemiBold, new Rectangle(tx, ty, Math.Max(0, right - tx - pw - Dpi.S(6)), Dpi.S(20)),
                _selected ? Color.White : Theme.Text, flags);
            Theme.DrawPill(g, right - pw, ty - Dpi.S(1), pw, st, bg, fg, dot);
            string sub = _agent == null ? "" : _agent.Configured ? _agent.ProviderName + " · " + _agent.ModelName : "点击配置 DeepSeek 等模型";
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(tx, ty + Dpi.S(24), Math.Max(0, right - tx), Dpi.S(18)), Theme.TextMuted, flags);
        }
    }
}