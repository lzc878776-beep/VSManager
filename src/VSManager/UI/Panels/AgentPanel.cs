using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>主界面 AI 总控助手：与 VS 对话界面同样的标题栏 / 快捷栏 / 居中对话记录 / 输入区。</summary>
    public class AgentPanel : Panel
    {
        private static readonly Color InputBg = Theme.Bubble;
        private static readonly Font BadgeFont = new Font(Theme.FontName, 13F);

        // 快捷任务：由 AI 用 send_task 派给合适的 VS，Copilot 在其仓库内执行 git 操作。
        // Quick tasks: the AI dispatches them via send_task; Copilot runs the git steps in that repository.
        internal const string SyncGitPrompt = "用 send_task 给解决方案位于 git 仓库的 VS 发布任务（同一仓库只派给一个 VS，优先空闲的，不必询问我）。任务内容："
            + "在当前解决方案所在的 git 仓库中同步主分支，使最终本地主分支与远程主分支（以远程默认分支 main/master 为准）代码完全一致："
            + "1) git fetch --prune；2) 若工作区有未提交修改，先提交，不得丢弃；3) 切换到主分支，把远程主分支的新提交以 rebase 方式并入；"
            + "4) 若本地主分支有未推送的提交，推送到远程；5) 最后确认 git status 干净，且本地主分支与 origin 主分支指向同一提交。"
            + "禁止 force push、reset --hard 或删除任何分支；遇到冲突先尝试正确解决，无法确定时停止并说明。完成后汇总每个仓库的结果。";

        internal const string MergeWorktreesPrompt = "用 send_task 给解决方案位于 git 仓库的 VS 发布任务（同一仓库只派给一个 VS，优先空闲的，不必询问我）。任务内容："
            + "用 git worktree list 列出该仓库的所有 worktree，把每个 worktree 所在分支的改动合并进本地主分支（main/master）："
            + "1) 各 worktree 中若有未提交修改，先在该 worktree 内提交，不得丢弃；2) 在主工作区切换到主分支，依次 git merge 各 worktree 分支，已合并的跳过；"
            + "3) 解决冲突后生成解决方案，确保编译通过；4) 不推送远程，不删除 worktree 或分支。"
            + "无法确定如何解决冲突时停止并说明。完成后列出合并了哪些分支及结果。";

        private static readonly (string Text, string Prompt)[] QuickPrompts =
        {
            ("🔄 同步 git", SyncGitPrompt),
            ("🔀 worktree 并入主分支", MergeWorktreesPrompt),
        };

        // 笔记助手的快捷指令 / Quick prompts of the note assistant
        private static readonly (string Text, string Prompt)[] NotePrompts =
        {
            ("📝 总结 / Summarize", "请先读取当前笔记，再用要点总结它的内容（保留关键事实与结论）。/ Read the current note first, then summarize it as bullet points (keep key facts and conclusions)."),
            ("✅ 待办 / To-dos", "请读取当前笔记，把其中的待办事项整理成 Markdown 任务清单（- [ ] 形式），不要编造新任务。/ Read the current note and list its to-dos as a Markdown checklist (- [ ]); do not invent tasks."),
            ("✍ 润色 / Polish", "请读取当前笔记，在不改变事实和结构的前提下润色文字，直接给出润色后的完整 Markdown。/ Read the current note and polish the wording without changing facts or structure; reply with the full polished Markdown."),
        };

        private readonly Panel _header = new Panel();
        private readonly Panel _toolbarRow = new Panel();
        private readonly FlowLayoutPanel _toolbar = new FlowLayoutPanel();
        private readonly Panel _transcriptHost = new Panel();
        private readonly TranscriptView _transcript = new TranscriptView { AssistantLabel = "AI 助手" };
        private readonly Panel _inputArea = new Panel();
        private readonly Label _inputStatus = new Label();
        private readonly Panel _inputBox = new Panel();
        private readonly AttachInputBox _input = new AttachInputBox();
        private readonly FlowLayoutPanel _chips = new FlowLayoutPanel();
        private readonly List<AttachmentRef> _pending = new List<AttachmentRef>();
        private readonly Label _placeholder = new Label();
        private readonly FlatButton _btnSend, _btnStop, _btnClear, _btnSettings, _btnAttach;
        private readonly ReleaseLevelSlider _releaseSlider = new ReleaseLevelSlider();
        private readonly Timer _renderTimer = new Timer { Interval = 60 };
        private readonly Timer _pulse = new Timer { Interval = 400 };
        private readonly ToolTip _tips = new ThemedToolTip();
        private AgentService _agent;
        private int _dots;
        /// <summary>笔记助手模式：隐藏放行滑块与 @ 提及，快捷指令改为笔记操作。/ Note-assistant mode: no release slider or @ mentions; quick prompts work on notes.</summary>
        private readonly bool _noteMode;
        private readonly FlatButton _btnInsert;

        /// <summary>笔记模式下点击「插入到笔记」，参数为最新一条回复的正文。/ Raised in note mode by "Insert into note" with the latest reply text.</summary>
        public event Action<string> InsertRequested;

        public event Action SettingsRequested;
        /// <summary>用户拖动顶栏滑块改变放行等级。/ The user changed the release level with the header slider.</summary>
        public event Action<ReleaseLevel> ReleaseLevelChanged;
        private VsMentionInput _mentions;
        private string _mentionStatus;
        public Func<string, AttachmentRef[], MentionSubmission> MentionRequested { get; set; }
        private VsMentionSession _mentionSession;
        private Func<VsMentionTarget[]> _mentionTargets;
        public void BindMentions(VsMentionSession session, Func<VsMentionTarget[]> targets)
        {
            _mentions?.Dispose();
            _mentionSession = session; _mentionTargets = targets;
            _mentions = new VsMentionInput(_input, session, targets);
        }
        public void RefreshMentions() { if (_mentions?.IsOpen == true) _mentions.Refresh(); }

        public AgentPanel() : this(false) { }

        /// <summary>noteMode 为 true 时作为笔记助手面板。/ Acts as the note-assistant panel when noteMode is true.</summary>
        public AgentPanel(bool noteMode)
        {
            _noteMode = noteMode;
            if (noteMode) _transcript.AssistantLabel = AgentService.NoteAgentTitle;
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
            _releaseSlider.ValueChanged += () =>
            {
                _tips.SetToolTip(_releaseSlider, SliderTip(_releaseSlider.Value));
                ReleaseLevelChanged?.Invoke(_releaseSlider.Value);
            };
            _tips.SetToolTip(_releaseSlider, SliderTip(_releaseSlider.Value));
            _header.Controls.Add(_releaseSlider);

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
            foreach (var q in noteMode ? NotePrompts : QuickPrompts)
            {
                var b = new FlatButton { Text = q.Text, Ghost = true, Height = Dpi.S(30), Margin = new Padding(Dpi.S(6), 0, 0, 0) };
                b.Width = TextRenderer.MeasureText(q.Text, b.Font).Width + Dpi.S(26);
                string prompt = q.Prompt;
                b.Click += (s, e) => Send(prompt);
                _tips.SetToolTip(b, prompt);
                _toolbar.Controls.Add(b);
            }
            if (noteMode)
            {
                const string insertText = "📥 插入到笔记 / Insert into note";
                _btnInsert = new FlatButton { Text = insertText, Height = Dpi.S(30), Margin = new Padding(Dpi.S(12), 0, 0, 0) };
                _btnInsert.Width = TextRenderer.MeasureText(insertText, _btnInsert.Font).Width + Dpi.S(26);
                _btnInsert.Click += (s, e) => { string reply = LastReply(); if (reply.Length > 0) InsertRequested?.Invoke(reply); };
                _tips.SetToolTip(_btnInsert, "把最新一条回复写入当前笔记（编辑过则插入光标处，否则追加到末尾）\nWrite the latest reply into the current note (at the caret if you edited it, otherwise at the end)");
                _toolbar.Controls.Add(_btnInsert);
            }
            _toolbarRow.Controls.Add(_toolbar);

            // ---- 对话记录 ----
            _transcriptHost.Dock = DockStyle.Fill;
            _transcriptHost.BackColor = Theme.Background;
            _transcript.Dock = DockStyle.Fill;
            _transcript.AttachmentClicked += id =>
            {
                string error = AttachmentStore.Reveal(id);
                if (error != null) _inputStatus.Text = "📎 " + error;
            };
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
            SetDoubleBuffered(_inputStatus);

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
            _input.PasteAttachment = TryPasteAttachment;
            _input.KeyDown += Input_KeyDown;
            _input.TextChanged += (s, e) => { _mentionStatus = null; UpdateUi(); };
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
            _btnAttach = new FlatButton { Text = "＋ 附件 / Attach", Ghost = true, Dock = DockStyle.Left, Width = Dpi.S(118) };
            _btnAttach.Click += (s, e) => ChooseFiles();
            _tips.SetToolTip(_btnAttach, "添加图片或文件（也可粘贴截图或拖入文件）\n附件保存在 %APPDATA%\\VSManager\\attachments\\，只在本机\nAdd images or files (you can also paste a screenshot or drop files)\nStored in %APPDATA%\\VSManager\\attachments\\ on this computer only");
            var hint = new Label
            {
                Dock = DockStyle.Fill, Text = "Enter 发送 · Shift+Enter 换行 · Esc 停止 · 可粘贴截图 / 拖入文件", ForeColor = Theme.TextMuted, BackColor = InputBg,
                Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false
            };
            actions.Controls.Add(hint);
            actions.Controls.Add(_btnAttach);
            actions.Controls.Add(_btnStop);
            actions.Controls.Add(gap);
            actions.Controls.Add(_btnSend);

            _inputBox.Controls.Add(_placeholder);
            _inputBox.Controls.Add(_input);
            _inputBox.Controls.Add(actions);
            _placeholder.BringToFront();
            _chips.Dock = DockStyle.Top;
            _chips.Height = Dpi.S(36);
            _chips.WrapContents = false;
            _chips.AutoScroll = true;
            _chips.BackColor = Theme.Background;
            _chips.Padding = new Padding(0, Dpi.S(2), 0, Dpi.S(2));
            _chips.Visible = false;
            _inputArea.Controls.Add(_inputBox);
            _inputArea.Controls.Add(_chips);
            _inputArea.Controls.Add(_inputStatus);
            foreach (var target in new Control[] { _inputArea, _inputBox, _input, _chips })
            {
                target.AllowDrop = true;
                target.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && _agent?.Running != true ? DragDropEffects.Copy : DragDropEffects.None;
                target.DragDrop += (s, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files); };
            }

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
            var s = _agent?.CurrentSettings;
            if (s != null) SetReleaseLevel(s.ReleaseLevel);
            RenderNow();
            UpdateUi();
        }

        /// <summary>同步滑块显示的放行等级（不触发 ReleaseLevelChanged）。/ Syncs the slider's release level (does not raise ReleaseLevelChanged).</summary>
        public void SetReleaseLevel(ReleaseLevel level)
        {
            _releaseSlider.SetValueSilently(level);
            _tips.SetToolTip(_releaseSlider, SliderTip(level));
        }

        private static string SliderTip(ReleaseLevel level) =>
            ReleaseLevels.Describe(level) + "\r\n拖动或点击切换（←/→ 键也可）/ Drag or click to switch (←/→ keys work too)";

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
            _releaseSlider.Size = new Size(Dpi.S(216), Dpi.S(48));
            _releaseSlider.Location = new Point(_btnClear.Left - Dpi.S(16) - _releaseSlider.Width, (_header.Height - _releaseSlider.Height) / 2);
            // 窄窗口时为标题让位 / Hide on narrow windows to keep the title readable
            _releaseSlider.Visible = !_noteMode && _releaseSlider.Left > Dpi.S(260);
        }

        private void RenderNow()
        {
            if (_agent == null) return;
            if (_agent.Transcript.Messages.Count == 0)
            {
                _transcript.SetEmpty(!_agent.Configured
                    ? "尚未配置 AI 模型\n\n点击右上角「⚙ 模型设置」，选择 DeepSeek 并填写 API Key 后即可使用\n（在 platform.deepseek.com 创建 Key）"
                    : _noteMode
                    ? "我是笔记助手，可以阅读并整理你的笔记：\n\n· 总结这篇笔记\n· 找出所有提到发布的笔记\n· 把这篇改写成周报\n\nI am the note assistant: ask me to summarize, search or rewrite notes."
                    : "我是 AI 总控助手，可以统一管理所有 VS：\n\n· 各个 VS 现在都在做什么？\n· 让 2 号 VS 修复编译错误，完成后告诉我\n· 给所有空闲的 VS 生成解决方案");
                return;
            }
            _transcript.Render(_agent.Transcript, true);
        }

        private void Send(string text)
        {
            text = (text ?? "").Trim();
            bool fromInput = text == _input.Text.Trim();
            var files = fromInput ? _pending.ToArray() : new AttachmentRef[0];
            if (fromInput && !_noteMode && VsMentionSession.HasIntent(text))
            {
                if (TryRouteMentionToAgent(text, files)) return;
                MentionSubmission result;
                try { result = MentionRequested?.Invoke(text, files) ?? new MentionSubmission(null, VsMentionSession.ChooseError); }
                catch (Exception ex) { result = new MentionSubmission(null, "提及任务未接纳；草稿已保留 / Mention task not accepted; draft retained: " + ex.Message); }
                if (result.Accepted) { _input.Clear(); _pending.Clear(); RefreshChips(); }
                _mentionStatus = result.Message;
                _inputStatus.Text = _mentionStatus;
                return;
            }
            if (_agent == null) return;
            if (_agent.Running || (text.Length == 0 && files.Length == 0)) return;
            if (!_agent.Configured) { SettingsRequested?.Invoke(); return; }
            if (fromInput) { _input.Clear(); _pending.Clear(); RefreshChips(); }
            _ = _agent.RunAsync(text, null, files);
            FocusInput();
        }

        /// <summary>
        /// 模型可用时，@ 只告诉 AI 目标 VS，由 AI 整理并用 send_task 发布；模型不可用时才直接入队。
        /// When the model is available, @ only tells the AI the target VS and the AI publishes via send_task; enqueue directly only without a model.
        /// </summary>
        private bool TryRouteMentionToAgent(string text, AttachmentRef[] files)
        {
            if (_agent == null || !_agent.Configured || _agent.Running || _mentionSession == null) return false;
            var chips = _mentionSession.Chips(text).ToArray();
            if (chips.Length == 0 || chips.Length != VsMentionSession.TokenStarts(text).Count()) return false;
            var live = _mentionTargets?.Invoke() ?? new VsMentionTarget[0];
            var display = new StringBuilder(text);
            var names = new List<string>();
            foreach (var chip in chips.AsEnumerable().Reverse())
            {
                if (!_mentionSession.TryGetTarget(text.Substring(chip.Start, chip.Length), out var chosen)) return false;
                var now = live.FirstOrDefault(t => t.InstanceKey == chosen.InstanceKey
                    && string.Equals(t.SolutionPath, chosen.SolutionPath, StringComparison.OrdinalIgnoreCase));
                if (now == null) return false;
                string label = "#" + now.Number + " " + now.Name;
                display.Remove(chip.Start, chip.Length).Insert(chip.Start, "@" + label);
                if (!names.Contains(label)) names.Insert(0, label);
            }
            string shown = display.ToString().Trim();
            string prompt = shown + "\n\n[用户用 @ 指定了目标 VS / Target VS chosen via @：" + string.Join("、", names) + "] "
                + "请把这条需求用 send_task 发布给该 VS（vs 参数填编号），任务文字按规则只做语言梳理；需要附件时 attachments 填 \"last\"。如果只是咨询或意图不完整，先向用户确认。"
                + " / Publish this request to that VS with send_task (use its number for vs), language cleanup only; pass attachments \"last\" when needed. If it is only a question or incomplete, confirm with the user first.";
            _input.Clear(); _pending.Clear(); RefreshChips();
            _mentionStatus = "已交给 AI 发布到 " + string.Join("、", names) + " / Handed to the AI to publish";
            _inputStatus.Text = _mentionStatus;
            _ = _agent.RunAsync(prompt, shown, files);
            FocusInput();
            return true;
        }

        private int MaxCount => AttachmentPolicy.ClampMaxCount(_agent?.CurrentSettings?.AttachmentMaxCount ?? 0);
        private int MaxFileMB => AttachmentPolicy.ClampMaxFileMB(_agent?.CurrentSettings?.AttachmentMaxFileMB ?? 0);

        private void ChooseFiles()
        {
            string images = string.Join(";", AttachmentPolicy.ImageExtensions.Select(x => "*" + x));
            string texts = string.Join(";", AttachmentPolicy.TextExtensions.Select(x => "*" + x));
            string docs = string.Join(";", AttachmentPolicy.DocumentExtensions.Select(x => "*" + x));
            using (var dialog = new OpenFileDialog
            {
                Title = $"添加附件（最多 {MaxCount} 个，每个 {MaxFileMB} MB）/ Add attachments (up to {MaxCount}, {MaxFileMB} MB each)",
                Filter = $"支持的文件 / Supported|{images};{texts};{docs}|图片 / Images|{images}|文本与代码 / Text and code|{texts}|文档 / Documents|{docs}",
                Multiselect = true
            })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
        }

        /// <summary>逐个导入文件；超限或不支持的文件跳过并集中提示。/ Imports files one by one; over-limit or unsupported ones are skipped and reported together.</summary>
        private void AddFiles(IEnumerable<string> files)
        {
            if (_agent == null || _agent.Running) return;
            var errors = new List<string>();
            foreach (string file in files ?? new string[0])
            {
                if (Directory.Exists(file)) { errors.Add("不支持文件夹：" + Path.GetFileName(file) + " / Folders are not supported"); continue; }
                try { _pending.Add(AttachmentStore.Import(file, _pending.Count, MaxCount, MaxFileMB)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    errors.Add(ex.Message);
                }
            }
            RefreshChips();
            if (errors.Count > 0) ShowAttachError(errors);
        }

        private void ShowAttachError(IEnumerable<string> errors) =>
            MessageBox.Show(this, string.Join("\r\n", errors), "附件 / Attachments", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        /// <summary>粘贴：剪贴板图片保存为 PNG 附件，文件列表按文件导入；其他内容按普通文字粘贴。/ Paste: clipboard images become PNG attachments, file lists are imported; anything else pastes as text.</summary>
        private bool TryPasteAttachment()
        {
            if (_agent == null || _agent.Running) return false;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    using (var image = Clipboard.GetImage())
                    {
                        if (image == null) return false;
                        string name = "截图-" + DateTime.Now.ToString("HHmmss") + ".png";
                        var png = ChatImage.FromImage(image, name).PngBytes();
                        _pending.Add(AttachmentStore.SaveBytes(png, name, _pending.Count, MaxCount, MaxFileMB));
                    }
                    RefreshChips();
                    return true;
                }
                if (Clipboard.ContainsFileDropList())
                {
                    AddFiles(Clipboard.GetFileDropList().Cast<string>().ToArray());
                    return true;
                }
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is System.Runtime.InteropServices.ExternalException || ex is OutOfMemoryException || ex is UnauthorizedAccessException)
            {
                ShowAttachError(new[] { "无法粘贴附件 / Cannot paste the attachment：" + ex.Message });
                return true;
            }
        }

        private void RefreshChips()
        {
            _chips.SuspendLayout();
            try
            {
                while (_chips.Controls.Count > 0) _chips.Controls[0].Dispose();
                foreach (var a in _pending.ToArray())
                {
                    string text = (a.IsImage ? "🖼 " : "📄 ") + a.Name + " · " + AttachmentPolicy.FormatSize(a.Size);
                    var chip = new Panel { Height = Dpi.S(28), BackColor = Theme.Elevated, Margin = new Padding(0, 0, Dpi.S(6), 0) };
                    var label = new Label
                    {
                        Dock = DockStyle.Fill, Text = text, Font = Theme.Small, ForeColor = Theme.TextSecondary, BackColor = Theme.Elevated,
                        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Cursor = Cursors.Hand,
                        Padding = new Padding(Dpi.S(6), 0, 0, 0)
                    };
                    label.Click += (s, e) => AttachmentStore.Reveal(a.Id);
                    _tips.SetToolTip(label, a.Describe() + "\n点击在资源管理器中定位 / Click to locate in Explorer");
                    var remove = new FlatButton { Text = "×", Dock = DockStyle.Right, Width = Dpi.S(24), Ghost = true, AccessibleName = "移除附件 / Remove attachment " + a.Name };
                    remove.Click += (s, e) => { _pending.Remove(a); RefreshChips(); };
                    chip.Width = Math.Min(Dpi.S(260), TextRenderer.MeasureText(text, Theme.Small).Width + Dpi.S(44));
                    chip.Controls.Add(label);
                    chip.Controls.Add(remove);
                    _chips.Controls.Add(chip);
                }
                _chips.Visible = _pending.Count > 0;
                _inputArea.Height = Dpi.S(_pending.Count > 0 ? 222 : 184);
            }
            finally { _chips.ResumeLayout(); }
            UpdateUi();
        }

        /// <summary>拦截粘贴的输入框：可以把剪贴板中的图片或文件转为附件。/ Input box that intercepts paste so clipboard images or files become attachments.</summary>
        private sealed class AttachInputBox : TextBox
        {
            public Func<bool> PasteAttachment;
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0302 && PasteAttachment != null && PasteAttachment()) return;
                base.WndProc(ref m);
            }
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (_mentions?.HandleKeyDown(e) == true) return;
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

        private string _headerState;

        private void UpdateUi()
        {
            bool running = _agent?.Running == true;
            // 禁用有焦点的按钮会把焦点推给下一个控件；记下来并把焦点留在输入框。
            // Disabling a focused button pushes focus to the next control; remember it and keep focus in the input instead.
            bool buttonFocused = _btnSend.Focused || (_btnAttach?.Focused ?? false) || _btnClear.Focused
                || _toolbar.Controls.Cast<Control>().Any(c => c.Focused);
            bool placeholder = _input.TextLength == 0 && !_input.Focused;
            if (_placeholder.Visible != placeholder) _placeholder.Visible = placeholder;
            string hint = _agent?.Configured == false ? "尚未配置模型，点击右上角「⚙ 模型设置」…"
                : _noteMode ? "让笔记助手总结、查找、改写笔记… / Ask about your notes…" : "让 AI 查看所有 VS 状态、发布任务、调试与生成…";
            if (_placeholder.Text != hint) _placeholder.Text = hint;
            _btnSend.Enabled = (!running || (!_noteMode && VsMentionSession.HasIntent(_input.Text))) && (_input.Text.Trim().Length > 0 || _pending.Count > 0);
            _btnStop.Enabled = running;
            if (_btnAttach != null) _btnAttach.Enabled = !running;
            _btnClear.Enabled = _agent != null && (running || _agent.Transcript.Messages.Count > 0);
            foreach (Control c in _toolbar.Controls) c.Enabled = !running;
            if (_btnInsert != null) _btnInsert.Enabled = !running && LastReply().Length > 0;
            bool stillFocused = _btnSend.Focused || (_btnAttach?.Focused ?? false) || _btnClear.Focused
                || _toolbar.Controls.Cast<Control>().Any(c => c.Focused);
            if (buttonFocused && !stillFocused && Form.ActiveForm != null && Form.ActiveForm == FindForm() && _input.CanFocus) _input.Focus();
            if (running && !_pulse.Enabled) { _dots = 0; _pulse.Start(); }
            else if (!running && _pulse.Enabled) _pulse.Stop();
            UpdateStatus();
            _transcript.SetActivity(running ? (_agent.Activity.Length > 0 ? _agent.Activity : "思考中…") : null, null);
            // 标题栏只在状态变化时重绘，输入时不再逐键刷新。/ Repaint the header only when its state changes, not on every keystroke.
            string header = _agent == null ? "" : _agent.Configured + "|" + running + "|" + _agent.ProviderName + "|" + _agent.ModelName;
            if (header != _headerState) { _headerState = header; _header.Invalidate(); }
        }

        private void UpdateStatus()
        {
            if (_mentionStatus != null) { _inputStatus.Text = _mentionStatus; _inputStatus.ForeColor = Theme.AccentText; return; }
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
                text = (_input.Focused ? "正在输入" : _input.TextLength > 0 ? "草稿待发送" : "等待输入") + " · " + _input.TextLength + " 字"
                    + (_pending.Count > 0 ? $" · 📎 {AttachmentPolicy.CountText(_pending)} / {AttachmentPolicy.CountText(_pending, true)}" : "");
                color = _input.Focused ? Theme.AccentText : Theme.TextSecondary;
            }
            if (_inputStatus.Text != text) _inputStatus.Text = text;
            if (_inputStatus.ForeColor != color) _inputStatus.ForeColor = color;
        }

        /// <summary>最新一条助手回复的正文（不含工具步骤）。/ Body of the latest assistant reply (without tool steps).</summary>
        internal string LastReply()
        {
            var last = _agent?.Transcript.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (last == null) return "";
            return string.Join("\n\n", last.Parts.Where(p => !p.IsStep && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text.Trim())).Trim();
        }

        private void Header_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int x = Dpi.S(28), right = (_releaseSlider.Visible ? _releaseSlider.Left : _btnClear.Left) - Dpi.S(16);

            var badge = new RectangleF(x, Dpi.S(14), Dpi.S(36), Dpi.S(36));
            using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(badge, Theme.Accent, Color.FromArgb(59, 130, 246), 45f))
            using (var p = Theme.RoundRect(badge, Dpi.S(10)))
                g.FillPath(br, p);
            TextRenderer.DrawText(g, "✦", BadgeFont, Rectangle.Round(badge), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += Dpi.S(48);

            string title = _noteMode ? "笔记 AI 助手" : "AI 总控助手";
            var tsz = TextRenderer.MeasureText(g, title, Theme.Big, Size.Empty, flags);
            TextRenderer.DrawText(g, title, Theme.Big, new Rectangle(x, Dpi.S(10), Math.Min(tsz.Width, Math.Max(0, right - x)), Dpi.S(26)), Theme.Text, flags | TextFormatFlags.VerticalCenter);
            string st; Color fg, bg, dot;
            if (_agent == null || !_agent.Configured) { st = "未配置"; fg = Theme.Warning; bg = Theme.NoneBg; dot = Theme.Warning; }
            else if (_agent.Running) { st = "思考中"; fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; }
            else { st = "空闲"; fg = Theme.IdleFg; bg = Theme.IdleBg; dot = Theme.IdleDot; }
            int px = x + tsz.Width + Dpi.S(12);
            if (right - px > Dpi.S(40)) Theme.DrawPill(g, px, Dpi.S(12), Math.Min(Dpi.S(160), right - px), st, bg, fg, dot);

            string sub = _agent == null ? "" : _agent.Configured
                ? _agent.ProviderName + " · " + _agent.ModelName + (_noteMode ? " · 阅读、整理、改写笔记 / Notes" : " · 统一管理所有 VS、发布任务")
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

            int x0 = (int)card.X + Dpi.S(12), right = (int)card.Right - Dpi.S(12);
            var badge = new RectangleF(x0, card.Y + (card.Height - Dpi.S(28)) / 2, Dpi.S(28), Dpi.S(28));
            Theme.FillRound(g, Theme.AccentLight, badge, Dpi.S(6));
            TextRenderer.DrawText(g, "✦", BadgeFont, Rectangle.Round(badge), Theme.AccentText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            int tx = (int)badge.Right + Dpi.S(10), ty = (int)card.Y + Dpi.S(10);
            string st; Color fg, bg, dot;
            if (_agent == null || !_agent.Configured) { st = "未配置"; fg = Theme.Warning; bg = Theme.NoneBg; dot = Theme.Warning; }
            else if (_agent.Running) { st = "思考中"; fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; }
            else { st = "空闲"; fg = Theme.IdleFg; bg = Theme.IdleBg; dot = Theme.IdleDot; }
            var pw = TextRenderer.MeasureText(g, st, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(30);
            int titleWidth = TextRenderer.MeasureText(g, "AI 总控助手", Theme.SemiBold, Size.Empty, TextFormatFlags.NoPadding).Width;
            bool compact = right - tx < titleWidth + pw + Dpi.S(6);
            TextRenderer.DrawText(g, "AI 总控助手", Theme.SemiBold, new Rectangle(tx, ty, Math.Max(0, right - tx - (compact ? 0 : pw + Dpi.S(6))), Dpi.S(20)),
                _selected ? Color.White : Theme.Text, flags);
            Theme.DrawPill(g, right - pw, ty + Dpi.S(compact ? 23 : -1), pw, st, bg, fg, dot);
            string sub = _agent == null ? "" : _agent.Configured ? _agent.ProviderName + " · " + _agent.ModelName : "点击配置 DeepSeek 等模型";
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(tx, ty + Dpi.S(24), Math.Max(0, right - tx - (compact ? pw + Dpi.S(6) : 0)), Dpi.S(18)), Theme.TextMuted, flags);
        }
    }
}
