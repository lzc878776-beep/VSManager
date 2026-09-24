using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VSManager
{
    /// <summary>仅显示排序，不读取或修改调度顺序。/ Display ordering only; never reads or changes dispatch order.</summary>
    public static class TaskDisplayOrder
    {
        public const string DisplayOnly = "仅显示，不改变执行顺序 / Display only; execution order unchanged";
        public const string CrossGroup = "不能跨 VS 分组移动条目；请切换平铺排序 / Cannot move entries across VS groups; switch to flat view";
        public const string StaleDrag = "条目或分组已变化，请重新拖拽 / Entry or group changed; drag again";

        public static string KeyOf(object item)
        {
            if (item is TaskGroupHeader h) return h.Key;
            if (item is QueuedTask t) return t.Id > 0 ? "t:" + t.Id.ToString(CultureInfo.InvariantCulture) : null;
            if (!(item is ExternalChat c)) return null;
            // 对话编号在重启时重建；使用归档保留的身份字段和毫秒精度。/ Chat IDs restart; use archived identity fields at millisecond precision.
            string identity = TaskGrouping.NormalizeKey(string.IsNullOrWhiteSpace(c.VsKey) ? c.VsName : c.VsKey)
                + "\n" + c.Started.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "\n" + (c.Question ?? "");
            using (var hash = SHA256.Create())
                return "c:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
        }

        public static List<string> Normalize(IEnumerable<string> keys, bool groups = false)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in keys ?? Enumerable.Empty<string>())
            {
                string key = groups ? TaskGrouping.NormalizeKey(raw) : (raw ?? "").Trim().ToLowerInvariant();
                if (key.Length == 0 || key.Any(char.IsControl)) continue;
                if (!groups)
                {
                    if (key.StartsWith("t:", StringComparison.Ordinal) && int.TryParse(key.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id > 0)
                        key = "t:" + id.ToString(CultureInfo.InvariantCulture);
                    else if (!(key.StartsWith("c:", StringComparison.Ordinal) && key.Length == 66 && key.Substring(2).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))) continue;
                }
                if (seen.Add(key)) result.Add(key);
            }
            return result;
        }

        public static List<T> Apply<T>(IEnumerable<T> items, IEnumerable<string> order, Func<T, string> key)
        {
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var k in order ?? Enumerable.Empty<string>())
                if (k != null && !ranks.ContainsKey(k)) ranks.Add(k, ranks.Count);
            return items.OrderBy(x => { string k = key(x); return k != null && ranks.TryGetValue(k, out int rank) ? rank : int.MaxValue; }).ToList();
        }

        /// <summary>只替换可见槽位，隐藏、折叠和暂不显示的键仍保留。/ Replace visible slots only; retain hidden, collapsed and temporarily absent keys.</summary>
        public static List<string> MergeVisible(IEnumerable<string> saved, IEnumerable<string> known, IEnumerable<string> visible)
        {
            var result = (saved ?? Enumerable.Empty<string>()).Concat(known ?? Enumerable.Empty<string>()).Where(k => k != null).Distinct(StringComparer.Ordinal).ToList();
            var ordered = visible.Where(k => k != null).Distinct(StringComparer.Ordinal).ToList();
            var set = new HashSet<string>(ordered, StringComparer.Ordinal);
            var existing = new HashSet<string>(result, StringComparer.Ordinal);
            foreach (var k in ordered) if (existing.Add(k)) result.Add(k);
            int next = 0;
            for (int i = 0; i < result.Count; i++) if (set.Contains(result[i])) result[i] = ordered[next++];
            return result;
        }

        /// <summary>返回吸附后的插入槽位；负值表示拒绝。/ Returns the snapped insertion slot; negative means rejected.</summary>
        public static int DropSlot(IReadOnlyList<object> rows, string source, bool group, bool grouped, int hit, bool after, out string reason)
        {
            reason = StaleDrag;
            int from = -1;
            for (int i = 0; i < rows.Count; i++)
                if ((rows[i] is TaskGroupHeader) == group && KeyOf(rows[i]) == source) { from = i; break; }
            if (from < 0 || rows.Count == 0 || group && !grouped) return -1;
            hit = Math.Max(0, Math.Min(rows.Count, hit));
            if (!grouped) { reason = null; return Math.Min(rows.Count, hit + (after ? 1 : 0)); }
            int target = hit == rows.Count ? rows.Count - 1 : hit;
            int start = target;
            while (start > 0 && !(rows[start] is TaskGroupHeader)) start--;
            int end = start + 1;
            while (end < rows.Count && !(rows[end] is TaskGroupHeader)) end++;
            if (group) { reason = null; return hit == rows.Count || after ? end : start; }
            int own = from;
            while (own > 0 && !(rows[own] is TaskGroupHeader)) own--;
            if (own != start) { reason = CrossGroup; return -1; }
            reason = null;
            return hit == rows.Count ? end : rows[hit] is TaskGroupHeader ? start + 1 : Math.Min(end, hit + (after ? 1 : 0));
        }
    }
}
