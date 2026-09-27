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
        private sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation => true;
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
        [DataRow(" ", false, ManualChatObservation.Draft)]
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
