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

        // 笔记 AI 助手：与笔记本各占半屏，可拖动停靠到侧边 / 底部或浮动。
        // Note AI assistant: shares the notebook page half and half; drag it to dock at a side / the bottom or to float.
        private AgentService _noteAgent;
        private AgentPanel _notePanel;
        private NoteAgentDock _noteDock;

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
                _notebookHost.Controls.Add(BuildNoteAgent(_notebook));
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
            NoteUserNavigation("用户打开笔记本 / user opened the notebook");
            _agentMode = false;
            _agentCard.Selected = false;
            _workspaceSidebar.SetNotionCollapsed(false);
            if (_list.SelectedIndex >= 0) { _list.ClearSelected(); OnSelectionChanged(); }
        }

        private bool SaveNotebook() => _notebook == null || _notebook.TrySave();

        /// <summary>创建笔记 AI 助手与分屏容器，返回放入笔记本页面的控件。/ Creates the note AI assistant and the split host; returns the control for the notebook page.</summary>
        private Control BuildNoteAgent(NotebookWorkspace notebook)
        {
            _noteAgent = new AgentService(this, () => _settings, null, AgentProfile.Notes) { CurrentNoteSource = CurrentNoteSnapshot };
            _notePanel = new AgentPanel(true);
            _notePanel.Bind(_noteAgent);
            _notePanel.RefreshConfig();
            _notePanel.SettingsRequested += OpenSettings;
            _notePanel.InsertRequested += text => SetStatus(_notebook?.InsertIntoCurrent(text) ?? "已插入到当前笔记 / Inserted into the current note");
            _noteDock = new NoteAgentDock(notebook, _notePanel, "笔记 AI 助手 / Note assistant");
            _noteDock.Restore(_settings.NoteAgentDock, _settings.NoteAgentPercent, _settings.NoteAgentFloatBounds);
            _noteDock.AgentVisible = _settings.AgentEnabled;
            _noteDock.LayoutChanged += () =>
            {
                _settings.NoteAgentDock = NoteAgentDock.Format(_noteDock.Position);
                _settings.NoteAgentPercent = _noteDock.Percent;
                _settings.NoteAgentFloatBounds = NoteAgentDock.FormatBounds(_noteDock.FloatBounds);
                _settings.Save();
            };
            return _noteDock;
        }

        private void UpdateNoteAgentVisibility(bool on)
        {
            if (_noteDock == null) return;
            if (!on) _noteAgent.Stop();
            _noteDock.AgentVisible = on;
        }

        /// <summary>读取当前笔记（可在后台线程调用，自动切换到界面线程）。/ Reads the current note (callable from background threads; marshals to the UI thread).</summary>
        private NoteSnapshot CurrentNoteSnapshot()
        {
            if (IsDisposed || _notebook == null) return null;
            if (InvokeRequired) return (NoteSnapshot)Invoke((Func<NoteSnapshot>)CurrentNoteSnapshot);
            return _notebook.CurrentNote();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_notebook != null && (_notebookMode || _notebookSidebarHost.ContainsFocus) && _notePanel?.ContainsFocus != true &&
                _notebook.HandleShortcut(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
