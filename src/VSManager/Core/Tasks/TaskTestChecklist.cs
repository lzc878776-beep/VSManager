using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 测试清单：从 Copilot 的最终回复中提取需要用户在环境中实测的项目；用户逐项勾选，全部勾选后任务转为已完成。
    /// Test checklist: extracts from Copilot's final reply the items the user must test in the environment; the user checks them
    /// off one by one and the task completes once all are checked.
    /// </summary>
    public static class TaskTestChecklist
    {
        public const int MaxItems = 12;
        public const int MaxItemLength = 200;

        /// <summary>回复中没有可识别的列表时使用的单项。/ Single item used when the reply has no recognizable list.</summary>
        public const string FallbackItem = "按 Copilot 回复中的说明在运行环境中验证 / Verify in the running environment as described in the reply";

        private static readonly Regex CheckboxLine = new Regex(@"^\s*(?:[-*+•]\s+|\d{1,2}[.)、．]\s*)?\[(?: |x|X|✓)\]\s+(?<text>\S.*)$", RegexOptions.Compiled);
        private static readonly Regex BulletLine = new Regex(@"^\s*(?:[-*+•]\s+|\d{1,2}[.)、．]\s*)(?<text>\S.*)$", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex(
            @"未验证|待验证|需要验证|需验证|验证项|测试项|测试清单|手动测试|需要用户|需要你|请你?(?:测试|验证|确认)|unverified|to verify|verification|test checklist|manual(?:ly)? test|please (?:test|verify|check)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>任务是否等待用户测试（未验证，或已完成但待用户验证）。/ Whether the task awaits user testing (unverified, or done but awaiting verification).</summary>
        public static bool Pending(QueuedTask t) =>
            t != null && (t.Status == QueueStatus.Unverified || (t.Status == QueueStatus.Done && t.NeedsUser));

        /// <summary>
        /// 解析测试项：优先「- [ ] 项目」复选框行；否则取「未验证 / 需要验证…」等标题之后的列表；都没有时返回一个通用项。
        /// Parses test items: "- [ ] item" checkbox lines first; otherwise the list after a heading such as "unverified / to verify…";
        /// a single generic item when neither exists.
        /// </summary>
        public static TaskTestItem[] Parse(string reply)
        {
            var lines = (reply ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var items = lines.Select(l => CheckboxLine.Match(l)).Where(m => m.Success).Select(m => m.Groups["text"].Value).ToList();
            if (items.Count == 0) items = ListAfterHeading(lines);
            var result = items.Select(Clean).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxItems)
                .Select(s => new TaskTestItem { Text = s }).ToArray();
            return result.Length > 0 ? result : new[] { new TaskTestItem { Text = FallbackItem } };
        }

        private static List<string> ListAfterHeading(string[] lines)
        {
            // 取最后一个带关键字的标题后的列表（结论通常在回复末尾）/ Use the list after the last keyword heading (conclusions usually come last)
            for (int h = lines.Length - 1; h >= 0; h--)
            {
                if (!Heading.IsMatch(lines[h]) || BulletLine.IsMatch(lines[h])) continue;
                var block = new List<string>();
                for (int i = h + 1; i < lines.Length; i++)
                {
                    if (lines[i].Trim().Length == 0) { if (block.Count > 0) break; continue; }
                    var m = BulletLine.Match(lines[i]);
                    if (!m.Success) break;
                    block.Add(m.Groups["text"].Value);
                }
                if (block.Count > 0) return block;
            }
            return new List<string>();
        }

        private static string Clean(string text)
        {
            string s = Regex.Replace(text ?? "", @"\*\*|__", "").Trim();
            if (s.Length > MaxItemLength) s = s.Substring(0, MaxItemLength - 1).TrimEnd() + "…";
            return s;
        }

        /// <summary>
        /// 确保等待测试的任务有测试清单（旧任务从已保存的回复中补齐），返回清单；不等待测试时返回空数组。
        /// Ensures a task awaiting testing has a checklist (older tasks are filled from the saved reply) and returns it; empty when not pending.
        /// </summary>
        public static TaskTestItem[] Ensure(QueuedTask t)
        {
            if (!Pending(t)) return new TaskTestItem[0];
            if (t.TestItems == null || t.TestItems.Length == 0 || t.TestItems.Any(i => i == null))
                t.TestItems = Parse(t.FullResult ?? t.Result);
            return t.TestItems;
        }

        /// <summary>
        /// 用改写后的结果文字更新测试清单：新文字能解析出测试项时替换清单（项数不变时按位置保留勾选，便于翻译）；
        /// 解析不出时保留原清单。只处理等待测试的任务。
        /// Updates the checklist from an edited result: when the new text yields test items they replace the list (checks are kept
        /// by position when the count is unchanged, e.g. after a translation); otherwise the old list is kept. Pending tasks only.
        /// </summary>
        public static void Replace(QueuedTask t, string text)
        {
            if (!Pending(t)) return;
            var parsed = Parse(text);
            if (parsed.Length == 1 && parsed[0].Text == FallbackItem) return;
            var old = t.TestItems;
            if (old != null && old.Length == parsed.Length)
                for (int i = 0; i < parsed.Length; i++) parsed[i].Checked = old[i]?.Checked ?? false;
            t.TestItems = parsed;
        }

        /// <summary>尚未勾选的项数。/ Number of unchecked items.</summary>
        public static int Remaining(QueuedTask t) => t?.TestItems?.Count(i => i != null && !i.Checked) ?? 0;

        private static readonly Regex ReceiptLine = new Regex(@"^\s*\[VSManager:[0-9a-fA-F]{8,}:[A-Z_]+\]\s*$", RegexOptions.Compiled);

        /// <summary>
        /// 去掉回复中的测试清单（复选框行、验证标题及其后的列表）与回执行，剩下已完成内容的说明。
        /// Removes the test checklist (checkbox lines, verification headings and the list after them) and receipt lines from a reply, leaving the description of what was done.
        /// </summary>
        public static string StripChecklist(string reply)
        {
            var lines = (reply ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var kept = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (CheckboxLine.IsMatch(line) || ReceiptLine.IsMatch(line)) continue;
                if (Heading.IsMatch(line) && !BulletLine.IsMatch(line))
                {
                    int j = i + 1;
                    while (j < lines.Length && lines[j].Trim().Length == 0) j++;
                    if (j < lines.Length && (BulletLine.IsMatch(lines[j]) || CheckboxLine.IsMatch(lines[j])))
                    {
                        while (j < lines.Length && (BulletLine.IsMatch(lines[j]) || CheckboxLine.IsMatch(lines[j]))) j++;
                        i = j - 1;
                        continue;
                    }
                }
                kept.Add(line);
            }
            return string.Join("\n", kept).Trim();
        }
    }
}
