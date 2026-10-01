using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace VSManager
{
    internal sealed class NotebookWorkspace : UserControl
    {
        public event Action ContentRequested;
        public event Action SidebarRequested;
        public event Action<string> Error;
        internal Panel Sidebar { get; private set; }
        private Splitter _sidebarSplitter;
        private Label _sidebarTitle;
        private bool _initialized;
        private readonly NotebookStore _store;
        private readonly TreeView _tree = new BufferedTreeView();
        private readonly TextBox _search = new TextBox();
        private readonly TextBox _editor = new TextBox();
        private readonly NotebookPreview _preview = new NotebookPreview();
        private readonly SplitContainer _split = new SplitContainer();
        private readonly Label _breadcrumb = new Label();
        private Button _sourceToggle;
        private readonly Label _status = new Label();
        private readonly Label _empty = new Label();
        private readonly Timer _saveTimer = new Timer { Interval = 900 };
        private readonly Timer _searchTimer = new Timer { Interval = 350 };
        private readonly ImageList _icons = new ImageList();
        private NotebookDocument _document, _pendingDocument, _previousDocument;
        private TreeNode _hoverNode;
        private bool _loading, _dirty;
        // 是否在查看 / 编辑 Markdown 源码；平时直接在渲染后的页面上编辑。/ Whether the Markdown source is shown; normally editing happens directly on the rendered page.
        private bool _editing;
        // 光标位置是否来自用户在本页的编辑；否则插入内容追加到末尾。/ Whether the caret comes from editing this page; otherwise inserted text is appended.
        private bool _caretValid;
        private string _treeSignature;

        public NotebookWorkspace(NotebookStore store = null)
        {
            _store = store ?? new NotebookStore();
            Text = "笔记本 / Notebooks";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Size = new Size(Dpi.S(1200), Dpi.S(820));
            BuildUi();
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); TrySave(); };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); RefreshTree(); };
            Load += (s, e) => Initialize();
            _preview.NoteLinkRequested += OpenLinkedNote;
            _preview.EditRequested += BeginEdit;
            _preview.MarkdownEdited += ApplyRenderedEdit;
            _preview.ImagePasteRequested += PasteImagesIntoPreview;
        }

        internal void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            RefreshTree();
        }

        /// <summary>点击笔记内链接后在笔记本中打开目标笔记。/ Opens the linked note in the notebook after a link click.</summary>
        internal void OpenLinkedNote(string target)
        {
            if (_document == null || !TrySave()) return;
            try
            {
                string path = _store.ResolveLink(_document.Path, target);
                if (path == null) { SetStatus("链接的笔记不存在 / Linked note not found: " + target, true); return; }
                ClearSearch();
                RefreshTree(path);
            }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
        }

        /// <summary>笔记被外部写入后刷新目录；有未保存编辑时不打断。/ Refreshes after external writes; never interrupts unsaved edits.</summary>
        internal void ReloadIfClean()
        {
            if (!_initialized || _dirty) return;
            // 后台刷新（任务 / 对话写入日志）不得打断用户：保留焦点、滚动、编辑状态，内容未变时不重新显示页面。
            // Background refreshes (journal writes from tasks / chats) must not interrupt the user: keep focus, scrolling and editing state; skip redisplay when unchanged.
            RefreshTree(Selected?.Path, true);
        }

        internal Control DetachSidebar()
        {
            Controls.Remove(Sidebar);
            _sidebarSplitter.Dispose();
            _sidebarTitle.Visible = false;
            Sidebar.Dock = DockStyle.Fill;
            return Sidebar;
        }

        internal bool HandleShortcut(Keys keys)
        {
            if (keys == (Keys.Control | Keys.S)) TrySave();
            else if (keys == (Keys.Control | Keys.N)) Create(true);
            else if (keys == (Keys.Control | Keys.F)) { SidebarRequested?.Invoke(); _search.Focus(); }
            else if (keys == Keys.F5) RefreshTree();
            else return false;
            return true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
            HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

        private void BuildUi()
        {
            var sidebar = new Panel { Dock = DockStyle.Left, Width = Dpi.S(264), BackColor = Theme.Sidebar, ForeColor = Theme.Text, Font = Theme.Regular,
                Padding = new Padding(Dpi.S(10), Dpi.S(8), Dpi.S(10), Dpi.S(6)) };
            var title = new Label { Text = "我的笔记本 / Notebooks", Dock = DockStyle.Top, Height = Dpi.S(44),
                Font = Theme.SemiBold, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            _sidebarTitle = title;
            var searchHost = new Panel { Dock = DockStyle.Top, Height = Dpi.S(58) };
            searchHost.Controls.Add(new Label { Text = "搜索标题与正文 / Search", Dock = DockStyle.Top, Height = Dpi.S(22),
                Font = Theme.Small, ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft });
            var searchBox = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(32), BackColor = Theme.Sidebar,
                Padding = new Padding(Dpi.S(10), Dpi.S(6), Dpi.S(10), Dpi.S(6)) };
            searchBox.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var bounds = new RectangleF(0.5f, 0.5f, searchBox.Width - 1.5f, searchBox.Height - 1.5f);
                Theme.FillRound(e.Graphics, Theme.Surface, bounds, Dpi.S(6));
                Theme.DrawRound(e.Graphics, _search.Focused ? Theme.Accent : Theme.Border, bounds, Dpi.S(6));
            };
            searchBox.Click += (s, e) => _search.Focus();
            _search.Dock = DockStyle.Top;
            _search.BorderStyle = BorderStyle.None;
            _search.BackColor = Theme.Surface;
            _search.ForeColor = Theme.Text;
            Theme.DarkControl(_search);
            _search.AccessibleName = "搜索笔记 / Search notes";
            _search.GotFocus += (s, e) => searchBox.Invalidate();
            _search.LostFocus += (s, e) => searchBox.Invalidate();
            _search.TextChanged += (s, e) => { if (!_loading) { _searchTimer.Stop(); _searchTimer.Start(); } };
            searchBox.Controls.Add(_search);
            searchHost.Controls.Add(searchBox);
            var createBar = new Panel { Dock = DockStyle.Top, Height = Dpi.S(10) };
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = Dpi.S(76), ColumnCount = 1, RowCount = 2,
                Padding = new Padding(0, Dpi.S(8), 0, 0), Margin = Padding.Empty };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            bottom.Controls.Add(SidebarAction("导出 Markdown / Export", ExportMarkdown, true), 0, 0);
            bottom.Controls.Add(SidebarAction("刷新目录 / Refresh", () => RefreshTree(), true), 0, 1);
            bottom.Paint += (s, e) => { using (var pen = new Pen(Theme.Divider)) e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0); };

            _icons.ImageSize = new Size(16, 16);
            _icons.ColorDepth = ColorDepth.Depth32Bit;
            _icons.Images.Add(DrawIcon(true));
            _icons.Images.Add(DrawIcon(false));
            _tree.Dock = DockStyle.Fill;
            _tree.BorderStyle = BorderStyle.None;
            _tree.BackColor = Theme.Sidebar;
            _tree.ForeColor = Theme.Text;
            Theme.DarkControl(_tree);
            _tree.FullRowSelect = true;
            _tree.HideSelection = false;
            _tree.ShowLines = false;
            _tree.ShowNodeToolTips = true;
            _tree.ItemHeight = Dpi.S(34);
            _tree.Indent = Dpi.S(18);
            _tree.ImageList = _icons;
            _tree.AccessibleName = "笔记本目录树 / Notebook tree";
            _tree.MouseMove += (s, e) =>
            {
                var node = _tree.GetNodeAt(e.Location);
                if (_hoverNode == node) return;
                _hoverNode = node;
                _tree.Invalidate();
            };
            _tree.MouseLeave += (s, e) => { _hoverNode = null; _tree.Invalidate(); };
            _tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
            _tree.DrawNode += (s, e) =>
            {
                bool selected = (e.State & TreeNodeStates.Selected) != 0;
                // AI 提示词页使用独立的玫红色调，与普通笔记区分。/ The AI instructions page uses its own rose palette to stand apart from ordinary notes.
                bool prompt = e.Node.Parent == null && e.Node.Tag is NotebookEntry pe && IsAgentPrompt(pe);
                var bounds = new Rectangle(0, e.Bounds.Y, _tree.ClientSize.Width, e.Bounds.Height);
                using (var brush = new SolidBrush(Theme.Sidebar)) e.Graphics.FillRectangle(brush, bounds);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (selected || e.Node == _hoverNode)
                    Theme.FillRound(e.Graphics, selected ? (prompt ? Theme.PromptSelected : Theme.RowSelected) : Theme.RowHover,
                        new RectangleF(0, bounds.Y + Dpi.S(2), bounds.Width - 1, bounds.Height - Dpi.S(4)), Dpi.S(6));
                if (selected || prompt)
                    Theme.FillRound(e.Graphics, prompt ? Theme.PromptAccent : Theme.Accent, new RectangleF(0, bounds.Y + Dpi.S(10), Dpi.S(3), bounds.Height - Dpi.S(20)), Dpi.S(1));
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                int textLeft = e.Node.Bounds.X;
                int iconLeft = textLeft - _icons.ImageSize.Width - Dpi.S(3);
                _icons.Draw(e.Graphics, iconLeft, bounds.Y + (bounds.Height - _icons.ImageSize.Height) / 2, e.Node.ImageIndex);
                if (e.Node.Nodes.Count > 0)
                {
                    int size = Dpi.S(8);
                    var glyph = new Rectangle(iconLeft - _tree.Indent / 2 - size / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
                    using (var pen = new Pen(Theme.TextMuted))
                    {
                        e.Graphics.DrawRectangle(pen, glyph);
                        e.Graphics.DrawLine(pen, glyph.Left + 2, glyph.Top + size / 2, glyph.Right - 2, glyph.Top + size / 2);
                        if (!e.Node.IsExpanded) e.Graphics.DrawLine(pen, glyph.Left + size / 2, glyph.Top + 2, glyph.Left + size / 2, glyph.Bottom - 2);
                    }
                }
                var textBounds = new Rectangle(textLeft, bounds.Y, Math.Max(0, bounds.Width - textLeft), bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Node.Text, _tree.Font, textBounds, prompt ? Theme.PromptText : selected ? Theme.AccentText : Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            _tree.BeforeSelect += BeforeSelect;
            _tree.AfterSelect += (s, e) =>
            {
                if (!_loading) { Display(_pendingDocument); ContentRequested?.Invoke(); }
            };
            _tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node;
                if (e.Node == _tree.SelectedNode) ContentRequested?.Invoke();
            };
            _tree.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ContentRequested?.Invoke(); e.Handled = true; } };
            var menu = new GroupedContextMenuStrip();
            menu.AddGroup("新建 / Create");
            MenuItem(menu, "新建子页面 / New subpage", MenuGlyph.Note, "Ctrl+N", () => Create(true));
            MenuItem(menu, "新建同级页面 / New sibling page", MenuGlyph.Folder, null, () => Create(false));
            menu.AddGroup("整理 / Organize");
            var rename = MenuItem(menu, "重命名 / Rename", MenuGlyph.Rename, "F2", RenameSelected);
            var trash = MenuItem(menu, "移入废纸篓 / Move to trash", MenuGlyph.Trash, "Del", DeleteSelected);
            trash.ForeColor = Theme.Danger;
            menu.Opening += (s, e) =>
            {
                bool entry = Selected != null && Selected.Path.Length > 0;
                rename.Enabled = trash.Enabled = entry;
            };
            Theme.Apply(menu);
            _tree.ContextMenuStrip = menu;
            _tree.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            };
            sidebar.Controls.Add(_tree);
            sidebar.Controls.Add(bottom);
            sidebar.Controls.Add(createBar);
            sidebar.Controls.Add(searchHost);
            sidebar.Controls.Add(title);

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
            _breadcrumb.Dock = DockStyle.Fill;
            _breadcrumb.Padding = new Padding(Dpi.S(24), 0, Dpi.S(16), 0);
            _breadcrumb.TextAlign = ContentAlignment.MiddleLeft;
            _breadcrumb.ForeColor = Theme.TextSecondary;
            _breadcrumb.AutoEllipsis = true;
            _breadcrumb.UseMnemonic = false;
            var header = new Panel { Dock = DockStyle.Top, Height = Dpi.S(45), BackColor = Theme.Background };
            _sourceToggle = ActionButton(SourceLabel, ToggleSource);
            _sourceToggle.AutoSize = false;
            _sourceToggle.Dock = DockStyle.Fill;
            _sourceToggle.Font = Theme.Small;
            _sourceToggle.ForeColor = Theme.TextSecondary;
            _sourceToggle.Margin = Padding.Empty;
            _sourceToggle.AccessibleName = "切换 Markdown 源码 / Toggle Markdown source";
            int toggleWidth = new[] { SourceLabel, RenderedLabel }.Max(t => TextRenderer.MeasureText(t, Theme.Small).Width) + Dpi.S(24);
            var toggleHost = new Panel { Dock = DockStyle.Right, Width = toggleWidth + Dpi.S(16), Padding = new Padding(0, Dpi.S(8), Dpi.S(16), Dpi.S(7)) };
            toggleHost.Controls.Add(_sourceToggle);
            header.Controls.Add(_breadcrumb);
            header.Controls.Add(toggleHost);
            _split.Dock = DockStyle.Fill;
            _split.Size = new Size(Dpi.S(800), Dpi.S(500));
            _split.BackColor = Theme.Divider;
            _split.Panel1.BackColor = Theme.Background;
            _split.Panel2.BackColor = Theme.Background;
            _split.Panel1.Padding = new Padding(Dpi.S(24), Dpi.S(20), Dpi.S(16), Dpi.S(20));
            _split.Panel1MinSize = Dpi.S(120);
            _split.Panel2MinSize = Dpi.S(120);
            _split.SplitterWidth = Dpi.S(3);
            _split.Resize += (s, e) => UpdateLayout();
            _editor.Dock = DockStyle.Fill;
            _editor.Multiline = true;
            _editor.AcceptsReturn = true;
            _editor.AcceptsTab = true;
            _editor.ScrollBars = ScrollBars.Vertical;
            _editor.BorderStyle = BorderStyle.None;
            _editor.Font = new Font("Consolas", 11F);
            _editor.ForeColor = Theme.Text;
            _editor.BackColor = Theme.Background;
            Theme.DarkControl(_editor);
            _editor.MaxLength = NotebookStore.MaxNoteBytes;
            _editor.AccessibleName = "Markdown 编辑器 / Markdown editor";
            _editor.TextChanged += (s, e) =>
            {
                if (_loading || _document == null) return;
                _dirty = true;
                SetStatus("未保存 · 停止输入后自动保存 / Unsaved · Autosave after typing");
                _saveTimer.Stop();
                _saveTimer.Start();
            };
            _editor.GotFocus += (s, e) => { _editing = true; _caretValid = true; };
            _editor.KeyDown += (s, e) =>
            {
                if (((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert)) && PasteImages())
                    e.Handled = e.SuppressKeyPress = true;
                else if (e.KeyCode == Keys.Escape && e.Modifiers == Keys.None)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    EndEdit();
                }
            };
            _editor.AllowDrop = true;
            _editor.DragEnter += (s, e) => e.Effect = _document != null && DroppedImages(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            _editor.DragDrop += (s, e) =>
            {
                var files = DroppedImages(e.Data);
                if (_document == null || files.Length == 0) return;
                BeginEdit();
                _editor.SelectionStart = _editor.GetCharIndexFromPosition(_editor.PointToClient(new Point(e.X, e.Y)));
                _editor.SelectionLength = 0;
                InsertImages(files.Select(f => (Func<string>)(() => _store.ImportImage(_document.Path, f))));
            };
            _split.Panel1.Controls.Add(_editor);
            _preview.Dock = DockStyle.Fill;
            _preview.Error += message => SetStatus(message, true);
            _split.Panel2.Controls.Add(_preview);
            _empty.Dock = DockStyle.Fill;
            _empty.TextAlign = ContentAlignment.MiddleCenter;
            _empty.ForeColor = Theme.TextSecondary;
            _empty.BackColor = Theme.Background;
            _empty.Text = "把想法留下来。\r\n\r\n在左侧选择页面，或右键新建页面（Ctrl+N）；每个页面都可以写内容并包含子页面。\r\n\r\nYour ideas, saved in a local notebook database.\r\nChoose a page, or right-click to create one (Ctrl+N); every page has content and can hold subpages.";
            _status.Dock = DockStyle.Bottom;
            _status.Height = Dpi.S(48);
            _status.Padding = new Padding(Dpi.S(24), 0, Dpi.S(12), 0);
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.AutoEllipsis = true;
            _status.UseMnemonic = false;
            _status.Font = Theme.Small;
            content.Controls.Add(_split);
            content.Controls.Add(_empty);
            content.Controls.Add(header);
            content.Controls.Add(_status);
            Controls.Add(content);
            _sidebarSplitter = new Splitter { Dock = DockStyle.Left, Width = Dpi.S(3), MinSize = Dpi.S(200), MinExtra = Dpi.S(450), BackColor = Theme.Divider };
            Controls.Add(_sidebarSplitter);
            Sidebar = sidebar;
            Controls.Add(sidebar);
            Display(null);
        }

        private static Bitmap DrawIcon(bool folder)
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            using (var pen = new Pen(folder ? Theme.AccentText : Theme.TextSecondary))
            {
                if (folder) { g.DrawRectangle(pen, 2, 5, 12, 8); g.DrawRectangle(pen, 2, 3, 5, 2); }
                else { g.DrawRectangle(pen, 4, 2, 8, 12); g.DrawLine(pen, 6, 6, 10, 6); g.DrawLine(pen, 6, 9, 10, 9); }
            }
            return bitmap;
        }

        private enum MenuGlyph { Note, Folder, Rename, Trash }

        private static ToolStripMenuItem MenuItem(ToolStrip menu, string text, MenuGlyph glyph, string shortcut, Action click)
        {
            var item = new ToolStripMenuItem(text, DrawMenuGlyph(glyph), (s, e) => click()) { ShortcutKeyDisplayString = shortcut };
            menu.Items.Add(item);
            return item;
        }

        private static Bitmap DrawMenuGlyph(MenuGlyph glyph)
        {
            int size = Dpi.S(16);
            float u = size / 16f;
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            using (var pen = new Pen(glyph == MenuGlyph.Trash ? Theme.Danger : Theme.AccentText, 1.4f * u))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                switch (glyph)
                {
                    case MenuGlyph.Note:
                        g.DrawLines(pen, new[] { new PointF(9 * u, 2 * u), new PointF(4 * u, 2 * u), new PointF(4 * u, 14 * u), new PointF(12 * u, 14 * u), new PointF(12 * u, 5 * u), new PointF(9 * u, 2 * u), new PointF(9 * u, 5 * u), new PointF(12 * u, 5 * u) });
                        g.DrawLine(pen, 6.5f * u, 9.5f * u, 9.5f * u, 9.5f * u);
                        g.DrawLine(pen, 8 * u, 8 * u, 8 * u, 11 * u);
                        break;
                    case MenuGlyph.Folder:
                        g.DrawLines(pen, new[] { new PointF(2 * u, 4 * u), new PointF(6 * u, 4 * u), new PointF(7.5f * u, 5.5f * u), new PointF(14 * u, 5.5f * u), new PointF(14 * u, 13 * u), new PointF(2 * u, 13 * u), new PointF(2 * u, 4 * u) });
                        g.DrawLine(pen, 6.5f * u, 9.25f * u, 9.5f * u, 9.25f * u);
                        g.DrawLine(pen, 8 * u, 7.75f * u, 8 * u, 10.75f * u);
                        break;
                    case MenuGlyph.Rename:
                        g.DrawLines(pen, new[] { new PointF(3 * u, 13 * u), new PointF(3.5f * u, 10 * u), new PointF(10.5f * u, 3 * u), new PointF(13 * u, 5.5f * u), new PointF(6 * u, 12.5f * u), new PointF(3 * u, 13 * u) });
                        g.DrawLine(pen, 9 * u, 4.5f * u, 11.5f * u, 7 * u);
                        break;
                    case MenuGlyph.Trash:
                        g.DrawLine(pen, 2.5f * u, 4.5f * u, 13.5f * u, 4.5f * u);
                        g.DrawLines(pen, new[] { new PointF(6 * u, 4.5f * u), new PointF(6.5f * u, 2.5f * u), new PointF(9.5f * u, 2.5f * u), new PointF(10 * u, 4.5f * u) });
                        g.DrawLines(pen, new[] { new PointF(4 * u, 4.5f * u), new PointF(5 * u, 14 * u), new PointF(11 * u, 14 * u), new PointF(12 * u, 4.5f * u) });
                        g.DrawLine(pen, 8 * u, 7 * u, 8 * u, 11.5f * u);
                        break;
                }
            }
            return bitmap;
        }

        private static SidebarButton SidebarAction(string text, Action click, bool ghost = false)
        {
            var button = new SidebarButton { Text = text, Dock = DockStyle.Fill, Ghost = ghost,
                TextAlign = ghost ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter,
                Margin = Padding.Empty, Padding = new Padding(Dpi.S(4), 0, Dpi.S(4), 0) };
            button.Click += (s, e) => click();
            return button;
        }

        private static Button ActionButton(string text, Action click, Color? background = null)
        {
            var button = new Button { Text = text, AutoSize = true, Height = Dpi.S(30), FlatStyle = FlatStyle.Flat,
                BackColor = background ?? Theme.Background, ForeColor = Theme.Text, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
                Margin = new Padding(0, 0, Dpi.S(4), Dpi.S(4)), Padding = new Padding(Dpi.S(4), 0, Dpi.S(4), 0) };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.RowHover;
            button.FlatAppearance.MouseDownBackColor = Theme.RowSelected;
            button.FlatAppearance.BorderColor = Theme.Border;
            button.GotFocus += (s, e) => { button.FlatAppearance.BorderSize = 1; button.FlatAppearance.BorderColor = Theme.Accent; };
            button.LostFocus += (s, e) => button.FlatAppearance.BorderSize = 0;
            button.EnabledChanged += (s, e) => button.ForeColor = button.Enabled ? Theme.Text : Theme.TextMuted;
            button.Click += (s, e) => click();
            return button;
        }

        private NotebookEntry Selected => _tree.SelectedNode?.Tag as NotebookEntry;
        private bool HasPage => Selected != null && Selected.Path.Length > 0;

        private void BeforeSelect(object sender, TreeViewCancelEventArgs e)
        {
            if (_loading) return;
            if (!TrySave()) { e.Cancel = true; return; }
            try
            {
                var entry = (NotebookEntry)e.Node.Tag;
                _pendingDocument = entry.Path.Length == 0 ? null : _store.Read(entry.Path);
            }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); e.Cancel = true; }
        }

        private void Display(NotebookDocument document, bool keepView = false)
        {
            _saveTimer.Stop();
            bool samePage = keepView && document != null && _document != null
                && string.Equals(document.Path, _document.Path, StringComparison.OrdinalIgnoreCase);
            _loading = true;
            if (!samePage && _document != null && document != _document) _previousDocument = _document;
            _document = document;
            _dirty = false;
            if (!samePage) { _editing = false; _caretValid = false; }
            _editor.Text = document == null ? "" : NotebookStore.NormalizeNewLines(document.Text, "\r\n");
            _editor.Enabled = document != null;
            _loading = false;
            _empty.Visible = document == null;
            _split.Visible = document != null;
            if (document == null) _empty.BringToFront();
            _breadcrumb.Text = "笔记本 / Notebooks" + (document == null ? "" : "  /  " + string.Join("  /  ", SafeTitlePath(document.Path)));
            Text = (document == null ? "" : document.Title + " — ") + "笔记本 / Notebooks";
            SetStatus(document == null ? "仅本地存储，不自动上传 / Local only · No automatic upload"
                : NotebookTaskTable.IsJournal(document.Title) ? "已读取 · 任务记录页只读，可点右上角查看源码 / Loaded · Task journal pages are read-only; use Source at the top right"
                : "已读取 · 直接在页面上编辑，Ctrl+单击打开网页链接 / Loaded · Edit right on the page; Ctrl+Click opens web links");
            UpdateLayout();
            RenderPreview(!samePage);
        }

        /// <summary>当前打开页面的快照（含未保存的编辑），没有打开页面时返回 null。/ Snapshot of the open page (including unsaved edits); null when none is open.</summary>
        internal NoteSnapshot CurrentNote()
        {
            if (_document == null) return null;
            return new NoteSnapshot
            {
                Id = _document.Path,
                TitlePath = string.Join(" / ", SafeTitlePath(_document.Path)),
                Text = NotebookStore.NormalizeNewLines(_editor.Text, "\n")
            };
        }

        /// <summary>
        /// 把文字写入当前笔记并保存：编辑过本页时插入到光标处，否则追加到末尾。返回错误信息，null 表示成功。
        /// Writes text into the current note and saves: at the caret if this page was edited, otherwise appended at the end. Returns the error, null on success.
        /// </summary>
        internal string InsertIntoCurrent(string text)
        {
            if (_document == null) return "请先在笔记本中打开一篇笔记 / Open a note first";
            text = NotebookStore.NormalizeNewLines((text ?? "").Trim(), "\r\n");
            if (text.Length == 0) return "没有可插入的内容 / Nothing to insert";
            if (_editor.TextLength + text.Length + 4 > _editor.MaxLength) return "笔记已达长度上限 / The note is at its size limit";
            if (_caretValid)
            {
                int at = Math.Min(_editor.SelectionStart, _editor.TextLength);
                string before = at > 0 && _editor.Text[at - 1] != '\n' ? "\r\n\r\n" : "";
                _editor.Select(at, _editor.SelectionLength);
                _editor.SelectedText = before + text + "\r\n";
            }
            else
            {
                string sep = _editor.TextLength == 0 ? "" : _editor.Text.EndsWith("\r\n\r\n", StringComparison.Ordinal) ? "" : _editor.Text.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\r\n\r\n";
                _editor.AppendText(sep + text + "\r\n");
                _caretValid = true;
            }
            if (!TrySave()) return "已插入，但保存失败，请查看笔记本底部的提示 / Inserted, but saving failed; see the notebook status";
            RenderPreview(false);
            return null;
        }

        internal bool TrySave()
        {
            _saveTimer.Stop();
            if (!_dirty || _document == null) return true;
            try
            {
                _store.Save(_document, _editor.Text);
                _dirty = false;
                SetStatus("已保存 " + DateTime.Now.ToString("HH:mm:ss") + " · Ctrl+S / Saved locally");
                RenderPreview(false);
                return true;
            }
            catch (NotebookConflictException) { return SaveDraftCopy(); }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); return false; }
        }

        /// <summary>
        /// 页面已在别处修改或删除时，自动把草稿另存为同级新页面，之后的编辑写入该副本。
        /// When the page changed or was deleted elsewhere, saves the draft as a new sibling page; later edits go to that copy.
        /// </summary>
        private bool SaveDraftCopy()
        {
            string original = _document.Path;
            string stamp = "-draft-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string title = _document.Title.Length + stamp.Length > NotebookStore.MaxTitleLength
                ? _document.Title.Substring(0, NotebookStore.MaxTitleLength - stamp.Length).TrimEnd() : _document.Title;
            try
            {
                string copy = _store.CreatePage(_store.ParentOf(original), title + stamp, _editor.Text);
                _document = _store.Read(copy);
                _dirty = false;
                SetStatus("页面已在别处修改，草稿已自动另存为「" + _document.Title + "」 / Page changed elsewhere; your draft was saved as \"" + _document.Title + "\"");
                if (IsHandleCreated)
                    BeginInvoke((Action)(() =>
                    {
                        string selected = Selected?.Path;
                        RefreshTree(selected == null || selected == original ? copy : selected);
                    }));
                return true;
            }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); return false; }
        }

        private void RenderPreview(bool scrollTop)
        {
            if (_document == null) { _preview.Render("", true); return; }
            try
            {
                string imageError = null;
                string ImageData(string target)
                {
                    try { return _store.ImageData(_document.Path, target); }
                    catch (Exception ex) when (IsFileError(ex)) { imageError = ex.Message; return null; }
                }
                string html;
                if (NotebookTaskTable.IsJournal(_document.Title))
                {
                    html = NotebookTaskTable.Render(_document.Path, _document.Title, _editor.Text, _store.ReadChildHeaders(_document.Path), ImageData);
                    _preview.Render(html, scrollTop, _document.Path, null, null);
                }
                else
                {
                    html = NotebookMarkdown.RenderEditable(_editor.Text, ImageData, out var spans);
                    _preview.Render(html, scrollTop, _document.Path, _editor.Text, spans);
                }
                if (imageError != null) SetStatus("图片未加载 / Image not loaded: " + imageError, true);
            }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
        }

        /// <summary>
        /// 默认显示渲染后的页面并可直接在其中编辑；点右上角「Markdown 源码」改为只显示源码编辑器，再点一次或按 Esc 返回。
        /// Shows the rendered page by default and edits happen right on it; "Markdown source" at the top right shows the source editor alone, and clicking again or pressing Esc returns.
        /// </summary>
        private void UpdateLayout()
        {
            if (_editing && _document != null) { _split.Panel1Collapsed = false; _split.Panel2Collapsed = true; }
            else { _split.Panel2Collapsed = false; _split.Panel1Collapsed = true; }
            if (_sourceToggle != null)
            {
                _sourceToggle.Text = _editing && _document != null ? RenderedLabel : SourceLabel;
                _sourceToggle.Visible = _document != null;
            }
        }

        private const string SourceLabel = "Markdown 源码 / Source";
        private const string RenderedLabel = "返回页面编辑 / Back to page";

        private void ToggleSource()
        {
            if (_editing) EndEdit();
            else BeginEdit();
        }

        private void BeginEdit()
        {
            if (_document == null) return;
            _editing = true;
            UpdateLayout();
            _editor.Focus();
        }

        private void EndEdit()
        {
            TrySave();
            _editing = false;
            RenderPreview(false);
            UpdateLayout();
            _preview.FocusEditor();
        }

        /// <summary>
        /// 渲染视图中就地编辑后生成的 Markdown：写入当前页并触发自动保存；切换页面后才到达的编辑写回它所属的页面。
        /// Markdown from in-place editing: goes into the current page and triggers autosave; edits arriving after a page switch are saved to their own page.
        /// </summary>
        private void ApplyRenderedEdit(string key, string markdown)
        {
            if (_document != null && string.Equals(key, _document.Path, StringComparison.OrdinalIgnoreCase))
            {
                if (_editing && _dirty) return;
                string text = NotebookStore.NormalizeNewLines(markdown ?? "", "\r\n");
                if (text == _editor.Text) return;
                _editor.Text = text;
                _caretValid = false;
                return;
            }
            var previous = _previousDocument;
            if (previous == null || !string.Equals(key, previous.Path, StringComparison.OrdinalIgnoreCase)) return;
            try { _store.Save(previous, markdown); }
            catch (NotebookConflictException) { SetStatus("切换页面前的最后修改未能保存：页面已在别处修改 / The last edit before switching pages was not saved: the page changed elsewhere", true); }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
        }

        private void RefreshTree(string selectedPath = null, bool keepView = false)
        {
            if (!TrySave()) return;
            try
            {
                var entries = _store.LoadTree(_search.Text);
                string selection = selectedPath ?? Selected?.Path ?? "";
                string signature = TreeSignature(entries);
                // 后台刷新时目录未变就不重建树，避免侧栏整体重绘闪烁。/ Background refreshes skip rebuilding an unchanged tree so the sidebar does not repaint and flicker.
                if (keepView && signature == _treeSignature && Selected != null
                    && string.Equals(Selected.Path, selection, StringComparison.OrdinalIgnoreCase))
                {
                    var current = selection.Length == 0 ? null : _store.Read(selection);
                    bool same = SameDocument(current) || (current != null && _document != null && (_editor.Focused || _preview.EditorFocused)
                        && string.Equals(current.Path, _document.Path, StringComparison.OrdinalIgnoreCase));
                    if (!same) Display(current, true);
                    return;
                }
                _treeSignature = signature;
                var expanded = new HashSet<string>(Nodes(_tree.Nodes).Where(n => n.IsExpanded).Select(n => ((NotebookEntry)n.Tag).Path), StringComparer.OrdinalIgnoreCase);
                string topPath = keepView ? (_tree.TopNode?.Tag as NotebookEntry)?.Path : null;
                var focused = keepView ? FocusedDescendant() : null;
                var root = Node(new NotebookEntry { Name = "笔记本 / Notebooks", Path = "" });
                // 「AI 助手补充提示词」与「笔记本」同级显示为顶级节点，不混在普通笔记里。/ The AI assistant instructions page is shown as a top-level node beside "Notebooks" rather than among ordinary notes.
                var prompt = entries.FirstOrDefault(IsAgentPrompt);
                foreach (var entry in entries) if (entry != prompt) root.Nodes.Add(Node(entry));
                var tops = new List<TreeNode> { root };
                if (prompt != null) tops.Add(Node(prompt));
                var selected = Nodes(tops).FirstOrDefault(n => string.Equals(((NotebookEntry)n.Tag).Path, selection, StringComparison.OrdinalIgnoreCase)) ?? root;
                var item = (NotebookEntry)selected.Tag;
                var document = item.Path.Length == 0 ? null : _store.Read(item.Path);
                _loading = true;
                _tree.BeginUpdate();
                try
                {
                    _tree.Nodes.Clear();
                    _tree.Nodes.AddRange(tops.ToArray());
                    foreach (var node in Nodes(_tree.Nodes))
                        if (expanded.Contains(((NotebookEntry)node.Tag).Path) || _search.Text.Length > 0) node.Expand();
                    root.Expand();
                    _tree.SelectedNode = selected;
                    var top = topPath == null ? null : Nodes(_tree.Nodes).FirstOrDefault(n => string.Equals(((NotebookEntry)n.Tag).Path, topPath, StringComparison.OrdinalIgnoreCase));
                    if (top != null) _tree.TopNode = top;
                    else selected.EnsureVisible();
                }
                finally { _tree.EndUpdate(); _loading = false; }
                bool unchanged = keepView && (SameDocument(document) || (document != null && _document != null && (_editor.Focused || _preview.EditorFocused)
                    && string.Equals(document.Path, _document.Path, StringComparison.OrdinalIgnoreCase)));
                if (!unchanged) Display(document, keepView);
                if (focused != null && !focused.IsDisposed && focused.Visible && focused.CanFocus && !focused.Focused) focused.Focus();
                if (entries.Count == 0 && _search.Text.Length > 0) SetStatus("没有匹配的笔记 / No matching notes");
            }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
        }

        /// <summary>根目录下的「AI 助手补充提示词」页面。/ The root-level AI assistant instructions page.</summary>
        private static bool IsAgentPrompt(NotebookEntry entry) =>
            entry != null && entry.Path.Length > 0 && entry.Name == NotebookAgentPrompt.PageTitle;

        /// <summary>目录结构与搜索词的签名
        private string TreeSignature(IEnumerable<NotebookEntry> entries)
        {
            var sb = new StringBuilder(_search.Text).Append('\u0002');
            void Append(NotebookEntry e)
            {
                sb.Append(e.Path).Append('\u0001').Append(e.Name).Append('(');
                foreach (var child in e.Children) Append(child);
                sb.Append(')');
            }
            foreach (var entry in entries) Append(entry);
            return sb.ToString();
        }

        /// <summary>开启原生双缓冲的目录树，重建或悬停时不闪烁。/ Tree view with native double buffering so rebuilds and hover do not flicker.</summary>
        private sealed class BufferedTreeView : TreeView
        {
            [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // 设置扩展样式：双缓冲 / TVM_SETEXTENDEDSTYLE + TVS_EX_DOUBLEBUFFER
                SendMessage(Handle, 0x112C, (IntPtr)0x0004, (IntPtr)0x0004);
            }
        }

        /// <summary>当前显示的页面与读取结果是否为同一版本。/ Whether the shown page is the same version as the one just read.</summary>
        private bool SameDocument(NotebookDocument document) =>
            document == null ? _document == null
                : _document != null && string.Equals(document.Path, _document.Path, StringComparison.OrdinalIgnoreCase)
                    && document.Version == _document.Version && document.Text == _document.Text;

        /// <summary>本工作区内当前拥有焦点的控件。/ The control inside this workspace that currently has focus.</summary>
        private Control FocusedDescendant() =>
            ContainsFocus ? Controls.Cast<Control>().SelectMany(AllControls).FirstOrDefault(x => x.Focused) : null;

        private static IEnumerable<Control> AllControls(Control c)
        {
            yield return c;
            foreach (Control child in c.Controls)
                foreach (var x in AllControls(child)) yield return x;
        }

        private static TreeNode Node(NotebookEntry entry)
        {
            var node = new TreeNode(entry.Name) { Tag = entry, ToolTipText = entry.Path, ImageIndex = entry.HasChildren || entry.Path.Length == 0 ? 0 : 1, SelectedImageIndex = entry.HasChildren || entry.Path.Length == 0 ? 0 : 1 };
            foreach (var child in entry.Children) node.Nodes.Add(Node(child));
            return node;
        }

        private static IEnumerable<TreeNode> Nodes(System.Collections.IEnumerable nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                foreach (var child in Nodes(node.Nodes)) yield return child;
            }
        }

        /// <summary>新建子页面或同级页面；未选中页面时建在根下。/ Creates a subpage or sibling page; at the root when no page is selected.</summary>
        private void Create(bool child)
        {
            if (!TrySave()) return;
            string name = AskName(child && HasPage ? "新建子页面 / New subpage" : "新建页面 / New page", "");
            if (name == null) return;
            try
            {
                string parent = !HasPage ? "" : child ? Selected.Path : _store.ParentOf(Selected.Path);
                string path = _store.CreatePage(parent, name);
                ClearSearch();
                RefreshTree(path);
                ContentRequested?.Invoke();
                _preview.FocusEditor();
            }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
        }

        private void RenameSelected()
        {
            if (!HasPage) { SetStatus("请先选择页面 / Select a page first"); return; }
            if (!TrySave()) return;
            string name = AskName("重命名 / Rename", Selected.Name);
            if (name == null) return;
            try { string path = _store.Rename(Selected.Path, name); ClearSearch(); RefreshTree(path); }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
        }

        private void DeleteSelected()
        {
            if (!HasPage) { SetStatus("请先选择页面 / Select a page first"); return; }
            if (!TrySave()) return;
            if (MessageBox.Show(this, "连同子页面一起移入废纸篓；数据仍保留在本地笔记数据库中。\r\nMove to trash together with subpages; the data stays in the local notebook database.\r\n\r\n" + Selected.Name,
                "移入废纸篓 / Move to trash", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { _store.Trash(Selected.Path); RefreshTree(""); }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
        }

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        private static string[] DroppedImages(IDataObject data) =>
            data?.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && File.Exists(f)).ToArray()
                : new string[0];

        /// <summary>粘贴剪贴板中的截图或图片文件；没有图片时返回 false，按普通文本粘贴。/ Pastes a clipboard screenshot or image files; returns false to fall back to text paste.</summary>
        private bool PasteImages()
        {
            var imports = ClipboardImageImports();
            if (imports == null) return false;
            InsertImages(imports);
            return true;
        }

        /// <summary>在渲染视图中粘贴图片：导入笔记库后插入到页面光标处。/ Pastes images in the rendered view: imports them into the library and inserts them at the page caret.</summary>
        private void PasteImagesIntoPreview()
        {
            var imports = ClipboardImageImports();
            if (imports == null || _document == null) return;
            var images = new List<KeyValuePair<string, string>>();
            foreach (var import in imports)
            {
                try
                {
                    string target = import();
                    string data = _store.ImageData(_document.Path, target);
                    if (data != null) images.Add(new KeyValuePair<string, string>(target, data));
                }
                catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
            }
            _preview.InsertImages(images);
        }

        /// <summary>剪贴板中图片的导入操作；没有图片时返回 null。/ Import actions for clipboard images; null when there are none.</summary>
        private Func<string>[] ClipboardImageImports()
        {
            if (_document == null) return null;
            try
            {
                var files = Clipboard.ContainsFileDropList() ? DroppedImages(Clipboard.GetDataObject()) : new string[0];
                if (files.Length > 0) return files.Select(f => (Func<string>)(() => _store.ImportImage(_document.Path, f))).ToArray();
                if (!Clipboard.ContainsImage()) return null;
                using (var image = Clipboard.GetImage())
                using (var stream = new MemoryStream())
                {
                    if (image == null) return null;
                    image.Save(stream, ImageFormat.Png);
                    byte[] bytes = stream.ToArray();
                    return new Func<string>[] { () => _store.ImportImage(_document.Path, bytes, ".png") };
                }
            }
            catch (ExternalException ex) { Report(ex); return new Func<string>[0]; }
        }

        private void InsertImages(IEnumerable<Func<string>> imports)
        {
            var text = new StringBuilder();
            foreach (var import in imports)
            {
                try { text.Append("\r\n![图片 / Image](").Append(import()).Append(")\r\n"); }
                catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
            }
            if (text.Length == 0) return;
            _editor.SelectedText = text.ToString();
            _editor.Focus();
        }

        /// <summary>把全部页面导出为 Markdown 目录并打开。/ Exports all pages to a Markdown folder and opens it.</summary>
        private void ExportMarkdown()
        {
            if (!TrySave()) return;
            try
            {
                string folder = _store.ExportMarkdown();
                SetStatus("已导出为 Markdown / Exported as Markdown");
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (IsFileError(ex)) { Report(ex); }
            catch (System.ComponentModel.Win32Exception ex) { Report(ex); }
        }

        private IEnumerable<string> SafeTitlePath(string id)
        {
            try { return _store.TitlePath(id); }
            catch (Exception ex) when (IsFileError(ex)) { return new[] { "…" }; }
        }

        private void ClearSearch()
        {
            _loading = true;
            _searchTimer.Stop();
            _search.Clear();
            _loading = false;
        }

        private string AskName(string title, string value)
        {
            using (var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Dpi.S(460), Dpi.S(125)),
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = Font, BackColor = Theme.Sidebar, ForeColor = Theme.Text })
            {
                dialog.HandleCreated += (s, e) => Theme.DarkTitleBar(dialog);
                var input = new TextBox { Text = value, Left = Dpi.S(20), Top = Dpi.S(20), Width = Dpi.S(420), MaxLength = 120,
                    BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
                Theme.DarkControl(input);
                var ok = ActionButton("确定 / OK", () => { }, Theme.AccentLight);
                ok.ForeColor = Theme.AccentText;
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(Dpi.S(230), Dpi.S(70), Dpi.S(100), Dpi.S(30));
                var cancel = ActionButton("取消 / Cancel", () => { }, Theme.Sidebar);
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(Dpi.S(340), Dpi.S(70), Dpi.S(100), Dpi.S(30));
                dialog.Controls.AddRange(new Control[] { input, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.Shown += (s, e) => { input.Focus(); input.SelectAll(); };
                return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
            }
        }

        private static bool IsFileError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException;
        private void Report(Exception ex) => SetStatus(ex.Message, true);
        private void SetStatus(string text, bool error = false)
        {
            _status.Text = text;
            _status.ForeColor = error ? Theme.Danger : Theme.TextSecondary;
            _status.AccessibleDescription = text;
            if (error) Error?.Invoke(text);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _saveTimer.Dispose();
                _searchTimer.Dispose();
                _tree.ContextMenuStrip?.Dispose();
                _editor.Font.Dispose();
                Sidebar?.Dispose();
                _icons.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
