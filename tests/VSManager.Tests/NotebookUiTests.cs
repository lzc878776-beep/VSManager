using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Web.WebView2.WinForms;

namespace VSManager.Tests
{
    [TestClass]
    public class NotebookUiTests
    {
        [TestMethod]
        public void Notebook_SelectEditSaveAndReopen_UsesNamedTreeAndDiskContent()
        {
            RunUi((form, store) =>
            {
                var tree = Field<TreeView>(form, "_tree");
                Assert.AreEqual("Project", tree.Nodes[0].Nodes[0].Text);
                Assert.AreEqual("Ideas", tree.Nodes[0].Nodes[0].Nodes[0].Text);
                tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes[0];
                var editor = Field<TextBox>(form, "_editor");
                Assert.AreEqual("original", editor.Text);
                editor.Text = "# Ideas\r\n\r\n- [ ] Ship it\r\n";
                Assert.IsTrue(form.TrySave());
                string ideas = ((NotebookEntry)tree.Nodes[0].Nodes[0].Nodes[0].Tag).Path;
                string saved = store.Read(ideas).Text;
                Assert.AreEqual(editor.Text.Replace("\r\n", "\n"), saved);
                Assert.AreEqual("original", store.Read(((NotebookEntry)tree.Nodes[0].Nodes[0].Tag).Path).Text, "父页面也有正文 / Parent pages have content");
                tree.SelectedNode = tree.Nodes[0];
                Assert.IsFalse(editor.Enabled);
                tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes[0];
                Assert.AreEqual(saved, editor.Text.Replace("\r\n", "\n"));
            });
        }

        [TestMethod]
        public void Notebook_LayoutIsAutomatic_WithoutToolbarButtons()
        {
            RunUi((form, store) =>
            {
                var tree = Field<TreeView>(form, "_tree");
                tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes[0];
                var split = Field<SplitContainer>(form, "_split");
                split.Dock = DockStyle.None;
                split.Width = Dpi.S(1000);
                Assert.IsTrue(split.Panel1Collapsed, "宽布局也不显示 Markdown 原文 / Wide layout does not show the Markdown source");
                Assert.IsFalse(split.Panel2Collapsed, "宽布局显示阅读视图 / Wide layout shows the preview");
                split.Width = Dpi.S(500);
                Assert.IsTrue(split.Panel1Collapsed, "窄布局默认阅读 / Narrow layout reads by default");
                Assert.IsFalse(split.Panel2Collapsed);
                split.Width = Dpi.S(1000);
                typeof(NotebookWorkspace).GetMethod("BeginEdit", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form.Workspace, null);
                Assert.IsFalse(split.Panel1Collapsed, "编辑时显示编辑器 / Editing shows the editor");
                Assert.IsTrue(split.Panel2Collapsed, "编辑时不并排显示 / Editing does not show both side by side");
                tree.SelectedNode = tree.Nodes[0].Nodes[0];
                Assert.IsTrue(split.Panel1Collapsed, "切换页面后回到阅读 / Switching pages returns to reading");
                var labels = new[] { "阅读 / Read", "编辑 / Edit", "分栏 / Split", "另存草稿 / Save copy", "插图 / Image", "复制给 AI / Copy" };
                foreach (Control control in Descendants(form.Workspace))
                    Assert.IsFalse(control is Button && labels.Contains(control.Text), control.Text);
            });
        }

        [TestMethod]
        public void Notebook_AgentPromptPage_IsTopLevelBesideNotebooks()
        {
            RunUi((form, store) =>
            {
                string prompt = NotebookAgentPrompt.Ensure(store);
                typeof(NotebookWorkspace).GetMethod("RefreshTree", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form.Workspace, new object[] { prompt, false });
                var tree = Field<TreeView>(form, "_tree");
                Assert.AreEqual(2, tree.Nodes.Count, "提示词页与笔记本同级 / The prompt page sits beside the notebooks root");
                Assert.AreEqual(NotebookAgentPrompt.PageTitle, tree.Nodes[1].Text);
                Assert.AreSame(tree.Nodes[1], tree.SelectedNode, "可直接选中提示词页 / The prompt page can be selected");
                foreach (TreeNode child in tree.Nodes[0].Nodes)
                    Assert.AreNotEqual(NotebookAgentPrompt.PageTitle, child.Text, "不再混在普通笔记中 / No longer listed among ordinary notes");
            });
        }

