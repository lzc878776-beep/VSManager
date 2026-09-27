using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class ManualChatUiTests
    {
        private TempDataFolder _data;
        [TestInitialize] public void Initialize() => _data = new TempDataFolder();
        [TestCleanup] public void Cleanup() => _data.Dispose();
        private sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        [DataTestMethod]
        [DataRow(QueueStatus.Waiting, "dispatch")]
        [DataRow(QueueStatus.WaitingVs, "dispatch")]
        [DataRow(QueueStatus.Failed, "retry")]
        [DataRow(QueueStatus.Cancelled, "retry")]
        [DataRow(QueueStatus.Running, null)]
        [DataRow(QueueStatus.Sending, null)]
        [DataRow(QueueStatus.Done, null)]
        public void TaskMenu_ExplicitRecoveryAvailableWithoutGlobalStart(string status, string expected)
        {
            RunSta(() =>
            {
                using (var panel = new TaskPanel { CanRunTask = _ => false })
                {
                    var task = new QueuedTask { Id = 1, Status = status };
                    var list = (ListBox)typeof(TaskPanel).GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                    list.Items.Add(task); list.SelectedItem = task;
                    var menu = list.ContextMenuStrip;
                    typeof(ToolStripDropDown).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
                    var dispatch = menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "重新检查并推送 / Recheck and send");
                    var retry = menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "手动重新排队 / Requeue manually");
                    Assert.AreEqual(expected == "dispatch", dispatch.Enabled);
                    Assert.AreEqual(expected == "retry", retry.Enabled);
                    Assert.IsFalse(panel.IsTaskEligible(task));
                    string action = null;
                    panel.ActionRequested += (selected, value) => { Assert.AreSame(task, selected); action = value; };
                    if (expected != null) (expected == "dispatch" ? dispatch : retry).PerformClick();
                    Assert.AreEqual(expected, action);
                }
            });
        }

        [TestMethod]
        public void SettingsToggleAndNumericTimeout_UseExistingChangedEvent()
        {
            RunSta(() =>
            {
                var settings = new AppSettings();
                using (var form = new SettingsForm(settings, null, new SettingsForm.Actions()))
                {
                    int changes = 0; form.Changed += () => changes++;
                    var controls = Descendants(form).ToList();
                    var toggle = controls.OfType<ToggleSwitch>().Single(t => t.Text.Contains("Wait for manual chat"));
                    Assert.IsTrue(toggle.Checked); toggle.Checked = false; Assert.IsFalse(settings.WaitForManualChat);
                    var timeout = controls.OfType<NumericUpDown>().Single(t => t.Name == nameof(AppSettings.ManualChatWaitTimeoutSeconds));
                    Assert.AreEqual(300m, timeout.Value); Assert.AreEqual(10m, timeout.Minimum); Assert.AreEqual(86400m, timeout.Maximum);
                    timeout.Value = 600; Assert.AreEqual(600, settings.ManualChatWaitTimeoutSeconds);
                    Assert.IsTrue(changes >= 2);
                }
            });
        }

        [DataTestMethod]
        [DataRow("草稿 / Draft", false, ManualChatObservation.Draft)]
        [DataRow("", false, ManualChatObservation.Idle)]
        [DataRow(" ", false, ManualChatObservation.Idle)]
        [DataRow("\u200B\uFEFF\u2060", false, ManualChatObservation.Idle)]
        [DataRow("\u00A0\u3000", false, ManualChatObservation.Idle)]
        [DataRow("，", false, ManualChatObservation.Draft)]
        [DataRow("\u0301", false, ManualChatObservation.Draft)]
        [DataRow("", true, ManualChatObservation.Generating)]
        public void SyntheticUi_InputAndStopAreReadOnly(string draft, bool stopVisible, ManualChatObservation expected)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                using (var form = new QuietForm { Text = "隔离探测测试 / Isolated probe test", Width = 320, Height = 160, ShowInTaskbar = false })
                using (var input = new TextBox { Dock = DockStyle.Top, Text = draft })
                using (var stop = new Button { Dock = DockStyle.Bottom, Text = "停止 / Stop", Visible = stopVisible })
                {
                    form.Controls.Add(input); form.Controls.Add(stop);
                    form.Shown += async (_, __) =>
                    {
                        try
                        {
                            var target = new VsInstance { Pid = Process.GetCurrentProcess().Id, MainHwnd = form.Handle };
                            IntPtr inputHandle = input.Handle, stopHandle = stop.Handle;
                            await Task.Run(() =>
                            {
                                var pane = AutomationElement.FromHandle(target.MainHwnd);
                                var edit = AutomationElement.FromHandle(inputHandle);
                                var button = AutomationElement.FromHandle(stopHandle);
                                var settings = new AppSettings { BusyButtonIds = button.Current.AutomationId };
                                var chat = new CopilotChat(() => settings);
                                var observe = typeof(CopilotChat).GetMethod("ObserveManualInput", BindingFlags.Instance | BindingFlags.NonPublic);
                                Assert.AreEqual(expected, (ManualChatObservation)observe.Invoke(chat, new object[] { target, pane, edit }));
                                var guard = typeof(CopilotChat).GetMethod("GuardQueueInput", BindingFlags.Instance | BindingFlags.NonPublic);
                                var callback = typeof(CopilotChat).GetField("_queueGuard", BindingFlags.Static | BindingFlags.NonPublic);
                                var touched = typeof(CopilotChat).GetField("_queueTouched", BindingFlags.Static | BindingFlags.NonPublic);
                                callback.SetValue(null, (Func<bool>)(() => true));
                                touched.SetValue(null, false);
                                try
                                {
                                    var result = (string)guard.Invoke(chat, new object[] { target, pane, edit, false });
                                    Assert.AreEqual(expected != ManualChatObservation.Idle, ManualChatProtection.IsWait(result));
                                    settings.WaitForManualChat = false;
                                    result = (string)guard.Invoke(chat, new object[] { target, pane, edit, false });
                                    Assert.AreEqual(expected != ManualChatObservation.Idle, ManualChatProtection.IsWait(result));
                                    callback.SetValue(null, (Func<bool>)(() => false));
                                    Assert.IsTrue(ManualChatProtection.IsWait((string)guard.Invoke(chat, new object[] { target, pane, edit, false })));
                                    Assert.IsFalse((bool)touched.GetValue(null));
                                    target.Pid = int.MaxValue;
                                    Assert.AreEqual(ManualChatObservation.Unknown, observe.Invoke(chat, new object[] { target, pane, edit }));
                                }
                                finally { callback.SetValue(null, null); touched.SetValue(null, false); }
                            });
                            Assert.AreEqual(draft, input.Text);
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "隔离 UIA 探测超时 / Isolated UIA probe timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [DataTestMethod]
        [DataRow("text-no-list", true)]
        [DataRow("text-empty-list", true)]
        [DataRow("text-foreign", false)]
        [DataRow("images-exact", true)]
        [DataRow("images-extra", false)]
        [DataRow("images-missing", false)]
        [DataRow("images-replaced", false)]
        [DataRow("images-unreadable", false)]
        [DataRow("text-unreadable", false)]
        public void SyntheticUi_QueueSubmitRequiresExactOwnedAttachments(string scenario, bool allowed)
        {
            RunAttachmentUi((window, input, list, target, pane, edit) =>
            {
                var chat = new CopilotChat(() => new AppSettings());
                var read = typeof(CopilotChat).GetMethod("TryAttachmentIds", BindingFlags.Static | BindingFlags.NonPublic);
                var guard = typeof(CopilotChat).GetMethod("GuardQueueSubmit", BindingFlags.Instance | BindingFlags.NonPublic);
                var callback = typeof(CopilotChat).GetField("_queueGuard", BindingFlags.Static | BindingFlags.NonPublic);
                var touched = typeof(CopilotChat).GetField("_queueTouched", BindingFlags.Static | BindingFlags.NonPublic);
                HashSet<string> Read(AutomationElement element)
                {
                    object[] args = { element, null };
                    Assert.IsTrue((bool)read.Invoke(null, args));
                    return (HashSet<string>)args[1];
                }
                var owned = new HashSet<string>();
                if (scenario.StartsWith("images-", StringComparison.Ordinal))
                {
                    window.Dispatcher.Invoke(() => { list.Items.Add("own-1"); list.Items.Add("own-2"); window.UpdateLayout(); });
                    owned = Read(pane);
                    Assert.AreEqual(2, owned.Count);
                }
                window.Dispatcher.Invoke(() =>
                {
                    if (scenario == "text-no-list") ((System.Windows.Controls.Panel)list.Parent).Children.Remove(list);
                    if (scenario == "text-foreign" || scenario == "images-extra") list.Items.Add("foreign");
                    if (scenario == "images-missing" || scenario == "images-replaced") list.Items.RemoveAt(0);
                    if (scenario == "images-replaced") list.Items.Add("replacement");
                    window.UpdateLayout();
                });
                if (scenario.EndsWith("unreadable", StringComparison.Ordinal))
                {
                    var request = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
                    pane = pane.GetUpdatedCache(request);
                    object[] args = { pane, null };
                    Assert.IsFalse((bool)read.Invoke(null, args));
                    Assert.IsNull(args[1]);
                }
                else
                {
                    int expectedCount = scenario == "text-foreign" ? 1 : scenario == "images-extra" ? 3 :
                        scenario == "images-missing" ? 1 : scenario.StartsWith("images-", StringComparison.Ordinal) ? 2 : 0;
                    Assert.AreEqual(expectedCount, Read(pane).Count);
                }
                callback.SetValue(null, (Func<bool>)(() => true));
                touched.SetValue(null, true);
                try
                {
                    string result = (string)guard.Invoke(chat, new object[] { target, pane, edit, "queued prompt", owned });
                    if (allowed) Assert.IsNull(result);
                    else
                    {
                        StringAssert.StartsWith(result, ManualChatProtection.UncertainPrefix);
                        Assert.IsFalse(SendRetryPolicy.IsBlocked(result));
                        Assert.AreEqual(SendDecision.Fail, SendRetryPolicy.Decide(1, result));
                        var task = new QueuedTask { Status = QueueStatus.Sending, Attempts = 1 };
                        Assert.AreEqual(SendDecision.Fail, TaskStateMachine.ApplySendResult(task, result, DateTime.Now));
                        Assert.AreEqual(QueueStatus.Failed, task.Status);
                        Assert.AreEqual(1, task.Attempts);
                        Assert.IsTrue((bool)touched.GetValue(null));
                        var inputGuard = typeof(CopilotChat).GetMethod("GuardQueueInput", BindingFlags.Instance | BindingFlags.NonPublic);
                        StringAssert.StartsWith((string)inputGuard.Invoke(chat, new object[] { target, pane, edit, true }),
                            ManualChatProtection.UncertainPrefix);
                    }
                    Assert.AreEqual("queued prompt", window.Dispatcher.Invoke(() => input.Text));
                    callback.SetValue(null, null);
                    Assert.IsNull(guard.Invoke(chat, new object[] { target, pane, edit, "queued prompt", owned }));
                }
                finally { callback.SetValue(null, null); touched.SetValue(null, false); }
            });
        }

        [TestMethod]
        public void SyntheticUi_AddReferenceButtonAndActiveDocumentAreNotDrafts()
        {
            RunAttachmentUi((window, input, list, target, pane, edit) =>
            {
                var read = typeof(CopilotChat).GetMethod("TryAttachmentIds", BindingFlags.Static | BindingFlags.NonPublic);
                int Count()
                {
                    object[] args = { pane, null };
                    Assert.IsTrue((bool)read.Invoke(null, args));
                    return ((HashSet<string>)args[1]).Count;
                }
                window.Dispatcher.Invoke(() =>
                {
                    input.Text = "";
                    var add = new System.Windows.Controls.Button { Content = "添加引用" };
                    AutomationProperties.SetAutomationId(add, "PART_AttachmentsButton");
                    var host = new System.Windows.Controls.StackPanel();
                    host.Children.Add(add);
                    list.Items.Add(host);
                    list.Items.Add("活动文档");
                    list.Items.Add("Active document");
                    window.UpdateLayout();
                });
                Assert.AreEqual(0, Count());
                window.Dispatcher.Invoke(() => { list.Items.Add("截图.png"); window.UpdateLayout(); });
                Assert.AreEqual(1, Count());
            });
        }

        private static void RunAttachmentUi(Action<System.Windows.Window, System.Windows.Controls.TextBox,
            System.Windows.Controls.ListBox, VsInstance, AutomationElement, AutomationElement> action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var window = new System.Windows.Window
                {
                    Title = "隔离附件测试 / Isolated attachment test", Width = 320, Height = 200,
                    ShowInTaskbar = false, ShowActivated = false
                };
                var input = new System.Windows.Controls.TextBox { Text = "queued prompt" };
                var list = new System.Windows.Controls.ListBox();
                AutomationProperties.SetAutomationId(input, "QueueInput");
                AutomationProperties.SetAutomationId(list, "PART_AttachmentsList");
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(input); panel.Children.Add(list); window.Content = panel;
                window.ContentRendered += async (_, __) =>
                {
                    try
                    {
                        var target = new VsInstance { Pid = Process.GetCurrentProcess().Id,
                            MainHwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle };
                        await Task.Run(() =>
                        {
                            var pane = AutomationElement.FromHandle(target.MainHwnd);
                            var edit = pane.FindFirst(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.AutomationIdProperty, "QueueInput"));
                            Assert.IsNotNull(edit);
                            action(window, input, list, target, pane, edit);
                        });
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { window.Close(); window.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
                };
                window.Show();
                System.Windows.Threading.Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "隔离附件探测超时 / Isolated attachment probe timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)));
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
