using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>settings.json：默认值、读写往返、损坏恢复与取值规范化。/ settings.json: defaults, round trip, recovery and normalization.</summary>
    [TestClass]
    public class SettingsTests
    {
        private TempDataFolder _data;

        [TestInitialize]
        public void Init() => _data = new TempDataFolder();

        [TestCleanup]
        public void Cleanup() => _data.Dispose();

        [TestMethod]
        public void FilePath_IsInsideDataFolder()
        {
            Assert.AreEqual(_data.File("settings.json"), AppSettings.FilePath);
        }

        [TestMethod]
        public void Load_WithoutFile_ReturnsDefaults()
        {
            var s = AppSettings.Load();
            Assert.AreEqual(1500, s.PollMs);
            Assert.AreEqual(8765, s.WebPort);
            Assert.IsTrue(s.VoiceEnabled);
            Assert.AreEqual(VoiceLanguages.Chinese, s.VoiceLanguage);
            Assert.AreEqual(0, s.TaskHistoryLimit);
            Assert.AreEqual(0, s.TaskNextId);
            Assert.IsTrue(s.ArchiveEnabled);
            Assert.AreEqual(0, s.ArchiveRetentionDays);
            Assert.AreEqual(50, s.ExternalRestoreLimit);
            Assert.AreEqual(24, s.ExternalRestoreHours);
            Assert.AreEqual(AgentChatLog.DefaultKeepDays, s.AgentChatKeepDays);
            Assert.AreEqual(AgentChatLog.DefaultMaxRecords, s.AgentChatMaxRecords);
            Assert.IsFalse(s.VsMemoryAutoEnabled);
            Assert.AreEqual(VsMemory.DefaultThresholdMB, s.VsMemoryThresholdMB);
            Assert.AreEqual("VSManager", s.PublishRepoName);
            Assert.AreEqual("public", s.PublishVisibility);
            Assert.AreEqual("main", s.PublishBranch);
            Assert.AreEqual("VSManager contributors", s.PublishAuthorName);
            Assert.AreEqual("", s.PublishAuthorEmail);
            Assert.IsNotNull(s.Aliases);
            Assert.IsNotNull(s.VsNotes);
            Assert.IsFalse(File.Exists(AppSettings.FilePath), "只读取不写入 / loading never writes");
        }

        [TestMethod]
        public void MissingFields_InOldFile_GetDefaults()
        {
            File.WriteAllText(AppSettings.FilePath, "{\"PollMs\":2000}");
            var s = AppSettings.Load();
            Assert.AreEqual(2000, s.PollMs);
            Assert.AreEqual(8765, s.WebPort);
            Assert.IsTrue(s.VoiceEnabled);
            Assert.AreEqual("main", s.PublishBranch);
        }

        [TestMethod]
        public void SaveThenLoad_RoundTrips_AndKeepsJsonNames()
        {
            var s = new AppSettings { PollMs = 3000, VoiceEnabled = false, TaskNextId = 42, VoiceLanguage = VoiceLanguages.English, PublishOwner = "example" };
            s.SetAlias("Demo", "演示");
            Assert.IsTrue(s.Save());
            string json = File.ReadAllText(AppSettings.FilePath);
            StringAssert.Contains(json, "\"VoiceAnnounce\"");
            StringAssert.Contains(json, "\"TaskNextId\"");
            StringAssert.Contains(json, "\"ArchiveRoot\"");
            StringAssert.Contains(json, "\"VoiceLanguage\"");
            Assert.IsFalse(File.Exists(AppSettings.FilePath + ".tmp"));

            var back = AppSettings.Load();
            Assert.AreEqual(3000, back.PollMs);
            Assert.IsFalse(back.VoiceEnabled);
            Assert.AreEqual(42, back.TaskNextId);
            Assert.AreEqual(VoiceLanguages.English, back.VoiceLanguage);
            Assert.AreEqual("example", back.PublishOwner);
            Assert.AreEqual("演示", back.GetAlias("demo"));
        }

        [TestMethod]
        public void Save_RaisesSavedEvent()
        {
            string result = "not raised";
            Action<string> h = e => result = e;
            AppSettings.Saved += h;
            try { new AppSettings().Save(); }
            finally { AppSettings.Saved -= h; }
            Assert.IsNull(result);
        }

        [TestMethod]
        public void CorruptFile_FallsBackToBackup()
        {
            new AppSettings { PollMs = 2500 }.Save();
            File.Copy(AppSettings.FilePath, AppSettings.FilePath + ".bak", true);
            File.WriteAllText(AppSettings.FilePath, "{ broken");
            Assert.AreEqual(2500, AppSettings.Load().PollMs);
            Assert.AreEqual(0, Directory.GetFiles(_data.Path, "settings.json.corrupt-*").Length);
        }

        [TestMethod]
        public void CorruptFileWithoutBackup_UsesDefaults_AndKeepsCopy()
        {
            File.WriteAllText(AppSettings.FilePath, "{ broken");
            var s = AppSettings.Load();
            Assert.AreEqual(1500, s.PollMs);
            Assert.AreEqual(1, Directory.GetFiles(_data.Path, "settings.json.corrupt-*").Length);
            Assert.AreEqual("{ broken", File.ReadAllText(AppSettings.FilePath), "损坏的文件不被覆盖 / damaged file not overwritten");
        }

        [TestMethod]
        public void EmptyFile_UsesDefaults()
        {
            File.WriteAllText(AppSettings.FilePath, "");
            Assert.AreEqual(1500, AppSettings.Load().PollMs);
        }

        [TestMethod]
        public void Load_ClampsAndNormalizesValues()
        {
            File.WriteAllText(AppSettings.FilePath,
                "{\"AgentChatKeepDays\":-5,\"AgentChatMaxRecords\":-1,\"VsMemoryThresholdMB\":10,\"VoiceLanguage\":\"fr\"}");
            var s = AppSettings.Load();
            Assert.AreEqual(0, s.AgentChatKeepDays);
            Assert.AreEqual(0, s.AgentChatMaxRecords);
            Assert.AreEqual(VsMemory.MinThresholdMB, s.VsMemoryThresholdMB);
            Assert.AreEqual(VoiceLanguages.Chinese, s.VoiceLanguage);

            File.WriteAllText(AppSettings.FilePath, "{\"VsMemoryThresholdMB\":0,\"VoiceLanguage\":\"en-US\"}");
            s = AppSettings.Load();
            Assert.AreEqual(VsMemory.DefaultThresholdMB, s.VsMemoryThresholdMB);
            Assert.AreEqual(VoiceLanguages.English, s.VoiceLanguage);
        }

        [TestMethod]
        public void DesktopTools_DefaultsAndOptOutSurviveReload()
        {
            File.WriteAllText(AppSettings.FilePath, "{\"PollMs\":2000}");
            var settings = AppSettings.Load();
            Assert.IsTrue(settings.AgentScreenshotEnabled);
            Assert.IsFalse(settings.AgentPowerShellEnabled);
            settings.AgentScreenshotEnabled = false;
            settings.AgentPowerShellEnabled = false;
            Assert.IsTrue(settings.Save());
            settings = AppSettings.Load();
            Assert.IsFalse(settings.AgentScreenshotEnabled);
            Assert.IsFalse(settings.AgentPowerShellEnabled);
        }

        [TestMethod]
        public void SkipFailedPredecessors_DefaultsOn_ForOldFiles_AndOptOutPersists()
        {
            Assert.IsTrue(new AppSettings().SkipFailedPredecessors);
            Assert.IsTrue(AppSettings.Load().SkipFailedPredecessors);
            File.WriteAllText(AppSettings.FilePath, "{\"PollMs\":2000}");
            var settings = AppSettings.Load();
            Assert.IsTrue(settings.SkipFailedPredecessors);
            settings.SkipFailedPredecessors = false;
            Assert.IsTrue(settings.Save());
            StringAssert.Contains(File.ReadAllText(AppSettings.FilePath), "\"SkipFailedPredecessors\"");
            Assert.IsFalse(AppSettings.Load().SkipFailedPredecessors);
            settings.SkipFailedPredecessors = true;
            Assert.IsTrue(settings.Save());
            Assert.IsTrue(AppSettings.Load().SkipFailedPredecessors);
        }

        [TestMethod]
        public void AutoNormalize_DefaultsOn_AndExplicitOptOutSurvivesReload()
        {
            Assert.IsTrue(new AppSettings().SendAutoNormalizeLineEndings);
            File.WriteAllText(AppSettings.FilePath, "{\"PollMs\":2000}");
            var settings = AppSettings.Load();
            Assert.IsTrue(settings.SendAutoNormalizeLineEndings);
            settings.SendAutoNormalizeLineEndings = false;
            Assert.IsTrue(settings.Save());
            Assert.IsFalse(AppSettings.Load().SendAutoNormalizeLineEndings);
        }

        [TestMethod]
        public void AutoDismissNotices_DefaultsOn_AndCanBeDisabledIndependently()
        {
            Assert.IsTrue(new AppSettings().SendAutoDismissNotices);
            File.WriteAllText(AppSettings.FilePath, "{\"PollMs\":2000}");
            var settings = AppSettings.Load();
            Assert.IsTrue(settings.SendAutoDismissNotices);
            settings.SendAutoDismissNotices = false;
            Assert.IsTrue(settings.Save());
            var loaded = AppSettings.Load();
            Assert.IsFalse(loaded.SendAutoDismissNotices);
            Assert.IsTrue(loaded.SendAutoNormalizeLineEndings);
            loaded.SendAutoDismissNotices = true;
            loaded.SendAutoNormalizeLineEndings = false;
            Assert.IsTrue(loaded.Save());
            loaded = AppSettings.Load();
            Assert.IsTrue(loaded.SendAutoDismissNotices);
            Assert.IsFalse(loaded.SendAutoNormalizeLineEndings);
        }

        [TestMethod]
        public void SendConfirmation_DefaultsAndClamp()
        {
            var s = AppSettings.Load();
            Assert.AreEqual(10, s.SendConfirmTimeoutSeconds);
            Assert.IsTrue(s.SendAutoRetry);
            Assert.AreEqual(1, s.SendRetryCount);

            File.WriteAllText(AppSettings.FilePath, "{\"SendConfirmTimeoutSeconds\":1,\"SendRetryCount\":9,\"SendAutoRetry\":false}");
            s = AppSettings.Load();
            Assert.AreEqual(2, s.SendConfirmTimeoutSeconds);
            Assert.AreEqual(5, s.SendRetryCount);
            Assert.IsFalse(s.SendAutoRetry);

            File.WriteAllText(AppSettings.FilePath, "{\"SendConfirmTimeoutSeconds\":0,\"SendRetryCount\":-1}");
            s = AppSettings.Load();
            Assert.AreEqual(AppSettings.DefaultSendConfirmTimeoutSeconds, s.SendConfirmTimeoutSeconds);
            Assert.AreEqual(0, s.SendRetryCount);
        }

        /// <summary>重新排队后隐藏原失败条目：默认开启，隐藏标记可持久化。/ Hiding requeued failed entries: on by default, marks persist.</summary>
        [TestMethod]
        public void AutoHideResentFailed_DefaultsAndPersist()
        {
            var s = AppSettings.Load();
            Assert.IsTrue(s.AutoHideResentFailedTasks);
            Assert.IsTrue(s.AutoHideResentFailedNotify);
            Assert.IsNotNull(s.HiddenResentTasks);
            Assert.AreEqual(0, s.HiddenResentTasks.Count);

            TaskHideList.Add(s.HiddenResentTasks, 26, 34, new DateTime(2026, 1, 2, 3, 4, 5));
            s.AutoHideResentFailedTasks = false;
            s.Save();
            s = AppSettings.Load();
            Assert.IsFalse(s.AutoHideResentFailedTasks);
            Assert.AreEqual(34, TaskHideList.ReplacedBy(s.HiddenResentTasks, 26));

            File.WriteAllText(AppSettings.FilePath, "{\"HiddenResentTasks\":null}");
            s = AppSettings.Load();
            Assert.IsNotNull(s.HiddenResentTasks);
            Assert.IsTrue(s.AutoHideResentFailedTasks);
        }

        /// <summary>输入框定位超时与重试的默认值与夹取。/ Defaults and clamping of the input locate timeout and retries.</summary>
        [TestMethod]
        public void SendLocate_DefaultsAndClamp()
        {
            var s = AppSettings.Load();
            Assert.AreEqual(6, s.SendLocateTimeoutSeconds);
            Assert.AreEqual(1, s.SendLocateRetryCount);

            File.WriteAllText(AppSettings.FilePath, "{\"SendLocateTimeoutSeconds\":999,\"SendLocateRetryCount\":9}");
            s = AppSettings.Load();
            Assert.AreEqual(60, s.SendLocateTimeoutSeconds);
            Assert.AreEqual(5, s.SendLocateRetryCount);

            File.WriteAllText(AppSettings.FilePath, "{\"SendLocateTimeoutSeconds\":0,\"SendLocateRetryCount\":-1}");
            s = AppSettings.Load();
            Assert.AreEqual(AppSettings.DefaultSendLocateTimeoutSeconds, s.SendLocateTimeoutSeconds);
            Assert.AreEqual(0, s.SendLocateRetryCount);
        }

        [TestMethod]
        public void VoiceLanguage_Normalize()
        {
            Assert.AreEqual("zh", VoiceLanguages.Normalize(null));
            Assert.AreEqual("zh", VoiceLanguages.Normalize(" "));
            Assert.AreEqual("en", VoiceLanguages.Normalize("EN"));
            Assert.AreEqual("en", VoiceLanguages.Normalize("English"));
            Assert.AreEqual("zh", VoiceLanguages.Normalize("ja"));
        }
    }
}
