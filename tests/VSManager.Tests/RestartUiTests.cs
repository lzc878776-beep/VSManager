using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>无感重启的界面状态交接。/ UI state handoff for seamless restarts.</summary>
    [TestClass]
    public class RestartUiTests
    {
        private static RestartUiState State(DateTime created, int pid = 4242) => new RestartUiState
        {
            CreatedUtc = created, OldPid = pid, X = 100, Y = 80, Width = 1200, Height = 800,
            Maximized = true, Foreground = true, AgentPage = false, SelectedVsPid = 777, AgentDraft = "草稿 @[#1 A|x] draft", CoverEvent = RestartUi.CoverEventName("abc"), CoverReadyMilliseconds = 1234
        };

        [TestMethod]
        public void SaveTake_RoundTripsOnceAndRejectsStaleOrOwnState()
        {
            using (new TempDataFolder())
            {
                var now = DateTime.UtcNow;
                Assert.IsNull(RestartUi.Save(State(now)));
                var s = RestartUi.Take(now, 1);
                Assert.IsNotNull(s);
                Assert.AreEqual(new Rectangle(100, 80, 1200, 800), s.Bounds);
                Assert.IsTrue(s.Maximized && s.Foreground && !s.AgentPage && !s.Hidden);
                Assert.AreEqual(777, s.SelectedVsPid);
                Assert.AreEqual("草稿 @[#1 A|x] draft", s.AgentDraft);
                StringAssert.StartsWith(s.CoverEvent, @"Local\VSManager-restart-cover-");
                Assert.AreEqual(1234, s.CoverReadyMilliseconds);
                Assert.IsFalse(File.Exists(RestartUi.FilePath), "只消费一次 / Consumed once");
                Assert.IsNull(RestartUi.Take(now, 1));

                RestartUi.Save(State(now - RestartUi.MaxAge - TimeSpan.FromMinutes(1)));
                Assert.IsNull(RestartUi.Take(now, 1), "过期 / Expired");
                Assert.IsFalse(File.Exists(RestartUi.FilePath));

                RestartUi.Save(State(now, pid: 9));
                Assert.IsNull(RestartUi.Take(now, 9), "本进程写的状态不恢复 / Own state is not restored");

                File.WriteAllText(RestartUi.FilePath, "{broken");
                Assert.IsNull(RestartUi.Take(now, 1));
                Assert.IsFalse(File.Exists(RestartUi.FilePath));

                var big = State(now);
                big.AgentDraft = new string('x', RestartUi.MaxDraftChars + 50);
                RestartUi.Save(big);
                Assert.AreEqual(RestartUi.MaxDraftChars, RestartUi.Take(now, 1).AgentDraft.Length);
            }
        }

        [DataTestMethod]
        [DataRow(false, true, true, true)]
        [DataRow(true, true, true, true)]
        [DataRow(false, false, true, false)]
        [DataRow(true, false, true, true)]
        [DataRow(false, true, false, false)]
        [DataRow(true, true, false, false)]
        public void RestorePage_SelfTestUsesAgentAndOrdinaryRestartKeepsSavedPage(bool savedPage, bool selfTest, bool enabled, bool expected)
        {
            var state = State(DateTime.UtcNow);
            state.AgentPage = savedPage;
            state.SelfTest = selfTest;
            Assert.AreEqual(expected, RestartUi.WantsAgentPage(state, enabled));
            Assert.AreEqual(savedPage, state.AgentPage);
            Assert.IsFalse(RestartUi.WantsAgentPage(null, enabled));
        }

        [TestMethod]
        public void SelfTest_RoundTripsPagePolicyWithoutChangingOtherUiState()
        {
            using (new TempDataFolder())
            {
                var now = DateTime.UtcNow;
                var before = State(now);
                before.SelfTest = true;
                before.Hidden = true;
                Assert.IsNull(RestartUi.Save(before));
                var after = RestartUi.Take(now, 1);
                Assert.IsTrue(after.SelfTest);
                Assert.IsFalse(after.AgentPage);
                Assert.IsTrue(RestartUi.WantsAgentPage(after, true));
                Assert.AreEqual(before.Bounds, after.Bounds);
                Assert.AreEqual(before.Maximized, after.Maximized);
                Assert.AreEqual(before.Hidden, after.Hidden);
                Assert.AreEqual(before.Foreground, after.Foreground);
                Assert.AreEqual(before.SelectedVsPid, after.SelectedVsPid);
                Assert.AreEqual(before.AgentDraft, after.AgentDraft);
                Assert.AreEqual(before.CoverEvent, after.CoverEvent);
                Assert.AreEqual(before.CoverReadyMilliseconds, after.CoverReadyMilliseconds);
            }
        }

        [TestMethod]
        public void Save_UnwritableStatePathReportsFailure()
        {
            using (new TempDataFolder())
            {
                Directory.CreateDirectory(RestartUi.FilePath);
                Assert.IsNotNull(RestartUi.Save(State(DateTime.UtcNow)));
                Assert.IsFalse(File.Exists(RestartUi.FilePath));
            }
        }

        [TestMethod]
        public void FitBounds_KeepsOnScreenWindowsAndEnforcesMinimumSize()
        {
            var screens = new[] { new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, 0, 2560, 1400) };
            var s = State(DateTime.UtcNow);
            Assert.AreEqual(new Rectangle(100, 80, 1200, 800), RestartUi.FitBounds(s, screens, new Size(960, 620)));
            s.X = 2200; s.Width = 500;
            Assert.AreEqual(new Rectangle(2200, 80, 960, 800), RestartUi.FitBounds(s, screens, new Size(960, 620)), "副屏窗口保留，尺寸不小于最小值 / Secondary-screen window kept, at least the minimum size");
            s.X = 9000;
            Assert.IsNull(RestartUi.FitBounds(s, screens, new Size(960, 620)), "屏幕已断开时按默认位置 / Default placement when the screen is gone");
            Assert.IsNull(RestartUi.FitBounds(null, screens, Size.Empty));
            s.X = 100; s.Width = 0;
            Assert.IsNull(RestartUi.FitBounds(s, screens, Size.Empty));
        }

        [TestMethod]
        public void CoverScript_PreparesWithoutActivationAndAcknowledgesAfterPainting()
        {
            string script = RestartUi.CoverScript;
            StringAssert.Contains(script, "Get-Process -Id $OldPid");
            StringAssert.Contains(script, "EventWaitHandle]::OpenExisting");
            StringAssert.Contains(script, "SetProcessDpiAwarenessContext");
            StringAssert.Contains(script, "protected override bool ShowWithoutActivation");
            StringAssert.Contains(script, "p.ExStyle |= 0x08000080");
            StringAssert.Contains(script, "$f.ShowInTaskbar = $false");
            StringAssert.Contains(script, "$f.Update(); [void]$ready.Set()");
            StringAssert.Contains(script, "$script:exitedAt -ge 1500");
            StringAssert.Contains(script, "Remove-Item -LiteralPath $Image");
            Assert.IsFalse(script.Contains("TopMost = $true"));
            Assert.IsTrue(script.IndexOf("$ready.Set()", StringComparison.Ordinal) < script.IndexOf("Get-Process -Id $OldPid", StringComparison.Ordinal));
        }

        [DataTestMethod]
        [DataRow(false, true, false, false, 10, false)]
        [DataRow(true, false, false, false, 2, false)]
        [DataRow(true, false, false, false, 3, true)]
        [DataRow(true, true, false, false, 0, true)]
        [DataRow(false, false, true, false, 0, true)]
        [DataRow(false, false, false, true, 0, true)]
        public void CoverRelease_RequiresFrameAndSelectionUnlessHiddenOrUserMoved(bool painted, bool selected, bool hidden, bool userMoved, int seconds, bool expected)
        {
            Assert.AreEqual(expected, RestartUi.CanReleaseCover(painted, selected, hidden, userMoved, TimeSpan.FromSeconds(seconds)));
        }

        [DataTestMethod]
        [DataRow(false, 1, false, 0, true)]
        [DataRow(true, 1, false, 1, false)]
        [DataRow(false, 0, false, 7, false)]
        [DataRow(false, 0, false, 8, true)]
        [DataRow(true, 0, false, 8, true)]
        [DataRow(false, 1, true, 20, false)]
        [DataRow(false, 0, true, 20, false)]
        public void StartupResume_RemovesFixedDelayButKeepsDiscoveryAndBusyGuards(bool refreshing, int count, bool busy, int seconds, bool expected)
        {
            Assert.AreEqual(expected, RestartUi.CanResumeStartup(refreshing, count, busy, TimeSpan.FromSeconds(seconds)));
        }

        [TestMethod]
        public void SignalCover_ReportsOnlyActualDeliveryAndReleasesConsumedState()
        {
            string name = RestartUi.CoverEventName(Guid.NewGuid().ToString("N"));
            Assert.IsFalse(RestartUi.SignalCover(null));
            Assert.IsFalse(RestartUi.SignalCover(name));
            using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, name))
            using (new TempDataFolder())
            {
                Assert.IsTrue(RestartUi.SignalCover(name));
                Assert.IsTrue(stop.WaitOne(0));
                stop.Reset();
                var state = State(DateTime.UtcNow);
                state.CoverEvent = name;
                Assert.IsNull(RestartUi.Save(state));
                Assert.IsNotNull(RestartUi.Take(DateTime.UtcNow, 1));
                RestartUi.ReleaseActiveCover();
                Assert.IsTrue(stop.WaitOne(0));
            }
            Assert.IsFalse(RestartUi.SignalCover(name));
        }

        [TestMethod]
        [TestCategory(TestKind.Console)]
        public void CoverProcess_CancelledBeforeStartup_CleansFilesAndEventsWithoutShowing()
        {
            RunCoverWithoutWindow(true);
        }

        [TestMethod]
        [TestCategory(TestKind.Console)]
        public void CoverProcess_InvalidImage_CompilesHelperThenCleansUpWithoutShowing()
        {
            RunCoverWithoutWindow(false);
        }

        [TestMethod]
        [TestCategory(TestKind.Ui)]
        [TestCategory(TestKind.Console)]
        public void CoverProcess_OffscreenReadiness_DoesNotWaitForOldExitOrActivate()
        {
            RunCoverWithoutWindow(false, true);
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        private static void RunCoverWithoutWindow(bool cancelled, bool offscreen = false)
        {
            var foreground = Native.GetForegroundWindow();
            if (offscreen && foreground == IntPtr.Zero) Assert.Inconclusive("无前台窗口，跳过就绪场景 / No foreground window for the readiness scenario");
            string id = Guid.NewGuid().ToString("N");
            string name = RestartUi.CoverEventName(id);
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cover-test-" + id);
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "cover.ps1");
            string image = Path.Combine(dir, "invalid.png");
            Process child = null;
            try
            {
                File.WriteAllText(script, RestartUi.CoverScript, new UTF8Encoding(true));
                if (offscreen)
                {
                    // 仅绘制屏幕外的空白像素，不截图、不更改用户窗口。/ Draw only a blank pixel outside all screens; never capture or modify a user window.
                    using (var pixel = new Bitmap(1, 1)) pixel.Save(image, System.Drawing.Imaging.ImageFormat.Png);
                }
                else File.WriteAllText(image, "invalid image");
                using (var stop = new EventWaitHandle(cancelled, EventResetMode.ManualReset, name))
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + "-ready"))
                {
                    string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                    var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
                    string placement = " -X " + (desktop.Left - 4096) + " -Y " + (desktop.Top - 4096) + " -W 1 -H 1 -OldPid " + Process.GetCurrentProcess().Id + " -OldWindow " + foreground.ToInt64();
                    child = Process.Start(new ProcessStartInfo(powershell,
                        "-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File \"" + script + "\" -Image \"" + image + "\" -EventName \"" + name + "\"" + placement)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                    });
                    var output = child.StandardOutput.ReadToEndAsync();
                    var error = child.StandardError.ReadToEndAsync();
                    if (offscreen)
                    {
                        bool acknowledged = ready.WaitOne(10000);
                        if (!acknowledged && Native.GetForegroundWindow() != foreground) Assert.Inconclusive("前台在准备期间变化，辅助窗口正确让步 / Foreground changed during preparation; helper yielded");
                        Assert.IsTrue(acknowledged, "旧进程仍存活时必须确认绘制就绪 / Must acknowledge painting while the old process is still alive");
                        Assert.IsFalse(child.HasExited);
                        Assert.AreEqual(foreground, Native.GetForegroundWindow(), "辅助窗口不得抢焦点 / Helper must not take focus");
                        var windows = Native.GetProcessWindows(child.Id);
                        Assert.AreEqual(1, windows.Count);
                        int style = GetWindowLong(windows[0], -20);
                        Assert.AreEqual(0x08000080, style & 0x08000080, "不激活的工具窗口 / Nonactivating tool window");
                        Assert.AreEqual(0, style & 0x00040008, "不置顶且无独立任务栏按钮 / Neither topmost nor an independent taskbar button");
                        Assert.IsTrue(RestartUi.SignalCover(name));
                    }
                    Assert.IsTrue(child.WaitForExit(30000), "辅助进程应及时结束 / Helper must exit promptly");
                    string diagnostic = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
                    if (cancelled || offscreen) Assert.AreEqual(0, child.ExitCode, diagnostic);
                    else
                    {
                        Assert.AreNotEqual(0, child.ExitCode, diagnostic);
                        StringAssert.Contains(diagnostic, "FromFile", "必须完成脚本与辅助类型编译后才在图片加载处失败 / Must compile the script and helper type before failing at image loading");
                    }
                    Assert.AreEqual(offscreen, ready.WaitOne(0), "只有完成绘制的截图才能报告就绪 / Only a painted snapshot can report readiness");
                    Assert.IsFalse(File.Exists(script));
                    Assert.IsFalse(File.Exists(image));
                }
                Assert.IsFalse(RestartUi.SignalCover(name), "辅助进程不应泄漏事件句柄 / Helper must not leak event handles");
            }
            finally
            {
                if (child != null)
                {
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                    child.Dispose();
                }
                Directory.Delete(dir, true);
            }
        }
    }
}
