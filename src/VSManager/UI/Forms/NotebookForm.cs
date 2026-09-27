using System.Drawing;
using System.Windows.Forms;

namespace VSManager
{
    internal sealed class NotebookForm : Form
    {
        internal NotebookWorkspace Workspace { get; }

        public NotebookForm(NotebookStore store = null)
        {
            Text = "笔记本 / Notebooks";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            HandleCreated += (s, e) => Theme.DarkTitleBar(this);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Dpi.S(1200), Dpi.S(820));
            MinimumSize = new Size(Dpi.S(840), Dpi.S(560));
            Workspace = new NotebookWorkspace(store) { Dock = DockStyle.Fill };
            Controls.Add(Workspace);
            Workspace.TextChanged += (s, e) => Text = Workspace.Text;
        }

        internal bool TrySave() => Workspace.TrySave();

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!TrySave())
            {
                e.Cancel = true;
                MessageBox.Show(this, "笔记尚未保存，已取消关闭。请查看底部错误。\r\nClosing cancelled: note was not saved. Check the status message.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            base.OnFormClosing(e);
        }
    }
}
