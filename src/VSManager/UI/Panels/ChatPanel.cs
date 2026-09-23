using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>主界面：所选 VS 的 Copilot 对话，含标题栏、调试工具栏、对话记录与输入框。</summary>
    public class ChatPanel : Panel
    {
        private readonly Panel _header = new Panel();
        private readonly Panel _toolbarRow = new Panel();
        public readonly FlowLayoutPanel Toolbar = new FlowLayoutPanel();
        private readonly Panel _transcriptHost = new Panel();
        private readonly TranscriptView _transcript = new TranscriptView();
        private readonly Panel _inputArea = new Panel();
        private readonly Panel _inputBox = new Panel();
        private readonly ImageInputBox _input = new ImageInputBox();
        private readonly List<ChatImage> _images = new List<ChatImage>();
        private readonly FlowLayoutPanel _imagePreview = new FlowLayoutPanel();
        private readonly FlatButton _btnImage;
        private readonly Label _placeholder = new Label();
        private readonly Label _hint = new Label();
        private readonly Label _inputStatus = new Label();
        private readonly FlatButton _btnSend;
        private readonly FlatButton _btnStop;
        private readonly FlatButton _btnNew;
        private readonly FlatButton _btnOpen;
        private readonly FlatButton _btnOpenPane;
        private readonly FlatButton _btnDock;
        public readonly ToggleSwitch ShowSteps = new ToggleSwitch();

        private string _title = "未选择 VS", _subtitle = "";
        private string _stateText = "未选择 VS";
        private Color _stateFg = Theme.NoneFg, _stateBg = Theme.NoneBg, _stateDot = Theme.NoneDot;
        private string _lastSig;
        private bool _lastSteps;
        private bool _sending;

        public event Action<string> SendRequested;
        public event Action StopRequested;
        public event Action NewThreadRequested;
        public event Action OpenInVsRequested;
        public event Action OpenPaneRequested;
        /// <summary>把所有 VS 的 Copilot 对话窗格切换为停靠的工具窗口。</summary>
        public event Action DockPaneRequested;
        /// <summary>按住空格 / 麦克风按钮开始说话。</summary>
        public event Action VoiceBegin;
        /// <summary>松开空格 / 按钮，结束录音并识别。</summary>
        public event Action VoiceEnd;
        /// <summary>录音中按 Esc 取消。</summary>
        public event Action VoiceCancel;

        private readonly FlatButton _btnMic;
        private readonly Timer _spaceTimer = new Timer { Interval = 350 };
        private bool _spaceHeld, _voiceHeld, _voiceEnabled;
        private string _voiceStatus;
        private Color _voiceColor;
        private float _voiceLevel;

        private static readonly Color InputBg = Color.FromArgb(24, 24, 30);

        public ChatPanel()
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

            _btnOpen = HeaderButton("在 VS 中查看", () => OpenInVsRequested?.Invoke());
            _btnNew = HeaderButton("＋  新线程", () => NewThreadRequested?.Invoke());
            _btnOpenPane = HeaderButton("打开对话助手", () => OpenPaneRequested?.Invoke());
            _btnOpenPane.Primary = true;
            _btnOpenPane.Visible = false;
            _btnDock = HeaderButton("⇲ 工具窗模式", () => DockPaneRequested?.Invoke());
            _btnDock.AccessibleName = "把所有 VS 的 Copilot 对话窗格切换为停靠的工具窗口";
            new ToolTip().SetToolTip(_btnDock, "把所有 VS 的 Copilot 对话助手切换为停靠的工具窗口，\r\n切换文档标签时不再被隐藏，始终可以监听");
            _header.Controls.AddRange(new Control[] { _btnOpen, _btnNew, _btnOpenPane, _btnDock });

            // ---- 工具栏（调试按钮由主窗体填充） ----
            _toolbarRow.Dock = DockStyle.Top;
            _toolbarRow.Height = Dpi.S(46);
            _toolbarRow.BackColor = Theme.Background;
            _toolbarRow.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Divider)) e.Graphics.DrawLine(pen, 0, _toolbarRow.Height - 1, _toolbarRow.Width, _toolbarRow.Height - 1);
            };
            Toolbar.Dock = DockStyle.Fill;
            Toolbar.WrapContents = false;
            Toolbar.AutoScroll = false;
            Toolbar.BackColor = Theme.Background;
            Toolbar.Padding = new Padding(0, Dpi.S(6), 0, 0);
            ShowSteps.Text = "显示过程步骤";
            ShowSteps.Dock = DockStyle.Right;
            ShowSteps.Width = Dpi.S(140);
            ShowSteps.BackColor = Theme.Background;
            ShowSteps.CheckedChanged += (s, e) => { _lastSig = null; };
            _toolbarRow.Controls.Add(Toolbar);
            _toolbarRow.Controls.Add(ShowSteps);

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
            _input.AccessibleName = "Copilot 消息输入框";
            _input.KeyDown += Input_KeyDown;
            _input.KeyUp += Input_KeyUp;
            _spaceTimer.Tick += (s, e) => { _spaceTimer.Stop(); if (_spaceHeld) StartVoice(); };
            Disposed += (s, e) => _spaceTimer.Dispose();
            _input.TextChanged += (s, e) => UpdateButtons();
            _input.GotFocus += (s, e) => { UpdateInputStatus(); _inputBox.Invalidate(); };
            _input.LostFocus += (s, e) =>
            {
                // 松开空格前焦点被抢走时，KeyUp 不会到达，这里兜底结束录音
                if (_spaceHeld) { _spaceHeld = false; _spaceTimer.Stop(); if (_voiceHeld && !_btnMic.Capture) EndVoice(); }
                UpdateInputStatus(); _inputBox.Invalidate();
            };
            _input.PasteImage = TryPasteImage;
            _input.AllowDrop = true;
            _input.DragEnter += (s, e) => e.Effect = !_sending && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            _input.DragDrop += (s, e) => { if (!_sending && e.Data.GetData(DataFormats.FileDrop) is string[] files) AddImageFiles(files); };
            Theme.DarkControl(_input);

            _placeholder.Text = "向 Copilot 提问或下达任务…";
            _placeholder.AutoSize = true;
            _placeholder.ForeColor = Theme.TextMuted;
            _placeholder.BackColor = InputBg;
            _placeholder.Font = _input.Font;
            _placeholder.Location = new Point(Dpi.S(14), Dpi.S(12));
            _placeholder.Cursor = Cursors.IBeam;
            _placeholder.Click += (s, e) => _input.Focus();

            var actions = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(38), BackColor = InputBg, Padding = new Padding(0, Dpi.S(4), 0, 0) };
            _btnImage = new FlatButton { Text = "＋ 图片", Dock = DockStyle.Left, Width = Dpi.S(76), Ghost = true };
            _btnImage.Click += (s, e) => ChooseImages();
            _btnMic = new FlatButton { Text = "🎙 按住说话", Dock = DockStyle.Left, Width = Dpi.S(104), Ghost = true, Visible = false, AccessibleName = "按住说话（豆包语音识别）" };
            _btnMic.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) StartVoice(); };
            _btnMic.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left && _voiceHeld) EndVoice(); };
            _btnSend = new FlatButton { Text = "发送  ➤", Primary = true, Dock = DockStyle.Right, Width = Dpi.S(92) };
            _btnSend.Click += (s, e) => DoSend();
            _btnStop = new FlatButton { Text = "■  停止", Tint = Theme.Danger, Dock = DockStyle.Right, Width = Dpi.S(80) };
            _btnStop.Click += (s, e) => StopRequested?.Invoke();
            var gap = new Panel { Dock = DockStyle.Right, Width = Dpi.S(8), BackColor = InputBg };
            _hint.Dock = DockStyle.Fill;
            _hint.Text = "Enter 发送 · Shift+Enter 换行";
            _hint.ForeColor = Theme.TextMuted;
            _hint.BackColor = InputBg;
            _hint.Font = Theme.Small;
            _hint.TextAlign = ContentAlignment.MiddleLeft;
            _hint.AutoEllipsis = true;
            actions.Controls.Add(_hint);
            actions.Controls.Add(_btnStop);
            actions.Controls.Add(gap);
            actions.Controls.Add(_btnSend);
            actions.Controls.Add(_btnMic);
            actions.Controls.Add(_btnImage);

            _imagePreview.Dock = DockStyle.Top;
            _imagePreview.Height = Dpi.S(72);
            _imagePreview.WrapContents = false;
            _imagePreview.AutoScroll = true;
            _imagePreview.Visible = false;
            _imagePreview.BackColor = Theme.Background;
            _inputBox.Controls.Add(_placeholder);
            _inputBox.Controls.Add(_input);
            _inputBox.Controls.Add(actions);
            _placeholder.BringToFront();
            _inputArea.Controls.Add(_inputBox);
            _inputArea.Controls.Add(_imagePreview);
            _inputArea.Controls.Add(_inputStatus);

            Controls.Add(_transcriptHost);
            Controls.Add(_inputArea);
            Controls.Add(_toolbarRow);
            Controls.Add(_header);

            Resize += (s, e) => LayoutContent();
            LayoutContent();
            SetEmpty("请在左侧选择一个 VS");
            UpdateButtons();
        }

        private static void SetDoubleBuffered(Control c) =>
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(c, true, null);

        private FlatButton HeaderButton(string text, Action a)
        {
            var b = new FlatButton { Text = text, Height = Dpi.S(32) };
            b.Width = TextRenderer.MeasureText(text, b.Font).Width + Dpi.S(28);
            b.Click += (s, e) => a();
            return b;
        }

        /// <summary>对话内容居中，限制最大阅读宽度。</summary>
        private void LayoutContent()
        {
            int side = Math.Max(Dpi.S(28), (Width - Dpi.S(1000)) / 2);
            _transcriptHost.Padding = new Padding(side, Dpi.S(12), side - Dpi.S(8) < 0 ? 0 : side - Dpi.S(8), Dpi.S(4));
            _inputArea.Padding = new Padding(side, Dpi.S(8), side, Dpi.S(18));
            _header.Padding = new Padding(Dpi.S(28), 0, Dpi.S(28), 0);
            _toolbarRow.Padding = new Padding(Dpi.S(22), 0, Dpi.S(28), 0);
            LayoutHeaderButtons();
            _header.Invalidate();
        }

        private void LayoutHeaderButtons()
        {
            int y = (_header.Height - _btnOpen.Height) / 2, right = _header.Width - Dpi.S(28);
            _btnOpen.Location = new Point(right - _btnOpen.Width, y);
            _btnNew.Location = new Point(_btnOpen.Left - Dpi.S(8) - _btnNew.Width, y);
            _btnOpenPane.Location = new Point(_btnNew.Left - Dpi.S(8) - _btnOpenPane.Width, y);
            _btnDock.Location = new Point((_paneMissing ? _btnOpenPane.Left : _btnNew.Left) - Dpi.S(8) - _btnDock.Width, y);
        }

        #region 状态 / 渲染

        public bool HasTarget { get; private set; }
        public bool Busy { get; private set; }

        public void SetTarget(string name, string subtitle)
        {
            if (HasTarget == (name != null) && _title == (name ?? "未选择 VS") && _subtitle == (subtitle ?? "")) return;
            HasTarget = name != null;
            _title = name ?? "未选择 VS";
            _subtitle = subtitle ?? "";
            _header.Invalidate();
            UpdateButtons();
        }

        public void SetState(string text, Color fg, Color bg, Color dot, bool busy)
        {
            if (_stateText == text && _stateFg == fg && Busy == busy) return;
            _stateText = text; _stateFg = fg; _stateBg = bg; _stateDot = dot; Busy = busy;
            _header.Invalidate();
            UpdateButtons();
        }

        public void SetPaneMissing(bool missing)
        {
            if (_paneMissing == missing) return;
            _paneMissing = missing;
            _btnOpenPane.Visible = missing;
            LayoutHeaderButtons();
            _header.Invalidate();
        }

        private bool _paneMissing;

        public void SetDocking(bool docking)
        {
            _btnDock.Enabled = !docking;
            _btnDock.Text = docking ? "切换中…" : "⇲ 工具窗模式";
        }

        public void SetSending(bool sending)
        {
            _sending = sending;
            _btnSend.Text = sending ? "发送中…" : "发送  ➤";
            _input.ReadOnly = sending;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            _btnSend.Enabled = HasTarget && !Busy && !_sending && (!string.IsNullOrWhiteSpace(_input.Text) || _images.Count > 0);
            _btnStop.Enabled = HasTarget && Busy;
            _btnNew.Enabled = HasTarget && !Busy && !_sending;
            _btnOpen.Enabled = HasTarget;
            _btnOpenPane.Enabled = HasTarget;
            _btnImage.Enabled = !_sending && _images.Count < ChatImage.MaxCount;
            _imagePreview.Enabled = !_sending;
            UpdateInputStatus();
        }

        private void UpdateInputStatus()
        {
            _placeholder.Visible = _input.TextLength == 0 && !_input.Focused && _voiceStatus == null;
            _placeholder.Text = HasTarget ? "向 Copilot 提问或下达任务…" : "请先在左侧选择 VS，可先输入草稿…";
            if (_voiceStatus != null)
            {
                _inputStatus.Text = _voiceStatus;
                _inputStatus.ForeColor = _voiceColor;
                return;
            }
            string state = !HasTarget ? "未选择 VS · 请选择发送目标" :
                _sending ? "正在发送，请稍候…" :
                Busy ? "Copilot 正在处理 · 可编辑草稿，完成后发送" :
                _input.Focused ? "正在输入" : _input.TextLength > 0 || _images.Count > 0 ? "草稿待发送" : "等待输入";
            _inputStatus.Text = state + " · " + _input.TextLength + " 字" +
                (_images.Count > 0 ? " · " + _images.Count + " 张图片（发送时短暂切换 VS）" : " · 可粘贴或拖入图片");
            _inputStatus.ForeColor = _sending || Busy ? Theme.BusyFg : _input.Focused ? Theme.AccentText : Theme.TextSecondary;
        }

        private void Header_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            int x = Dpi.S(28);
            int right = _btnDock.Left - Dpi.S(16);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

            int pillMax = Dpi.S(240);
            var tsz = TextRenderer.MeasureText(g, _title, Theme.Big, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int titleW = Math.Min(tsz.Width, Math.Max(Dpi.S(60), right - x - pillMax - Dpi.S(12)));
            TextRenderer.DrawText(g, _title, Theme.Big, new Rectangle(x, Dpi.S(10), titleW, Dpi.S(26)), HasTarget ? Theme.Text : Theme.TextMuted, flags | TextFormatFlags.VerticalCenter);
            if (HasTarget)
                Theme.DrawPill(g, x + titleW + Dpi.S(12), Dpi.S(12), Math.Max(0, Math.Min(pillMax, right - x - titleW - Dpi.S(12))), _stateText, _stateBg, _stateFg, _stateDot);
            if (!string.IsNullOrEmpty(_subtitle))
                TextRenderer.DrawText(g, _subtitle, Theme.Small, new Rectangle(x, Dpi.S(38), Math.Max(0, right - x), Dpi.S(18)), Theme.TextMuted,
                    flags | TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter);
        }

        private void InputBox_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, _inputBox.Width - 1.5f, _inputBox.Height - 1.5f);
            Theme.FillRound(g, InputBg, r, Dpi.S(12));
            if (_voiceHeld)
            {
                var red = Color.FromArgb(239, 68, 68);
                using (var p = Theme.RoundRect(r, Dpi.S(12)))
                using (var pen = new Pen(Color.FromArgb(120 + (int)(135 * _voiceLevel), red), 1.5f + 2f * _voiceLevel))
                    g.DrawPath(pen, p);
                return;
            }
            using (var p = Theme.RoundRect(r, Dpi.S(12)))
            using (var pen = new Pen(_input.Focused ? Theme.AccentBorder : Theme.Border, _input.Focused ? 1.5f : 1f))
                g.DrawPath(pen, p);
        }

        public void SetEmpty(string message)
        {
            _lastSig = null;
            _transcript.SetEmpty(message);
        }

        /// <summary>在最后一条 Copilot 回答下显示“任务已完成”标记；null 表示隐藏。</summary>
        public void SetCompletion(string text) => _transcript.SetCompletion(text);

        public void SetActivity(string activity, string pending) => _transcript.SetActivity(activity, pending);

        public void Render(ChatTranscript transcript)
        {
            bool steps = ShowSteps.Checked;
            if (transcript.Signature == _lastSig && steps == _lastSteps) return;
            _lastSig = transcript.Signature;
            _lastSteps = steps;
            _transcript.Render(transcript, steps);
        }

        #endregion

        #region 输入

        public IReadOnlyList<ChatImage> Images
        {
            get => _images.ToArray();
            set
            {
                var images = value?.ToArray() ?? Array.Empty<ChatImage>();
                if (images.Length > ChatImage.MaxCount || images.Any(image => image == null))
                    throw new ArgumentException("Invalid image attachments.", nameof(value));
                _images.Clear();
                _images.AddRange(images);
                RefreshImages();
            }
        }

        private void ChooseImages()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "添加图片（最多 4 张，每张 10 MB）",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
                Multiselect = true
            })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddImageFiles(dialog.FileNames);
        }

        private void AddImageFiles(IEnumerable<string> files)
        {
            try
            {
                var paths = files.ToArray();
                if (_images.Count + paths.Length > ChatImage.MaxCount)
                    throw new InvalidDataException("每条消息最多添加 4 张图片。");
                var added = paths.Select(ChatImage.FromFile).ToArray();
                _images.AddRange(added);
                RefreshImages();
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is ExternalException || ex is OutOfMemoryException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(this, "无法添加图片：" + ex.Message, "图片附件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool TryPasteImage()
        {
            if (_sending) return true;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    if (_images.Count >= ChatImage.MaxCount) throw new InvalidDataException("每条消息最多添加 4 张图片。");
                    using (var image = Clipboard.GetImage())
                        _images.Add(ChatImage.FromImage(image, "截图 " + DateTime.Now.ToString("HH-mm-ss") + ".png"));
                    RefreshImages();
                    return true;
                }
                if (Clipboard.ContainsFileDropList())
                {
                    AddImageFiles(Clipboard.GetFileDropList().Cast<string>());
                    return true;
                }
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is ExternalException || ex is OutOfMemoryException)
            {
                MessageBox.Show(this, "无法粘贴图片：" + ex.Message, "图片附件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
        }

        private void RefreshImages()
        {
            _imagePreview.SuspendLayout();
            try
            {
                while (_imagePreview.Controls.Count > 0) _imagePreview.Controls[0].Dispose();
                foreach (var image in _images)
                {
                    var tile = new Panel { Size = new Size(Dpi.S(156), Dpi.S(60)), BackColor = InputBg, Margin = new Padding(0, 0, Dpi.S(8), 0) };
                    var preview = new PictureBox { Dock = DockStyle.Left, Width = Dpi.S(56), SizeMode = PictureBoxSizeMode.Zoom, Image = image.CreateThumbnail(Dpi.S(60)) };
                    preview.Disposed += (s, e) => preview.Image?.Dispose();
                    var remove = new FlatButton { Text = "×", Dock = DockStyle.Right, Width = Dpi.S(24), Ghost = true, AccessibleName = "移除图片 " + image.Name };
                    remove.Click += (s, e) => { _images.Remove(image); RefreshImages(); };
                    var name = new Label { Dock = DockStyle.Fill, Text = image.Name, Font = Theme.Small, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
                    tile.Controls.Add(name);
                    tile.Controls.Add(remove);
                    tile.Controls.Add(preview);
                    _imagePreview.Controls.Add(tile);
                }
                _imagePreview.Visible = _images.Count > 0;
                _inputArea.Height = Dpi.S(_images.Count > 0 ? 256 : 184);
            }
            finally { _imagePreview.ResumeLayout(); }
            UpdateButtons();
        }

        private sealed class ImageInputBox : TextBox
        {
            public Func<bool> PasteImage;
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0302 && PasteImage != null && PasteImage()) return;
                base.WndProc(ref m);
            }
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (_voiceEnabled && e.KeyCode == Keys.Space && e.Modifiers == Keys.None && !_input.ReadOnly)
            {
                // 短按输入空格，按住超过 350ms 开始语音；自动重复的 KeyDown 全部吞掉
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (!_spaceHeld && !_voiceHeld) { _spaceHeld = true; _spaceTimer.Start(); }
                return;
            }
            if (_spaceHeld && _spaceTimer.Enabled)
            {
                // 快速连打（空格还没松开就按了下一个键）时立即补上空格
                _spaceTimer.Stop();
                _spaceHeld = false;
                _input.SelectedText = " ";
            }
            if (_voiceHeld && e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _spaceHeld = false;
                EndVoice(true);
                return;
            }
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control && !e.Alt)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                DoSend();
            }
        }

        private void Input_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space || !_spaceHeld) return;
            _spaceHeld = false;
            e.Handled = true;
            if (_spaceTimer.Enabled)
            {
                _spaceTimer.Stop();
                _input.SelectedText = " ";
            }
            else if (_voiceHeld) EndVoice();
        }

        /// <summary>是否启用按住空格语音输入（显示麦克风按钮）。</summary>
        public bool VoiceInputEnabled
        {
            get => _voiceEnabled;
            set
            {
                _voiceEnabled = value;
                _btnMic.Visible = value;
                if (!value && _voiceHeld) EndVoice();
            }
        }

        private void StartVoice()
        {
            if (!_voiceEnabled || _voiceHeld || _sending) return;
            _voiceHeld = true;
            _btnMic.Text = "● 松开结束";
            _btnMic.Tint = Color.FromArgb(239, 68, 68);
            _inputBox.Invalidate();
            VoiceBegin?.Invoke();
        }

        private void EndVoice(bool cancel = false)
        {
            if (!_voiceHeld) return;
            _voiceHeld = false;
            _voiceLevel = 0;
            _btnMic.Text = "🎙 按住说话";
            _btnMic.Tint = null;
            _inputBox.Invalidate();
            if (cancel) VoiceCancel?.Invoke();
            else VoiceEnd?.Invoke();
        }

        /// <summary>语音状态提示（替换输入状态栏）；null 恢复默认。</summary>
        public void SetVoiceStatus(string text, Color color)
        {
            _voiceStatus = text;
            _voiceColor = color;
            UpdateInputStatus();
        }

        public void SetVoiceLevel(float level)
        {
            if (!_voiceHeld) return;
            _voiceLevel = _voiceLevel * 0.5f + Math.Max(0f, Math.Min(1f, level)) * 0.5f;
            _inputBox.Invalidate();
        }

        /// <summary>识别被外部终止（如达到时长上限）时复位按钮。</summary>
        public void ForceEndVoice() => EndVoice();

        /// <summary>在光标处插入识别文本。</summary>
        public void InsertVoiceText(string text)
        {
            if (string.IsNullOrEmpty(text) || _input.ReadOnly) return;
            _input.SelectedText = text;
            _input.Focus();
        }

        private void DoSend()
        {
            var text = _input.Text.Trim();
            if ((text.Length == 0 && _images.Count == 0) || !HasTarget || Busy || _sending) return;
            SendRequested?.Invoke(text);
        }

        /// <summary>切换 VS 时重置渲染状态，下一次 Render 必定重绘并滚动到底部。</summary>
        public void ResetView()
        {
            _lastSig = null;
            _transcript.ResetView();
        }

        /// <summary>输入框草稿（切换 VS 时保存 / 恢复）。</summary>
        public string InputText
        {
            get => _input.Text;
            set
            {
                _input.Text = value ?? "";
                _input.SelectionStart = _input.TextLength;
            }
        }

        public void ClearInput()
        {
            _input.Clear();
            Images = Array.Empty<ChatImage>();
            _input.Focus();
        }

        public void FocusInput() => _input.Focus();

        public string Hint { get => _hint.Text; set => _hint.Text = value; }

        #endregion

    }
}
