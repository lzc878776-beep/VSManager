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
