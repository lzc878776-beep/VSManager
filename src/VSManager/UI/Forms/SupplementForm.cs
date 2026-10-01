using System;
using System.Drawing;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 为失败 / 待验证的任务输入补充信息，确认后插入补充信息重新排队；排队中的任务则把补充合并到原任务。
    /// Enters supplementary info for a failed / awaiting-verification task; on confirm the task is requeued with it. For a queued task the info is merged into it.
    /// </summary>
    public sealed class SupplementForm : Form
    {
        private readonly TextBox _input = new TextBox();
        private readonly ToggleSwitch _replace = new ToggleSwitch();
        private readonly ToggleSwitch _fresh = new ToggleSwitch();

        public string Info => _input.Text.Trim();
        /// <summary>替换此前的补充信息与前次反馈（只发送本次内容）。/ Replace earlier supplements and feedback (send only this text).</summary>
        public bool ReplacePrevious => _replace.Checked;
        /// <summary>Copilot 对话已清空 / 新建线程，让 Copilot 重新阅读相关内容。/ The Copilot conversation was cleared; Copilot re-reads the relevant content.</summary>
        public bool FreshContext => _fresh.Checked;

        public SupplementForm(QueuedTask t)
        {
            bool queued = TaskStateMachine.IsQueued(t);
            Text = queued ? $"补充要求（合并到排队中的 #{t.Id}）/ Add to queued #{t.Id}" : $"补充信息后重试 #{t.Id} / Retry #{t.Id} with info";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(560), Dpi.S(360));
            MinimumSize = new Size(Dpi.S(420), Dpi.S(240));
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter && e.Control) Confirm();
            };

            string reason = queued
                ? "排队中，补充要求会合并到原任务，不会新建任务 / Queued: merged into this task, no new task is created"
                : t.Status == QueueStatus.Failed
                ? "失败原因 / Failure: " + TextUtil.Clip(t.FailureReason ?? t.Error ?? "", 240)
                : "待处理 / Pending: " + TextUtil.Clip(t.PendingNote ?? t.Result ?? "", 240);
            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = Dpi.S(78),
                Font = Theme.Small,
                ForeColor = Theme.TextSecondary,
                UseMnemonic = false,
                Padding = new Padding(Dpi.S(10), Dpi.S(8), Dpi.S(10), 0),
                Text = $"「{t.VsName}」任务：{TextUtil.Clip(t.Text, 120)}\r\n{reason}\r\n"
                    + (queued
                        ? "发送时与任务内容一起发给 Copilot（Ctrl+Enter 确认） / Sent to Copilot together with the task (Ctrl+Enter to confirm)"
                        : $"补充信息会与前次反馈一起发给原 VS 的 Copilot（已补充 {t.SupplementCount} 次，Ctrl+Enter 确认）"
                            + " / Sent to the same Copilot with the previous feedback (Ctrl+Enter to confirm)")
            };

            _input.Dock = DockStyle.Fill;
            _input.Multiline = true;
            _input.AcceptsReturn = true;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.BackColor = Theme.Surface;
            _input.ForeColor = Theme.Text;
            var inputHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Dpi.S(10), Dpi.S(4), Dpi.S(10), Dpi.S(4)) };
            inputHost.Controls.Add(_input);

            bool hasPrevious = !string.IsNullOrEmpty(t.Supplement) || !string.IsNullOrEmpty(t.PriorFailure);
            _replace.Text = "只发送本次内容，替换此前的补充与反馈 / Replace earlier supplements and feedback";
            _replace.Enabled = hasPrevious;
            _fresh.Text = "对话已清空，让 Copilot 重新阅读相关内容 / Conversation cleared: re-read the relevant content";
            var options = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = Dpi.S(60), ColumnCount = 1, RowCount = 2, Padding = new Padding(Dpi.S(10), 0, Dpi.S(10), 0) };
            options.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            options.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            foreach (var sw in new[] { _replace, _fresh })
            {
                sw.Dock = DockStyle.Fill;
                sw.Margin = Padding.Empty;
                options.Controls.Add(sw);
            }

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Dpi.S(48), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Dpi.S(8)), BackColor = Theme.Sidebar };
            buttons.Controls.Add(Button("取消 / Cancel", false, () => { DialogResult = DialogResult.Cancel; Close(); }));
            buttons.Controls.Add(Button(queued ? "合并到原任务 / Merge" : "补充并重试 / Retry", true, Confirm));

            Controls.Add(inputHost);
            Controls.Add(hint);
            Controls.Add(options);
            Controls.Add(buttons);
            Shown += (s, e) => _input.Focus();
        }

        private void Confirm()
        {
            if (Info.Length == 0) { _input.Focus(); return; }
            DialogResult = DialogResult.OK;
            Close();
        }

        private FlatButton Button(string text, bool primary, Action action)
        {
            var b = new FlatButton { Text = text, Height = Dpi.S(30), Primary = primary };
            b.Width = TextRenderer.MeasureText(text, b.Font).Width + Dpi.S(28);
            b.Click += (s, e) => action();
            return b;
        }
    }
}