        [TestMethod]
        [TestCategory(TestKind.Ui)]
        public void Notebook_PastedScreenshot_IsStoredAndLinked()
        {
            RunUi((form, store) =>
            {
                var tree = Field<TreeView>(form, "_tree");
                tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes[0];
                string page = ((NotebookEntry)tree.SelectedNode.Tag).Path;
                var editor = Field<TextBox>(form, "_editor");
                using (var bitmap = new Bitmap(4, 4))
                {
                    Clipboard.SetImage(bitmap);
                    Assert.IsTrue((bool)typeof(NotebookWorkspace).GetMethod("PasteImages", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form.Workspace, null));
                }
                StringAssert.Contains(editor.Text, "](attachments/");
                string target = editor.Text.Substring(editor.Text.IndexOf("](", StringComparison.Ordinal) + 2);
                target = target.Substring(0, target.IndexOf(')'));
                StringAssert.StartsWith(store.ImageData(page, target), "data:image/png;base64,");
                Clipboard.SetText("plain");
                Assert.IsFalse((bool)typeof(NotebookWorkspace).GetMethod("PasteImages", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form.Workspace, null));
            });
        }

        [TestMethod]
        public void Notebook_Conflict_AutomaticallySavesDraftAsCopy()
        {
            RunUi((form, store) =>
            {
                var tree = Field<TreeView>(form, "_tree");
                var note = tree.Nodes[0].Nodes[0].Nodes[0];
                tree.SelectedNode = note;
                var editor = Field<TextBox>(form, "_editor");
                editor.Text = "local draft";
                string path = ((NotebookEntry)note.Tag).Path;
                var other = new NotebookStore(store.Root);
                other.Save(other.Read(path), "external change");
                Assert.IsTrue(form.TrySave(), "冲突时自动另存草稿 / Conflicts save the draft as a copy");
                Assert.AreEqual("external change", store.Read(path).Text);
                StringAssert.Contains(Field<Label>(form, "_status").Text, "Page changed");
                Assert.AreEqual(Theme.TextSecondary, Field<Label>(form, "_status").ForeColor);
                string parent = ((NotebookEntry)tree.Nodes[0].Nodes[0].Tag).Path;
                var copy = store.LoadTree().Single().Children.Single(c => c.Name.StartsWith("Ideas-draft-", StringComparison.Ordinal));
                Assert.AreEqual("local draft", store.Read(copy.Path).Text);
                Assert.AreEqual(parent, store.ParentOf(copy.Path));
                editor.Text = "local draft 2";
                Assert.IsTrue(form.TrySave());
                Assert.AreEqual("local draft 2", store.Read(copy.Path).Text, "后续编辑写入副本 / Later edits go to the copy");
                Assert.AreEqual("external change", store.Read(path).Text);
            });
        }

