using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>「清除已完成」在重启后仍然有效。/ "Clear completed" stays effective after a restart.</summary>
    [TestClass]
    [DoNotParallelize]
    public class TaskClearPersistenceTests
    {
        private static readonly DateTime Cleared = new DateTime(2026, 1, 1, 12, 0, 0);

        [TestMethod]
        public void RestoredInterruptedChat_EndedBeforeClear_StaysHidden()
        {
            var chat = new ExternalChat { Restored = true, Interrupted = true, Started = Cleared.AddHours(-2), Finished = Cleared.AddHours(-1) };
            Assert.IsTrue(TaskPanel.IsCleared(chat, Cleared));
            chat.Finished = Cleared.AddMinutes(1);
            Assert.IsFalse(TaskPanel.IsCleared(chat, Cleared), "结束晚于清除的仍显示 / Ended after the clear stays visible");
        }

        [TestMethod]
        public void StoppedOrGeneratingChats_AreKept()
        {
            Assert.IsFalse(TaskPanel.IsCleared(new ExternalChat { Stopped = true, Started = Cleared.AddHours(-1), Finished = Cleared.AddHours(-1) }, Cleared));
            Assert.IsFalse(TaskPanel.IsCleared(new ExternalChat { Generating = true, Started = Cleared.AddHours(-1) }, Cleared));
            Assert.IsTrue(TaskPanel.IsCleared(new ExternalChat { Started = Cleared.AddHours(-1), Finished = Cleared.AddHours(-1) }, Cleared));
            Assert.IsFalse(TaskPanel.IsCleared(new ExternalChat { Started = Cleared.AddHours(-1), Finished = Cleared.AddHours(-1) }, null));
        }

        [TestMethod]
        public void RestoreManual_UsesNewestRecordAcrossVolumes()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
            try
            {
                Assert.IsNull(Archive.Configure(new AppSettings { ArchiveEnabled = true, ArchiveRoot = root }));
                string chat = Path.Combine(root, "chat");
                Directory.CreateDirectory(chat);
                var started = DateTime.Now.AddHours(-1);
                string day = DateTime.Now.ToString("yyyyMMdd");
                string T(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string Rec(string entry, string status, DateTime time) =>
                    "{\"time\":\"" + T(time) + "\",\"kind\":\"manual\",\"vs\":\"A\",\"vsKey\":\"A\",\"entry\":\"" + entry + "\",\"status\":\"" + status
                    + "\",\"question\":\"q " + entry + "\",\"answer\":\"a\",\"started\":\"" + T(started) + "\",\"finished\":\"" + T(time) + "\"}\n";
                // 主文件是旧记录，分卷 .2 是新记录（按文件名分卷排在前面）/ Main file holds the older records, volume .2 the newer ones (sorted first by name)
                File.WriteAllText(Path.Combine(chat, "vs-A-" + day + ".jsonl"),
                    Rec("e1", "generating", started.AddMinutes(1)) + Rec("e2", "done", started.AddMinutes(1)));
                File.WriteAllText(Path.Combine(chat, "vs-A-" + day + ".2.jsonl"),
                    Rec("e1", "done", started.AddMinutes(5)) + Rec("e2", "removed", started.AddMinutes(5)));

                var list = Archive.RestoreManual(24, 50);
                Assert.AreEqual(1, list.Count, "已移除的条目不恢复 / Removed entries are not restored");
                var e1 = list.Single();
                Assert.AreEqual("e1", e1.ArchiveId);
                Assert.IsFalse(e1.Interrupted, "取最新的「已完成」记录 / The newest done record wins");
                Assert.IsTrue(TaskPanel.IsCleared(e1, DateTime.Now));
            }
            finally
            {
                Archive.Configure(new AppSettings { ArchiveEnabled = false });
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
