using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class TaskTestChecklistTests
    {
        private static QueuedTask Task(string status = QueueStatus.Unverified, bool needsUser = false) =>
            new QueuedTask { Id = 7, Text = "任务 / Task", VsName = "VS", Status = status, NeedsUser = needsUser, CompletionToken = "abc", Created = DateTime.Now, Finished = DateTime.Now };

        [TestMethod]
        public void Parse_SplitsAiAndManualItems()
        {
            var items = TaskTestChecklist.Parse("- [ ] [AI] 编译通过\n- [ ] 【人工】单元测试后看界面\n- [ ] (AI 可验证) tasks.log 中有记录\n- [ ] 单元测试全部通过\n- [ ] 点击按钮后弹出窗口\n- [ ] 日志中出现提示且窗口显示\n- [ ] 重启后状态保留");
            CollectionAssert.AreEqual(new[] { "编译通过", "单元测试后看界面", "tasks.log 中有记录", "单元测试全部通过", "点击按钮后弹出窗口", "日志中出现提示且窗口显示", "重启后状态保留" },
                items.Select(i => i.Text).ToArray());
            CollectionAssert.AreEqual(new[] { true, false, true, true, false, false, false }, items.Select(TaskTestChecklist.IsAi).ToArray(),
                "标注优先，未标注时只有明确可用工具验证的归为 AI / Tags win; untagged items are AI only when clearly tool-checkable");
            Assert.IsFalse(TaskTestChecklist.IsAi(TaskTestChecklist.Parse("")[0]), "通用项为人工 / The fallback item is manual");
            Assert.IsTrue(TaskTestChecklist.IsAi(new TaskTestItem { Text = "msbuild 生成成功" }), "旧数据按文字推断 / Older data is inferred");
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Parse("- [ ] [AI] x")[0].Clone().By);
        }

        [DataTestMethod]
        [DataRow("重启后窗口位置、大小与最大化状态不变", "ai")]
        [DataRow("窗口最小化时重启，恢复后仍在原屏幕", "ai")]
        [DataRow("在 VS 对话页重启后仍停在该页并选中同一个 VS", "ai")]
        [DataRow("AI 输入框的草稿在重启后保留", "ai")]
        [DataRow("重启后托盘只有一个图标，没有残影", "ai")]
        [DataRow("先切到其他应用再触发重启，新窗口不抢焦点、不到前台", "ai")]
        [DataRow("restart-ui.json 与 restart-handoff.json 已被消费", "ai")]
        [DataRow("After restart the window position and size match", "ai")]
        [DataRow("重启时过渡画面没有闪烁", "user")]
        [DataRow("托盘图标颜色正确", "user")]
        [DataRow("点击「重启」按钮后窗口位置恢复", "user")]
        [DataRow("故意让生成失败，确认不会重启", "user")]
        [DataRow("Click the tray icon to open the window", "user")]
        public void Rejudge_UiStateIsAi_LooksClicksAndForcedFailuresStayManual(string text, string expected)
        {
            Assert.AreEqual(expected, TaskTestChecklist.Rejudge(text, TaskTestChecklist.ByUser));
            Assert.AreEqual(expected, TaskTestChecklist.Rejudge(text, TaskTestChecklist.ByAi));
            Assert.AreEqual(expected, TaskTestChecklist.Classify(text));
        }

        [TestMethod]
        public void Rejudge_KeepsOtherVerdictsAndOverridesTagsInParse()
        {
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Rejudge("编译通过", TaskTestChecklist.ByAi));
            Assert.AreEqual(TaskTestChecklist.ByUser, TaskTestChecklist.Rejudge("单元测试后看界面", TaskTestChecklist.ByUser));
            var items = TaskTestChecklist.Parse("- [ ] [人工] 重启后窗口位置不变\n- [ ] [AI] 过渡画面动画流畅\n- [ ] [人工] 看界面");
            CollectionAssert.AreEqual(new[] { "ai", "user", "user" }, items.Select(i => i.By).ToArray());
        }

        [TestMethod]
        public void Reclassify_ChangesOnlyUncheckedItemsOfExistingChecklists()
        {
            var t = Task();
            t.TestItems = new[]
            {
                new TaskTestItem { Text = "重启后草稿保留", By = TaskTestChecklist.ByUser },
                new TaskTestItem { Text = "托盘没有残影", By = TaskTestChecklist.ByUser, Checked = true },
                new TaskTestItem { Text = "点击按钮后弹窗动画正常", By = TaskTestChecklist.ByAi },
                new TaskTestItem { Text = "编译通过", By = TaskTestChecklist.ByAi },
            };
            Assert.AreEqual(2, TaskTestChecklist.Reclassify(t));
            CollectionAssert.AreEqual(new[] { "ai", "user", "user", "ai" }, t.TestItems.Select(i => i.By).ToArray());
            Assert.AreEqual(0, TaskTestChecklist.Reclassify(t));
            t.TestItems[0].By = TaskTestChecklist.ByUser;
            TaskTestChecklist.Ensure(t);
            Assert.AreEqual(TaskTestChecklist.ByAi, t.TestItems[0].By, "读取已有清单时也会重判 / Reading an existing checklist re-judges it");
            Assert.AreEqual(0, TaskTestChecklist.Reclassify(new QueuedTask()));
        }

        [TestMethod]
        public void SelfVerifyNotice_AutoVariantNamesProbesAndScenarioWithoutRetry()
        {
            var t = Task();
            t.TestItems = TaskTestChecklist.Parse("- [ ] 重启后窗口位置不变\n- [ ] 过渡画面没有闪烁");
            string auto = TaskTestChecklist.SelfVerifyNotice(t, loop: false);
            StringAssert.Contains(auto, "[自动验证 / Auto-verify]");
            foreach (string tool in new[] { "get_window_state", "get_foreground_window", "list_tray_icons", "read_restart_handoff", "prepare_restart_scenario", "mark_test_item" })
                StringAssert.Contains(auto, tool);
            Assert.IsFalse(auto.Contains("retry_task_with_info"), "非循环模式不重试 / No retry outside the loop");
            StringAssert.Contains(auto, "不得勾选");
            StringAssert.Contains(TaskTestChecklist.SelfVerifyNotice(t), "prepare_restart_scenario");
        }

        [TestMethod]
        public void SkillGap_RefusesHumanOnlyCheckedAndRepeatedItems()
        {
            Assert.IsNull(TaskTestChecklist.SkillGapRefusal(new TaskTestItem { Text = "设置页的保存路径显示为默认值", By = "user" }));
            StringAssert.Contains(TaskTestChecklist.SkillGapRefusal(new TaskTestItem { Text = "过渡画面没有闪烁" }), "human eyes");
            StringAssert.Contains(TaskTestChecklist.SkillGapRefusal(new TaskTestItem { Text = "设置页的保存路径显示为默认值", Checked = true }), "already checked");
            StringAssert.Contains(TaskTestChecklist.SkillGapRefusal(new TaskTestItem { Text = "设置页的保存路径显示为默认值", Skill = "list_tray_menu" }), "one per item");
            Assert.IsFalse(TaskTestChecklist.CanAiCheck(new TaskTestItem { Text = "设置页的保存路径显示为默认值", By = "user" }));
            Assert.IsTrue(TaskTestChecklist.CanAiCheck(new TaskTestItem { Text = "设置页的保存路径显示为默认值", By = "user", Skill = "list_tray_menu" }));
            Assert.IsFalse(TaskTestChecklist.CanAiCheck(new TaskTestItem { Text = "点击按钮后菜单弹出", By = "user", Skill = "x" }), "人眼 / 人手项仍不能勾选 / Human-only items stay manual");
            Assert.AreEqual("list_tray_menu", new TaskTestItem { Skill = "list_tray_menu" }.Clone().Skill);
        }

        [TestMethod]
        public void AutoVerifyNotice_FiresForSkillCandidatesOutsideTheLoop()
        {
            var t = Task();
            t.TestItems = TaskTestChecklist.Parse("- [ ] [人工] 设置页的保存路径显示为默认值\n- [ ] 过渡画面没有闪烁");
            Assert.AreEqual(0, TaskTestChecklist.RemainingAi(t));
            Assert.AreEqual(1, TaskTestChecklist.SkillCandidates(t));
            Assert.IsNull(TaskTestChecklist.SelfVerifyNotice(t), "循环只针对 AI 项 / The loop only targets AI items");
            string auto = TaskTestChecklist.SelfVerifyNotice(t, loop: false);
            StringAssert.Contains(auto, "start_skill_gap_loop");
            t.TestItems[0].Skill = "list_tray_menu";
            Assert.AreEqual(0, TaskTestChecklist.SkillCandidates(t));
            Assert.IsNull(TaskTestChecklist.SelfVerifyNotice(t, loop: false));
            StringAssert.Contains(TaskTestChecklist.Describe(new[] { t }), "skill in progress: list_tray_menu");
        }

        [TestMethod]
        public void RestartScenario_NormalizesAndRejectsContradictions()
        {
            var s = RestartScenario.Create(null, " Demo ", "草稿", "Max", 2, "其他应用", out string error);
            Assert.IsNull(error);
            Assert.AreEqual(RestartScenario.PageVs, s.Page, "指定 VS 隐含 VS 对话页 / A VS implies the VS page");
            Assert.AreEqual("Demo", s.Vs);
            Assert.AreEqual(RestartScenario.WindowMaximize, s.Window);
            Assert.AreEqual(RestartScenario.ForeOther, s.Foreground);
            StringAssert.Contains(s.Describe(), "草稿=2 字");
            Assert.IsFalse(s.Describe().Contains("草稿=草稿"), "不回显草稿内容 / Draft content is not echoed");
            Assert.IsNull(RestartScenario.Create("agent", "Demo", null, null, 0, null, out error));
            Assert.IsNull(RestartScenario.Create(null, null, null, "minimize", 0, "vsmanager", out error));
            Assert.IsNull(RestartScenario.Create(null, null, null, null, -1, null, out error));
            Assert.IsNull(RestartScenario.Create("desktop", null, null, null, 0, null, out error));
            StringAssert.Contains(error, "invalid page");
            Assert.IsNull(RestartScenario.Create(null, null, new string('x', RestartUi.MaxDraftChars + 1), null, 0, null, out error));
            Assert.AreEqual(RestartScenario.PageAgent, RestartScenario.Create("AI", null, null, null, 0, null, out _).Page);
        }

        [TestMethod]
        public void Rejudge_MetaItemsAboutOtherItemsTagsAreAi()
        {
            // 描述其他项应显示什么标注的元测试项：引号里的「动画 / 颜色 / 点击按钮」是在引用别的项
            // A meta item describing other items' tags: the quoted "animation / colors / click button" cite other items
            string meta = "启动后调用 list_test_checklists：已有待验证清单里『重启后窗口位置 / 草稿 / 托盘残影』类未勾选项显示为 [AI]，『动画 / 颜色 / 点击按钮』类显示为 [人工]";
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Rejudge(meta, TaskTestChecklist.ByUser));
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Classify(meta));
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.MetaVerdict("「动画」类测试项被标为【人工】"));
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Classify("Items like \"colors\" are shown as [人工] by list_tasks"));
            // 本项自身需要点击按钮时仍为人工 / Still manual when the item itself needs a click
            Assert.AreEqual(TaskTestChecklist.ByUser, TaskTestChecklist.Rejudge("点击「AI 验证」按钮后 list_tasks 中该项显示为 [AI]", TaskTestChecklist.ByAi));
            // 非元测试项不受影响 / Non-meta items are unaffected
            Assert.IsNull(TaskTestChecklist.MetaVerdict("重启后观察『正在重启』遮罩的动画"));
            Assert.AreEqual(TaskTestChecklist.ByUser, TaskTestChecklist.Rejudge("重启后观察『正在重启』遮罩的动画", TaskTestChecklist.ByAi));
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Rejudge("重启后草稿「测试草稿」仍在输入框", TaskTestChecklist.ByUser));
        }

        [TestMethod]
        public void RestartScenario_MergesRepeatedCalls()
        {
            // 先设草稿、再设页面与窗口：草稿不能丢 / Draft first, then page and window: the draft must survive
            var draft = RestartScenario.Create(null, null, "测试草稿", null, 0, null, out _);
            var page = RestartScenario.Create("vs", "Demo", null, "max", 0, null, out _);
            var m = page.MergeOnto(draft);
            Assert.AreEqual("测试草稿", m.Draft);
            Assert.AreEqual(RestartScenario.PageVs, m.Page);
            Assert.AreEqual("Demo", m.Vs);
            Assert.AreEqual(RestartScenario.WindowMaximize, m.Window);
            Assert.AreSame(page, page.MergeOnto(null));
            // 改成 AI 页时丢弃之前的 VS / Switching to the AI page drops the earlier VS
            var agent = RestartScenario.Create("agent", null, null, null, 0, null, out _).MergeOnto(m);
            Assert.AreEqual(RestartScenario.PageAgent, agent.Page);
            Assert.AreEqual("", agent.Vs);
            Assert.AreEqual("测试草稿", agent.Draft);
            // 本次最小化覆盖之前的「前台=VSManager」/ A new minimize overrides an earlier foreground=vsmanager
            var fore = RestartScenario.Create(null, null, null, null, 0, "vsmanager", out _);
            var min = RestartScenario.Create(null, null, null, "minimize", 0, null, out _).MergeOnto(fore);
            Assert.AreEqual(RestartScenario.WindowMinimize, min.Window);
            Assert.IsNull(min.Foreground);
            var fore2 = fore.MergeOnto(RestartScenario.Create(null, null, null, "minimize", 0, null, out _));
            Assert.IsNull(fore2.Window);
            Assert.AreEqual(RestartScenario.ForeVsManager, fore2.Foreground);
        }

        [TestMethod]
        public void SelfVerifyNotice_ListsUncheckedItemsOnlyWhenAiItemsRemain()
        {
            var t = Task();
            t.TestItems = TaskTestChecklist.Parse("- [ ] [AI] 编译通过\n- [ ] [人工] 看界面");
            string notice = TaskTestChecklist.SelfVerifyNotice(t);
            StringAssert.Contains(notice, "1. 编译通过");
            StringAssert.Contains(notice, "2. 看界面");
            StringAssert.Contains(notice, "mark_test_item");
            StringAssert.Contains(notice, "retry_task_with_info");
            Assert.AreEqual(1, TaskTestChecklist.RemainingAi(t));
            t.TestItems[0].Checked = true;
            Assert.IsNull(TaskTestChecklist.SelfVerifyNotice(t), "只剩人工项时不再提醒 / No notice when only manual items remain");
            t.TestItems[0].Checked = false;
            t.Status = QueueStatus.Done;
            Assert.IsNull(TaskTestChecklist.SelfVerifyNotice(t));
        }

        [TestMethod]
        public void Describe_ListsEveryPendingTaskWithNumberedItems()
        {
            var tasks = new List<QueuedTask>();
            for (int i = 1; i <= 15; i++)
            {
                var t = Task();
                t.Id = i;
                t.Result = "- [ ] [AI] 编译通过 " + i + "\n- [ ] [人工] 看界面 " + i;
                tasks.Add(t);
            }
            tasks[3].Status = QueueStatus.Done;
            tasks[0].TestItems = TaskTestChecklist.Parse(tasks[0].Result);
            tasks[0].TestItems[0].Checked = true;
            string all = TaskTestChecklist.Describe(tasks);
            foreach (var t in tasks.Where(t => t.Id != 4))
            {
                StringAssert.Contains(all, "#" + t.Id + " → VS");
                StringAssert.Contains(all, "2. [ ] [人工] 看界面 " + t.Id);
            }
            Assert.IsFalse(all.Contains("#4 → VS"), "非待验证任务不列出 / Tasks not awaiting verification are skipped");
            StringAssert.Contains(all, "1. [x] [AI] 编译通过 1");
            StringAssert.Contains(all, "Total: 14 ");
            Assert.IsNotNull(tasks[14].TestItems, "旧任务从回复补齐清单 / Older tasks get their checklist from the reply");
            string one = TaskTestChecklist.Describe(tasks, 15);
            StringAssert.Contains(one, "#15 → VS");
            Assert.IsFalse(one.Contains("#14 → VS"));
            StringAssert.Contains(TaskTestChecklist.Describe(tasks, 4), "not awaiting verification");
            StringAssert.Contains(TaskTestChecklist.Describe(tasks, 99), "No task #99");
            StringAssert.Contains(TaskTestChecklist.Describe(new QueuedTask[0]), "No task is awaiting verification");
        }

        [TestMethod]
        public void Parse_PrefersCheckboxLines()
        {
            var items = TaskTestChecklist.Parse("改动已完成。\n- [ ] 打开设置页，确认按钮可见\n- [x] **重启** 后状态保留\n1. 不是测试项\n");
            CollectionAssert.AreEqual(new[] { "打开设置页，确认按钮可见", "重启 后状态保留" }, items.Select(i => i.Text).ToArray());
            Assert.IsTrue(items.All(i => !i.Checked), "新清单一律未勾选 / New checklists start unchecked");
        }

        [TestMethod]
        public void Parse_UsesListAfterVerificationHeading()
        {
            string reply = "已修改：\n- 文件 A\n- 文件 B\n\n**未验证项**\n1. 在运行中的程序里点击链接\n2. 拖入图片后预览正常\n\n构建通过。";
            CollectionAssert.AreEqual(new[] { "在运行中的程序里点击链接", "拖入图片后预览正常" }, TaskTestChecklist.Parse(reply).Select(i => i.Text).ToArray());
        }

        [TestMethod]
        public void Parse_FallsBackToSingleGenericItem()
        {
            var items = TaskTestChecklist.Parse("功能已实现，构建通过。");
            Assert.AreEqual(1, items.Length);
            Assert.AreEqual(TaskTestChecklist.FallbackItem, items[0].Text);
            Assert.AreEqual(1, TaskTestChecklist.Parse(null).Length);
        }

        [TestMethod]
        public void CheckingAllItems_CompletesUnverifiedAndNeedsUserTasks()
        {
            var t = Task();
            t.TestItems = TaskTestChecklist.Parse("- [ ] A\n- [ ] B");
            Assert.IsTrue(TaskStateMachine.SetTestItem(t, 0, true, out bool all));
            Assert.IsFalse(all);
            Assert.AreEqual(1, TaskTestChecklist.Remaining(t));
            Assert.IsFalse(TaskStateMachine.SetTestItem(t, 5, true, out _));
            Assert.IsTrue(TaskStateMachine.SetTestItem(t, 1, true, out all));
            Assert.IsTrue(all);
            Assert.IsTrue(TaskStateMachine.MarkVerified(t));
            Assert.AreEqual(QueueStatus.Done, t.Status);
            Assert.IsFalse(TaskTestChecklist.Pending(t));

            var n = Task(QueueStatus.Done, needsUser: true);
            n.Result = "请验证：\n- 打开窗口";
            // 旧的「已完成（待用户验证）」迁移为待验证 / Legacy done-awaiting-verification migrates to awaiting verification
            Assert.IsFalse(TaskTestChecklist.Pending(n));
            TaskStateMachine.MigrateLegacy(n);
            Assert.AreEqual(QueueStatus.Unverified, n.Status);
            Assert.IsTrue(TaskTestChecklist.Pending(n));
            Assert.AreEqual("打开窗口", TaskTestChecklist.Ensure(n).Single().Text);
            Assert.IsTrue(TaskStateMachine.MarkVerified(n));
            Assert.IsFalse(n.NeedsUser);
            Assert.IsTrue(n.TestItems.All(i => i.Checked));
            Assert.IsFalse(TaskStateMachine.MarkVerified(n));
        }

        [TestMethod]
        public void RequeueAndFailure_ClearChecklist()
        {
            var t = Task();
            t.TestItems = TaskTestChecklist.Parse("- [ ] A");
            Assert.AreEqual("A", t.Clone().TestItems.Single().Text);
            Assert.AreNotSame(t.TestItems[0], t.Clone().TestItems[0]);
            TaskStateMachine.Requeue(t);
            Assert.IsNull(t.TestItems);
            t.TestItems = TaskTestChecklist.Parse("- [ ] A");
            TaskStateMachine.Fail(t, "x", DateTime.Now);
            Assert.IsNull(t.TestItems);
        }

        [TestMethod]
        public void TaskStore_RoundTripsChecklistAndTitle()
        {
            string dir = Path.Combine(Path.GetTempPath(), "vsm-checklist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new JsonTaskStore(Path.Combine(dir, "tasks.json"));
                var t = Task();
                t.Title = "测试清单";
                t.TestItems = new[] { new TaskTestItem { Text = "A", Checked = true }, new TaskTestItem { Text = "B" } };
                Assert.IsNull(store.Save(new List<QueuedTask> { t }));
                var loaded = store.Load(new List<string>()).Single();
                Assert.AreEqual("测试清单", loaded.Title);
                CollectionAssert.AreEqual(new[] { "A", "B" }, loaded.TestItems.Select(i => i.Text).ToArray());
                CollectionAssert.AreEqual(new[] { true, false }, loaded.TestItems.Select(i => i.Checked).ToArray());
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void ChecklistPanel_ListsOnlyTasksAwaitingTests()
        {
            var unverified = Task();
            var needsUser = Task(QueueStatus.Done, true); needsUser.Id = 8; needsUser.TestItems = TaskTestChecklist.Parse("- [ ] A");
            var legacyNeedsUser = Task(QueueStatus.Done, true); legacyNeedsUser.Id = 9;
            TaskStateMachine.MigrateLegacy(needsUser); TaskStateMachine.MigrateLegacy(legacyNeedsUser);
            var done = Task(QueueStatus.Done); done.Id = 10;
            var ids = TestChecklistPanel.ListedTasks(new[] { unverified, needsUser, legacyNeedsUser, done }, null).Select(t => t.Id).ToArray();
            CollectionAssert.AreEquivalent(new[] { 7, 8, 9 }, ids);
        }

        [TestMethod]
        public void DispatchRules_AskForCheckboxTestList()
        {
            string text = TaskStateMachine.DispatchText(Task(QueueStatus.Running));
            StringAssert.Contains(text, "- [ ]");
            StringAssert.Contains(text, "测试清单");
        }

        [TestMethod]
        public void UserBusy_IsRetriedLaterWithoutCountingAttempts()
        {
            var t = Task(QueueStatus.Sending);
            t.Attempts = 1;
            var now = DateTime.Now;
            string result = SendRetryPolicy.UserBusyPrefix + "用户正在操作";
            Assert.AreEqual(SendDecision.Retry, TaskStateMachine.ApplySendResult(t, result, now));
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
            Assert.AreEqual(0, t.Attempts);
            Assert.IsTrue(t.NextTry > now);
            Assert.AreEqual("等待用户操作空闲", TaskStateMachine.StatusText(t, now));
        }

        [TestMethod]
        public void WaitIdle_WaitsForQuietInputAndGivesUp()
        {
            int idle = 0, slept = 0;
            Assert.IsTrue(UserActivity.WaitIdle(() => idle, 1500, 4000, ms => { slept += ms; idle += ms; }));
            Assert.IsTrue(slept >= 1500 && slept < 4000);
            slept = 0;
            Assert.IsFalse(UserActivity.WaitIdle(() => 0, 1500, 1000, ms => slept += ms));
            Assert.IsTrue(slept >= 1000);
            Assert.IsTrue(UserActivity.WaitIdle(() => 5000, 1500, 0, ms => Assert.Fail()));
        }
    }
}