        [TestMethod]
        public void Notebook_NativeControlsAndPreviewStates_UseSharedDarkPalette()
        {
            RunUi((form, store) =>
            {
                Assert.AreEqual(Theme.Background, form.BackColor);
                Assert.AreEqual(Theme.Text, form.ForeColor);
                Assert.AreEqual(Theme.Background, form.Workspace.BackColor);
                Assert.AreEqual(Theme.Sidebar, form.Workspace.Sidebar.BackColor);
                var tree = Field<TreeView>(form, "_tree");
                Assert.AreEqual(Theme.Sidebar, tree.BackColor);
                Assert.AreEqual(Theme.Text, tree.ForeColor);
                Assert.AreEqual(Theme.FontName, tree.Font.Name);
                Assert.AreEqual(TreeViewDrawMode.OwnerDrawAll, tree.DrawMode);
                tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes[0];
                tree.SelectedNode.EnsureVisible();
                using (var image = new Bitmap(tree.Width, tree.Height))
                {
                    tree.DrawToBitmap(image, tree.ClientRectangle);
                    int centerY = tree.SelectedNode.Bounds.Top + tree.ItemHeight / 2;
                    Assert.AreEqual(Theme.RowSelected.ToArgb(), image.GetPixel(Dpi.S(5), centerY).ToArgb());
                    Assert.AreEqual(Theme.Accent.ToArgb(), image.GetPixel(Dpi.S(1), centerY).ToArgb());
                }
                Assert.AreEqual(Theme.Elevated, tree.ContextMenuStrip.BackColor);
                Assert.AreEqual(Theme.Text, tree.ContextMenuStrip.ForeColor);
                using (var reference = new ContextMenuStrip())
                {
                    Theme.Apply(reference);
                    Assert.AreEqual(reference.Renderer.GetType(), tree.ContextMenuStrip.Renderer.GetType());
                }
                var search = Field<TextBox>(form, "_search");
                Assert.AreEqual(Theme.Surface, search.BackColor);
                Assert.AreEqual(Theme.Text, search.ForeColor);
                var editor = Field<TextBox>(form, "_editor");
                Assert.AreEqual(Theme.Background, editor.BackColor);
                Assert.AreEqual(Theme.Text, editor.ForeColor);
                Assert.AreEqual("Consolas", editor.Font.Name);
                Assert.AreEqual(Theme.Background, Field<Label>(form, "_empty").BackColor);
                Assert.AreEqual(Theme.TextSecondary, Field<Label>(form, "_empty").ForeColor);
                Assert.AreEqual(Theme.TextSecondary, Field<Label>(form, "_breadcrumb").ForeColor);
                Assert.AreEqual(Theme.TextSecondary, Field<Label>(form, "_status").ForeColor);
                Assert.AreEqual(Theme.Divider, Field<SplitContainer>(form, "_split").BackColor);
                var preview = Field<NotebookPreview>(form, "_preview");
                Assert.AreEqual(Theme.Background, preview.BackColor);
                Assert.AreEqual(Theme.Background, Field<WebView2>(preview, "_web").DefaultBackgroundColor);
                var message = Field<Label>(preview, "_message");
                Assert.AreEqual(Theme.Background, message.BackColor);
                Assert.AreEqual(Theme.TextSecondary, message.ForeColor);
                typeof(NotebookPreview).GetMethod("ShowError", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(preview, new object[] { "测试错误 / Test error" });
                Assert.AreEqual(Theme.Background, message.BackColor);
                Assert.AreEqual(Theme.Danger, message.ForeColor);
                Assert.AreEqual(Theme.Danger, Field<Label>(form, "_status").ForeColor);
            });
        }

        [TestMethod]
        public void Notebook_SidebarActionsFitNarrowAndWideLayouts()
        {
            RunUi((form, store) =>
            {
                using (var host = new Panel { Height = Dpi.S(500) })
                {
                    var sidebar = form.Workspace.DetachSidebar();
                    host.Controls.Add(sidebar);
                    foreach (int width in new[] { 220, 240, 320, 440 })
                    {
                        host.Width = Dpi.S(width);
                        host.PerformLayout();
                        sidebar.PerformLayout();
                        int actions = 0;
                        foreach (Control child in sidebar.Controls)
                        {
                            if (!(child is TableLayoutPanel table)) continue;
                            table.PerformLayout();
                            foreach (Control control in table.Controls)
                            {
                                Assert.IsInstanceOfType(control, typeof(SidebarButton));
                                Assert.IsTrue(control.Width > Dpi.S(80));
                                Assert.IsTrue(control.Height >= Dpi.S(30));
                                Assert.IsTrue(table.ClientRectangle.Contains(control.Bounds));
                                Assert.AreEqual(Theme.FontName, control.Font.Name);
                                actions++;
                            }
                        }
                        Assert.AreEqual(2, actions);
                        var search = Field<TextBox>(form, "_search");
                        Assert.AreEqual(BorderStyle.None, search.BorderStyle);
                        Assert.IsTrue(search.Parent.ClientRectangle.Contains(search.Bounds));
                    }
                }
            });
        }

        [TestMethod]
        public void Notebook_TreeMenu_IsGroupedWithIconsShortcutsAndEntryRules()
        {
            RunUi((form, store) =>
            {
                var tree = Field<TreeView>(form, "_tree");
                var menu = (GroupedContextMenuStrip)tree.ContextMenuStrip;
                var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
                CollectionAssert.AreEqual(new[] { "新建子页面 / New subpage", "新建同级页面 / New sibling page", "重命名 / Rename", "移入废纸篓 / Move to trash" },
                    items.Select(i => i.Text).ToArray());
                Assert.AreEqual(2, menu.Items.OfType<MenuGroupHeader>().Count());
                Assert.IsTrue(items.All(i => i.Image != null));
                CollectionAssert.AreEqual(new[] { "Ctrl+N", null, "F2", "Del" }, items.Select(i => i.ShortcutKeyDisplayString).ToArray());
                Assert.AreEqual(Theme.Danger, items[3].ForeColor);
                var opening = typeof(ToolStripDropDown).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic);
                tree.SelectedNode = tree.Nodes[0];
                opening.Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
                Assert.IsFalse(items[2].Enabled || items[3].Enabled);
                Assert.IsTrue(items[0].Enabled && items[1].Enabled);
                tree.SelectedNode = tree.Nodes[0].Nodes[0];
                opening.Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
                Assert.IsTrue(items[2].Enabled && items[3].Enabled);
                foreach (Control child in form.Workspace.Sidebar.Controls)
                    foreach (Control control in child.Controls)
                        Assert.IsFalse(control.Text.StartsWith("+ "));
            });
        }

        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }

