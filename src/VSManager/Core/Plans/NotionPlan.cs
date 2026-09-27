using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// Notion 计划中的一条任务：数据库记录提供结构化字段，页面正文提供详情。
    /// One task of a Notion plan: the database record supplies structured fields and the page body supplies details.
    /// </summary>
    internal sealed class PlanItem
    {
        public string PageId;
        public string Title = "";
        public string Target = "";
        public string Detail = "";
        public double Order = double.MaxValue;
        public List<string> DependsOn = new List<string>();
        /// <summary>状态属性的类型（rich_text / select），null 表示记录缺少该属性。/ Status property type; null when missing.</summary>
        public string StatusType;
        public string Hash;
        public SolutionEntry Entry;
        /// <summary>需要用户确认的问题；非空时不派发。/ Issues the user must resolve; never dispatched while non-empty.</summary>
        public List<string> Issues = new List<string>();
        public bool Clear => Issues.Count == 0;
    }

    /// <summary>
    /// Notion 计划解析（纯逻辑）：只做结构化读取与校验，模糊内容标记为问题交给用户，不猜测补全。
    /// Notion plan parsing (pure logic): structured reading and validation only; ambiguous content is flagged for the user, never guessed.
    /// </summary>
    internal static class NotionPlanParser
    {
        internal const string DefaultStatusProperty = "VSManager 状态";
        internal static readonly string[] TargetNames = { "目标", "目标项目", "Target", "Project", "VS" };
        internal static readonly string[] DependsNames = { "依赖", "前置", "Depends On", "Dependencies", "Blocked By" };
        internal static readonly string[] OrderNames = { "顺序", "序号", "Order", "Sequence" };
        private const int BriefTitleLength = 12;
        private const int ExactScore = 88;

        /// <summary>从 ID 或 Notion 链接中提取 32 位编号。/ Extracts the 32-hex id from an id or a Notion URL.</summary>
        public static string NormalizeId(string idOrUrl)
        {
            string s = (idOrUrl ?? "").Trim().Split('?', '#')[0].Replace("-", "");
            var m = Regex.Match(s, "[0-9a-fA-F]{32}(?![0-9a-fA-F])");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        public static List<PlanItem> ParseDatabase(IEnumerable<object> pages, string statusProperty)
        {
            string status = string.IsNullOrWhiteSpace(statusProperty) ? DefaultStatusProperty : statusProperty.Trim();
            var items = new List<PlanItem>();
            foreach (var page in pages ?? Enumerable.Empty<object>())
            {
                if (Get(page, "archived") is bool a && a || Get(page, "in_trash") is bool t && t) continue;
                var item = new PlanItem { PageId = NormalizeId(Get(page, "id") as string) };
                if (item.PageId == null) continue;
                if (Get(page, "properties") is IDictionary<string, object> props)
                {
                    foreach (var kv in props)
                    {
                        string type = Get(kv.Value, "type") as string;
                        if (string.Equals(kv.Key, status, StringComparison.OrdinalIgnoreCase)) item.StatusType = type;
                        else if (type == "title") item.Title = PropertyText(kv.Value);
                        else if (Named(kv.Key, TargetNames)) item.Target = PropertyText(kv.Value);
                        else if (Named(kv.Key, DependsNames) && type == "relation")
                            item.DependsOn.AddRange(Array(Get(kv.Value, "relation")).Select(r => NormalizeId(Get(r, "id") as string)).Where(id => id != null));
                        else if (Named(kv.Key, OrderNames) && type == "number" && Get(kv.Value, "number") is object n)
                            item.Order = Convert.ToDouble(n, CultureInfo.InvariantCulture);
                    }
                }
                item.DependsOn = item.DependsOn.Distinct().ToList();
                if (item.StatusType == null)
                    item.Issues.Add($"缺少状态字段「{status}」，无法回写 / Missing status property \"{status}\"; results cannot be written back");
                else if (item.StatusType != "rich_text" && item.StatusType != "select")
                    item.Issues.Add($"状态字段「{status}」需为文本或单选类型 / Status property \"{status}\" must be Text or Select");
                items.Add(item);
            }
            return items;
        }

        /// <summary>把页面块转为纯文本详情（不含子页面）。/ Converts page blocks to plain-text details (child pages excluded).</summary>
        public static string BlocksToText(IEnumerable<object> blocks)
        {
            var sb = new StringBuilder();
            foreach (var b in blocks ?? Enumerable.Empty<object>())
            {
                string type = Get(b, "type") as string;
                if (type == null || type == "child_page" || type == "child_database") continue;
                string text = RichText(Get(Get(b, type), "rich_text")).Trim();
                if (text.Length == 0) continue;
                if (type == "to_do") text = (Get(Get(b, type), "checked") is bool c && c ? "[x] " : "[ ] ") + text;
                else if (type == "bulleted_list_item" || type == "numbered_list_item") text = "- " + text;
                sb.Append(text).Append('\n');
            }
            return sb.ToString().Trim();
        }

        /// <summary>幂等内容哈希：标题、目标、依赖与详情任一变化即改变。/ Idempotency hash over title, target, dependencies and details.</summary>
        public static string ComputeHash(PlanItem item)
        {
            string canonical = string.Join("\n", item.Title.Trim(), item.Target.Trim(), string.Join(",", item.DependsOn.OrderBy(d => d, StringComparer.Ordinal)),
                (item.Detail ?? "").Replace("\r\n", "\n").Trim());
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)).Take(16).Select(x => x.ToString("x2")));
        }

        /// <summary>派发给 Copilot 的任务文本（单段）。/ The task text dispatched to Copilot (one paragraph).</summary>
        public static string TaskText(PlanItem item)
        {
            string detail = string.Join(" ", (item.Detail ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            return "【Notion 计划 / Notion plan】" + item.Title.Trim() + (detail.Length > 0 ? "：" + detail : "");
        }

        /// <summary>
        /// 校验并排序：解析目标（仅接受精确匹配）、检查粒度与长度、依赖范围、循环依赖，并把依赖项的问题传递给后续项；返回拓扑顺序。
        /// Validates and orders: resolves targets (exact matches only), checks granularity and length, dependency scope and cycles,
        /// and propagates dependency issues to dependents; returns the topological order.
        /// </summary>
        public static List<PlanItem> Validate(List<PlanItem> items, Func<string, SolutionLookup> resolve, int maxTaskText)
        {
            var byId = items.ToDictionary(i => i.PageId);
            foreach (var item in items)
            {
                item.Hash = ComputeHash(item);
                if (item.Title.Trim().Length == 0) item.Issues.Add("缺少标题 / Missing title");
                if (item.Target.Trim().Length == 0) item.Issues.Add("未指定目标项目 / Target project not specified");
                else
                {
                    var lookup = resolve(item.Target.Trim());
                    int score = lookup.Candidates.FirstOrDefault(c => c.Entry == lookup.Hit)?.Score ?? 0;
                    if (lookup.Found && score >= ExactScore) item.Entry = lookup.Hit;
                    else if (lookup.Candidates.Count > 0)
                        item.Issues.Add($"目标「{item.Target}」匹配不确定，候选：{string.Join("、", lookup.Candidates.Take(4).Select(c => c.Entry.Alias))} / Target \"{item.Target}\" is uncertain; candidates listed");
                    else item.Issues.Add($"目标「{item.Target}」不在解决方案登记表中 / Target \"{item.Target}\" is not registered");
                }
                if (item.Detail.Trim().Length == 0 && item.Title.Trim().Length < BriefTitleLength)
                    item.Issues.Add("内容过简，归属或粒度不明确，请补充页面详情 / Too brief; scope or granularity unclear, add page details");
                if (TaskText(item).Length > maxTaskText)
                    item.Issues.Add($"任务文本超过上限 {maxTaskText} 字，请在 Notion 中拆分 / Task text exceeds {maxTaskText} characters; split it in Notion");
                if (item.DependsOn.Contains(item.PageId)) item.Issues.Add("依赖自身 / Depends on itself");
                foreach (var d in item.DependsOn.Where(d => d != item.PageId && !byId.ContainsKey(d)))
                    item.Issues.Add($"依赖 {d} 不在本计划中 / Dependency {d} is outside this plan");
            }

            var ordered = new List<PlanItem>();
            var pending = items.OrderBy(i => i.Order).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            var done = new HashSet<string>();
            while (pending.Count > 0)
            {
                var next = pending.FirstOrDefault(i => i.DependsOn.All(d => done.Contains(d) || !byId.ContainsKey(d) || d == i.PageId));
                if (next == null)
                {
                    foreach (var c in pending) { c.Issues.Add("存在循环依赖 / Circular dependency"); ordered.Add(c); }
                    break;
                }
                foreach (var d in next.DependsOn.Where(d => d != next.PageId && byId.TryGetValue(d, out var dep) && !dep.Clear))
                    next.Issues.Add($"依赖「{byId[d].Title}」待确认 / Dependency \"{byId[d].Title}\" needs review");
                pending.Remove(next);
                done.Add(next.PageId);
                ordered.Add(next);
            }
            return ordered;
        }

        internal static object Get(object o, string key) =>
            o is IDictionary<string, object> d && key != null && d.TryGetValue(key, out var v) ? v : null;

        internal static IEnumerable<object> Array(object o) => o is IEnumerable e && !(o is string) && !(o is IDictionary) ? e.Cast<object>() : Enumerable.Empty<object>();

        private static string RichText(object array) => string.Concat(Array(array).Select(r => Get(r, "plain_text") as string ?? ""));

        private static bool Named(string key, string[] names) => names.Any(n => string.Equals(n, key.Trim(), StringComparison.OrdinalIgnoreCase));

        private static string PropertyText(object p)
        {
            string type = Get(p, "type") as string;
            switch (type)
            {
                case "title":
                case "rich_text": return RichText(Get(p, type)).Trim();
                case "select":
                case "status": return (Get(Get(p, type), "name") as string ?? "").Trim();
                case "multi_select": return string.Join(",", Array(Get(p, type)).Select(x => Get(x, "name") as string).Where(x => !string.IsNullOrEmpty(x)));
                default: return "";
            }
        }
    }
}
