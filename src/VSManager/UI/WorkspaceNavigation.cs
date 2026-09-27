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
            _vs.Visible = page == WorkspacePage.VisualStudio;
            _agent.Visible = page == WorkspacePage.Agent;
            _notebook.Visible = page == WorkspacePage.Notebook;
            var active = page == WorkspacePage.VisualStudio ? _vs : page == WorkspacePage.Agent ? _agent : _notebook;
            active.BringToFront();
            return true;
        }
    }
}
