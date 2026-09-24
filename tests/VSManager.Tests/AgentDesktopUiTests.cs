using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AgentDesktopUiTests
    {
        [TestMethod]
        public void Approval_CancellationClosesDialogAndDenies()
        {
            RunUi(async owner =>
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                    Assert.IsFalse(AgentToolApproval.Show(owner, "Test script approval", "Read-only test script", null, cts.Token));
                await Task.CompletedTask;
            });
        }

        [TestMethod]
        public void Approval_DefaultActionIsCancel()
        {
            RunUi(async owner =>
            {
                Exception error = null;
                using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
                {
                    timer.Tick += (s, e) =>
                    {
                        foreach (Form dialog in Application.OpenForms)
                        {
                            if (dialog.Text != "Test default action") continue;
                            try
                            {
                                Assert.AreEqual(DialogResult.Cancel, dialog.AcceptButton.DialogResult);
                                Assert.AreSame(dialog.AcceptButton, dialog.CancelButton);
                                dialog.AcceptButton.PerformClick();
                            }
                            catch (Exception ex) { error = ex; dialog.Close(); }
                            break;
                        }
                    };
                    timer.Start();
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        Assert.IsFalse(AgentToolApproval.Show(owner, "Test default action", "Do not execute", null, cts.Token));
                    timer.Stop();
                }
                Assert.IsNull(error, error?.ToString());
                await Task.CompletedTask;
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Screenshot_CapturesOnlyTargetWindow_AsBoundedPng(bool maximized)
        {
            RunUi(async owner =>
            {
                owner.Text = "Synthetic screenshot test";
                owner.BackColor = Color.Navy;
                owner.Controls.Add(new Label { Text = "Synthetic UI - no project data", ForeColor = Color.White, Dock = DockStyle.Top, Height = 40 });
                if (maximized) owner.WindowState = FormWindowState.Maximized;
                Native.Activate(owner.Handle);
                await Task.Delay(250);
                var vs = new VsInstance { Pid = Process.GetCurrentProcess().Id, MainHwnd = owner.Handle };
                byte[] png = AgentScreenshot.Capture(vs);
                using (var stream = new MemoryStream(png))
                using (var image = Image.FromStream(stream))
                {
                    Assert.IsTrue(image.Width > 0 && image.Width <= 1600);
                    Assert.IsTrue(image.Height > 0 && image.Height <= 1600);
                }
                vs.Pid = int.MaxValue;
                Assert.ThrowsException<InvalidOperationException>(() => AgentScreenshot.Capture(vs));
            });
        }

        private static void RunUi(Func<Form, Task> action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                using (var owner = new Form { Width = 420, Height = 220, StartPosition = FormStartPosition.CenterScreen })
                {
                    owner.Shown += async (s, e) =>
                    {
                        try { await action(owner); }
                        catch (Exception ex) { failure = ex; }
                        finally { owner.Close(); }
                    };
                    Application.Run(owner);
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "UI tool did not finish");
            Assert.IsNull(failure, failure?.ToString());
        }
    }
}
