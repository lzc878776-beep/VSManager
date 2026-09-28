using System;
using System.Drawing;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 任务清单底部的详情区：选中待确认或失败的任务时显示待处理内容或失败原因。
    /// Detail area at the bottom of the task list: shows the pending items or the failure reason of the selected awaiting-confirmation or failed task.
    /// </summary>
    public sealed partial class TaskPanel
    {
        private readonly Panel _detail = new Panel();
        private readonly Label _detailTitle = new Label();
        private readonly TextBox _detailText = new TextBox();
        private readonly FlatButton _detailClose = new FlatButton { Text = "×", Ghost = true };
        /// <summary>用户关闭详情后，直到选中其他条目前不再弹出。/ After the user closes it, stays closed until another item is selected.</summary>
        private string _detailDismissedKey;
        private string _detailKey;

        private static int DetailHeight => Dpi.S(170);

        private void InitDetail()
        {
            _detail.Dock = DockStyle.Bottom;
            _detail.Height = DetailHeight;
            _detail.BackColor = Theme.Sidebar;
            _detail.Padding = new Padding(Dpi.S(10), Dpi.S(6), Dpi.S(10), Dpi.S(8));
            _detail.Visible = false;
            _detail.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, _detail.Width, 0);
            };

            _detailTitle.Dock = DockStyle.Top;
            _detailTitle.Height = Dpi.S(24);
            _detailTitle.Font = Theme.SemiBold;
            _detailTitle.TextAlign = ContentAlignment.MiddleLeft;
            _detailTitle.AutoEllipsis = true;

            _detailText.Dock = DockStyle.Fill;
            _detailText.Multiline = true;
            _detailText.ReadOnly = true;
            _detailText.WordWrap = true;
            _detailText.ScrollBars = ScrollBars.Vertical;
            _detailText.BorderStyle = BorderStyle.None;
            _detailText.BackColor = Theme.Surface;
            _detailText.ForeColor = Theme.Text;
            _detailText.Font = Theme.Small;
            _detailText.AccessibleName = "任务详情 / Task detail";

            _detailClose.Size = new Size(Dpi.S(24), Dpi.S(22));
            _detailClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _detailClose.Click += (s, e) => { _detailDismissedKey = _detailKey; _detail.Visible = false; };
            _tips.SetToolTip(_detailClose, "关闭详情（选中其他条目后再次显示）/ Close (shows again when another item is selected)");

            _detail.Controls.Add(_detailText);
            _detail.Controls.Add(_detailTitle);
            _detail.Controls.Add(_detailClose);
            _detail.Resize += (s, e) => _detailClose.Location = new Point(_detail.Width - _detailClose.Width - Dpi.S(6), Dpi.S(6));
            _detailClose.BringToFront();

            _list.SelectedIndexChanged += (s, e) => UpdateDetail();
            _list.MouseClick += (s, e) =>
            {
                // 再次点击同一条目时重新打开已关闭的详情 / Clicking the same item again reopens a closed detail
                if (e.Button == MouseButtons.Left && ItemAt(e.Location) is QueuedTask t && TaskDisplayOrder.KeyOf(t) == _detailDismissedKey)
                {
                    _detailDismissedKey = null;
                    UpdateDetail();
                }
            };
        }

        /// <summary>按当前选中条目刷新详情区。/ Refreshes the detail area for the selected item.</summary>
        private void UpdateDetail()
        {
            var t = _collapsed ? null : _list.SelectedItem as QueuedTask;
            string key = t == null ? null : TaskDisplayOrder.KeyOf(t);
            if (key != _detailKey && key != _detailDismissedKey) _detailDismissedKey = null;
            _detailKey = key;
            string title = TaskHoldNote.DetailTitle(t), body = TaskHoldNote.DetailText(t);
            bool show = title != null && key != _detailDismissedKey;
            if (show)
            {
                _detailTitle.Text = title;
                _detailTitle.ForeColor = t.Status == QueueStatus.Failed ? Theme.Danger : Theme.Warning;
                string text = (body ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                if (_detailText.Text != text) { _detailText.Text = text; _detailText.SelectionStart = 0; }
            }
            if (_detail.Visible != show) _detail.Visible = show;
        }
    }
}