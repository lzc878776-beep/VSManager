using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>界面探测工具：托盘残影结论、重启交接文件读取与测试项归类。/ UI probes: ghost verdicts, restart file reading and item classification.</summary>
    [TestClass]
    public class UiProbeTests
    {
        private const string Tip = "多 VS 管理工具";

        [TestMethod]
        public void GhostVerdict_CoversDeadOwnersExtraIconsAndUnreadOverflow()
        {
            StringAssert.Contains(UiProbe.GhostVerdict(1, 1, 1, true, true), "ghost icon(s)");
            StringAssert.Contains(UiProbe.GhostVerdict(2, 0, 1, true, true), "Possible ghost");
            StringAssert.Contains(UiProbe.GhostVerdict(1, 0, 1, true, true), "No ghost");
            StringAssert.Contains(UiProbe.GhostVerdict(2, 0, 2, true, true), "No ghost");
            StringAssert.Contains(UiProbe.GhostVerdict(0, 0, 1, false, true), "No ghost");
            StringAssert.Contains(UiProbe.GhostVerdict(0, 0, 1, true, true), "not found");
            StringAssert.Contains(UiProbe.GhostVerdict(0, 0, 1, true, false), "Inconclusive");
            StringAssert.Contains(UiProbe.GhostVerdict(1, 0, 1, true, false), "cannot be ruled out");
        }

        [TestMethod]
        public void TrayText_ListsIconsAndMatchesOwnTooltip()
        {
            var icons = new List<TrayIconInfo>
            {
                new TrayIconInfo { Tooltip = " " + Tip + "\n第二行", Overflow = true },
                new TrayIconInfo { Tooltip = Tip, Pid = 5, ProcessName = "VSManager", OwnerAlive = false },
                new TrayIconInfo { Tooltip = "Other", Pid = 6, ProcessName = "other", OwnerAlive = true },
            };
            string text = UiProbe.TrayText(icons, "test", true, Tip, true, 42, 1);
            StringAssert.Contains(text, "1. 「" + Tip + " 第二行」 | 溢出区 / overflow");
            StringAssert.Contains(text, "VSManager (PID 5) | 所属窗口已不存在（残影）");
            StringAssert.Contains(text, "3. 「Other」 | 进程 / process: other (PID 6)");
            StringAssert.Contains(text, "2 matching icons");
            StringAssert.Contains(text, "1 VSManager ghost icon(s)");
        }

        [TestMethod]
        public void ForegroundText_NeverThrows()
        {
            Assert.IsFalse(string.IsNullOrEmpty(UiProbe.ForegroundText(System.Diagnostics.Process.GetCurrentProcess().Id, IntPtr.Zero)));
        }

        [TestMethod]
        public void RestartProbe_ReportsConsumedPendingAndExpiredFiles()
        {
            using (new TempDataFolder())
            {
                var now = DateTime.UtcNow;
                string none = RestartProbe.DescribeFiles(now);
                StringAssert.Contains(none, "【restart-ui.json】");
                StringAssert.Contains(none, "【restart-handoff.json】");
                StringAssert.Contains(none, "consumed (read and deleted)");
                Assert.IsFalse(none.Contains(AppPaths.DataFolder), "不输出本机路径 / No local paths");

                Assert.IsNull(RestartUi.Save(new RestartUiState { CreatedUtc = now, OldPid = 7, Width = 900, Height = 600, AgentDraft = "draft-x" }));
                string pending = RestartProbe.Describe("restart-ui.json", RestartUi.FilePath, RestartUi.MaxAge, now);
                StringAssert.Contains(pending, "Not consumed");
                StringAssert.Contains(pending, "still valid");
                StringAssert.Contains(pending, "draft-x");

                File.SetLastWriteTimeUtc(RestartUi.FilePath, now - RestartUi.MaxAge - TimeSpan.FromMinutes(1));
                StringAssert.Contains(RestartProbe.Describe("restart-ui.json", RestartUi.FilePath, RestartUi.MaxAge, now), "expired");

                File.WriteAllText(RestartUi.FilePath, new string('x', RestartProbe.MaxContentChars + 50));
                StringAssert.Contains(RestartProbe.Describe("restart-ui.json", RestartUi.FilePath, RestartUi.MaxAge, now), "truncated");
            }
        }

        [TestMethod]
        public void ItemsNamingProbeTools_AreAiVerifiable()
        {
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Classify("重启后用 get_window_state 确认窗口位置一致"));
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Classify("list_tray_icons 显示没有托盘残影"));
            Assert.AreEqual(TaskTestChecklist.ByAi, TaskTestChecklist.Classify("重启后窗口位置不变"), "界面探测可读的状态归为 AI / Probe-readable state is AI");
            Assert.AreEqual(TaskTestChecklist.ByUser, TaskTestChecklist.Classify("重启时过渡画面动画流畅"));
            StringAssert.Contains(TaskTestChecklist.TagRule, "托盘图标");
        }
    }
}
