using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    /// <summary>当前已打开的 VS（用于分组标题与编号）。/ A currently open VS (used for group titles and numbers).</summary>
    public sealed class TaskGroupVs
    {
        public string Key;
        public string Name;
        /// <summary>左侧 VS 列表中的编号（从 1 开始）。/ Number in the VS list on the left (1-based).</summary>
        public int Number;
        /// <summary>实例键（<see cref="VsInstance.InstanceKey"/>）；同一解决方案被多个 VS 打开时用它区分分组。/ Instance key; tells groups apart when one solution is open in several VS.</summary>
        public string InstanceKey;
        /// <summary>进程 ID，用于归属手动对话。/ Process ID, used to place manual chats.</summary>
        public int Pid;
    }

    /// <summary>
    /// 任务清单中的分组标题行：不是任务条目，不可选中，点击折叠 / 展开。
    /// Group header row in the task list: not a task entry, never selectable, click to collapse / expand.
    /// </summary>
    public sealed class TaskGroupHeader
    {
        public string Key;
        public string Title;
        /// <summary>VS 编号；0 表示未打开或「等待打开」分组。/ VS number; 0 when not open or for the "waiting to open" group.</summary>
        public int Number;
        public bool IsOpen;
        public bool IsWaitingOpen;
        public bool Collapsed;
        public int Tasks, Chats, Running, Queued, Parked, Failed, Done, Unverified;
        public DateTime LastActivity;

        public bool HasRunning => Running > 0;

        /// <summary>统计文字（中文在前，英文在后）。/ Statistics text (Chinese first, then English).</summary>
        public string StatsText
        {
            get
            {
                var zh = new List<string>();
                var en = new List<string>();
                if (Tasks > 0 || Chats == 0) { zh.Add(Tasks + " 个任务"); en.Add(Tasks + (Tasks == 1 ? " task" : " tasks")); }
                if (Chats > 0) { zh.Add(Chats + " 个对话"); en.Add(Chats + (Chats == 1 ? " chat" : " chats")); }
                if (Running > 0) { zh.Add(Running + " 个执行中"); en.Add(Running + " running"); }
                if (Queued > 0) { zh.Add(Queued + " 个排队"); en.Add(Queued + " queued"); }
                if (Parked > 0) { zh.Add(Parked + " 个待打开"); en.Add(Parked + " waiting"); }
                if (Failed > 0) { zh.Add(Failed + " 个失败"); en.Add(Failed + " failed"); }
                return string.Join("，", zh) + " / " + string.Join(", ", en);
            }
        }

        public override string ToString() => Title;
    }

    /// <summary>
    /// 任务清单按目标 VS 分组的纯逻辑（只影响显示，不改变任务本身）。
    /// 分组键 = 任务的 VsKey（解决方案路径，大小写与分隔符不敏感）；「等待目标 VS」且目标未打开的任务归入单独的「等待打开」分组。
    /// 同一解决方案被多个 VS 打开时，每个 VS 各自成组（键为实例键），任务按其记录的目标实例归组，不再全部归到第一个 VS 名下。
    /// Pure logic for grouping the task list by target VS (display only; tasks are untouched).
    /// Group key = the task's VsKey (solution path, case- and separator-insensitive); "waiting for VS" tasks whose target is not
    /// open go to a separate "waiting to open" group. When one solution is open in several VS, each VS gets its own group (keyed
    /// by instance key) and tasks follow their recorded target instance instead of all landing under the first VS.
    /// </summary>
    public static class TaskGrouping
    {
        public const string SortByActivity = "activity";
        public const string SortByNumber = "number";
        public const string SortManual = "manual";
        /// <summary>「等待打开」分组的键。/ Key of the "waiting to open" group.</summary>
        public const string WaitingOpenKey = "group:waiting-open";
        /// <summary>折叠状态最多保存的分组数。/ Maximum number of collapsed groups remembered.</summary>
        public const int MaxCollapsed = 100;

        public static string NormalizeSort(string sort) =>
            string.Equals(sort, SortManual, StringComparison.OrdinalIgnoreCase) ? SortManual :
            string.Equals(sort, SortByNumber, StringComparison.OrdinalIgnoreCase) ? SortByNumber : SortByActivity;

        public static List<string> NormalizeCollapsed(IEnumerable<string> keys) =>
            (keys ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(NormalizeKey)
                .Distinct(StringComparer.Ordinal).Take(MaxCollapsed).ToList();

        /// <summary>规范化分组键：去空白、统一分隔符、去掉末尾分隔符、转小写。/ Normalizes a group key: trimmed, unified separators, no trailing separator, lower case.</summary>
        public static string NormalizeKey(string key)
        {
            string k = (key ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
            return k.ToLowerInvariant();
        }

        /// <summary>条目的分组键。/ Group key of an item.</summary>
        public static string KeyOf(object item, ISet<string> openKeys)
        {
            if (item is QueuedTask t)
            {
                string k = NormalizeKey(t.VsKey);
                if (t.Status == QueueStatus.WaitingVs && (k.Length == 0 || openKeys == null || !openKeys.Contains(k))) return WaitingOpenKey;
                return k.Length == 0 ? NormalizeKey("name:" + t.VsName) : k;
            }
            if (item is ExternalChat c)
            {
                string k = NormalizeKey(c.VsKey);
                return k.Length == 0 ? NormalizeKey("name:" + c.VsName) : k;
            }
            return "";
        }

        /// <summary>打开的 VS 对应的分组键：解决方案只被一个 VS 打开时沿用解决方案键，否则用实例键。/ Group key of an open VS: the solution key when only one VS has it open, otherwise the instance key.</summary>
        private static string GroupKeyOf(TaskGroupVs v, Dictionary<string, List<TaskGroupVs>> bySolution)
        {
            string solution = NormalizeKey(v.Key);
            if (!bySolution.TryGetValue(solution, out var list) || list.Count < 2) return solution;
            string inst = NormalizeKey(v.InstanceKey);
            return inst.Length > 0 ? inst : NormalizeKey("pid:" + v.Pid + ":" + solution);
        }

        /// <summary>
        /// 解决方案被多个 VS 打开时条目所属的分组：@ 指定的实例 → 记录的目标实例 → 手动对话的进程 → 名称唯一匹配；
        /// 都对不上（例如目标实例已关闭）时留在解决方案键下，显示为未打开的该名称分组，而不是并入某个 VS。
        /// Group of an item whose solution is open in several VS: the @-chosen instance → the recorded target instance → the
        /// manual chat's process → a unique name match; when nothing matches (e.g. the target instance has closed) it stays under
        /// the solution key, shown as a not-open group with its own name, instead of being merged into some VS.
        /// </summary>
        private static string SharedKeyOf(object item, string solutionKey, List<TaskGroupVs> shared, Dictionary<string, List<TaskGroupVs>> bySolution)
        {
            TaskGroupVs hit = null;
            string name = null;
            if (item is QueuedTask t)
            {
                string inst = t.ExplicitInstanceKey ?? t.TargetInstanceKey;
                if (!string.IsNullOrEmpty(inst)) hit = shared.FirstOrDefault(v => string.Equals(v.InstanceKey, inst, StringComparison.OrdinalIgnoreCase));
                name = t.VsName;
            }
            else if (item is ExternalChat c)
            {
                if (c.Pid > 0) hit = shared.FirstOrDefault(v => v.Pid == c.Pid);
                name = c.VsName;
            }
            if (hit == null && !string.IsNullOrWhiteSpace(name))
            {
                var named = shared.Where(v => string.Equals((v.Name ?? "").Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count == 1) hit = named[0];
            }
            return hit != null ? GroupKeyOf(hit, bySolution) : solutionKey;
        }

        private static DateTime ActivityOf(object item) =>
            item is QueuedTask t ? Max(t.Finished, t.Started, t.Created)
            : item is ExternalChat c ? (c.Finished ?? c.Started) : DateTime.MinValue;

        private static DateTime Max(DateTime? a, DateTime? b, DateTime c)
        {
            var m = c;
            if (a.HasValue && a.Value > m) m = a.Value;
            if (b.HasValue && b.Value > m) m = b.Value;
            return m;
        }

        /// <summary>
        /// 生成分组后的显示序列：每组一个标题行，其后是该组条目（保持传入顺序，即现有的组内排序规则）；折叠的组只显示标题。
        /// Builds the grouped display sequence: one header per group followed by its items (in the incoming order, i.e. the
        /// existing in-group ordering); collapsed groups show the header only.
        /// </summary>
        public static List<object> Build(IEnumerable<object> ordered, IReadOnlyList<TaskGroupVs> open, string sort, ICollection<string> collapsed, IEnumerable<string> manualOrder = null)
        {
            var openList = (open ?? new TaskGroupVs[0]).Where(v => v != null && NormalizeKey(v.Key).Length > 0).ToList();
            var bySolution = openList.GroupBy(v => NormalizeKey(v.Key), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var openByKey = new Dictionary<string, TaskGroupVs>(StringComparer.Ordinal);
            foreach (var v in openList)
            {
                string k = GroupKeyOf(v, bySolution);
                if (!openByKey.ContainsKey(k)) openByKey[k] = v;
            }
            // 「等待打开」判断仍按解决方案 / "Waiting to open" is still decided per solution
            var openKeys = new HashSet<string>(bySolution.Keys, StringComparer.Ordinal);
            var groups = new List<TaskGroupHeader>();
            var members = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            var headers = new Dictionary<string, TaskGroupHeader>(StringComparer.Ordinal);
            foreach (var item in ordered ?? Enumerable.Empty<object>())
            {
                if (item == null || item is TaskGroupHeader) continue;
                string key = KeyOf(item, openKeys);
                if (bySolution.TryGetValue(key, out var shared) && shared.Count > 1) key = SharedKeyOf(item, key, shared, bySolution);
                if (!members.TryGetValue(key, out var list))
                {
                    members[key] = list = new List<object>();
                    groups.Add(headers[key] = NewHeader(key, item, openByKey, collapsed));
                }
                list.Add(item);
                Count(headers[key], item);
            }
            IEnumerable<TaskGroupHeader> sorted = NormalizeSort(sort) == SortByNumber
                ? groups.OrderBy(g => g.IsWaitingOpen ? 2 : g.IsOpen ? 0 : 1).ThenBy(g => g.Number).ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                : groups.OrderByDescending(g => g.HasRunning).ThenByDescending(g => g.LastActivity).ThenBy(g => g.Number);
            if (NormalizeSort(sort) == SortManual) sorted = TaskDisplayOrder.Apply(sorted, manualOrder, g => g.Key);
            var result = new List<object>();
            foreach (var g in sorted)
            {
                result.Add(g);
                if (!g.Collapsed) result.AddRange(members[g.Key]);
            }
            return result;
        }

        private static TaskGroupHeader NewHeader(string key, object first, Dictionary<string, TaskGroupVs> open, ICollection<string> collapsed)
        {
            var h = new TaskGroupHeader { Key = key, Collapsed = collapsed != null && collapsed.Contains(key) };
            if (key == WaitingOpenKey)
            {
                h.IsWaitingOpen = true;
                h.Title = "⏳ 等待打开 / Waiting to open";
            }
            else if (open.TryGetValue(key, out var v))
            {
                h.IsOpen = true;
                h.Number = v.Number;
                h.Title = "@" + v.Number + " " + (string.IsNullOrWhiteSpace(v.Name) ? "VS" : v.Name);
            }
            else
            {
                string name = first is QueuedTask t ? (t.VsName ?? t.Target) : (first as ExternalChat)?.VsName;
                h.Title = (string.IsNullOrWhiteSpace(name) ? "VS" : name) + "（未打开 / not open）";
            }
            return h;
        }

        private static void Count(TaskGroupHeader h, object item)
        {
            var at = ActivityOf(item);
            if (at > h.LastActivity) h.LastActivity = at;
            if (item is ExternalChat c)
            {
                h.Chats++;
                if (c.Generating) h.Running++;
                return;
            }
            var t = (QueuedTask)item;
            h.Tasks++;
            switch (t.Status)
            {
                case QueueStatus.Running: case QueueStatus.Sending: h.Running++; break;
                case QueueStatus.Waiting: h.Queued++; break;
                case QueueStatus.WaitingVs: h.Parked++; break;
                case QueueStatus.Failed: h.Failed++; break;
                case QueueStatus.Done: h.Done++; break;
                case QueueStatus.Unverified: h.Unverified++; break;
            }
        }
    }
}
