using System;
using System.Drawing;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 为失败 / 待验证的任务输入补充信息，确认后插入补充信息重新排队。
    /// Enters supplementary info for a failed / awaiting-verification task; on confirm the task is requeued with it.
    /// </summary>
    public sealed class SupplementForm : Form
    {
        private readonly TextBox _input = new TextBox();

        public string Info => _input.Text.Trim();

        public SupplementForm(QueuedTask t)
        {
            Text = $"补充信息后重试 #{t.Id} / Retry #{t.Id} with info";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(560), Dpi.S(300));
            MinimumSize = new Size(Dpi.S(420), Dpi.S(240));
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter && e.Control) Confirm();
            };

            string reason = t.Status == QueueStatus.Failed
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
                    + $"补充信息会与前次反馈一起发给原 VS 的 Copilot（已补充 {t.SupplementCount}/{TaskStateMachine.MaxSupplements} 次，Ctrl+Enter 确认）"
                    + " / Sent to the same Copilot with the previous feedback (Ctrl+Enter to confirm)"
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

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Dpi.S(48), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Dpi.S(8)), BackColor = Theme.Sidebar };
            buttons.Controls.Add(Button("取消 / Cancel", false, () => { DialogResult = DialogResult.Cancel; Close(); }));
            buttons.Controls.Add(Button("补充并重试 / Retry", true, Confirm));

            Controls.Add(inputHost);
            Controls.Add(hint);
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
