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
                foreach (var button in Field<List<Button>>(form, "_noteActions"))
                {
                    Assert.IsFalse(button.Enabled);
                    Assert.AreEqual(Theme.TextMuted, button.ForeColor);
                }
                tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes[0];
                Assert.AreEqual(saved, editor.Text.Replace("\r\n", "\n"));
                foreach (int mode in new[] { 1, 2, 0, 2, 1 })
                {
                    typeof(NotebookWorkspace).GetMethod("SetMode", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form.Workspace, new object[] { mode });
                    var split = Field<SplitContainer>(form, "_split");
                    Assert.AreEqual(mode == 0, split.Panel1Collapsed);
                    Assert.AreEqual(mode == 1, split.Panel2Collapsed);
                    var buttons = Field<List<Button>>(form, "_modeActions");
                    for (int i = 0; i < buttons.Count; i++)
                    {
                        Assert.AreEqual(i == mode ? Theme.AccentLight : Theme.Background, buttons[i].BackColor);
                        Assert.AreEqual(i == mode ? Theme.AccentText : Theme.Text, buttons[i].ForeColor);
                        Assert.AreEqual(i == mode ? Theme.RowSelected : Theme.RowHover, buttons[i].FlatAppearance.MouseOverBackColor);
                        Assert.AreEqual(Theme.AccentPressed, buttons[i].FlatAppearance.MouseDownBackColor);
                    }
                }
            });
        }

        [TestMethod]
        public void Notebook_ConflictKeepsDraftAndSelection_UntilSavedAsCopy()
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
                Assert.IsFalse(form.TrySave());
                tree.SelectedNode = tree.Nodes[0];
                Assert.AreSame(note, tree.SelectedNode);
                Assert.AreEqual("local draft", editor.Text);
                Assert.AreEqual("external change", store.Read(path).Text);
                StringAssert.Contains(Field<Label>(form, "_status").Text, "Page changed");
                Assert.AreEqual(Theme.Danger, Field<Label>(form, "_status").ForeColor);
                string copy = store.CreatePage(((NotebookEntry)tree.Nodes[0].Nodes[0].Tag).Path, "Draft", editor.Text);
                Assert.AreEqual("local draft", store.Read(copy).Text);
                typeof(NotebookWorkspace).GetMethod("Display", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(form.Workspace, new object[] { store.Read(path) });
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