        [TestMethod]
        public void Notebook_DragPlan_ShowsSeparatorAtTargetAndReordersTree()
        {
            RunUi((form, store) =>
            {
                string other = store.CreatePage("", "Other");
                var refresh = typeof(NotebookWorkspace).GetMethod("RefreshTree", BindingFlags.NonPublic | BindingFlags.Instance);
                refresh.Invoke(form.Workspace, new object[] { null, false });
                var tree = Field<TreeView>(form, "_tree");
                var root = tree.Nodes[0];
                TreeNode Find(string title) => root.Nodes.Cast<TreeNode>().Single(n => n.Text == title);
                string Id(TreeNode n) => ((NotebookEntry)n.Tag).Path;
                var project = Find("Project");
                var otherNode = Find("Other");
                project.Collapse();
                Assert.AreSame(otherNode, root.Nodes[0]);

                var before = NotebookWorkspace.PlanDrop(otherNode, project, project.Bounds.Top + 1, 0);
                Assert.AreEqual("", before.Parent);
                Assert.AreEqual(Id(project), before.Before);
                Assert.AreEqual(project.Bounds.Top, before.Y, "分割线在目标行上沿 / Separator on the target row's top edge");
                var after = NotebookWorkspace.PlanDrop(otherNode, project, project.Bounds.Bottom - 1, 0);
                Assert.IsNull(after.Before);
                Assert.AreEqual(project.Bounds.Bottom, after.Y, "分割线在目标行下沿 / Separator on the target row's bottom edge");
                project.Expand();
                var inside = NotebookWorkspace.PlanDrop(otherNode, project, project.Bounds.Bottom - 1, 0);
                Assert.AreEqual(Id(project), inside.Parent, "展开的父页下半行放入其子页最前 / Lower half of an expanded parent drops before its first child");
                Assert.AreEqual(Id(project.Nodes[0]), inside.Before);
                Assert.IsTrue(inside.X > before.X, "子级分割线更靠右 / Child-level separator is indented");
                Assert.IsNull(NotebookWorkspace.PlanDrop(project, project.Nodes[0], project.Nodes[0].Bounds.Top, 0), "不能拖到自己的子页面 / Cannot drop into own subpage");
                Assert.IsNull(NotebookWorkspace.PlanDrop(project, project, project.Bounds.Top, 0));
                var first = NotebookWorkspace.PlanDrop(project, root, root.Bounds.Bottom - 1, 0);
                Assert.AreEqual(Id(otherNode), first.Before, "拖到根节点放在最前 / Dropping on the root goes first");

                Assert.IsTrue(store.Move(other, after.Parent, after.Before));
                refresh.Invoke(form.Workspace, new object[] { null, false });
                CollectionAssert.AreEqual(new[] { "Project", "Other" }, tree.Nodes[0].Nodes.Cast<TreeNode>().Select(n => n.Text).ToArray());
            });
        }

