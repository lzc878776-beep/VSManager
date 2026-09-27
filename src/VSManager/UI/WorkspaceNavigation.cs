using System;
using System.Windows.Forms;

namespace VSManager
{
    internal enum WorkspacePage { VisualStudio, Agent, Notebook }

    internal sealed class WorkspaceNavigation
    {
        private readonly Control _vs, _agent, _notebook;
        private readonly Func<bool> _saveNotebook;
        public WorkspacePage Current { get; private set; }

        public WorkspaceNavigation(Control vs, Control agent, Control notebook, Func<bool> saveNotebook)
        {
            _vs = vs;
            _agent = agent;
            _notebook = notebook;
            _saveNotebook = saveNotebook;
            Select(WorkspacePage.VisualStudio);
        }

        public bool Select(WorkspacePage page)
        {
            if (Current == WorkspacePage.Notebook && page != Current && !_saveNotebook()) return false;
            Current = page;
            var active = page == WorkspacePage.VisualStudio ? _vs : page == WorkspacePage.Agent ? _agent : _notebook;
            // 先显示并置顶新页面、再隐藏旧页面，并合并为一次布局：切换过程中不会露出空白底色或让侧栏重新布局而闪烁。
            // Show and raise the new page before hiding the old ones, in a single layout pass, so switching never exposes the bare background or relayouts the sidebar.
            var parent = active.Parent;
            parent?.SuspendLayout();
            try
            {
                active.Visible = true;
                if (parent == null || parent.Controls.GetChildIndex(active) != 0) active.BringToFront();
                foreach (var other in new[] { _vs, _agent, _notebook })
                    if (other != active) other.Visible = false;
            }
            finally { parent?.ResumeLayout(true); }
            return true;
        }
    }
}
