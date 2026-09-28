using System;
using System.IO;
using System.Windows.Forms;

namespace VSManager
{
    public partial class MainForm
    {
        private NotebookWorkspace _notebook;
        private WorkspaceSidebar _workspaceSidebar;
        private WorkspaceNavigation _workspaceNavigation;
        private readonly Panel _notebookHost = new Panel { Dock = DockStyle.Fill, Visible = false };
        private readonly Panel _notebookSidebarHost = new Panel { Dock = DockStyle.Fill };
        private bool _notebookMode => _workspaceNavigation?.Current == WorkspacePage.Notebook;

        private void BuildWorkspaceSidebar(Control vsBody, Control refresh)
        {
            _workspaceSidebar = new WorkspaceSidebar(vsBody, _agentCard, _notebookSidebarHost, _sideCount, refresh, () =>
            {
                int extra = 0;
                foreach (Control c in vsBody.Controls) if (c != _list) extra += c.Height;
                return extra + Math.Max(_list.Items.Count * _list.ItemHeight, Dpi.S(160)) + Dpi.S(6);
            }) { Dock = DockStyle.Fill };
            _workspaceSidebar.NotebookRequested += OpenNotebooks;
            _sidebar.Controls.Add(_workspaceSidebar);
            InitializeNotebook();
        }

        private bool InitializeNotebook()
        {
            if (_notebook != null) return true;
            try
            {
                EnsureAgentPromptPage();
                _notebook = new NotebookWorkspace { Dock = DockStyle.Fill };
                _notebook.ContentRequested += OpenNotebooks;
                _notebook.SidebarRequested += () => _workspaceSidebar.SetNotionCollapsed(false);
                _notebook.Error += SetStatus;
                _notebookHost.Controls.Add(_notebook);
                while (_notebookSidebarHost.Controls.Count > 0) _notebookSidebarHost.Controls[0].Dispose();
                _notebookSidebarHost.Controls.Add(_notebook.DetachSidebar());
                _notebook.Initialize();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                string message = "无法打开笔记本 / Cannot open notebooks: " + ex.Message;
                SetStatus(message);
                _notebookSidebarHost.Controls.Clear();
                var error = new Label { Text = message, Dock = DockStyle.Fill, ForeColor = Theme.Danger, Padding = new Padding(Dpi.S(12)) };
                var retry = new FlatButton { Text = "重试 / Retry", Dock = DockStyle.Bottom, Height = Dpi.S(36) };
                retry.Click += (s, e) => InitializeNotebook();
                _notebookSidebarHost.Controls.Add(error);
                _notebookSidebarHost.Controls.Add(retry);
                return false;
            }
        }

        /// <summary>预先建立「AI 助手补充提示词」页面，方便用户在笔记本中找到并编辑。/ Creates the AI-assistant instructions page up front so users can find and edit it.</summary>
        private void EnsureAgentPromptPage()
        {
            try { NotebookAgentPrompt.Ensure(new NotebookStore()); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is Microsoft.Data.Sqlite.SqliteException)
            {
                SetStatus("无法建立 AI 补充提示词页面 / Cannot create the AI instructions page: " + ex.Message);
            }
        }

        private void OpenNotebooks()
        {
            if (!InitializeNotebook() || _workspaceNavigation == null) return;
            _workspaceNavigation.Select(WorkspacePage.Notebook);
            _agentMode = false;
            _agentCard.Selected = false;
            _workspaceSidebar.SetNotionCollapsed(false);
            if (_list.SelectedIndex >= 0) { _list.ClearSelected(); OnSelectionChanged(); }
        }

        private bool SaveNotebook() => _notebook == null || _notebook.TrySave();

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_notebook != null && (_notebookMode || _notebookSidebarHost.ContainsFocus) &&
                _notebook.HandleShortcut(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
