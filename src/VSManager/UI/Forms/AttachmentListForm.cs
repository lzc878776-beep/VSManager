using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 任务附件列表：显示原始文件名、类型、大小、哈希与存储位置（相对 %APPDATA%\VSManager\attachments），可打开或在资源管理器中定位。
    /// Task attachment list: shows the original name, kind, size, hash and stored location (relative to
    /// %APPDATA%\VSManager\attachments); each entry can be opened or located in Explorer.
    /// </summary>
    public sealed class AttachmentListForm : Form
    {
        private readonly ListView _list = new ListView();
        private readonly Label _status = new Label();

        public AttachmentListForm(string title, IReadOnlyList<AttachmentRef> attachments, string note)
        {
            Text = title;
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(780), Dpi.S(360));
            MinimumSize = new Size(Dpi.S(520), Dpi.S(260));
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Theme.Surface;
            _list.ForeColor = Theme.Text;
            _list.Columns.Add("文件名 / Name", Dpi.S(220));
            _list.Columns.Add("类型 / Kind", Dpi.S(100));
            _list.Columns.Add("大小 / Size", Dpi.S(80));
            _list.Columns.Add("SHA-256", Dpi.S(120));
            _list.Columns.Add("位置 / Location", Dpi.S(240));
            foreach (var a in attachments ?? new AttachmentRef[0])
            {
                bool exists = AttachmentStore.FindById(a.Id) != null;
                var item = new ListViewItem(new[]
                {
                    a.Name, AttachmentPolicy.KindText(a.Kind), AttachmentPolicy.FormatSize(a.Size), a.ShortHash,
                    exists ? "attachments/" + a.RelPath : "（已清理 / removed）attachments/" + a.RelPath
                }) { Tag = a, ForeColor = exists ? Theme.Text : Theme.TextMuted };
                _list.Items.Add(item);
            }
            _list.DoubleClick += (s, e) => Act(true);

            _status.Dock = DockStyle.Bottom;
            _status.Height = Dpi.S(44);
            _status.Font = Theme.Small;
            _status.ForeColor = Theme.TextSecondary;
            _status.UseMnemonic = false;
            _status.Text = (string.IsNullOrEmpty(note) ? "" : note + "\r\n") + "双击打开；附件只保存在本机，不会上传或提交 / Double-click to open; attachments stay on this computer and are never uploaded or committed";

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Dpi.S(48), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Dpi.S(8)), BackColor = Theme.Sidebar };
            buttons.Controls.Add(Button("关闭 / Close", Close));
            buttons.Controls.Add(Button("打开附件目录 / Open folder", AttachmentStore.OpenRoot));
            buttons.Controls.Add(Button("在资源管理器中定位 / Locate", () => Act(false)));
            buttons.Controls.Add(Button("打开 / Open", () => Act(true)));

            Controls.Add(_list);
            Controls.Add(_status);
            Controls.Add(buttons);
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        }

        private FlatButton Button(string text, Action action)
        {
            var b = new FlatButton { Text = text, Height = Dpi.S(30) };
            b.Width = TextRenderer.MeasureText(text, b.Font).Width + Dpi.S(28);
            b.Click += (s, e) => action();
            return b;
        }

        private void Act(bool open)
        {
            if (!(_list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag is AttachmentRef a)) return;
            string error = AttachmentStore.Reveal(a.Id, open);
            if (error != null) _status.Text = error;
        }
    }
}
