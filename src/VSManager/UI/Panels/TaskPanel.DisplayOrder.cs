using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    public sealed partial class TaskPanel
    {
        private bool _manualOrder;
        private List<string> _itemOrder = new List<string>();
        private List<string> _groupOrder = new List<string>();
        private List<string> _knownItemKeys = new List<string>();
        private object[] _displayItems = new object[0];
        public bool ManualOrder => _manualOrder;
        public List<string> ItemOrder => new List<string>(_itemOrder);
        public List<string> GroupOrder => new List<string>(_groupOrder);
        private string DisplayOrderNotice => (_manualOrder ? "条目：手动显示 / Entries: manual display" : "条目：默认显示 / Entries: default display")
            + "\r\n" + (_groupSort == TaskGrouping.SortManual ? "分组：手动显示 / Groups: manual display" : _groupSort == TaskGrouping.SortByNumber
                ? "分组：VS 编号 / Groups: VS number" : "分组：执行中优先、最近活动 / Groups: running first, latest activity")
            + "\r\n" + TaskDisplayOrder.DisplayOnly + "\r\n实际执行仍按编号及前序规则；拖动调整显示顺序，右键「恢复默认排序」还原 / Execution still follows IDs and predecessors; drag to reorder, right-click Reset order to restore";

        private sealed class DisplayDrag
        {
            internal string Key, GroupKey;
            internal bool Header, Grouped;
        }

        private void ConfigureDrag()
        {
            _list.HeaderClick = ToggleGroup;
            _list.CreateDrag = item =>
            {
                string key = TaskDisplayOrder.KeyOf(item);
                if (key == null) return null;
                var rows = _list.Items.Cast<object>().ToArray();
                return new DisplayDrag { Key = key, Header = item is TaskGroupHeader, Grouped = _groupByVs, GroupKey = GroupOf(rows, key) };
            };
            _list.PreviewDrop = PreviewDisplayDrop;
            _list.CommitDrop = CommitDisplayDrop;
        }

        private static string GroupOf(IEnumerable<object> rows, string key)
        {
            string group = null;
            foreach (var row in rows)
            {
                if (row is TaskGroupHeader h) group = h.Key;
                if (TaskDisplayOrder.KeyOf(row) == key) return group;
            }
            return null;
        }

        private int PreviewDisplayDrop(object payload, Point point)
        {
            var drag = payload as DisplayDrag;
            var rows = _list.Items.Cast<object>().ToArray();
            if (drag == null || drag.Grouped != _groupByVs || !drag.Header && drag.GroupKey != GroupOf(rows, drag.Key))
            {
                _tips.SetToolTip(_list, TaskDisplayOrder.StaleDrag);
                return -1;
            }
            if (!_list.ClientRectangle.Contains(point)) return -1;
            int hit = _list.IndexFromPoint(point);
            bool after = false;
            if (hit < 0 || hit >= rows.Length) hit = rows.Length;
            else
            {
                var bounds = _list.GetItemRectangle(hit);
                after = point.Y >= bounds.Top + bounds.Height / 2;
            }
            int slot = TaskDisplayOrder.DropSlot(rows, drag.Key, drag.Header, drag.Grouped, hit, after, out string reason);
            _tips.SetToolTip(_list, reason ?? TaskDisplayOrder.DisplayOnly);
            return slot;
        }

        private void CommitDisplayDrop(object payload, Point point)
        {
            // 拖拽的消息循环中队列可能更新；松开时重新验证身份、分组和目标。/ The queue may change in the drag message loop; revalidate identity, group and target on release.
            Reload();
            int slot = PreviewDisplayDrop(payload, point);
            if (slot < 0 || !(payload is DisplayDrag drag)) return;
            var rows = _list.Items.Cast<object>().ToArray();
            var visible = drag.Header ? rows.OfType<TaskGroupHeader>().Select(h => h.Key).ToList()
                : rows.Where(x => !(x is TaskGroupHeader)).Select(TaskDisplayOrder.KeyOf).Where(k => k != null).ToList();
            int from = visible.IndexOf(drag.Key);
            int to = rows.Take(slot).Count(x => (x is TaskGroupHeader) == drag.Header && TaskDisplayOrder.KeyOf(x) != null);
            if (from < 0) return;
            if (from < to) to--;
            if (from == to) return;
            visible.RemoveAt(from);
            visible.Insert(to, drag.Key);
            if (drag.Header)
            {
                _groupOrder = TaskDisplayOrder.MergeVisible(_groupOrder, _groupKeys, visible);
                _groupSort = TaskGrouping.SortManual;
            }
            else
            {
                // 分组视图只改本组成员，其他分组及折叠项的槽位不变。/ In grouped view change only this group's slots, preserving other and collapsed groups.
                if (_groupByVs)
                {
                    var members = new HashSet<string>(rows.SkipWhile(x => !(x is TaskGroupHeader h) || h.Key != drag.GroupKey)
                        .Skip(1).TakeWhile(x => !(x is TaskGroupHeader)).Select(TaskDisplayOrder.KeyOf), StringComparer.Ordinal);
                    visible = visible.Where(members.Contains).ToList();
                }
                var baseline = TaskDisplayOrder.MergeVisible(_itemOrder, _knownItemKeys, _displayItems.Select(TaskDisplayOrder.KeyOf));
                _itemOrder = TaskDisplayOrder.MergeVisible(baseline, _knownItemKeys, visible);
                _manualOrder = true;
            }
            UpdateViewButton();
            Reload();
            ViewOptionsChanged?.Invoke();
        }

        internal void SetManualOrder(bool manual)
        {
            if (_manualOrder == manual) return;
            if (manual && _itemOrder.Count == 0)
                _itemOrder = TaskDisplayOrder.MergeVisible(null, _knownItemKeys, _displayItems.Select(TaskDisplayOrder.KeyOf));
            _manualOrder = manual;
            UpdateViewButton();
            Reload();
            ViewOptionsChanged?.Invoke();
        }

        internal void ResetDisplayOrder()
        {
            _manualOrder = false;
            _itemOrder.Clear();
            _groupOrder.Clear();
            _groupSort = TaskGrouping.SortByActivity;
            UpdateViewButton();
            Reload();
            ViewOptionsChanged?.Invoke();
        }
    }
}
