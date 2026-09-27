using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class WorkspaceNavigationTests
    {
        [TestMethod]
        public void Navigation_SelectsOneView_AndBlocksLeavingUnsavedNotebook()
        {
            OnUi(() =>
            {
                using (var host = new Panel())
                {
                    var vs = new Panel();
                    var ai = new Panel();
                    var notebook = new Panel();
                    host.Controls.AddRange(new Control[] { vs, ai, notebook });
                    bool canSave = true;
                    int saves = 0;
                    var navigation = new WorkspaceNavigation(vs, ai, notebook, () => { saves++; return canSave; });
                    Assert.IsTrue(vs.Visible);
                    Assert.IsFalse(ai.Visible || notebook.Visible);
                    navigation.Select(WorkspacePage.Notebook);
                    Assert.IsTrue(notebook.Visible);
                    canSave = false;
                    Assert.IsFalse(navigation.Select(WorkspacePage.Agent));
                    Assert.AreEqual(WorkspacePage.Notebook, navigation.Current);
                    Assert.IsTrue(notebook.Visible);
                    Assert.IsFalse(vs.Visible || ai.Visible);
                    Assert.IsTrue(navigation.Select(WorkspacePage.Notebook));
                    Assert.AreEqual(1, saves);
                    canSave = true;
                    Assert.IsTrue(navigation.Select(WorkspacePage.VisualStudio));
                    Assert.AreEqual(2, saves);
                    Assert.IsTrue(vs.Visible);
                    navigation.Select(WorkspacePage.Agent);
                    Assert.AreEqual(2, saves);
                    Assert.IsTrue(ai.Visible);
                }
            });
        }

        [TestMethod]
        public void Sidebar_SectionsCollapseIndependently_WithoutHidingAiOrLosingNodes()
        {
            OnUi(() =>
            {
                var vs = new Panel();
                var ai = new Button { Height = Dpi.S(84) };
                var notion = new TreeView();
                notion.Nodes.Add("Project").Nodes.Add("Ideas");
                using (var sidebar = new WorkspaceSidebar(vs, ai, notion, new Label(), new Button()) { Size = new Size(Dpi.S(300), Dpi.S(800)) })
                {
                    sidebar.PerformLayout();
                    Assert.IsTrue(vs.Visible && ai.Visible && notion.Visible);
                    Assert.IsTrue(ai.Top < vs.Top && vs.Top < notion.Top);
                    sidebar.SetVsCollapsed(true);
                    Assert.IsFalse(vs.Visible);
                    Assert.IsTrue(ai.Visible && notion.Visible);
                    sidebar.SetNotionCollapsed(true);
                    Assert.IsFalse(notion.Visible);
                    Assert.IsTrue(ai.Visible);
                    sidebar.Size = new Size(Dpi.S(240), Dpi.S(350));
                    sidebar.SetVsCollapsed(false);
                    sidebar.SetNotionCollapsed(false);
                    sidebar.PerformLayout();
                    Assert.IsTrue(vs.Visible && notion.Visible);
                    Assert.IsTrue(vs.Height > 0 && notion.Height > 0);
                    Assert.AreEqual("Ideas", notion.Nodes[0].Nodes[0].Text);
                }
            });
        }

        [TestMethod]
        public void Sidebar_HeadersShareLayout_AndPreserveActions()
        {
            OnUi(() =>
            {
                var count = new Label { Text = "12" };
                var refresh = new Button();
                using (var sidebar = new WorkspaceSidebar(new Panel(), new AgentCard(), new Panel(), count, refresh))
                {
                    var rows = Field<TableLayoutPanel>(sidebar, "_rows");
                    var vsToggle = Field<SidebarButton>(sidebar, "_vsToggle");
                    var notesToggle = Field<SidebarButton>(sidebar, "_notionToggle");
                    var vsTitle = (SidebarButton)vsToggle.Parent.Controls[0];
                    var notesTitle = (SidebarButton)notesToggle.Parent.Controls[0];
                    foreach (int width in new[] { 240, 320, 440 })
                    {
                        sidebar.Size = new Size(Dpi.S(width), Dpi.S(800));
                        sidebar.PerformLayout();
                        rows.PerformLayout();
                        vsToggle.Parent.PerformLayout();
                        notesToggle.Parent.PerformLayout();
                        Assert.AreEqual(vsToggle.Bounds, notesToggle.Bounds);
                        Assert.AreEqual(vsTitle.Left, notesTitle.Left);
                        Assert.AreEqual(vsTitle.Height, notesTitle.Height);
                        Assert.AreEqual(ContentAlignment.MiddleLeft, vsTitle.TextAlign);
                        Assert.AreEqual(vsTitle.Font, notesTitle.Font);
                        Assert.IsTrue(vsTitle.Width >= Dpi.S(110));
                        Assert.IsTrue(vsTitle.Right <= count.Left);
                        Assert.IsTrue(count.Right <= refresh.Left);
                    }
                    int opened = 0;
                    sidebar.NotebookRequested += () => opened++;
                    vsTitle.PerformClick();
                    Assert.IsTrue(sidebar.VsCollapsed);
                    vsToggle.PerformClick();
                    Assert.IsFalse(sidebar.VsCollapsed);
                    notesToggle.PerformClick();
                    Assert.IsTrue(sidebar.NotionCollapsed);
                    notesTitle.PerformClick();
                    Assert.IsFalse(sidebar.NotionCollapsed);
                    Assert.AreEqual(1, opened);
                }
            });
        }

        [TestMethod]
        public void Sidebar_CollapsedNotes_StayBelowVsListInsteadOfBottom()
        {
            OnUi(() =>
            {
                var vs = new Panel();
                var ai = new Button { Height = Dpi.S(84) };
                var notes = new Panel();
                int preferred = Dpi.S(200);
                using (var sidebar = new WorkspaceSidebar(vs, ai, notes, new Label(), new Button(), () => preferred) { Size = new Size(Dpi.S(300), Dpi.S(900)) })
                {
                    var notesHeader = Field<SidebarButton>(sidebar, "_notionToggle").Parent.Parent;
                    sidebar.SetNotionCollapsed(true);
                    sidebar.PerformLayout();
                    Assert.AreEqual(preferred, vs.Height);
                    Assert.AreEqual(vs.Bottom, notesHeader.Top);
                    Assert.IsTrue(notesHeader.Bottom < sidebar.Height / 2);
                    sidebar.SetVsCollapsed(true);
                    sidebar.PerformLayout();
                    Assert.IsTrue(notesHeader.Bottom < ai.Bottom + Dpi.S(120));
                    sidebar.SetVsCollapsed(false);
                    sidebar.SetNotionCollapsed(false);
                    sidebar.PerformLayout();
                    Assert.AreEqual(preferred, vs.Height);
                    Assert.IsTrue(notes.Height > vs.Height);
                }
            });
        }

        [TestMethod]
        public void DetachedTree_RepeatedClickReturnsToSameNote_WithoutReloadingDraft()
        {
            OnUi(() =>
            {
                string root = Path.Combine(Path.GetTempPath(), "vsm-workspace-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var store = new NotebookStore(root);
                    string ideas = store.CreatePage("", "Ideas", "original");
                    using (var workspace = new NotebookWorkspace(store))
                    using (var sidebar = new Panel())
                    {
                        sidebar.Controls.Add(workspace.DetachSidebar());
                        workspace.Initialize();
                        var tree = Field<TreeView>(workspace, "_tree");
                        var handle = tree.Handle;
                        int activations = 0;
                        workspace.ContentRequested += () => activations++;
                        tree.SelectedNode = tree.Nodes[0].Nodes[0];
                        Assert.AreEqual(1, activations);
                        var editor = Field<TextBox>(workspace, "_editor");
                        editor.Text = "draft";
                        typeof(TreeView).GetMethod("OnNodeMouseClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tree,
                            new object[] { new TreeNodeMouseClickEventArgs(tree.SelectedNode, MouseButtons.Left, 1, 0, 0) });
                        Assert.AreEqual(2, activations);
                        Assert.AreEqual("draft", editor.Text);
                        Assert.AreEqual("original", store.Read(ideas).Text);
                        var other = new NotebookStore(root);
                        other.Save(other.Read(ideas), "external");
                        Assert.IsTrue(workspace.TrySave(), "冲突时草稿自动另存 / Conflicting drafts are saved as a copy");
                        Assert.AreEqual("draft", editor.Text);
                        Assert.AreEqual("external", store.Read(ideas).Text);
                        Assert.AreEqual("draft", store.Read(store.LoadTree().Single(e => e.Name.StartsWith("Ideas-draft-", StringComparison.Ordinal)).Path).Text);
                    }
                }
                finally { Directory.Delete(root, true); }
            });
        }

        private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);

        private static void OnUi(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Workspace UI test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
