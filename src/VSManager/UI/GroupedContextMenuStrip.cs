using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>带非交互分组标题的统一菜单；原有 Opening 处理后更新空组。/ Consistent menu with non-interactive group headings; refreshes empty groups after existing Opening handlers.</summary>
    internal class GroupedContextMenuStrip : ContextMenuStrip
    {
        public GroupedContextMenuStrip()
        {
            Theme.Apply(this);
            Padding = new Padding(Dpi.S(4));
            ImageScalingSize = new Size(Dpi.S(16), Dpi.S(16));
            ShowImageMargin = true;
            ShowCheckMargin = false;
        }

        public void AddGroup(string title) => Items.Add(new MenuGroupHeader(title));

        protected override void OnOpening(CancelEventArgs e)
        {
            base.OnOpening(e);
            RefreshGroups();
        }

        internal void RefreshGroups()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                item.ImageAlign = ContentAlignment.MiddleCenter;
                item.TextAlign = ContentAlignment.MiddleLeft;
                item.Padding = new Padding(Dpi.S(6), Dpi.S(4), Dpi.S(10), Dpi.S(4));
                if (!(item is MenuGroupHeader header)) continue;
                bool hasItems = false;
                for (int j = i + 1; j < Items.Count && !(Items[j] is MenuGroupHeader); j++)
                {
                    // 菜单打开前 Visible 为 false；Available 才反映条目自身的显示设置。/ Before opening, Visible is false; Available reflects the item's own visibility setting.
                    if (Items[j].Available && !(Items[j] is ToolStripSeparator)) hasItems = true;
                }
                header.Available = hasItems;
            }
        }
    }

    /// <summary>仅用于分类，不承载菜单操作。/ A category heading, never a menu action.</summary>
    internal sealed class MenuGroupHeader : ToolStripLabel
    {
        public MenuGroupHeader(string text) : base(text)
        {
            Enabled = false;
            Font = Theme.Small;
            ForeColor = Theme.TextSecondary;
            Margin = new Padding(0, Dpi.S(4), 0, 0);
        }
    }
}
