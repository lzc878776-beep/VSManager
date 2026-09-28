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

        /// <summary>AI 总控助手可用工具验证。/ The AI assistant can verify it with tools.</summary>
        public const string ByAi = "ai";
        /// <summary>必须人工验证。/ Must be verified by a person.</summary>
        public const string ByUser = "user";

        /// <summary>
        /// 回执规则中要求 Copilot 给每个测试项加的标注说明。
        /// The tagging instruction the receipt rules give Copilot for each checklist item.
        /// </summary>
        public const string TagRule = "每项开头标注「[AI]」或「[人工]」：[AI] 表示 AI 总控助手能用工具自动验证（编译 / 单元测试、日志、文件或配置内容、命令或接口返回、验证接口，以及 VSManager 自身的主窗口位置 / 大小 / 页面 / 草稿、前台窗口、托盘图标与重启交接文件等），[人工] 表示需要用户操作界面或观察画面、声音等才能判断，例如「- [ ] [人工] 具体操作与预期结果」。";

        private static readonly Regex TagPrefix = new Regex(
            @"^\s*(?:\[|【|\()\s*(?<tag>AI(?:\s*(?:可验证|验证|verifiable))?|自动|auto(?:mated)?|人工(?:验证)?|手动|用户|manual|user|human)\s*(?:\]|】|\))\s*[:：]?\s*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex UserHint = new Regex(
            @"观察|看到|看是否|肉眼|界面|外观|颜色|样式|显示为|显示在|显示出|闪烁|动画|布局|屏幕|窗口|面板|按钮|菜单|弹窗|对话框|点击|单击|双击|右键|拖动|拖拽|悬停|鼠标|键盘|按下|按住|输入框|听到|语音|播报|手机|浏览器|体验|手动|\bclick|\bhover|\bdrag|\bobserve|visual|\blook|\bsee\b|\bscreen|\bsound|manual|\bUI\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // 写明用界面探测工具验证的项归为 AI / Items that name a UI probe tool are AI-verifiable
        private static readonly Regex ProbeHint = new Regex(@"get_window_state|get_foreground_window|list_tray_icons|read_restart_handoff|prepare_restart_scenario", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AiHint = new Regex(
            @"编译|生成成功|构建|单元测试|测试通过|全部通过|日志|tasks\.log|\.json|\.log\b|注册表|文件内容|配置文件|返回值|退出码|命令行|接口返回|错误列表|verify|run_verify_check|\bbuild|compile|unit test|\blog\b|registry|exit code|\bAPI\b|vstest|msbuild",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 按文字推断由谁验证：带界面、观察类词语或无法判断时一律归为人工（保守），只有明确的工具可查项才归为 AI。
        /// Infers who verifies an item: anything mentioning UI / observation, or unclear, is manual (conservative); only clearly tool-checkable items are AI.
        /// </summary>
        public static string Classify(string text)
        {
            string s = text ?? "";
            string meta = MetaVerdict(s);
            if (meta != null) return meta;
            if (ManualOnly.IsMatch(s)) return ByUser;
            if (ProbeHint.IsMatch(s) || UiStateHint.IsMatch(s)) return ByAi;
            return AiHint.IsMatch(s) && !UserHint.IsMatch(s) ? ByAi : ByUser;
        }

        // 元测试项：核对清单工具的返回或其他项的标注，例如「list_test_checklists 中『草稿』类项显示为 [AI]」
        // Meta items: check a checklist tool's output or other items' tags, e.g. "items like 'draft' show as [AI] in list_test_checklists"
        private static readonly Regex MetaHint = new Regex(
            @"list_test_checklists|list_tasks|mark_test_item|(?:显示|标注|标记|标|判定?|重判|归|改)(?:为|成)\s*(?:\[|【)\s*(?:AI|人工)\s*(?:\]|】)|" +
            @"(?:shown|shows|tagged|marked|judged|classified|re-?judged)\s+(?:as\s+)?\[(?:AI|人工|manual)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // 引号 / 书名号 / 反引号内的文字：元测试项里用来引用其他测试项，不代表本项的操作
        // Quoted text (quotes, corner brackets, backticks): in meta items it cites other items, not this item's own action
        private static readonly Regex Quoted = new Regex(
            @"『[^』]*』|「[^」]*」|“[^”]*”|‘[^’]*’|""[^""\n]*""|`[^`\n]*`", RegexOptions.Compiled);

        /// <summary>
        /// 元测试项判定：文字核对清单工具（list_test_checklists / list_tasks / mark_test_item）的返回，或写明其他项「显示为 / 标为 [AI] / [人工]」时，
        /// 引号内的词是在引用别的项，不参与判定；去掉引号内容后仍有人工专属动作（点击按钮、观察动画等）归为人工，否则归为 AI（工具返回即可核对）。
        /// 不是元测试项时返回 null，按常规规则判定。
        /// Meta-item verdict: when the text checks a checklist tool's output (list_test_checklists / list_tasks / mark_test_item) or states that other items
        /// "show as / are tagged [AI] / [人工]", quoted words cite other items and are ignored; if a human-only action (clicking a button, watching an animation…)
        /// remains after removing quoted text it is manual, otherwise AI (the tool output can be checked). Returns null for non-meta items (normal rules apply).
        /// </summary>
        public static string MetaVerdict(string text)
        {
            string s = text ?? "";
            if (!MetaHint.IsMatch(s)) return null;
            string own = Quoted.Replace(s, " ");
            return ManualOnly.IsMatch(own) ? ByUser : ByAi;
        }

        // 界面探测工具能读到的状态：窗口位置 / 大小 / 最大化 / 屏幕、页面、选中的 VS、草稿、托盘与残影、前台焦点、交接文件
        // State the UI probes can read: window bounds / maximized / screen, page, selected VS, draft, tray and ghosts, foreground, handoff files
        private static readonly Regex UiStateHint = new Regex(
            @"窗口(?:的)?(?:位置|大小|尺寸|坐标|状态|边界)|位置(?:和|与|、|/|\s)*大小|最大化|最小化|所在屏幕|副屏|同一(?:块)?屏幕|原屏幕|当前页面|停在该页|(?:AI 总控|VS 对话|对话)页|选中的 ?VS|同一个? ?VS|草稿|托盘|残影|抢焦点|前台|交接(?:文件|单)|restart-ui\.json|restart-handoff\.json|已消费|" +
            @"window (?:position|size|bounds|state)|maximi[sz]ed|minimi[sz]ed|selected VS|draft|tray icon|ghost icon|steal(?:s|ing)? focus|foreground|handoff|consumed",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 必须人看或人手操作：画面观感、动画、颜色、点击按钮、人为制造故障等 / Needs human eyes or hands: looks, animation, colors, clicking buttons, deliberately causing failures
        private static readonly Regex ManualOnly = new Regex(
            @"观感|外观|美观|动画|闪烁|闪现|闪一下|颜色|配色|样式|字体|清晰|流畅|卡顿|画面|遮罩|看起来|肉眼|声音|播报|听到|点击?[^，。；,;\n]{0,12}按钮|单击|双击|右键|拖动|拖拽|悬停|手动(?:点击|操作)|故意|人为|制造(?:故障|失败|错误|异常)|模拟(?:故障|失败)|断开(?:网络|屏幕|显示器)|拔掉|手机|" +
            @"\bclick|animation|colou?r|flicker|\blooks?\b|visual|appearance|deliberately|on purpose|force (?:a )?fail|unplug|\bsound",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 按文字重判可验证性：人工专属（观感、动画、颜色、点击按钮、人为制造故障）一律人工；界面探测工具能读到的状态改为 AI；其余保持原判。
        /// Re-judges verifiability from the text: human-only items (looks, animation, colors, clicking buttons, deliberate failures) are manual;
        /// state the UI probes can read becomes AI; anything else keeps its current verdict.
        /// </summary>
        public static string Rejudge(string text, string current)
        {
            string s = text ?? "";
            string meta = MetaVerdict(s);
            if (meta != null) return meta;
            if (ManualOnly.IsMatch(s)) return ByUser;
            if (ProbeHint.IsMatch(s) || UiStateHint.IsMatch(s)) return ByAi;
            return current ?? Classify(s);
        }

        /// <summary>
        /// 对已有清单中尚未勾选的项重判可验证性（已勾选项不变），返回改动的项数。
        /// Re-judges the unchecked items of an existing checklist (checked items stay), returning how many changed.
        /// </summary>
        public static int Reclassify(QueuedTask t) => Reclassify(t, out _);

        /// <summary>同上，并返回本次重判的项数（未勾选项）。/ Same as above, also returning how many items were re-judged (the unchecked ones).</summary>
        public static int Reclassify(QueuedTask t, out int judged)
        {
            judged = 0;
            if (t?.TestItems == null) return 0;
            int changed = 0;
            foreach (var i in t.TestItems)
            {
                if (i == null || i.Checked) continue;
                judged++;
                string by = Rejudge(i.Text, i.By);
                if (string.Equals(by, i.By, StringComparison.OrdinalIgnoreCase)) continue;
                i.By = by;
                changed++;
            }
            return changed;
        }

        /// <summary>
        /// tasks.log 中的重判记录（固定写法，供 read_vsmanager_log 核对）：N 为本次重判的项数，M 为结论改变的项数。
        /// The re-judge record in tasks.log (fixed wording, checkable with read_vsmanager_log): N = items re-judged this time, M = items whose verdict changed.
        /// </summary>
        public static string RejudgeLogText(int judged, int changed) =>
            $"已按测试项文字重判 {judged} 个测试项的可验证性（其中 {changed} 项改变）/ Re-judged the verifiability of {judged} test item(s) from their text ({changed} changed)";

        /// <summary>是否必须人眼 / 人手（观感、动画、颜色、点击按钮、人为制造故障等），新增 skill 也无法代替。/ Whether the item needs human eyes or hands (looks, animation, colors, clicks, deliberate failures); no new skill can replace that.</summary>
        public static bool HumanOnly(string text) => ManualOnly.IsMatch(text ?? "");

        /// <summary>
        /// AI 能否勾选该项：AI 项，或已为它启动补 skill 闭环且不是必须人眼 / 人手的项。
        /// Whether the AI may check the item: an AI item, or one with a skill-gap loop that does not need human eyes or hands.
        /// </summary>
        public static bool CanAiCheck(TaskTestItem item) =>
            item != null && (IsAi(item) || (!string.IsNullOrWhiteSpace(item.Skill) && !HumanOnly(item.Text)));

        /// <summary>
        /// 能否为该项发起补 skill 闭环：可以时返回 null，否则返回中英原因（已勾选、必须人眼 / 人手、每项只发起一次）。
        /// Whether a skill-gap loop may start for the item: null when allowed, else a bilingual reason (already checked, needs human eyes or hands, one loop per item).
        /// </summary>
        public static string SkillGapRefusal(TaskTestItem item)
        {
            if (item == null) return "没有该测试项 / No such item.";
            if (item.Checked) return "该项已勾选，无需补 skill / The item is already checked.";
            if (HumanOnly(item.Text))
                return "该项需要人眼或人手（观感、动画、颜色、点击按钮、人为制造故障等），新增 skill 也无法代替；请转告用户测试 / The item needs human eyes or hands; no skill can replace that — relay it to the user.";
            if (!string.IsNullOrWhiteSpace(item.Skill))
                return $"已为该项发起过补 skill 闭环（{item.Skill.Trim()}），每项只发起一次；请用该 skill 验证，仍不行就如实转告用户 / A skill-gap loop was already started for this item (one per item); verify with that skill or tell the user.";
            return null;
        }

        /// <summary>该项是否可由 AI 总控助手验证。/ Whether the AI assistant can verify the item.</summary>
        public static bool IsAi(TaskTestItem item) =>
            item != null && string.Equals(item.By ?? Classify(item.Text), ByAi, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 可补 skill 的人工项数：未勾选、标为人工、不是必须人眼 / 人手、尚未发起补 skill 闭环。
        /// Manual items a new skill might verify: unchecked, tagged manual, not needing human eyes or hands, no skill-gap loop yet.
        /// </summary>
        public static int SkillCandidates(QueuedTask t) =>
            Pending(t) ? t.TestItems?.Count(i => i != null && !i.Checked && !IsAi(i) && !HumanOnly(i.Text) && string.IsNullOrWhiteSpace(i.Skill)) ?? 0 : 0;

        /// <summary>待验证任务中尚未勾选的 AI 可验证项数。/ Unchecked AI-verifiable items of a pending task.</summary>
        public static int RemainingAi(QueuedTask t) => Pending(t) ? t.TestItems?.Count(i => i != null && !i.Checked && IsAi(i)) ?? 0 : 0;

        /// <summary>去掉开头的 [AI] / [人工] 标注并返回对应的验证方；没有标注时按文字推断。/ Strips a leading [AI] / [manual] tag and returns the verifier; infers it when untagged.</summary>
        internal static string SplitTag(string text, out string rest)
        {
            var m = TagPrefix.Match(text ?? "");
            if (!m.Success) { rest = text ?? ""; return Classify(rest); }
            rest = (text ?? "").Substring(m.Length).Trim();
            string tag = m.Groups["tag"].Value.ToLowerInvariant();
            return tag.StartsWith("ai") || tag.StartsWith("auto") || tag == "自动" ? ByAi : ByUser;
        }

        private static readonly Regex CheckboxLine = new Regex(@"^\s*(?:[-*+•]\s+|\d{1,2}[.)、．]\s*)?\[(?: |x|X|✓)\]\s+(?<text>\S.*)$", RegexOptions.Compiled);
        private static readonly Regex BulletLine = new Regex(@"^\s*(?:[-*+•]\s+|\d{1,2}[.)、．]\s*)(?<text>\S.*)$", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex(
            @"未验证|待验证|需要验证|需验证|验证项|测试项|测试清单|手动测试|需要用户|需要你|请你?(?:测试|验证|确认)|unverified|to verify|verification|test checklist|manual(?:ly)? test|please (?:test|verify|check)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>任务是否待验证（等待用户测试）。/ Whether the task awaits verification (user testing).</summary>
        public static bool Pending(QueuedTask t) =>
            t != null && t.Status == QueueStatus.Unverified;

        /// <summary>
        /// 解析测试项：优先「- [ ] 项目」复选框行；否则取「未验证 / 需要验证…」等标题之后的列表；都没有时返回一个通用项。
        /// Parses test items: "- [ ] item" checkbox lines first; otherwise the list after a heading such as "unverified / to verify…";
        /// a single generic item when neither exists.
        /// </summary>
        public static TaskTestItem[] Parse(string reply) => Parse(reply, out _, out _);

        /// <summary>
        /// 同上，并返回本次按文字重判的项数与其中结论改变的项数（通用兜底项不计）。
        /// Same as above, also returning how many items were re-judged from their text and how many of them changed (the generic fallback item is not counted).
        /// </summary>
        public static TaskTestItem[] Parse(string reply, out int judged, out int changed)
        {
            judged = changed = 0;
            var lines = (reply ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var items = lines.Select(l => CheckboxLine.Match(l)).Where(m => m.Success).Select(m => m.Groups["text"].Value).ToList();
            if (items.Count == 0) items = ListAfterHeading(lines);
            var result = new List<TaskTestItem>();
            foreach (string raw in items)
            {
                string tagged = SplitTag(Clean(raw), out string text);
                if (text.Length == 0 || result.Any(r => r.Text == text)) continue;
                string by = Rejudge(text, tagged);
                judged++;
                if (!string.Equals(by, tagged, StringComparison.OrdinalIgnoreCase)) changed++;
                result.Add(new TaskTestItem { Text = text, By = by });
                if (result.Count >= MaxItems) break;
            }
            return result.Count > 0 ? result.ToArray() : new[] { new TaskTestItem { Text = FallbackItem, By = ByUser } };
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
            else Reclassify(t);
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

        /// <summary>
        /// 交给 AI 助手的自动验证说明：列出未勾选的 AI 项与人工项，说明怎样用工具验证（含界面探测与场景补齐）、勾选与转告；
        /// loop 为 true（自验证循环）时再附重试步骤。没有未勾选的 AI 项时返回 null。
        /// Auto-verify instructions for the AI assistant: lists the unchecked AI and manual items and how to verify them with tools
        /// (UI probes and scenario setup included), check and relay; with loop (self-verify loop) the retry steps are added. Null when no AI item is unchecked.
        /// </summary>
        public static string SelfVerifyNotice(QueuedTask t, bool loop = true)
        {
            var items = Pending(t) ? t.TestItems : null;
            // 非循环的自动验证也为「可补 skill」的人工项提醒 / Outside the loop, auto-verify also fires for manual items a new skill could verify
            if (items == null || (RemainingAi(t) == 0 && (loop || SkillCandidates(t) == 0))) return null;
            var ai = new List<string>();
            var user = new List<string>();
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == null || items[i].Checked) continue;
                string line = (i + 1) + ". " + (items[i].Text.Length > 120 ? items[i].Text.Substring(0, 119) + "…" : items[i].Text);
                (IsAi(items[i]) ? ai : user).Add(line);
            }
            string head = loop ? "[自验证循环 / Self-verify loop]" : "[自动验证 / Auto-verify]";
            string restart = loop
                ? "VSManager 自身的任务先用 list_tasks 写测试计划，需要特定前置场景（某个 VS 对话页、最大化 / 副屏、草稿、先切到其他应用）时先调用 prepare_restart_scenario 造出场景，再调用 restart_vsmanager_for_testing 重启后测；"
                : "需要重启 VSManager 才能验证的项（重启后状态恢复、托盘残影、是否抢焦点）只有用户要求、开启自验证循环或补 skill 闭环 / 自迭代进行中时才重启：先 prepare_restart_scenario 造场景，再 restart_vsmanager_for_testing；否则告诉用户可以让你「重启测试」；";
            string restartEn = loop
                ? "for VSManager's own task write a plan from list_tasks; when an item needs a precondition (a VS chat page, maximized / second screen, a draft, another app in front) call prepare_restart_scenario first, then restart_vsmanager_for_testing; "
                : "items that need a VSManager restart (state restore, tray ghosts, focus stealing) are restarted only when the user asks, the loop is on or a skill-gap loop / self-iteration is running: prepare_restart_scenario, then restart_vsmanager_for_testing; otherwise tell the user you can run a restart test; ";
            return $"{head} 任务 #{t.Id} 的测试清单 / checklist of task #{t.Id}\n" +
                "AI 可验证 / AI-verifiable:\n" + string.Join("\n", ai) + "\n" +
                "必须人工验证 / Manual:\n" + (user.Count == 0 ? "（无 / none）" : string.Join("\n", user)) + "\n" +
                "请现在用工具实际验证 AI 项：" + restart +
                "窗口位置 / 大小 / 最大化 / 屏幕 / 页面 / 选中的 VS / 草稿用 get_window_state，前台焦点用 get_foreground_window，托盘图标与残影用 list_tray_icons，交接文件消费状态用 read_restart_handoff；" +
                "其他项目用 run_verify_check、CAD 动作、读取日志 / 文件 / 错误列表等。每项通过即 mark_test_item（写明工具与返回的依据）；验证不了或结果不符的不得勾选，如实转告用户；人工项不要勾选，转告用户。" +
                "补 skill：某项用现有工具验证不了、但要看的是机器可读状态（窗口 / 控件 / 托盘 / 文件 / 日志 / 进程等），理论上新增一个 skill 就能自动验证时（标为 [人工] 的此类项同样适用），不必等用户提醒，直接调用 start_skill_gap_loop 写明需要的 skill 与原因，自动完成「发布实现任务 → 重启加载 → 用新 skill 验证」；必须人眼 / 人手的项不适用。" +
                (loop ? $"AI 项未通过时用 retry_task_with_info 带上失败证据重试（AI 自主补充上限 {TaskStateMachine.MaxSupplements} 次，已补充 {t.SupplementCount} 次），任务再次待验证时会自动进入下一轮；达到上限或无法用工具验证时如实交给用户，不要把未实测的项说成通过。" : "不要把未实测的项说成通过。") + "/ " +
                "Verify the AI items with tools now: " + restartEn +
                "window bounds / maximized / screen / page / selected VS / draft via get_window_state, foreground via get_foreground_window, tray icons and ghosts via list_tray_icons, handoff consumption via read_restart_handoff; " +
                "for other projects use run_verify_check, CAD actions, logs / files / the error list. Check each passed item with mark_test_item and evidence; never check what you could not verify or what did not match, and never check manual items: relay them to the user. " +
                "Skill gap: when existing tools cannot verify an item but it concerns machine-readable state (window / control / tray / file / log / process) that a new skill could verify (including such items tagged [人工]), do not wait for the user: call start_skill_gap_loop with the skill needed and the reason to run publish → restart → verify with the new skill automatically; not for items needing human eyes or hands. " +
                (loop ? $"On failure call retry_task_with_info with the evidence (AI retry cap {TaskStateMachine.MaxSupplements}, used {t.SupplementCount}); the next pending result re-enters the loop. At the cap or when tools cannot verify, hand over honestly." : "Never report an untested item as passed.");
        }

        /// <summary>
        /// 列出全部待验证任务（不限最近几条，taskId &gt; 0 时只列该任务）的完整测试清单：每项带序号（与 mark_test_item 一致）、勾选状态与 [AI] / [人工] 标注。
        /// 旧任务的清单先从已保存的回复中补齐。
        /// Lists the full checklist of every task awaiting verification (not only recent ones; only taskId when &gt; 0): each item with
        /// its number (as used by mark_test_item), check state and [AI] / [manual] tag. Older tasks are filled from the saved reply first.
        /// </summary>
        public static string Describe(IEnumerable<QueuedTask> tasks, int taskId = 0)
        {
            var all = (tasks ?? Enumerable.Empty<QueuedTask>()).Where(t => t != null).ToList();
            if (taskId > 0)
            {
                var one = all.FirstOrDefault(t => t.Id == taskId);
                if (one == null) return $"没有任务 #{taskId} / No task #{taskId}.";
                if (!Pending(one)) return $"任务 #{taskId} 不在待验证状态，没有待测的测试清单 / Task #{taskId} is not awaiting verification and has no checklist to test.";
                all = new List<QueuedTask> { one };
            }
            var pending = all.Where(Pending).OrderBy(t => t.Id).ToList();
            if (pending.Count == 0) return "当前没有待验证任务的测试清单 / No task is awaiting verification.";
            var sb = new System.Text.StringBuilder();
            int total = 0, open = 0, openAi = 0;
            foreach (var t in pending)
            {
                var items = Ensure(t);
                int ai = items.Count(i => i != null && IsAi(i)), aiOpen = items.Count(i => i != null && IsAi(i) && !i.Checked);
                int left = items.Count(i => i != null && !i.Checked);
                total += items.Length; open += left; openAi += aiOpen;
                string text = (t.Text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (text.Length > 120) text = text.Substring(0, 119) + "…";
                sb.Append('#').Append(t.Id).Append(" → ").Append(t.VsName).Append(" | ").Append(text).AppendLine();
                sb.Append("  共 ").Append(items.Length).Append(" 项，未勾选 ").Append(left).Append("（AI ").Append(aiOpen).Append(" / 人工 ").Append(left - aiOpen)
                  .Append("）/ ").Append(items.Length).Append(" items, ").Append(left).Append(" unchecked (AI ").Append(aiOpen).Append(", manual ").Append(left - aiOpen).Append(')').AppendLine();
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i] == null) continue;
                    sb.Append("  ").Append(i + 1).Append(". ").Append(items[i].Checked ? "[x] " : "[ ] ").Append(IsAi(items[i]) ? "[AI] " : "[人工] ").Append(items[i].Text);
                    if (!string.IsNullOrWhiteSpace(items[i].Skill)) sb.Append(" 〔补 skill 中 / skill in progress: ").Append(items[i].Skill.Trim()).Append('〕');
                    sb.AppendLine();
                }
            }
            sb.Append("合计 / Total: ").Append(pending.Count).Append(" 个待验证任务 / tasks awaiting verification, ").Append(total).Append(" 项 / items, 未勾选 / unchecked ")
              .Append(open).Append("（AI ").Append(openAi).Append("）");
            return sb.ToString();
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
