using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class MenuStyleTests
    {
        [TestMethod]
        public void TaskHeader_ManualStartIsVisible_AndButtonsDoNotOverlapHistory()
        {
            RunSta(() =>
            {
                using (var data = new TempDataFolder())
                using (var panel = new TaskPanel())
                {
                    var queue = NewQueue();
                    var done = queue.Add("A", "A", "done", "AI");
                    done.Status = QueueStatus.Done;
                    done.Finished = DateTime.Now.AddMinutes(-1);
                    panel.Bind(queue, clearedAt: () => DateTime.Now);
                    panel.SetCollapsed(false);
                    panel.PerformLayout();
                    var top = panel.Controls.OfType<Panel>().Single();
                    var buttons = top.Controls.OfType<FlatButton>().Where(b => b.Visible).ToArray();
                    var start = buttons.Single(b => b.Text == "开始流程 / Start");
                    Assert.IsTrue(start.Enabled);
                    Assert.IsTrue(buttons.Any(b => b.Text == "历史"));
                    Assert.IsTrue(start.Width >= Dpi.S(120));
                    foreach (var button in buttons)
                    {
                        Assert.IsTrue(top.ClientRectangle.Contains(button.Bounds), button.Text);
                        foreach (var other in buttons.Where(b => b != button))
                            Assert.IsFalse(button.Bounds.IntersectsWith(other.Bounds), button.Text + " / " + other.Text);
                    }
                    int clicks = 0;
                    panel.ActionRequested += (task, action) =>
                    {
                        Assert.IsNull(task);
                        Assert.AreEqual("start", action);
                        clicks++;
                        panel.SetWorkflowStarted(true);
                    };
                    typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(start, new object[] { EventArgs.Empty });
                    Assert.AreEqual(1, clicks);
                    Assert.IsFalse(start.Enabled);
                    Assert.AreEqual("已启动 / Started", start.Text);
                    start.PerformClick();
                    Assert.AreEqual(1, clicks);
                    panel.SetCollapsed(true);
                    Assert.IsFalse(start.Visible);
                    panel.SetCollapsed(false);
                    Assert.IsTrue(start.Visible);
                    Assert.IsFalse(start.Enabled);
                }
            });
        }

        [TestMethod]
        public void Headings_FollowVisibilityAfterOpeningHandlers_WithoutEmptyGroups()
        {
            RunSta(() =>
            {
                using (var menu = new TestMenu())
                {
                    menu.AddGroup("空组 / Empty");
                    menu.AddGroup("任务 / Tasks");
                    var task = menu.Items.Add("任务项 / Task item");
                    menu.AddGroup("AI 助手 / AI assistant");
                    var ai = menu.Items.Add("AI 项 / AI item");
                    menu.AddGroup("尾部空组 / Empty tail");
                    bool showAi = false;
                    menu.Opening += (s, e) => { task.Visible = !showAi; ai.Visible = showAi; };
                    menu.Prepare();
                    CollectionAssert.AreEqual(new[] { "任务 / Tasks", "任务项 / Task item" }, AvailableText(menu));
                    showAi = true;
                    menu.Prepare();
                    CollectionAssert.AreEqual(new[] { "AI 助手 / AI assistant", "AI 项 / AI item" }, AvailableText(menu));
                    Assert.IsFalse(menu.Items.OfType<ToolStripSeparator>().Any());
                    Assert.IsTrue(menu.Items.OfType<MenuGroupHeader>().All(h => !h.Enabled && !h.CanSelect));
                }
            });
        }

        [TestMethod]
        public void Opening_RebuiltMenus_HideAbsentTargets_AndKeepDisabledGroups()
        {
            RunSta(() =>
            {
                using (var menu = new TestMenu())
                {
                    bool hasVs = false;
                    menu.Opening += (s, e) =>
                    {
                        menu.Items.Clear();
                        menu.AddGroup("AI 助手 / AI assistant");
                        menu.Items.Add(new ToolStripMenuItem("重启 / Restart") { Enabled = false });
                        menu.AddGroup("VS 操作 / Visual Studio");
                        if (hasVs) menu.Items.Add("示例 VS / Example VS");
                    };
                    menu.Prepare();
                    CollectionAssert.AreEqual(new[] { "AI 助手 / AI assistant", "重启 / Restart" }, AvailableText(menu));
                    hasVs = true;
                    menu.Prepare();
                    Assert.AreEqual(4, AvailableText(menu).Length);
                    hasVs = false;
                    menu.Prepare();
                    Assert.AreEqual(2, AvailableText(menu).Length);
                }
            });
        }

        [TestMethod]
        public void NativePopup_DynamicOpeningWorks_AndCancellationIsRespected()
        {
            RunSta(() =>
            {
                using (var menu = new TestMenu())
                {
                    bool cancel = false;
                    Action populate = () =>
                    {
                        menu.Items.Clear();
                        menu.AddGroup("空组 / Empty");
                        menu.AddGroup("测试 / Test");
                        menu.Items.Add("示例操作 / Example action");
                    };
                    populate();
                    menu.Opening += (s, e) =>
                    {
                        populate();
                        if (cancel) e.Cancel = true;
                    };
                    menu.Show(new Point(20, 20));
                    Assert.IsTrue(menu.Visible);
                    Assert.AreEqual(2, menu.Items.Cast<ToolStripItem>().Count(i => i.Visible));
                    Assert.IsTrue(menu.Width > 0 && menu.Height > 0);
                    menu.Close();
                    cancel = true;
                    menu.Show(new Point(20, 20));
                    Assert.IsFalse(menu.Visible);
                }
            });
        }

        [TestMethod]
        public void Style_KeepsShortcutsChecksAndClickHandlers()
        {
            RunSta(() =>
            {
                using (var menu = new TestMenu())
                {
                    menu.AddGroup("AI 助手 / AI assistant");
                    int clicks = 0;
                    var action = new ToolStripMenuItem("示例 / Example") { ShortcutKeys = Keys.Control | Keys.R, CheckOnClick = true };
                    action.Click += (s, e) => clicks++;
                    menu.Items.Add(action);
                    menu.Prepare();
                    Assert.AreEqual(Theme.Regular, menu.Font);
                    Assert.AreEqual(Theme.Small, menu.Items[0].Font);
                    Assert.AreEqual(new Size(Dpi.S(16), Dpi.S(16)), menu.ImageScalingSize);
                    Assert.IsTrue(menu.ShowImageMargin);
                    Assert.IsFalse(menu.ShowCheckMargin);
                    Assert.AreEqual(new Padding(Dpi.S(6), Dpi.S(4), Dpi.S(10), Dpi.S(4)), action.Padding);
                    Assert.AreEqual(ContentAlignment.MiddleCenter, action.ImageAlign);
                    Assert.AreEqual(ContentAlignment.MiddleLeft, action.TextAlign);
                    Assert.AreEqual(Keys.Control | Keys.R, action.ShortcutKeys);
                    action.PerformClick();
                    Assert.IsTrue(action.Checked);
                    Assert.AreEqual(1, clicks);
                    action.Enabled = false;
                    menu.RefreshGroups();
                    action.PerformClick();
                    Assert.AreEqual(1, clicks);
                    Assert.IsTrue(menu.Items[0].Available);
                }
            });
        }

        [DataTestMethod]
        [DataRow(QueueStatus.Waiting)]
        [DataRow(QueueStatus.WaitingVs)]
        [DataRow(QueueStatus.Sending)]
        [DataRow(QueueStatus.Running)]
        [DataRow(QueueStatus.Done)]
        [DataRow(QueueStatus.Failed)]
        [DataRow(QueueStatus.Cancelled)]
        public void TaskMenu_PreservesActionsAndEnabledRules_ForEveryStatus(string status)
        {
            RunSta(() =>
            {
                using (var data = new TempDataFolder())
                using (var panel = new TaskPanel())
                {
                    var queue = NewQueue();
                    var task = queue.Add("example", "Example", "Example task", "AI");
                    task.Status = status;
                    panel.Bind(queue);
                    var list = panel.Controls.OfType<VsListBox>().Single();
                    list.SelectedItem = task;
                    var menu = (GroupedContextMenuStrip)list.ContextMenuStrip;
                    Prepare(menu);
                    string cancelText = status == QueueStatus.Running ? "停止跟踪（不停止 Copilot）" : "取消任务";
                    CollectionAssert.AreEqual(new[]
                    {
                        "任务操作与历史 / Tasks and history", "立即尝试发布", "重新排队", cancelText,
                        "复制任务内容", "从清单中删除", "清除已完成（仅界面）", "显示已清除的历史",
                        "撤销清除（恢复显示全部历史）", "VS 操作 / Visual Studio", "查看该 VS 的对话"
                    }, AvailableText(menu));
                    Assert.AreEqual(status == QueueStatus.Waiting || status == QueueStatus.WaitingVs, Find(menu, "立即尝试发布").Enabled);
                    Assert.AreEqual(status == QueueStatus.Failed || status == QueueStatus.Cancelled, Find(menu, "重新排队").Enabled);
                    Assert.AreEqual(status == QueueStatus.Waiting || status == QueueStatus.WaitingVs || status == QueueStatus.Running, Find(menu, cancelText).Enabled);
                    Assert.AreEqual(status != QueueStatus.Sending, Find(menu, "从清单中删除").Enabled);
                    var actions = new List<string>();
                    panel.ActionRequested += (t, action) => { Assert.AreSame(task, t); actions.Add(action); };
                    var pairs = new[] { Tuple.Create("立即尝试发布", "dispatch"), Tuple.Create("重新排队", "retry"), Tuple.Create(cancelText, "cancel"), Tuple.Create("从清单中删除", "remove"), Tuple.Create("查看该 VS 的对话", "open") };
                    foreach (var pair in pairs)
                    {
                        var item = Find(menu, pair.Item1);
                        int before = actions.Count;
                        item.PerformClick();
                        Assert.AreEqual(before + (item.Enabled ? 1 : 0), actions.Count);
                        if (item.Enabled) Assert.AreEqual(pair.Item2, actions.Last());
                    }
                    Assert.AreEqual(status, task.Status);
                }
            });
        }

        [TestMethod]
        public void TaskMenu_ShowsViewAttachments_OnlyForTasksWithAttachments()
        {
            RunSta(() =>
            {
                using (var data = new TempDataFolder())
                using (var panel = new TaskPanel())
                {
                    var queue = NewQueue();
                    var files = new[] { new AttachmentRef { Id = "20250101-000000000-abcdef", Name = "a.png", Kind = AttachmentKind.Image, Sha256 = "h" } };
                    var task = queue.Add("example", "Example", "Example task", "AI", files);
                    panel.Bind(queue);
                    var list = panel.Controls.OfType<VsListBox>().Single();
                    list.SelectedItem = task;
                    var menu = (GroupedContextMenuStrip)list.ContextMenuStrip;
                    Prepare(menu);
                    var item = Find(menu, "查看附件（1）/ View attachments");
                    Assert.IsTrue(item.Available);
                    Assert.IsTrue(item.Enabled);
                    string action = null;
                    panel.ActionRequested += (t, a) => action = a;
                    item.PerformClick();
                    Assert.AreEqual("attachments", action);
                }
            });
        }

        [TestMethod]
        public void TaskList_GroupsByVs_HeadersAreNotSelectable_AndCollapseAndModePersist()
        {
            RunSta(() =>
            {
                using (var data = new TempDataFolder())
                using (var panel = new TaskPanel())
                {
                    var queue = NewQueue();
                    var a1 = queue.Add("vs-a", "A", "Task one", "AI");
                    var b1 = queue.Add("vs-b", "B", "Task two", "AI");
                    var a2 = queue.Add("vs-a", "A", "Task three", "AI");
                    panel.VsProvider = () => new[] { new TaskGroupVs { Key = "vs-a", Name = "A", Number = 1 }, new TaskGroupVs { Key = "vs-b", Name = "B", Number = 2 } };
                    int changes = 0;
                    panel.ViewOptionsChanged += () => changes++;
                    panel.Bind(queue);
                    var list = panel.Controls.OfType<VsListBox>().Single();
                    Assert.IsTrue(panel.GroupByVs);
                    var headers = list.Items.OfType<TaskGroupHeader>().ToList();
                    Assert.AreEqual(2, headers.Count);
                    Assert.IsInstanceOfType(list.Items[0], typeof(TaskGroupHeader));

                    // 标题行不可选中：选中时跳到相邻任务 / Header rows are never selected: selection moves to the neighbouring task
                    list.SelectedIndex = 0;
                    Assert.IsInstanceOfType(list.SelectedItem, typeof(QueuedTask));

                    string keyA = headers.Single(h => h.Number == 1).Key;
                    panel.ToggleGroup(keyA);
                    Assert.AreEqual(1, changes);
                    CollectionAssert.Contains(panel.CollapsedGroups, keyA);
                    Assert.IsFalse(list.Items.Contains(a1) || list.Items.Contains(a2));
                    Assert.IsTrue(list.Items.Contains(b1));
                    panel.ToggleGroup(keyA);
                    Assert.IsTrue(list.Items.Contains(a1));

                    panel.SetGroupByVs(false);
                    Assert.AreEqual(3, changes);
                    Assert.IsFalse(list.Items.OfType<TaskGroupHeader>().Any());
                    Assert.AreEqual(3, list.Items.Count);

                    panel.SetViewOptions(true, TaskGrouping.SortByNumber, new[] { keyA });
                    Assert.AreEqual(3, changes);
                    Assert.AreEqual("@1 A", ((TaskGroupHeader)list.Items[0]).Title);
                    Assert.IsTrue(((TaskGroupHeader)list.Items[0]).Collapsed);
                }
            });
        }

        [TestMethod]
        public void TaskList_HeaderContextMenu_ShowsGroupActionsOnly()
        {
            RunSta(() =>
            {
                using (var data = new TempDataFolder())
                using (var panel = new TaskPanel())
                {
                    var queue = NewQueue();
                    queue.Add("vs-a", "A", "Task one", "AI");
                    panel.Bind(queue);
                    var list = panel.Controls.OfType<VsListBox>().Single();
                    var header = (TaskGroupHeader)list.Items[0];
                    typeof(TaskPanel).GetField("_menuHeader", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panel, header);
                    var menu = (GroupedContextMenuStrip)list.ContextMenuStrip;
                    Prepare(menu);
                    var shown = AvailableText(menu);
                    CollectionAssert.Contains(shown, "折叠该分组 / Collapse group");
                    CollectionAssert.Contains(shown, "切换为平铺列表 / Switch to flat list");
                    CollectionAssert.DoesNotContain(shown, "立即尝试发布");
                    CollectionAssert.DoesNotContain(shown, "查看该 VS 的对话");
                    Find(menu, "折叠该分组 / Collapse group").PerformClick();
                    Assert.IsTrue(((TaskGroupHeader)list.Items[0]).Collapsed);
                    Assert.AreEqual(1, list.Items.Count);

                    // 之后在任务上右键：恢复原任务菜单 / Right-clicking a task afterwards restores the task menu
                    Prepare(menu);
                    CollectionAssert.DoesNotContain(AvailableText(menu), "折叠该分组 / Collapse group");
                }
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ExternalChatMenu_PreservesDynamicNamesAndStopHandler(bool generating)
        {
            RunSta(() =>
            {
                using (var data = new TempDataFolder())
                using (var panel = new TaskPanel())
                {
                    var queue = NewQueue();
                    var task = queue.Add("example", "Example", "Example task", "AI");
                    var chat = new ExternalChat { Id = 1, VsName = "Example", Question = "Example", Generating = generating, Started = DateTime.Now };
                    panel.Bind(queue, () => new[] { chat });
                    var list = panel.Controls.OfType<VsListBox>().Single();
                    list.SelectedItem = chat;
                    var menu = (GroupedContextMenuStrip)list.ContextMenuStrip;
                    Prepare(menu);
                    CollectionAssert.AreEqual(new[]
                    {
                        "任务操作与历史 / Tasks and history", "复制提问与回答", "从清单中移除", "清除已完成（仅界面）",
                        "显示已清除的历史", "撤销清除（恢复显示全部历史）", "AI 对话 / AI chat", "■ 停止生成",
                        "VS 操作 / Visual Studio", "打开该 VS 并定位对话"
                    }, AvailableText(menu));
                    string requested = null;
                    panel.ExternalActionRequested += (c, action) => { Assert.AreSame(chat, c); requested = action; };
                    Find(menu, "■ 停止生成").PerformClick();
                    Assert.AreEqual(generating ? "stop" : null, requested);
                    Find(menu, "打开该 VS 并定位对话").PerformClick();
                    Assert.AreEqual("open", requested);
                    list.SelectedItem = task;
                    Prepare(menu);
                    Assert.IsFalse(menu.Items.OfType<MenuGroupHeader>().Single(h => h.Text == "AI 对话 / AI chat").Available);
                    Assert.IsTrue(Find(menu, "查看该 VS 的对话").Available);
                }
            });
        }

        [TestMethod]
        public void TaskHistoryMenu_PreservesHistoryToggleAndUnhideAction()
        {
            RunSta(() =>
            {
                using (var data = new TempDataFolder())
                using (var panel = new TaskPanel())
                {
                    var queue = NewQueue();
                    var task = queue.Add("example", "Example", "Example task", "AI");
                    task.Status = QueueStatus.Failed;
                    var marks = new List<HiddenTaskMark>();
                    TaskHideList.Add(marks, task.Id, task.Id + 1, DateTime.Now);
                    panel.Bind(queue, hiddenMarks: () => marks);
                    var list = panel.Controls.OfType<VsListBox>().Single();
                    var menu = (GroupedContextMenuStrip)list.ContextMenuStrip;
                    Prepare(menu);
                    Assert.AreEqual(0, list.Items.Count);
                    Assert.IsFalse(Find(menu, "恢复显示该失败条目 / Show this failed entry again").Available);
                    Find(menu, "显示已清除的历史").PerformClick();
                    list.SelectedItem = task;
                    Prepare(menu);
                    Assert.IsTrue(((ToolStripMenuItem)Find(menu, "显示已清除的历史")).Checked);
                    Assert.IsTrue(Find(menu, "恢复显示该失败条目 / Show this failed entry again").Available);
                    string action = null;
                    panel.ActionRequested += (t, a) => { Assert.AreSame(task, t); action = a; };
                    Find(menu, "恢复显示该失败条目 / Show this failed entry again").PerformClick();
                    Assert.AreEqual("unhide", action);
                    Assert.AreEqual(QueueStatus.Failed, task.Status);
                    Assert.AreEqual(1, marks.Count);
                }
            });
        }

        [TestMethod]
        public void DisabledText_IsRenderedInMutedThemeColor()
        {
            RunSta(() =>
            {
                using (var menu = new TestMenu())
                using (var image = new Bitmap(180, 36))
                using (var graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Theme.Elevated);
                    var item = new ToolStripMenuItem("Disabled") { Enabled = false };
                    menu.Items.Add(item);
                    menu.Renderer.DrawItemText(new ToolStripItemTextRenderEventArgs(graphics, item, item.Text,
                        new Rectangle(0, 0, image.Width, image.Height), Color.White, Theme.Regular, TextFormatFlags.NoPadding));
                    int brightest = 0;
                    for (int y = 0; y < image.Height; y++)
                        for (int x = 0; x < image.Width; x++)
                        {
                            Color pixel = image.GetPixel(x, y);
                            brightest = Math.Max(brightest, Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)));
                        }
                    Assert.IsTrue(brightest > 60 && brightest <= 140);
                }
            });
        }

        private static TaskQueue NewQueue() => new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), () => DateTime.Now);
        private static string[] AvailableText(ToolStrip menu) => menu.Items.Cast<ToolStripItem>().Where(i => i.Available).Select(i => i.Text).ToArray();
        private static ToolStripItem Find(ToolStrip menu, string text) => menu.Items.Cast<ToolStripItem>().Single(i => i.Text == text);
        private static void Prepare(GroupedContextMenuStrip menu) => typeof(GroupedContextMenuStrip)
            .GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(menu, new object[] { new CancelEventArgs() });

        private sealed class TestMenu : GroupedContextMenuStrip
        {
            public void Prepare() => OnOpening(new CancelEventArgs());
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "菜单测试超时 / Menu test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
