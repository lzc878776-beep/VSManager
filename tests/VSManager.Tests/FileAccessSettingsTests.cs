using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class FileAccessSettingsTests
    {
        private TempDataFolder _data;
        [TestInitialize] public void Init() => _data = new TempDataFolder();
        [TestCleanup] public void Cleanup() => _data.Dispose();

        [TestMethod]
        public void OldSettings_DefaultToRegisteredRootsOnly_AndNoDocumentCleanup()
        {
            File.WriteAllText(AppSettings.FilePath, "{\"AgentPowerShellEnabled\":true}");
            var settings = AppSettings.Load();
            Assert.IsTrue(settings.AgentIncludeSolutionRoots);
            Assert.AreEqual(0, settings.AgentFileRoots.Count);
            Assert.IsFalse(settings.AgentPowerShellEnabled);
            Assert.IsFalse(settings.CloseVsDocumentsBeforeSend);
            Assert.AreEqual(10, settings.CloseVsDocumentsThreshold);
        }

        [TestMethod]
        public void ExplicitRootsAndCleanupOptions_RoundTrip_WithoutExpandingStoredVariables()
        {
            var settings = new AppSettings
            {
                AgentIncludeSolutionRoots = false,
                AgentFileRoots = new List<string> { @"%APPDATA%\ExampleSource", _data.Path },
                CloseVsDocumentsBeforeSend = true,
                CloseVsDocumentsThreshold = 0
            };
            Assert.IsTrue(settings.Save());
            var loaded = AppSettings.Load();
            Assert.IsFalse(loaded.AgentIncludeSolutionRoots);
            CollectionAssert.AreEqual(settings.AgentFileRoots, loaded.AgentFileRoots);
            Assert.IsTrue(loaded.CloseVsDocumentsBeforeSend);
            Assert.AreEqual(0, loaded.CloseVsDocumentsThreshold);
            string json = File.ReadAllText(AppSettings.FilePath);
            foreach (string name in new[] { "AgentIncludeSolutionRoots", "AgentFileRoots", "CloseVsDocumentsBeforeSend", "CloseVsDocumentsThreshold" })
                StringAssert.Contains(json, "\"" + name + "\"");
        }

        [TestMethod]
        public void Normalization_DoesNotGrantDefaultRootsWhenExplicitlyDisabled()
        {
            var settings = new AppSettings { AgentIncludeSolutionRoots = false, AgentFileRoots = null, CloseVsDocumentsThreshold = -1, AgentPowerShellEnabled = true };
            settings.ClampAgentQuota();
            settings.ClampSend();
            Assert.AreEqual(0, settings.AgentFileRoots.Count);
            Assert.IsFalse(settings.AgentIncludeSolutionRoots);
            Assert.IsFalse(settings.AgentPowerShellEnabled);
            Assert.AreEqual(10, settings.CloseVsDocumentsThreshold);
            settings.AgentFileRoots = new List<string> { " ", _data.Path, " " + _data.Path.ToUpperInvariant() + " " };
            settings.CloseVsDocumentsThreshold = int.MaxValue;
            settings.ClampAgentQuota();
            settings.ClampSend();
            CollectionAssert.AreEqual(new[] { _data.Path }, settings.AgentFileRoots);
            Assert.AreEqual(1000, settings.CloseVsDocumentsThreshold);
        }

        [TestMethod]
        public void SettingsUi_RootTypingDoesNotAuthorize_AndCleanupToggleAndThresholdApply()
        {
            RunSta(() =>
            {
                var settings = new AppSettings();
                using (var form = new SettingsForm(settings, null, new SettingsForm.Actions()))
                {
                    var roots = (TextBox)typeof(SettingsForm).GetField("_agentFileRoots", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    roots.Text = _data.Path;
                    Assert.AreEqual(0, settings.AgentFileRoots.Count);
                    var controls = Descendants(form).ToList();
                    var cleanup = controls.OfType<ToggleSwitch>().Single(c => c.Text.Contains("Close saved documents before sending"));
                    Assert.IsFalse(cleanup.Checked);
                    cleanup.Checked = true;
                    Assert.IsTrue(settings.CloseVsDocumentsBeforeSend);
                    var threshold = controls.OfType<NumericUpDown>().Single(c => c.Maximum == 1000);
                    Assert.AreEqual(10m, threshold.Value);
                    threshold.Value = 12;
                    Assert.AreEqual(12, settings.CloseVsDocumentsThreshold);
                    controls.OfType<ToggleSwitch>().Single(c => c.Text.Contains("Include registered solution roots")).Checked = false;
                    Assert.IsFalse(settings.AgentIncludeSolutionRoots);
                    Assert.AreEqual(0, settings.AgentFileRoots.Count);
                }
            });
        }

        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "设置界面测试超时 / Settings UI test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
