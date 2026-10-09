using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    /// <summary>
    /// AI 助手对话的进程下拉框：「全部进程」加上记录中出现过的各个 VSManager 进程（新 → 旧），选中后只显示该进程的聊天记录。
    /// Process dropdown for the AI assistant chat: "All processes" plus every VSManager process seen in the records (newest first);
    /// selecting one shows only that process's chat records.
    /// </summary>
    public sealed class ProcessFilterCombo : DarkCombo
    {
        public const string AllText = "全部进程 / All processes";

        private sealed class Item
        {
            public string Key;
            public string Text;
            public override string ToString() => Text;
        }

        private List<string> _keys = new List<string>();
        private bool _updating;

        /// <summary>筛选条件改变（用户选择）。/ Raised when the user changes the filter.</summary>
        public event Action FilterChanged;

        public ProcessFilterCombo()
        {
            Items.Add(new Item { Key = null, Text = AllText });
            SelectedIndex = 0;
            SelectedIndexChanged += (s, e) => { if (!_updating) FilterChanged?.Invoke(); };
        }

        /// <summary>选中的进程标识；null 表示全部。/ Selected process key; null means all.</summary>
        public string SelectedKey => (SelectedItem as Item)?.Key;

        /// <summary>按时间正序给出各条记录的进程标识，更新下拉项并尽量保持当前选择。/ Takes per-record process keys in chronological order, updates the items and keeps the selection where possible.</summary>
        public void SetProcesses(IEnumerable<string> chronologicalKeys)
        {
            var keys = Ordered(chronologicalKeys);
            if (keys.SequenceEqual(_keys)) return;
            _keys = keys;
            string keep = SelectedKey;
            _updating = true;
            try
            {
                BeginUpdate();
                Items.Clear();
                Items.Add(new Item { Key = null, Text = AllText });
                foreach (var k in keys) Items.Add(new Item { Key = k, Text = AgentChatLog.DescribeProcess(k) });
                int index = keep == null ? 0 : Items.Cast<Item>().ToList().FindIndex(i => i.Key == keep);
                bool lost = index < 0;
                SelectedIndex = lost ? 0 : index;
                EndUpdate();
                if (lost) { _updating = false; FilterChanged?.Invoke(); }
            }
            finally { _updating = false; }
        }

        /// <summary>选择指定进程（null 为全部）。/ Selects a process (null = all).</summary>
        public void Select(string key)
        {
            int index = Items.Cast<Item>().ToList().FindIndex(i => i.Key == key);
            if (index >= 0) SelectedIndex = index;
        }

        /// <summary>去重并按最近出现排序（新 → 旧）。/ Distinct keys ordered by latest appearance (newest first).</summary>
        public static List<string> Ordered(IEnumerable<string> chronologicalKeys)
        {
            var list = new List<string>();
            foreach (var k in chronologicalKeys ?? Enumerable.Empty<string>())
            {
                string key = k ?? "";
                list.Remove(key);
                list.Add(key);
            }
            list.Reverse();
            return list;
        }
    }
}
