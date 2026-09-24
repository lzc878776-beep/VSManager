using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace VSManager
{
    internal static class AgentToolApproval
    {
        /// <summary>Must run on the UI thread. Only explicit approval returns true; cancellation always denies.</summary>
        internal static bool Show(IWin32Window owner, string title, string detail, byte[] screenshot, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (!Application.MessageLoop || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA ||
                (owner is Control control && control.InvokeRequired))
                throw new InvalidOperationException("审批窗口必须在 UI 线程显示 / Approval must be shown on the UI thread.");
            if (owner != null && Native.GetWindowThreadProcessId(owner.Handle, out _) != Native.GetCurrentThreadId())
                throw new InvalidOperationException("审批窗口与所有者必须在同一 UI 线程 / Approval and owner must share a UI thread.");

            using (var stream = screenshot == null ? null : new MemoryStream(screenshot, false))
            using (var image = stream == null ? null : Image.FromStream(stream, true, true))
            using (var dialog = new ApprovalDialog(title, detail, image))
            {
                // Create on the UI thread before registration. Register also fires for cancellation during construction.
                IntPtr handle = dialog.Handle;
                using (cancellationToken.Register(() => dialog.RequestCancellation()))
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    DialogResult result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                    return !cancellationToken.IsCancellationRequested && result == DialogResult.OK && dialog.Approved;
                }
            }
        }

        private sealed class ApprovalDialog : Form
        {
            private readonly Button _cancel;
            internal bool Approved { get; private set; }

            internal ApprovalDialog(string title, string detail, Image screenshot)
            {
                Text = title ?? "工具审批 / Tool approval";
                Font = Theme.Regular;
                BackColor = Theme.Background;
                ForeColor = Theme.Text;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
                AutoScaleMode = AutoScaleMode.Dpi;
                Size = new Size(Dpi.S(900), Dpi.S(screenshot == null ? 620 : 860));
                MinimumSize = new Size(Dpi.S(460), Dpi.S(360));
                Padding = new Padding(Dpi.S(12));

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = screenshot == null ? 2 : 3
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, screenshot == null ? 100 : 50));
                if (screenshot != null) layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(52)));
                Controls.Add(layout);

                var details = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    WordWrap = false,
                    ScrollBars = ScrollBars.Both,
                    MaxLength = int.MaxValue,
                    BackColor = Theme.Surface,
                    ForeColor = Theme.Text,
                    BorderStyle = BorderStyle.FixedSingle,
                    HideSelection = false,
                    Text = (screenshot == null ? "" :
                        "下方为即将共享的实际截图；可能包含覆盖层，请确认无敏感信息。\r\n" +
                        "The preview is the actual screenshot to be shared; overlays may be included. Check for sensitive content.\r\n\r\n") + (detail ?? "")
                };
                Theme.DarkControl(details);
                layout.Controls.Add(details, 0, 0);
                if (screenshot != null)
                {
                    layout.Controls.Add(new PictureBox
                    {
                        Dock = DockStyle.Fill,
                        BackColor = Theme.Surface,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Image = screenshot,
                        TabStop = false
                    }, 0, 1);
                }

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    Padding = new Padding(0, Dpi.S(8), 0, 0)
                };
                _cancel = MakeButton("取消 / Cancel");
                _cancel.DialogResult = DialogResult.Cancel;
                _cancel.Click += (s, e) => Deny();
                var approve = MakeButton("批准 / Approve");
                approve.Click += (s, e) =>
                {
                    Approved = true;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                buttons.Controls.Add(_cancel);
                buttons.Controls.Add(approve);
                layout.Controls.Add(buttons, 0, layout.RowCount - 1);
                AcceptButton = _cancel;
                CancelButton = _cancel;
                ActiveControl = _cancel;
                Shown += (s, e) => _cancel.Focus();
                HandleCreated += (s, e) => Theme.DarkTitleBar(this);
            }

            private static Button MakeButton(string text) => new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(Dpi.S(140), Dpi.S(32)),
                BackColor = Theme.Elevated,
                ForeColor = Theme.Text,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };

            private void Deny()
            {
                Approved = false;
                DialogResult = DialogResult.Cancel;
                Close();
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                // Enter always denies, even when the approval button or multiline details have focus.
                if ((keyData & Keys.KeyCode) == Keys.Enter || (keyData & Keys.KeyCode) == Keys.Escape)
                {
                    Deny();
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            internal void RequestCancellation()
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (!IsDisposed && !Disposing) Deny();
                    }));
                }
                catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
                {
                    // Expected only if closing destroyed the handle between the check and BeginInvoke.
                }
            }
        }
    }
}
