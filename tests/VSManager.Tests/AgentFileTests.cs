using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AgentFileTests
    {
        private TempDataFolder _data;
        private FileHost _host;
        private AppSettings _settings;
        private AgentService _agent;
        private string _root;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _root = _data.File("workspace");
            Directory.CreateDirectory(_root);
            _settings = new AppSettings { AgentIncludeSolutionRoots = false, AgentFileRoots = new List<string>() };
            _host = new FileHost();
            _host.Instances.Add(new VsInstance { Pid = 42, Key = "test", SolutionPath = Path.Combine(_root, "Demo.slnx") });
            _agent = new AgentService(_host, () => _settings);
        }

        [TestCleanup]
        public void Cleanup() { _agent.Dispose(); _data.Dispose(); }

        private string Put(string name, string text = "safe marker")
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        private void Grant() { _settings.AgentFileRoots.Add(_root); }
        private AIFunction Tool(string name) => ((IEnumerable<AITool>)typeof(AgentService)
            .GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_agent)).OfType<AIFunction>().Single(t => t.Name == name);
        private async Task<string> Invoke(string name, AIFunctionArguments args, CancellationToken token = default) => (await Tool(name).InvokeAsync(args, token)).ToString();
        private Task<string> Read(string path, int startLine = 1, int maxLines = 200) => Invoke("read_file", new AIFunctionArguments { ["path"] = path, ["startLine"] = startLine, ["maxLines"] = maxLines });
        private Task<string> Find(string directory = null, string pattern = "*", int maxDepth = 3, int maxResults = 100) => Invoke("find_files", new AIFunctionArguments
            { ["directory"] = directory ?? _root, ["pattern"] = pattern, ["maxDepth"] = maxDepth, ["maxResults"] = maxResults });
        private Task<string> Search(string keyword, string pattern = "*", int maxFileBytes = 1048576) => Invoke("search_file_contents", new AIFunctionArguments
            { ["directory"] = _root, ["keyword"] = keyword, ["filePattern"] = pattern, ["maxFileBytes"] = maxFileBytes });

        [TestMethod]
        public async Task RegisteredFunctions_DefaultDeny_GrantAndRevokeImmediately()
        {
            string file = Put("plain.txt");
            StringAssert.Contains(await Read(file), "Access denied");
            Grant();
            StringAssert.Contains(await Read(file), "safe marker");
            _settings.AgentFileRoots.Clear();
            StringAssert.Contains(await Read(file), "Access denied");
            StringAssert.Contains(await Find(), "Access denied");
        }

        [TestMethod]
        public async Task RegisteredSolutionRoots_AreDynamic_NotArbitraryRunningVs()
        {
            string file = Put("plain.txt");
            _settings.AgentIncludeSolutionRoots = true;
            StringAssert.Contains(await Read(file), "Access denied");
            Assert.IsNull(_host.Solutions.Upsert(new SolutionEntry { Alias = "Demo", Path = _host.Instances[0].SolutionPath }));
            StringAssert.Contains(await Read(file), "safe marker");
            _settings.AgentIncludeSolutionRoots = false;
            StringAssert.Contains(await Read(file), "Access denied");
            _settings.AgentIncludeSolutionRoots = true;
            Assert.IsNull(_host.Solutions.Replace(new SolutionEntry[0]));
            StringAssert.Contains(await Read(file), "Access denied");
        }

        [TestMethod]
        public async Task CanonicalBoundary_RejectsTraversalSiblingDevicesAndStreams()
        {
            Grant();
            string file = Put("plain.txt");
            string sibling = _root + "-sibling";
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "outside.txt"), "outside marker");
            foreach (string path in new[] { Path.Combine(_root, "..", "workspace", "plain.txt"), Path.Combine(sibling, "outside.txt"),
                "\\\\?\\" + file, "\\\\.\\" + file, "\\\\localhost\\share\\plain.txt", file + ":stream", file + ".", file + " ",
                Path.Combine(_root, "NUL"), "relative.txt", Path.GetPathRoot(file).Substring(0, 2) + "relative.txt" })
                StringAssert.Contains(await Read(path), "Access denied", path);
        }

        [DataTestMethod]
        [DataRow(".ssh\\config")]
        [DataRow(".aws\\config")]
        [DataRow(".azure\\profile.json")]
        [DataRow(".kube\\config")]
        [DataRow(".gnupg\\pubring.txt")]
        [DataRow("User Data\\Default\\Preferences")]
        [DataRow("Profiles\\profile\\prefs.js")]
        [DataRow("Credentials\\store.txt")]
        [DataRow("ProgramData\\store.txt")]
        [DataRow(".env.local")]
        [DataRow(".npmrc")]
        [DataRow("id_rsa")]
        [DataRow("private.pem")]
        [DataRow("settings.json")]
        [DataRow("passwords.txt")]
        [DataRow("my-credentials\\store.txt")]
        [DataRow("token-cache\\store.txt")]
        [DataRow(".env.private\\store.txt")]
        public async Task SensitiveNames_AreNeverReadFoundListedOrSearched(string name)
        {
            Grant();
            string path = Put(name, "hidden-canary");
            StringAssert.Contains(await Read(path), "Access denied");
            string find = await Find();
            Assert.IsFalse(find.Contains(Path.GetFileName(name)));
            Assert.IsFalse((await Search("hidden-canary")).Contains("hidden-canary"));
            string list = await Invoke("list_directory", new AIFunctionArguments { ["directory"] = _root });
            Assert.IsFalse(list.Contains(name.Split('\\')[0]));
        }

        [TestMethod]
        public async Task BroadGrant_NeverExposesSystemOrAppDataConfiguration()
        {
            _settings.AgentFileRoots.Add(Path.GetPathRoot(_root));
            foreach (string path in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "application", "config.xml"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "application", "config.json"), AppSettings.FilePath })
                StringAssert.Contains(await Read(path), "Access denied");
            StringAssert.Contains(await Read(Put("plain.txt")), "safe marker");
        }

        [TestMethod]
        public async Task WholeFileRedaction_PrecedesPaginationAndSearch_HandlesJsonXmlAndMultiline()
        {
            Grant();
            _settings.AgentApiKey = "configured-agent-value";
            _settings.VoiceApiKey = "configured-voice-value";
            _settings.GitHubToken = "configured-github-value";
            _settings.WebToken = "configured-web-value";
            string text = "normal marker\n{\"apiKey\": \"json-canary\", \"password\": \"first-canary\nsecond-canary\"}\n"
                + "<add key=\"ApiKey\" value=\"xml-canary\" />\n<Password>xml-multiline\nxml-next</Password>\n"
                + "password=env-canary\nServer=host;Password=connection-canary;User Id=test\n"
                + "-----BEGIN PRIVATE KEY-----\nprivate-canary\n-----END PRIVATE KEY-----\n"
                + "configured-agent-value configured-voice-value configured-github-value configured-web-value\n"
                + "Bearer bearer-canary sk-1234567890abcdef ghp_1234567890abcdef\nlast marker";
            string file = Put("sample.txt", text);
            string result = await Read(file);
            StringAssert.Contains(result, "normal marker");
            StringAssert.Contains(result, "last marker");
            StringAssert.Contains(result, "REDACTED");
            foreach (string secret in new[] { "json-canary", "first-canary", "second-canary", "xml-canary", "xml-multiline", "xml-next",
                "env-canary", "connection-canary", "private-canary", "configured-agent-value", "configured-voice-value", "configured-github-value", "configured-web-value", "bearer-canary", "sk-1234567890abcdef", "ghp_1234567890abcdef" })
            {
                Assert.IsFalse(result.Contains(secret), secret);
                Assert.IsFalse((await Search(secret)).Contains(secret), secret);
            }
            Assert.IsFalse((await Read(file, 3, 1)).Contains("second-canary"));
            string audit = File.ReadAllText(AppLog.PathOf(AgentFileService.AuditFile));
            Assert.IsFalse(audit.Contains("canary"));
            StringAssert.Contains(audit, _root);
        }

        [TestMethod]
        public async Task Redaction_YamlBlocksAndCredentialUrls_AreRemovedBeforeSearch()
        {
            Grant();
            string file = Put("sample.yaml", "api_key: |\n  yaml-canary\n  continued-canary\n");
            string result = await Read(file, 2);
            Assert.IsFalse(result.Contains("canary"));
            Assert.IsFalse((await Search("yaml-canary")).Contains("yaml-canary"));
            string url = Put("url.txt", "https://sample:uri-canary@example.invalid/data");
            Assert.IsFalse((await Read(url)).Contains("uri-canary"));
        }

        [TestMethod]
        public async Task ConfiguredSecretMatchingKeyNames_DoesNotDisableStructuralRedaction()
        {
            Grant();
            _settings.AgentApiKey = "api";
            string file = Put("data.json", "{\"apiKey\": \"structural-canary\"}");
            Assert.IsFalse((await Read(file)).Contains("structural-canary"));
        }

        [TestMethod]
        public async Task Read_Utf16Text_IsSupported_AndBoundedSearchOutputNeverExceedsCap()
        {
            Grant();
            string file = Put("unicode.txt");
            File.WriteAllText(file, "第一行 / first\n第二行 / second", Encoding.Unicode);
            StringAssert.Contains(await Read(file, 2, 1), "2: 第二行 / second");
            Put("wide.txt", string.Join("\n", Enumerable.Repeat("marker " + new string('x', 1500), 100)));
            string result = await Search("marker");
            Assert.IsTrue(result.Length <= AgentFileService.MaxOutput);
            StringAssert.Contains(result, "Truncated");
        }

        [TestMethod]
        public async Task EnumerationBudget_StopsLargeNoMatchWalk_AndAuditsLimit()
        {
            Grant();
            for (int i = 0; i <= AgentFileService.MaxEntries; i++) Put("entry" + i + ".txt", "");
            string result = await Find(pattern: "*.missing");
            StringAssert.Contains(result, "budget reached");
            StringAssert.Contains(File.ReadAllText(AppLog.PathOf(AgentFileService.AuditFile)), "status=limited");
        }

        [TestMethod]
        public void OpenHandles_PinFileAndAncestorsAgainstRename()
        {
            Grant();
            string file = Put("nested\\plain.txt");
            var policy = new AgentFilePolicy(() => _settings.AgentFileRoots);
            using (policy.Open(file, false))
            {
                Assert.ThrowsException<IOException>(() => File.Move(file, file + ".moved"));
                Assert.ThrowsException<IOException>(() => Directory.Move(Path.GetDirectoryName(file), Path.Combine(_root, "moved")));
            }
            File.Move(file, file + ".moved");
            Assert.IsTrue(File.Exists(file + ".moved"));
        }

        [TestMethod]
        public async Task Redaction_UnterminatedPrivateBlockAndJsonConnectionObject_FailClosed()
        {
            Grant();
            Assert.IsFalse((await Read(Put("block.txt", "-----BEGIN RSA PRIVATE KEY-----\nprivate-tail"))).Contains("private-tail"));
            Assert.IsFalse((await Read(Put("object.json", "{\"ConnectionStrings\": {\"Default\": \"Server=s;Password=object-canary\"}}"))).Contains("object-canary"));
        }

        [TestMethod]
        public async Task Find_GlobDepthGeneratedSkipsAndResultCaps()
        {
            Grant();
            Put("root.cs"); Put("root.txt"); Put("child\\one.cs"); Put("child\\grandchild\\two.cs");
            Put("bin\\hidden.cs"); Put("obj\\hidden.cs"); Put("node_modules\\hidden.cs"); Put(".git\\hidden.cs");
            string shallow = await Find(pattern: "*.CS", maxDepth: 0);
            StringAssert.Contains(shallow, "root.cs");
            Assert.IsFalse(shallow.Contains("one.cs"));
            string depth = await Find(pattern: "*.cs", maxDepth: 1);
            StringAssert.Contains(depth, "one.cs");
            Assert.IsFalse(depth.Contains("two.cs"));
            Assert.IsFalse(depth.Contains("hidden.cs"));
            string limited = await Find(pattern: "*.cs", maxResults: 1);
            StringAssert.Contains(limited, "results=1");
            StringAssert.Contains(limited, "Truncated");
            string nonrecursive = await Invoke("find_files", new AIFunctionArguments { ["directory"] = _root, ["recursive"] = false, ["pattern"] = "r??t.cs" });
            StringAssert.Contains(nonrecursive, "root.cs");
            Assert.IsFalse(nonrecursive.Contains("one.cs"));
        }

        [TestMethod]
        public async Task ContentSearch_FiltersBytesBinaryAndMatchesLiteralCaseInsensitively()
        {
            Grant();
            Put("plain.cs", "prefix MArker [x].* suffix\nsecond marker");
            Put("other.txt", "marker"); Put("oversize.cs", "marker" + new string('x', 100));
            File.WriteAllBytes(Put("binary.cs"), new byte[] { 0, 109, 97, 114, 107, 101, 114 });
            Put("binary.exe", "marker");
            string result = await Search("marker", "*.cs", 80);
            StringAssert.Contains(result, "plain.cs:1:");
            StringAssert.Contains(result, "plain.cs:2:");
            Assert.IsFalse(result.Contains("other.txt"));
            Assert.IsFalse(result.Contains("oversize.cs"));
            Assert.IsFalse(result.Contains("binary.cs"));
            StringAssert.Contains(await Search("[x].*"), "plain.cs:1:");
            StringAssert.Contains(await Find(), "binary.exe");
        }

        [TestMethod]
        public async Task Read_PaginatesAndBoundsHugeLinesWholeFilesAndOutput()
        {
            Grant();
            string file = Put("lines.txt", "first\nsecond\nthird");
            string result = await Read(file, 2, 1);
            StringAssert.Contains(result, "2: second");
            StringAssert.Contains(result, "nextStartLine=3");
            StringAssert.Contains(await Read(file, 3, 1), "nextStartLine=0");
            string huge = await Read(Put("huge.txt", new string('x', 500000) + "\nend"), 1, 1);
            Assert.IsTrue(huge.Length <= AgentFileService.MaxOutput);
            StringAssert.Contains(huge, "Line truncated");
            StringAssert.Contains(huge, "nextStartLine=2");
            string many = await Read(Put("many.txt", string.Join("\n", Enumerable.Repeat(new string('x', 2000), 40))), 1, 500);
            Assert.IsTrue(many.Length <= AgentFileService.MaxOutput);
            StringAssert.Contains(many, "nextStartLine=8");
            Assert.IsFalse((await Read(Put("large.txt", "large-canary" + new string('x', AgentFileService.MaxBytes)))).Contains("large-canary"));
            Assert.IsFalse((await Read(Put("control.txt", "binary-canary\0"))).Contains("binary-canary"));
        }

        [TestMethod]
        public async Task List_ProvidesImmediateMetadataWithoutContent()
        {
            Grant(); Put("plain.txt", "content-canary"); Put("nested\\inside.txt");
            string result = await Invoke("list_directory", new AIFunctionArguments { ["directory"] = _root });
            StringAssert.Contains(result, "file plain.txt; bytes=14; modifiedUtc=");
            StringAssert.Contains(result, "directory nested; bytes=-; modifiedUtc=");
            Assert.IsFalse(result.Contains("inside.txt"));
            Assert.IsFalse(result.Contains("content-canary"));
        }

        [TestMethod]
        public async Task InvalidAndDeniedAndMissingAndCancellation_AllAreAuditedWithoutArguments()
        {
            await Read(Path.Combine(_root, "deny-canary.txt"));
            Grant();
            await Read(Path.Combine(_root, "missing-canary.txt"));
            StringAssert.Contains(await Read(Put("plain.txt"), 0), "Invalid");
            StringAssert.Contains(await Find(maxDepth: 9), "Invalid");
            StringAssert.Contains(await Find(maxResults: 201), "Invalid");
            StringAssert.Contains(await Search("query-canary", maxFileBytes: AgentFileService.MaxBytes + 1), "Invalid");
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                // 直接调用也必须审计取消；绑定器可能在分派前拒绝取消。/ Direct calls must audit cancellation; the binder may reject before dispatch.
                StringAssert.Contains(await _agent.ReadFile(Put("cancel.txt"), cancellationToken: cts.Token), "Cancelled");
            }
            string audit = File.ReadAllText(AppLog.PathOf(AgentFileService.AuditFile));
            foreach (string status in new[] { "denied", "error", "invalid", "cancelled" }) StringAssert.Contains(audit, "status=" + status);
            StringAssert.Contains(audit, "elapsedMs=");
            Assert.IsFalse(audit.Contains("query-canary"));
            StringAssert.Contains(audit, _root);
            StringAssert.Contains(audit, "path=\"");
            Assert.AreEqual(14, File.ReadAllLines(AppLog.PathOf(AgentFileService.AuditFile)).Length);
        }

        [TestMethod]
        public async Task LegacyTools_ShareGrantsRedactionAndAudit_WithoutFolderBypass()
        {
            Put("plain.txt", "password=legacy-canary\nvisible marker");
            var read = new AIFunctionArguments { ["vs"] = "1", ["path"] = "plain.txt" };
            var scan = new AIFunctionArguments { ["vs"] = "1" };
            StringAssert.Contains(await Invoke("read_vs_file", read), "Access denied");
            StringAssert.Contains(await Invoke("scan_vs_code", scan), "Access denied");
            Grant();
            string result = await Invoke("read_vs_file", read);
            StringAssert.Contains(result, "visible marker"); Assert.IsFalse(result.Contains("legacy-canary"));
            StringAssert.Contains(await Invoke("scan_vs_code", scan), "plain.txt");
            read["folder"] = _data.Path;
            scan["folder"] = _data.Path;
            StringAssert.Contains(await Invoke("read_vs_file", read), "Access denied");
            StringAssert.Contains(await Invoke("scan_vs_code", scan), "Access denied");
            read["folder"] = _root; read["path"] = "nested"; Put("nested\\visible.txt");
            StringAssert.Contains(await Invoke("read_vs_file", read), "visible.txt");
            read["path"] = "..\\workspace\\plain.txt";
            StringAssert.Contains(await Invoke("read_vs_file", read), "Access denied");
            string audit = File.ReadAllText(AppLog.PathOf(AgentFileService.AuditFile));
            StringAssert.Contains(audit, "operation=read_vs_file"); StringAssert.Contains(audit, "operation=scan_vs_code");
        }

        [TestMethod]
        public async Task ChineseCredentialFieldsAndBasicAuth_AreRedacted()
        {
            Grant();
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("example:demo-value"));
            string file = Put("notes.txt", "密码：chinese-canary\n令牌 = token-canary\nBasic " + basic + "\nvisible");
            string result = await Read(file);
            Assert.IsFalse(result.Contains("canary"));
            Assert.IsFalse(result.Contains(basic));
            StringAssert.Contains(result, "visible");
            Assert.IsFalse((await Search("chinese-canary")).Contains("chinese-canary"));
        }

        [TestMethod]
        public async Task EnvironmentVariables_AuthorizeRootsAndResolveFileParameters()
        {
            string variable = "VSMANAGER_FILE_TEST_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(variable, _root);
            try
            {
                Put("plain.txt");
                _settings.AgentFileRoots.Add("%" + variable + "%");
                StringAssert.Contains(await Read("%" + variable + "%\\plain.txt"), "safe marker");
            }
            finally { Environment.SetEnvironmentVariable(variable, null); }
        }

        [TestMethod]
        public void InvalidRegistryPaths_DoNotCreateImplicitCurrentDirectoryGrants()
        {
            _settings.AgentIncludeSolutionRoots = true;
            _host.Solutions.Upsert(new SolutionEntry { Alias = "Relative", Path = "Demo.sln" });
            _host.Solutions.Upsert(new SolutionEntry { Alias = "Wrong type", Path = Path.Combine(_root, "file.txt") });
            var roots = (IEnumerable<string>)typeof(AgentService).GetMethod("FileRoots", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_agent, null);
            Assert.AreEqual(0, roots.Count());
        }

        [TestMethod]
        public async Task LowerUserQuotas_AreRespected_AndPagesMakeProgress()
        {
            Grant();
            _settings.AgentMaxToolText = 1000;
            _settings.AgentMaxFileLines = 10;
            string result = await Read(Put("long.txt", new string('x', 3000) + "\nend"), 1, 1);
            Assert.IsTrue(result.Length <= 1000);
            StringAssert.Contains(result, "1: ");
            StringAssert.Contains(result, "nextStartLine=2");
            result = await Read(Put("lines.txt", string.Join("\n", Enumerable.Range(1, 20).Select(i => "line" + i))));
            StringAssert.Contains(result, "nextStartLine=11");
            result = await Search("xxx");
            Assert.IsTrue(result.Length <= 1000);
            StringAssert.Contains(result, "long.txt:1:");
        }

        [TestMethod]
        public void AuditUnavailable_PreventsFileOperation_AndFailureAfterReadWithholdsContent()
        {
            Grant();
            string path = Put("plain.txt", "unlogged-canary");
            string audit = AppLog.PathOf(AgentFileService.AuditFile);
            Directory.CreateDirectory(audit);
            var service = new AgentFileService(() => _settings.AgentFileRoots, () => _settings);
            bool invoked = false;
            string result = service.Run("read_file", path, default, c => { invoked = true; return service.Read(c, path, 1, 10); });
            Assert.IsFalse(invoked);
            StringAssert.Contains(result, "File audit could not be written");
            Directory.Delete(audit);
            result = service.Run("read_file", path, default, c =>
            {
                string content = service.Read(c, path, 1, 10);
                File.Delete(audit);
                Directory.CreateDirectory(audit);
                return content;
            });
            Assert.IsFalse(result.Contains("unlogged-canary"));
            StringAssert.Contains(result, "File audit could not be written");
        }

        [TestMethod]
        public void LocalCleanupNotice_NeverQueuesFollowUpOrChangesModelHistory()
        {
            _settings.AgentAutoFollowUp = true;
            int changed = 0;
            _agent.Changed += () => changed++;
            var history = (System.Collections.ICollection)typeof(AgentService).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_agent);
            int before = history.Count;
            _agent.ShowLocalNotice("文档清理 / Document cleanup", "未保存，已跳过 / Unsaved, skipped: Demo.cs");
            Assert.AreEqual(before, history.Count);
            var notices = (System.Collections.ICollection)typeof(AgentService).GetField("_notices", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_agent);
            Assert.AreEqual(0, notices.Count);
            Assert.IsFalse(_agent.Running);
            Assert.AreEqual(1, changed);
            StringAssert.Contains(_agent.Transcript.Messages.Last().Parts[0].Text, "Demo.cs");
        }

        [TestMethod]
        public async Task Junctions_AreRejectedAtChildRootAndRootAncestor()
        {
            Grant();
            string target = _data.File("outside"); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "outside.txt"), "junction-canary");
            string link = Path.Combine(_root, "link");
            using (var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec"),
                "/d /c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                if (!process.WaitForExit(5000)) { process.Kill(); Assert.Fail("创建测试联接超时 / Test junction creation timed out"); }
                if (process.ExitCode != 0) Assert.Inconclusive("当前环境不支持联接 / Junction creation unavailable");
            }
            try
            {
                StringAssert.Contains(await Read(Path.Combine(link, "outside.txt")), "Access denied");
                Assert.IsFalse((await Find()).Contains("outside.txt"));
                Assert.IsFalse((await Search("junction-canary")).Contains("junction-canary"));
                _settings.AgentFileRoots.Clear(); _settings.AgentFileRoots.Add(link);
                StringAssert.Contains(await Read(Path.Combine(link, "outside.txt")), "Access denied");
                Directory.CreateDirectory(Path.Combine(target, "child"));
                File.WriteAllText(Path.Combine(target, "child", "plain.txt"), "ancestor-canary");
                _settings.AgentFileRoots.Clear(); _settings.AgentFileRoots.Add(Path.Combine(link, "child"));
                StringAssert.Contains(await Read(Path.Combine(link, "child", "plain.txt")), "Access denied");
            }
            finally { Directory.Delete(link); }
        }

        [TestMethod]
        public async Task HardLinks_AreRejectedEvenInsideGrant()
        {
            Grant(); string target = Put("target.txt", "hardlink-canary"); string link = Path.Combine(_root, "alias.txt");
            if (!CreateHardLink(link, target, IntPtr.Zero)) Assert.Inconclusive("当前环境不支持硬链接 / Hard links unavailable");
            try
            {
                StringAssert.Contains(await Read(link), "Access denied");
                Assert.IsFalse((await Find()).Contains("alias.txt"));
                Assert.IsFalse((await Search("hardlink-canary")).Contains("hardlink-canary"));
            }
            finally { File.Delete(link); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

        private sealed class FileHost : IAgentHost
        {
            public IList<VsInstance> Instances { get; } = new List<VsInstance>();
            public SolutionRegistry Solutions { get; } = new SolutionRegistry();
            public string NameOf(VsInstance v) => "Test VS";
            public string NoteOf(VsInstance v) => "";
            public VsInstance FindOpenSolution(SolutionEntry e) => null;
            public Task<string> SetNote(VsInstance v, string note) => throw new NotSupportedException();
            public Task<ChatTranscript> ReadChat(VsInstance v, int maxMessages) => throw new NotSupportedException();
            public Task<string> QueueTask(VsInstance v, string text) => throw new NotSupportedException();
            public Task<string> ListTasks() => throw new NotSupportedException();
            public Task<string> CancelTask(int id) => throw new NotSupportedException();
            public Task<string> DebugAction(VsInstance v, string action) => throw new NotSupportedException();
            public Task<string> InvokeChatButton(VsInstance v, string automationId, string name) => throw new NotSupportedException();
            public Task<string> Activate(VsInstance v) => throw new NotSupportedException();
            public Task<string> ErrorList(VsInstance v, int max) => throw new NotSupportedException();
            public Task<bool> Confirm(string title, string detail) => throw new NotSupportedException();
            public Task<string> DockPanes() => throw new NotSupportedException();
            public Task<string> ParkTask(SolutionEntry e, string text) => throw new NotSupportedException();
            public Task<string> LaunchSolution(string path) => throw new NotSupportedException();
            public Task<string> CheckCanClose(VsInstance v) => throw new NotSupportedException();
            public Task<string> CloseVs(VsInstance v) => throw new NotSupportedException();
        }
    }
}