        [TestMethod]
        public void Notebook_TaskJournal_IsReadOnlyAndCannotBeDragged()
        {
            RunUi((form, store) =>
            {
                string day = store.CreatePage("", "2026.9.28 任务记录");
                string detail = store.CreatePage(day, "任务 1 - 示例", "# 任务 #1 · 示例\n");
                var refresh = typeof(NotebookWorkspace).GetMethod("RefreshTree", BindingFlags.NonPublic | BindingFlags.Instance);
                refresh.Invoke(form.Workspace, new object[] { detail, false });
                var tree = Field<TreeView>(form, "_tree");
                var root = tree.Nodes[0];
                var dayNode = root.Nodes.Cast<TreeNode>().Single(n => n.Text == "2026.9.28 任务记录");
                var project = root.Nodes.Cast<TreeNode>().Single(n => n.Text == "Project");
                Assert.AreSame(dayNode, root.Nodes[0], "任务记录排在普通笔记之前 / Journals come before ordinary notes");
                Assert.IsTrue(NotebookWorkspace.IsJournalNode(dayNode.Nodes[0]));
                Assert.IsFalse(NotebookWorkspace.IsJournalNode(project));
                Assert.IsNull(NotebookWorkspace.PlanDrop(dayNode, project, project.Bounds.Top + 1, 0), "任务记录不能拖动 / Journals cannot be dragged");
                Assert.IsNull(NotebookWorkspace.PlanDrop(project, dayNode, dayNode.Bounds.Bottom - 1, 0), "不能放入任务记录 / Cannot drop into journals");
                Assert.IsTrue(Field<TextBox>(form, "_editor").ReadOnly, "详情页只读 / Detail page is read-only");
                Assert.IsTrue(Field<bool>(form, "_readOnly"));
                StringAssert.Contains((string)typeof(NotebookWorkspace).GetMethod("InsertIntoCurrent", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(form.Workspace, new object[] { "x" }), "不可编辑");
                Assert.AreEqual("# 任务 #1 · 示例\n", store.Read(detail).Text);
                refresh.Invoke(form.Workspace, new object[] { ((NotebookEntry)project.Tag).Path, false });
                Assert.IsFalse(Field<TextBox>(form, "_editor").ReadOnly, "普通笔记可编辑 / Ordinary notes stay editable");
            });
        }

        private static T Field<T>(object value, string name)
        {
            if (value is NotebookForm form) value = form.Workspace;
            return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
        }

        private static void RunUi(Action<NotebookForm, NotebookStore> action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "vsm-notebook-ui-" + Guid.NewGuid().ToString("N"));
                ThreadExceptionEventHandler onError = (s, e) => { if (failure == null) failure = e.Exception; };
                Application.ThreadException += onError;
                try
                {
                    var store = new NotebookStore(root);
                    string project = store.CreatePage("", "Project", "original");
                    store.CreatePage(project, "Ideas", "original");
                    using (var form = new NotebookForm(store))
                    {
                        // 不启动浏览器或消息循环，测试原生目录与编辑状态。 / Test native tree/editor state without starting a browser or message loop.
                        form.Workspace.Initialize();
                        var handle = Field<TreeView>(form, "_tree").Handle;
                        try { action(form, store); }
                        catch (Exception ex) { failure = ex; }
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    Application.ThreadException -= onError;
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Notebook UI test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
