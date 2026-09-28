using System;
using System.Drawing;
using System.IO;
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
            Maximized = true, Foreground = true, AgentPage = false, SelectedVsPid = 777, AgentDraft = "草稿 @[#1 A|x] draft", CoverEvent = RestartUi.CoverEventName("abc")
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
        public void CoverScript_WaitsForOldProcessAndClosesOnEvent()
        {
            string script = RestartUi.CoverScript;
            StringAssert.Contains(script, "Get-Process -Id $OldPid");
            StringAssert.Contains(script, "EventWaitHandle");
            StringAssert.Contains(script, "SetProcessDpiAwarenessContext");
            StringAssert.Contains(script, "Remove-Item -LiteralPath $Image");
        }
    }
}
