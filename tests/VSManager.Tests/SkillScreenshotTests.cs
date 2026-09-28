using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class SkillScreenshotTests
    {
        [DataTestMethod]
        [DataRow("127.0.0.1", true)]
        [DataRow("::1", true)]
        [DataRow("::ffff:127.0.0.1", true)]
        [DataRow("192.168.1.20", false)]
        [DataRow("10.0.0.5", false)]
        [DataRow("", false)]
        [DataRow(null, false)]
        public void ScreenshotApi_OnlyServesLocalClients(string ip, bool allowed)
        {
            var settings = new AppSettings { AgentScreenshotEnabled = true };
            Assert.AreEqual(allowed, WebRemote.ScreenshotDenial(ip, settings) == null);
        }

        [TestMethod]
        public void ScreenshotApi_RequiresScreenshotSetting()
        {
            StringAssert.Contains(WebRemote.ScreenshotDenial("127.0.0.1", new AppSettings { AgentScreenshotEnabled = false }), "disabled");
            Assert.IsNotNull(WebRemote.ScreenshotDenial("127.0.0.1", null));
        }

        [TestMethod]
        public void SkillResources_DescribeScreenshotCommand()
        {
            string skill = Resource("SKILL.md"), script = Resource("vsm.ps1");
            StringAssert.Contains(skill, "`screenshot <vs> [-Out <file.png>]`");
            StringAssert.Contains(skill, "untrusted");
            StringAssert.Contains(script, "\"screenshot\" {");
            StringAssert.Contains(script, "Call \"screenshot\"");
            StringAssert.Contains(script, "[string]$Out");
        }

        [TestMethod]
        public void RefreshInstalled_UpdatesOnlyExistingInstalls()
        {
            string root = Path.Combine(Path.GetTempPath(), "vsm-skill-" + Guid.NewGuid().ToString("N"));
            string installed = Path.Combine(root, "installed"), absent = Path.Combine(root, "absent"), current = Path.Combine(root, "current");
            try
            {
                Directory.CreateDirectory(installed);
                File.WriteAllText(Path.Combine(installed, "SKILL.md"), "old");
                Directory.CreateDirectory(current);
                File.WriteAllBytes(Path.Combine(current, "vsm.ps1"), ResourceBytes("vsm.ps1"));
                File.WriteAllBytes(Path.Combine(current, "SKILL.md"), ResourceBytes("SKILL.md"));

                Assert.AreEqual(1, SkillInstaller.RefreshInstalled(new[] { installed, absent, current }));
                CollectionAssert.AreEqual(ResourceBytes("SKILL.md"), File.ReadAllBytes(Path.Combine(installed, "SKILL.md")));
                CollectionAssert.AreEqual(ResourceBytes("vsm.ps1"), File.ReadAllBytes(Path.Combine(installed, "vsm.ps1")));
                Assert.IsFalse(Directory.Exists(absent));
                Assert.AreEqual(0, SkillInstaller.RefreshInstalled(new[] { installed, current }));
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [TestMethod]
        public void ScreenshotApi_ReturnsPngFromHost_AndHonorsPreviewSetting()
        {
            int port;
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var settings = new AppSettings { WebEnabled = true, WebPort = port, WebToken = "test-token", AgentScreenshotEnabled = true, AgentScreenshotRequirePreview = true };
            var host = new FakeHost();
            using (var web = new WebRemote(host, () => settings))
            {
                web.Apply();
                Assert.IsTrue(web.Running, web.Status);
                using (var client = new System.Net.WebClient { Encoding = Encoding.UTF8 })
                {
                    client.Headers["X-Key"] = "test-token";
                    string json = client.DownloadString("http://127.0.0.1:" + port + "/api/screenshot?vs=1");
                    StringAssert.Contains(json, "\"ok\":true");
                    string png = System.Text.RegularExpressions.Regex.Match(json, "\"png\":\"([^\"]+)\"").Groups[1].Value;
                    CollectionAssert.AreEqual(FakeHost.Png, Convert.FromBase64String(png));
                    Assert.AreEqual(true, host.LastPreview);
                    host.Cancel = true;
                    client.Headers["X-Key"] = "test-token";
                    StringAssert.Contains(client.DownloadString("http://127.0.0.1:" + port + "/api/screenshot?vs=1"), "cancelled");
                }
            }
        }

        private sealed class FakeHost : IRemoteHost
        {
            internal static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            internal bool? LastPreview;
            internal bool Cancel;
            public System.Collections.Generic.IList<VsInstance> Instances { get; } = new System.Collections.Generic.List<VsInstance> { new VsInstance { Pid = 42 } };
            public string NameOf(VsInstance v) => "Demo";
            public string NoteOf(VsInstance v) => "";
            public System.Threading.Tasks.Task<string> SetNote(VsInstance v, string note) => System.Threading.Tasks.Task.FromResult("");
            public ChatTranscript CachedChat(VsInstance v) => null;
            public System.Threading.Tasks.Task<string> SendChat(VsInstance v, string text) => System.Threading.Tasks.Task.FromResult("");
            public System.Threading.Tasks.Task<string> InvokeChatButton(VsInstance v, string automationId, string name) => System.Threading.Tasks.Task.FromResult("");
            public void Log(string s) { }
            public void FocusChat(int pid) { }
            public System.Threading.Tasks.Task<string> DockPanes() => System.Threading.Tasks.Task.FromResult("");
            public System.Threading.Tasks.Task<string> ErrorList(VsInstance v, int max) => System.Threading.Tasks.Task.FromResult("");
            public System.Threading.Tasks.Task<byte[]> CaptureScreenshot(VsInstance v, bool requirePreview, System.Threading.CancellationToken cancellationToken)
            {
                LastPreview = requirePreview;
                return System.Threading.Tasks.Task.FromResult(Cancel ? null : Png);
            }
        }

        private static byte[] ResourceBytes(string file)
        {
            var asm = typeof(SkillInstaller).Assembly;
            string name = asm.GetManifestResourceNames().Single(n => n.EndsWith("Skill." + file, StringComparison.OrdinalIgnoreCase));
            using (var s = asm.GetManifestResourceStream(name))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        private static string Resource(string file) => Encoding.UTF8.GetString(ResourceBytes(file));
    }
}
