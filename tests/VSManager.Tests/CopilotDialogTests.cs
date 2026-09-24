using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class CopilotDialogTests
    {
        [DataTestMethod]
        [DataRow("不一致的行尾", "以下文件中的行尾不一致。是否将行尾标准化?", "Windows (CR LF)", "是(Y)", "否(N)", true)]
        [DataRow("Inconsistent Line Endings", "The line endings are not consistent. Do you want to normalize them?", "Windows (CR LF)", "&Yes", "&No", true)]
        [DataRow("Save Changes", "line endings normalize", "Windows (CR LF)", "Yes", "No", false)]
        [DataRow("Inconsistent Line Endings", "Delete all files?", "Windows (CR LF)", "Yes", "No", false)]
        [DataRow("Inconsistent Line Endings", "line endings normalize", "Unix (LF)", "Yes", "No", false)]
        [DataRow("Inconsistent Line Endings", "line endings normalize", null, "Yes", "No", false)]
        [DataRow("Inconsistent Line Endings", "line endings normalize", "Windows (CR LF)", "Yes to All", "No", false)]
        [DataRow("Inconsistent Line Endings", "line endings normalize", "Windows (CR LF)", "Yes", "Cancel", false)]
        [DataRow(null, null, null, null, null, false)]
        public void AutoNormalize_OnlyAcceptsKnownDialog(string title, string body, string format, string yes, string no, bool expected)
        {
            Assert.AreEqual(expected, CopilotChat.IsLineEndingDialog(title, body, format, yes, no));
        }

        [DataTestMethod]
        [DataRow(true, "The operation completed successfully.", 1)]
        [DataRow(false, "The operation completed successfully.", 0)]
        [DataRow(true, "Overwrite the existing file?", 0)]
        public void SafeNotice_InvokesOnlyAllowedButtonOnce(bool enabled, string body, int expectedClicks)
        {
            using (var data = new TempDataFolder())
            {
                Exception failure = null;
                var ui = new Thread(() =>
                {
                    try
                    {
                        using (var owner = new Form())
                        using (var dialog = new Form { Text = "Microsoft Visual Studio", Width = 420, Height = 150, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false })
                        using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
                        using (var finished = new ManualResetEvent(false))
                        {
                            int clicks = 0;
                            var ok = new Button { Text = "OK", Dock = DockStyle.Bottom };
                            ok.Click += (s, e) => clicks++;
                            dialog.Controls.Add(new Label { Text = body, Dock = DockStyle.Top, Height = 50 });
                            dialog.Controls.Add(ok);
                            var vs = new VsInstance { Pid = Process.GetCurrentProcess().Id, MainHwnd = owner.Handle };
                            var chat = new CopilotChat(() => new AppSettings { SendAutoDismissNotices = enabled });
                            string result = null;
                            Exception sendError = null;
                            var sender = new Thread(() =>
                            {
                                try
                                {
                                    result = chat.Send(vs, "Original task", IntPtr.Zero, true);
                                    result = chat.Send(vs, "Original task", IntPtr.Zero, true);
                                }
                                catch (Exception ex) { sendError = ex; }
                                finally { finished.Set(); }
                            }) { IsBackground = true };
                            sender.SetApartmentState(ApartmentState.STA);
                            var elapsed = Stopwatch.StartNew();
                            timer.Tick += (s, e) =>
                            {
                                if (finished.WaitOne(0) || elapsed.Elapsed > TimeSpan.FromSeconds(10)) dialog.Close();
                            };
                            dialog.Shown += (s, e) => { timer.Start(); sender.Start(); };
                            dialog.ShowDialog(owner);
                            timer.Stop();
                            Assert.IsTrue(sender.Join(TimeSpan.FromSeconds(5)), "Dialog handling did not finish");
                            Assert.IsNull(sendError, sendError?.ToString());
                            Assert.IsTrue(SendRetryPolicy.IsBlocked(result), result);
                            Assert.AreEqual(expectedClicks, clicks);
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                }) { IsBackground = true };
                ui.SetApartmentState(ApartmentState.STA);
                ui.Start();
                Assert.IsTrue(ui.Join(TimeSpan.FromSeconds(20)), "Dialog test did not finish");
                Assert.IsNull(failure, failure?.ToString());
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Send_DisabledOwner_ReturnsBlockedBeforeAutomation(bool background)
        {
            using (var data = new TempDataFolder())
            {
                Exception failure = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        using (var owner = new Form { Text = "Disabled VS owner" })
                        {
                            var vs = new VsInstance
                            {
                                Pid = Process.GetCurrentProcess().Id,
                                MainHwnd = owner.Handle
                            };
                            owner.Enabled = false;
                            Assert.IsFalse(Native.IsWindowEnabled(vs.MainHwnd));
                            var chat = new CopilotChat(() => new AppSettings());
                            string result = chat.Send(vs, "Keep the original task", IntPtr.Zero, background);
                            Assert.IsTrue(SendRetryPolicy.IsBlocked(result), result);
                            Assert.IsFalse(SendRetryPolicy.IsDelivered(result));
                            Assert.IsFalse(owner.Enabled, "Do not dismiss or bypass a modal dialog");
                            Assert.AreEqual("Disabled VS owner", owner.Text);
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                }) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Blocked sends must return promptly");
                Assert.IsNull(failure, failure?.ToString());
            }
        }
    }
}
