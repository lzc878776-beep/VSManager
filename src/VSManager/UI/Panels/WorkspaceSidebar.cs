using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VSManager
{
    internal sealed class WorkspaceSidebar : UserControl
    {
        private const int AgentRow = 0, VsHeaderRow = 1, VsBodyRow = 2, NotesHeaderRow = 3, NotesBodyRow = 4, FillerRow = 5;
        private readonly TableLayoutPanel _rows = new TableLayoutPanel();
        private readonly Control _vsBody, _notionBody;
        private readonly SidebarButton _vsToggle, _notionToggle;
        private readonly Func<int> _vsPreferredHeight;
        private readonly int _agentHeight;
        private bool _agentVisible = true;
        internal bool VsCollapsed { get; private set; }
        internal bool NotionCollapsed { get; private set; }
        internal event Action NotebookRequested;

        /// <param name="vsPreferredHeight">VS 列表内容所需高度；提供后列表按内容收缩，折叠笔记本时标题紧随其后。
        /// / Height the VS list content needs; when set, the list shrinks to fit so a collapsed notebook header follows it.</param>
        public WorkspaceSidebar(Control vsBody, Control agent, Control notionBody, Label count, Control refresh, Func<int> vsPreferredHeight = null)
        {
            BackColor = Theme.Sidebar;
            ForeColor = Theme.Text;
            Font = Theme.Regular;
            Padding = new Padding(0, Dpi.S(2), 0, Dpi.S(6));
            _vsBody = vsBody;
            _notionBody = notionBody;
            _vsPreferredHeight = vsPreferredHeight;
            _agentHeight = agent.Height;
            _rows.Dock = DockStyle.Fill;
            _rows.ColumnCount = 1;
            _rows.RowCount = 6;
            _rows.Margin = Padding.Empty;
            _rows.AutoScroll = true;
            _rows.BackColor = Theme.Sidebar;
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++) _rows.RowStyles.Add(new RowStyle());
            var vsHeader = CreateHeader("VS 线程 / VS", Theme.Accent, () => SetVsCollapsed(!VsCollapsed), out _vsToggle, out var vsContent);
            _vsToggle.AccessibleName = "折叠 VS 线程 / Collapse VS";
            _vsToggle.Click += (s, e) => SetVsCollapsed(!VsCollapsed);
            count.Dock = DockStyle.Right;
            count.AutoSize = false;
            count.Width = Dpi.S(26);
            count.Font = Theme.Small;
            count.ForeColor = Theme.AccentText;
            count.BackColor = Theme.SectionHeader;
            count.TextAlign = ContentAlignment.MiddleCenter;
            count.TextChanged += (s, e) => UpdateRows();
            refresh.Dock = DockStyle.Right;
            refresh.Width = Dpi.S(28);
            vsContent.Controls.Add(count);
            vsContent.Controls.Add(refresh);
            var notionHeader = CreateHeader("笔记本 / Notes", Theme.NotesAccent, () =>
            {
                SetNotionCollapsed(false);
                NotebookRequested?.Invoke();
            }, out _notionToggle, out _);
            _notionToggle.AccessibleName = "折叠笔记本 / Collapse notebooks";
            _notionToggle.Click += (s, e) => SetNotionCollapsed(!NotionCollapsed);
            Add(agent, AgentRow);
            Add(vsHeader, VsHeaderRow);
            Add(vsBody, VsBodyRow);
            Add(notionHeader, NotesHeaderRow);
            Add(notionBody, NotesBodyRow);
            Controls.Add(_rows);
            Resize += (s, e) => UpdateRows();
            UpdateRows();
        }

        private static Panel CreateHeader(string text, Color accent, Action click, out SidebarButton toggle, out Panel content)
        {
            // 外层画圆角色块，内层承载控件并完全落在圆角内部，避免子控件露出直角
            // The outer panel paints the rounded band; the inner panel holds the controls fully inside it so no square corners show
            var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar,
                Padding = new Padding(Dpi.S(20), Dpi.S(9), Dpi.S(16), Dpi.S(5)) };
            header.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var band = new RectangleF(Dpi.S(10) + 0.5f, Dpi.S(5) + 0.5f, header.Width - Dpi.S(20) - 1, header.Height - Dpi.S(6) - 1);
                Theme.FillRound(g, Theme.SectionHeader, band, Dpi.S(8));
                Theme.DrawRound(g, Theme.Divider, band, Dpi.S(8));
                Theme.FillRound(g, accent, new RectangleF(band.X + Dpi.S(4), band.Y + Dpi.S(9), Dpi.S(3), Math.Max(0, band.Height - Dpi.S(18))), Dpi.S(1));
            };
            header.Resize += (s, e) => header.Invalidate();
            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SectionHeader };
            toggle = new SidebarButton { Dock = DockStyle.Left, Width = Dpi.S(26), Ghost = true, ForeColor = accent,
                Font = new Font(Theme.FontName, 10F), Text = "▾", TextAlign = ContentAlignment.MiddleCenter, Padding = Padding.Empty };
            var title = new SidebarButton { Text = text, Dock = DockStyle.Fill, Ghost = true,
                Font = Theme.SemiBold, ForeColor = Theme.Text, Padding = new Padding(Dpi.S(4), 0, Dpi.S(4), 0) };
            title.Click += (s, e) => click();
            content.Controls.Add(title);
            content.Controls.Add(toggle);
            header.Controls.Add(content);
            return header;
        }

        private void Add(Control control, int row)
        {
            control.Dock = DockStyle.Fill;
            control.Margin = Padding.Empty;
            _rows.Controls.Add(control, 0, row);
        }

        internal void SetVsCollapsed(bool collapsed)
        {
            VsCollapsed = collapsed;
            _vsToggle.Text = collapsed ? "▸" : "▾";
            _vsToggle.AccessibleName = collapsed ? "展开 VS 线程 / Expand VS" : "折叠 VS 线程 / Collapse VS";
            _vsBody.Visible = !collapsed;
            UpdateRows();
        }

        internal void SetNotionCollapsed(bool collapsed)
        {
            NotionCollapsed = collapsed;
            _notionToggle.Text = collapsed ? "▸" : "▾";
            _notionToggle.AccessibleName = collapsed ? "展开笔记本 / Expand notebooks" : "折叠笔记本 / Collapse notebooks";
            _notionBody.Visible = !collapsed;
            UpdateRows();
        }

        internal void SetAgentVisible(bool visible)
        {
            _agentVisible = visible;
            UpdateRows();
        }

        /// <summary>侧栏行高度：AI → VS → 笔记本；笔记本折叠时剩余空间放在末尾而不是把标题推到底部。行高未变化时不重新布局，避免切换页面时侧栏闪烁。
        /// / Row heights: AI → VS → notebooks; when notebooks are collapsed the spare space goes last instead of pushing the header down. Skips relayout when nothing changed so switching pages does not make the sidebar flicker.</summary>
        private void UpdateRows()
        {
            int header = Dpi.S(48), agent = _agentVisible ? _agentHeight : 0, minVs = Dpi.S(120);
            var styles = new (SizeType Type, float Height)[6];
            styles[AgentRow] = (SizeType.Absolute, agent);
            styles[VsHeaderRow] = (SizeType.Absolute, header);
            styles[NotesHeaderRow] = (SizeType.Absolute, header);
            styles[NotesBodyRow] = NotionCollapsed ? (SizeType.Absolute, 0f) : (SizeType.Percent, 65f);
            bool fitVs = _vsPreferredHeight != null;
            if (VsCollapsed) styles[VsBodyRow] = (SizeType.Absolute, 0f);
            else if (fitVs)
            {
                int available = Math.Max(minVs, _rows.ClientSize.Height - header * 2 - agent);
                int cap = NotionCollapsed ? available : Math.Max(minVs, (int)(available * 0.45));
                styles[VsBodyRow] = (SizeType.Absolute, Math.Min(cap, Math.Max(minVs, _vsPreferredHeight())));
            }
            else styles[VsBodyRow] = (SizeType.Percent, NotionCollapsed ? 100f : 35f);
            bool filler = NotionCollapsed && (VsCollapsed || fitVs);
            styles[FillerRow] = filler ? (SizeType.Percent, 100f) : (SizeType.Absolute, 0f);
            var minSize = new Size(0, header * 2 + agent + (VsCollapsed ? 0 : minVs) + (NotionCollapsed ? 0 : Dpi.S(310)));

            bool changed = _rows.AutoScrollMinSize != minSize;
            for (int i = 0; i < styles.Length && !changed; i++)
                changed = _rows.RowStyles[i].SizeType != styles[i].Type || _rows.RowStyles[i].Height != styles[i].Height;
            if (!changed) return;
            _rows.SuspendLayout();
            try
            {
                if (_rows.AutoScrollMinSize != minSize) _rows.AutoScrollMinSize = minSize;
                for (int i = 0; i < styles.Length; i++) SetRow(i, styles[i].Type, styles[i].Height);
            }
            finally { _rows.ResumeLayout(true); }
        }

        private void SetRow(int row, SizeType sizeType, float height)
        {
            var style = _rows.RowStyles[row];
            if (style.SizeType != sizeType) style.SizeType = sizeType;
            if (style.Height != height) style.Height = height;
        }
    }
}