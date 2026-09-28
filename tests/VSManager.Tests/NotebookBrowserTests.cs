using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VSManager.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class NotebookBrowserTests
    {
        [TestMethod]
        [TestCategory("WebView2")]
        public void Notebook_BrowserRendersMarkdown_AndTimerPersistsEdits()
        {
            try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (WebView2RuntimeNotFoundException) { Assert.Inconclusive("WebView2 Runtime is required for this UI integration test."); }
            Exception failure = null;
            var thread = new Thread(() =>
            {
                string root = Path.Combine(Path.GetTempPath(), "vsm-notebook-browser-" + Guid.NewGuid().ToString("N"));
                string previousFolder = TranscriptView.UserDataFolder;
                var environmentField = typeof(TranscriptView).GetField("_sharedEnvironment", BindingFlags.Static | BindingFlags.NonPublic);
                object previousEnvironment = environmentField.GetValue(null);
                Process browser = null;
                try
                {
                    TranscriptView.UserDataFolder = Path.Combine(root, "browser");
                    environmentField.SetValue(null, null);
                    var store = new NotebookStore(Path.Combine(root, "notes"));
                    string work = store.CreatePage("", "工作笔记");
                    string records = store.CreatePage(work, "项目记录");
                    string note = store.CreatePage(records, "项目修改记录",
                        "# 项目修改记录\r\n\r\n## 思路整理\r\n\r\n把思路、任务和决策保存在本地，随时回顾。\r\n\r\n" +
                        "- [x] 本地 Markdown 存储\r\n- [x] 笔记本树形目录\r\n- [ ] 整理下一步计划\r\n\r\n" +
                        "## 今日决策\r\n\r\n> 笔记属于你，AI 只读取你主动提供的资料。\r\n\r\n" +
                        "| 功能 | 状态 |\r\n| --- | --- |\r\n| 自动保存 | 已启用 |\r\n| 本地预览 | 已启用 |\r\n\r\n" +
                        "`inline code` [示例链接 / Example link](https://example.invalid)\r\n\r\n```text\r\ncode block\r\n```\r\n");
                    store.CreatePage(work, "待办任务");
                    using (var form = new Form { Size = new Size(Dpi.S(1200), Dpi.S(820)) })
                    using (var workspace = new NotebookWorkspace(store) { Dock = DockStyle.Fill, Visible = false })
                    {
                        var vs = new Panel { Dock = DockStyle.Fill };
                        var agent = new Panel { Dock = DockStyle.Fill };
                        var vsList = new Label { Text = "VS 1\r\nVS 2", ForeColor = Theme.Text, BackColor = Theme.Sidebar };
                        var aiCard = new Button { Text = "AI 总控助手 / AI", Height = Dpi.S(84),
                            BackColor = Theme.Surface, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
                        var sidebar = new WorkspaceSidebar(vsList, aiCard, workspace.DetachSidebar(), new Label { Text = "2" }, new Button { Text = "↻" })
                            { Dock = DockStyle.Left, Width = Dpi.S(300) };
                        form.Controls.Add(vs);
                        form.Controls.Add(agent);
                        form.Controls.Add(workspace);
                        form.Controls.Add(sidebar);
                        var navigation = new WorkspaceNavigation(vs, agent, workspace, workspace.TrySave);
                        workspace.ContentRequested += () => navigation.Select(WorkspacePage.Notebook);
                        workspace.SidebarRequested += () => sidebar.SetNotionCollapsed(false);
                        sidebar.NotebookRequested += () => navigation.Select(WorkspacePage.Notebook);
                        aiCard.Click += (s, e) => navigation.Select(WorkspacePage.Agent);
                        workspace.Initialize();
                        form.Shown += async (s, e) =>
                        {
                            try
                            {
                                var tree = Field<TreeView>(workspace, "_tree");
                                tree.ExpandAll();
                                tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes.Cast<TreeNode>().Single(n => n.Nodes.Count > 0).Nodes[0];
                                Assert.AreEqual(WorkspacePage.Notebook, navigation.Current);
                                Assert.IsTrue(workspace.Visible);
                                Assert.IsFalse(vs.Visible || agent.Visible);
                                var preview = Field<NotebookPreview>(workspace, "_preview");
                                var web = Field<WebView2>(preview, "_web");
                                DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                                while (!Field<bool>(preview, "_ready") && DateTime.UtcNow < deadline) await Task.Delay(50);
                                Assert.IsTrue(Field<bool>(preview, "_ready"), Field<Label>(preview, "_message").Text);
                                browser = Process.GetProcessById((int)web.CoreWebView2.BrowserProcessId);
                                Assert.AreEqual("3", await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('input[type=checkbox]').length"));
                                Assert.AreEqual("1", await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('table').length"));
                                Assert.AreEqual("true", await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('h1').textContent === '项目修改记录'"));
                                Assert.AreEqual("\"820px\"", await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('main')).maxWidth"));
                                Assert.AreEqual("\"dark\"", await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.documentElement).colorScheme"));
                                Assert.AreEqual("true", await web.CoreWebView2.ExecuteScriptAsync(
                                    "getComputedStyle(document.body).fontFamily.startsWith('\"' + '" + Theme.FontName + "' + '\"')"));
                                await AssertColor(web, "body", "backgroundColor", Theme.Background);
                                await AssertColor(web, "body", "color", Theme.Text);
                                await AssertColor(web, "a", "color", Theme.AccentText);
                                await AssertColor(web, "p code", "backgroundColor", Theme.AccentLight);
                                await AssertColor(web, "p code", "color", Theme.AccentText);
                                await AssertColor(web, "pre", "backgroundColor", Theme.SurfaceAlt);
                                await AssertColor(web, "pre code", "color", Theme.Text);
                                await AssertColor(web, "th", "backgroundColor", Theme.Surface);
                                await AssertColor(web, "td", "borderTopColor", Theme.Border);
                                await AssertColor(web, "blockquote", "borderLeftColor", Theme.Accent);
                                await AssertColor(web, "blockquote", "color", Theme.TextSecondary);
                                await AssertColor(web, "input[type=checkbox]", "accentColor", Theme.Accent);
                                await AssertColor(web, "body", "backgroundColor", Theme.RowSelected, "::selection");
                                Assert.AreEqual("\"" + CssColor(Theme.Border) + " " + CssColor(Theme.Background) + "\"",
                                    await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.body).scrollbarColor"));
                                Assert.AreEqual(Theme.Sidebar, workspace.Sidebar.BackColor);
                                Assert.AreEqual(Theme.Sidebar, sidebar.BackColor);
                                var editor = Field<TextBox>(workspace, "_editor");
                                editor.AppendText("\r\n自动保存验收\r\n");
                                await Task.Delay(1500);
                                StringAssert.Contains(store.Read(note).Text, "自动保存验收");
                                Assert.IsFalse(Field<bool>(workspace, "_dirty"));
                                editor.AppendText("切换前保存 / Save before switching\r\n");
                                aiCard.PerformClick();
                                Assert.AreEqual(WorkspacePage.Agent, navigation.Current);
                                Assert.IsTrue(agent.Visible);
                                StringAssert.Contains(store.Read(note).Text, "Save before switching");
                                Assert.IsTrue(navigation.Select(WorkspacePage.VisualStudio));
                                Assert.IsTrue(vs.Visible);
                                Assert.IsTrue(navigation.Select(WorkspacePage.Notebook));
                                sidebar.SetVsCollapsed(true);
                                Assert.IsFalse(vsList.Visible);
                                Assert.IsTrue(aiCard.Visible);
                                sidebar.SetNotionCollapsed(true);
                                Assert.IsFalse(workspace.Sidebar.Visible);
                                Assert.IsTrue(workspace.Visible);
                                sidebar.SetVsCollapsed(false);
                                Assert.IsTrue(workspace.HandleShortcut(Keys.Control | Keys.F));
                                Assert.IsFalse(sidebar.NotionCollapsed);
                                Assert.IsTrue(tree.Visible);
                                Assert.IsTrue(Field<Label>(workspace, "_breadcrumb").Visible);
                                Assert.IsTrue(Field<Label>(workspace, "_status").Visible);
                                Assert.AreEqual(editor.Text.Replace("\r\n", "\n"), store.Read(note).Text);
                                string screenshot = Environment.GetEnvironmentVariable("VSM_NOTEBOOK_SCREENSHOT");
                                if (!string.IsNullOrEmpty(screenshot))
                                {
                                    using (var browserImage = new MemoryStream())
                                    {
                                        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, browserImage);
                                        browserImage.Position = 0;
                                        using (var previewImage = Image.FromStream(browserImage))
                                        using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                                        {
                                            foreach (Control control in form.Controls)
                                                if (control.Visible) control.DrawToBitmap(image, control.Bounds);
                                            using (var graphics = Graphics.FromImage(image))
                                                graphics.DrawImage(previewImage, new Rectangle(form.PointToClient(web.PointToScreen(Point.Empty)), web.Size));
                                            image.Save(screenshot, ImageFormat.Png);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) { failure = ex; }
                            finally { form.Close(); }
                        };
                        Application.Run(form);
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    TranscriptView.UserDataFolder = previousFolder;
                    environmentField.SetValue(null, previousEnvironment);
                    if (browser != null) { browser.WaitForExit(10000); browser.Dispose(); }
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                    catch (IOException ex) { if (failure == null) failure = ex; }
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(45)), "Notebook browser test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static string CssColor(Color color) => $"rgb({color.R}, {color.G}, {color.B})";

        private static async Task AssertColor(WebView2 web, string selector, string property, Color expected, string pseudo = null)
        {
            string actual = await web.CoreWebView2.ExecuteScriptAsync(
                $"getComputedStyle(document.querySelector('{selector}'), {(pseudo == null ? "null" : "'" + pseudo + "'")}).{property}");
            Assert.AreEqual("\"" + CssColor(expected) + "\"", actual, selector + " " + property + " " + pseudo);
        }

        private static T Field<T>(object value, string name)
        {
            if (value is NotebookForm form) value = form.Workspace;
            return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
        }
    }
}
