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
        [TestCategory(TestKind.Console)]
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

        private NotebookStore UseNotebook()
        {
            var store = new NotebookStore(_data.File("Notebooks"));
            _agent.NotebookStoreFactory = () => store;
            return store;
        }

        [TestMethod]
        public async Task ListNotes_ReturnsIdAndTitlePath()
        {
            var store = UseNotebook();
            string parent = store.CreatePage("", "计划");
            string child = store.CreatePage(parent, "登录改造", "给 1 号 VS 加表单校验");
            Assert.IsTrue((await Invoke("list_notes", new AIFunctionArguments { ["query"] = "表单" })).Contains(child + "  计划 / 登录改造"));
        }

        [TestMethod]
        public async Task ReadNote_ReturnsBody()
        {
            var store = UseNotebook();
            string id = store.CreatePage("", "登录改造", "note-body-marker");
            Assert.IsTrue((await Invoke("read_note", new AIFunctionArguments { ["page"] = id })).Contains("note-body-marker"));
        }

        [TestMethod]
        public async Task ReadNote_InvalidId_ReturnsError()
        {
            UseNotebook();
            Assert.IsTrue((await Invoke("read_note", new AIFunctionArguments { ["page"] = "..\\x" })).StartsWith("读取笔记失败"));
        }

        [TestMethod]
        public async Task CreateNote_CreatesPageUnderParent_AndNotifiesHost()
        {
            _settings.AgentConfirm = false;
            var store = UseNotebook();
            string parent = store.CreatePage("", "计划");
            string result = await Invoke("create_note", new AIFunctionArguments { ["title"] = "会议记录", ["content"] = "- 结论 A", ["parent"] = parent.ToUpperInvariant() });
            StringAssert.Contains(result, "计划 / 会议记录");
            string id = store.FindChild(parent, "会议记录");
            Assert.IsNotNull(id);
            Assert.AreEqual("- 结论 A", store.Read(id).Text);
            CollectionAssert.AreEqual(new[] { id }, _host.NotebookChanges);
            StringAssert.StartsWith(await Invoke("create_note", new AIFunctionArguments { ["title"] = "会议记录", ["parent"] = parent }), "新建笔记失败");
            StringAssert.StartsWith(await Invoke("create_note", new AIFunctionArguments { ["title"] = " " }), "请提供页面标题");
        }

        [TestMethod]
        public async Task AppendToNote_KeepsExistingBody()
        {
            _settings.AgentConfirm = false;
            var store = UseNotebook();
            string id = store.CreatePage("", "待办", "# 待办\n\n- [ ] 旧事项\n\n");
            StringAssert.Contains(await Invoke("append_to_note", new AIFunctionArguments { ["page"] = id, ["content"] = "- [ ] 新事项" }), "Appended");
            Assert.AreEqual("# 待办\n\n- [ ] 旧事项\n\n- [ ] 新事项\n", store.Read(id).Text);
            string empty = store.CreatePage("", "空白", "");
            await Invoke("append_to_note", new AIFunctionArguments { ["page"] = empty, ["content"] = "first" });
            Assert.AreEqual("first\n", store.Read(empty).Text);
            StringAssert.StartsWith(await Invoke("append_to_note", new AIFunctionArguments { ["page"] = "..\\x", ["content"] = "x" }), "写入笔记失败");
            Assert.AreEqual(2, _host.NotebookChanges.Count);
        }

        [TestMethod]
        public async Task UpdateNote_ReplacesBody()
        {
            _settings.AgentConfirm = false;
            var store = UseNotebook();
            string id = store.CreatePage("", "草稿", "old");
            StringAssert.Contains(await Invoke("update_note", new AIFunctionArguments { ["page"] = id, ["content"] = "# 新版\n\n正文" }), "Rewrote");
            Assert.AreEqual("# 新版\n\n正文", store.Read(id).Text);
            StringAssert.Contains(await Invoke("update_note", new AIFunctionArguments { ["page"] = id, ["content"] = new string('x', AgentService.MaxNoteWriteChars + 1) }), "too long");
        }

        [TestMethod]
        public async Task NoteWrites_HonorAgentConfirm()
        {
            _settings.AgentConfirm = true;
            var store = UseNotebook();
            string id = store.CreatePage("", "草稿", "old");
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("update_note", new AIFunctionArguments { ["page"] = id, ["content"] = "new" }), "declined");
            StringAssert.Contains(await Invoke("append_to_note", new AIFunctionArguments { ["page"] = id, ["content"] = "new" }), "declined");
            StringAssert.Contains(await Invoke("create_note", new AIFunctionArguments { ["title"] = "另一篇" }), "declined");
            Assert.AreEqual("old", store.Read(id).Text);
            Assert.IsNull(store.FindChild("", "另一篇"));
            Assert.AreEqual(3, _host.Confirmations);
            Assert.AreEqual(0, _host.NotebookChanges.Count);
            _host.ConfirmResult = true;
            await Invoke("update_note", new AIFunctionArguments { ["page"] = id, ["content"] = "new" });
            Assert.AreEqual("new", store.Read(id).Text);
        }

        [TestMethod]
        public async Task AddNoteCard_AppendsCardBlock_AndHonorsConfirm()
        {
            _settings.AgentConfirm = true;
            var store = UseNotebook();
            string id = store.CreatePage("", "任务记录", "# 任务记录\n");
            var args = new AIFunctionArguments { ["page"] = id, ["title"] = "→ Demo", ["text"] = "实现导出", ["status"] = "done", ["duration"] = "16m57s", ["meta"] = "#108 · AI", ["time"] = "09:48" };
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("add_note_card", args), "declined");
            Assert.AreEqual("# 任务记录\n", store.Read(id).Text);
            _host.ConfirmResult = true;
            StringAssert.Contains(await Invoke("add_note_card", args), "Added card");
            string body = store.Read(id).Text;
            StringAssert.StartsWith(body, "# 任务记录\n\n```card\nstatus: done\n");
            var card = NoteCard.Parse(body.Substring(body.IndexOf("status:", StringComparison.Ordinal)).Replace("```", ""));
            Assert.AreEqual("→ Demo", card.Title);
            Assert.AreEqual("✓ 已完成 · 16m57s", card.PillText);
            CollectionAssert.AreEqual(new[] { id }, _host.NotebookChanges);
            StringAssert.Contains(await Invoke("add_note_card", new AIFunctionArguments { ["page"] = id, ["title"] = " " }), "Provide a title");
        }

        [TestMethod]
        public async Task AddNoteCardStyles_AppendsComparison_AndHonorsConfirm()
        {
            _settings.AgentConfirm = true;
            var store = UseNotebook();
            string id = store.CreatePage("", "样式", "# 样式\n");
            var args = new AIFunctionArguments { ["page"] = id, ["title"] = "镇墩稳定计算", ["status"] = "waiting", ["meta"] = "01", ["styles"] = "compact, numbered" };
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("add_note_card_styles", args), "declined");
            Assert.AreEqual("# 样式\n", store.Read(id).Text);
            _host.ConfirmResult = true;
            StringAssert.Contains(await Invoke("add_note_card_styles", args), "compact, numbered");
            string body = store.Read(id).Text;
            StringAssert.StartsWith(body, "# 样式\n\n### 卡片样式对比\n\n**紧凑型**");
            StringAssert.Contains(body, "style: compact\n");
            StringAssert.Contains(body, "style: numbered\n");
            string card = await Invoke("add_note_card", new AIFunctionArguments { ["page"] = id, ["title"] = "x", ["style"] = "带附注型" });
            StringAssert.Contains(card, "Added card");
            StringAssert.Contains(store.Read(id).Text, "status: info\nstyle: noted\ntitle: x");
            StringAssert.Contains(await Invoke("format_note_card", new AIFunctionArguments { ["title"] = "x", ["style"] = "accent" }), "style: accent");
            foreach (bool en in new[] { false, true })
                StringAssert.Contains(Prompts.AgentSystem(en, DateTime.Now, "", null), "add_note_card_styles");
        }

        [TestMethod]
        public async Task FormatNoteCard_ReturnsMarkdownWithoutWriting()
        {
            var store = UseNotebook();
            string md = await Invoke("format_note_card", new AIFunctionArguments { ["title"] = "→ Demo", ["status"] = "失败", ["note"] = "编译失败" });
            StringAssert.StartsWith(md, "```card\nstatus: failed\n");
            Assert.AreEqual(0, _host.NotebookChanges.Count);
            foreach (bool en in new[] { false, true })
            {
                StringAssert.Contains(Prompts.AgentSystem(en, DateTime.Now, "", null), "add_note_card");
                StringAssert.Contains(Prompts.AgentSystem(en, DateTime.Now, "", null), "format_note_card");
            }
        }

        [TestMethod]
        public void NotebookSkill_IsInSystemPromptInBothLanguages()
        {
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                StringAssert.Contains(prompt, "create_note");
                StringAssert.Contains(prompt, "append_to_note");
                StringAssert.Contains(prompt, "update_note");
            }
        }

        [TestMethod]
        public void Prompts_WriteSupplementaryPromptPageWithNoteTools_NotSendTask()
        {
            StringAssert.Contains(Prompts.AgentSystem(false, DateTime.Now, "", null), "根目录的「" + NotebookAgentPrompt.PageTitle + "」是笔记本页面");
            StringAssert.Contains(Prompts.AgentSystem(false, DateTime.Now, "", null), "绝不要为此用 send_task 发布任务");
            StringAssert.Contains(Prompts.AgentSystem(true, DateTime.Now, "", null), "The root page \"" + NotebookAgentPrompt.PageTitle + "\" is a notebook page");
            StringAssert.Contains(Prompts.AgentSystem(true, DateTime.Now, "", null), "never use send_task or code changes for this");
        }

        [TestMethod]
        public void TestChecklistLanguage_FollowsPromptLanguage()
        {
            StringAssert.Contains(Prompts.AgentSystem(false, DateTime.Now, "", null), "测试清单或未验证项时一律用中文输出");
            StringAssert.Contains(Prompts.AgentSystem(true, DateTime.Now, "", null), "always write them in English");
        }

        [TestMethod]
        public async Task EditTaskResult_HonorsAgentConfirm_AndCallsHost()
        {
            _settings.AgentConfirm = true;
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("edit_task_result", new AIFunctionArguments { ["id"] = 3, ["text"] = "- [ ] 中文" }), "declined");
            Assert.AreEqual(0, _host.ResultEdits.Count);
            _host.ConfirmResult = true;
            StringAssert.Contains(await Invoke("edit_task_result", new AIFunctionArguments { ["id"] = 3, ["text"] = "- [ ] 中文" }), "edited");
            CollectionAssert.AreEqual(new[] { "3:- [ ] 中文" }, _host.ResultEdits);
            StringAssert.Contains(await Invoke("edit_task_result", new AIFunctionArguments { ["id"] = 3, ["text"] = " " }), "empty");
            StringAssert.Contains(await Invoke("edit_task_result", new AIFunctionArguments { ["id"] = 3, ["text"] = new string('x', TaskQueue.MaxResultChars + 1) }), "too long");
            Assert.AreEqual(2, _host.Confirmations);
            foreach (bool en in new[] { false, true })
                StringAssert.Contains(Prompts.AgentSystem(en, DateTime.Now, "", null), "edit_task_result");
        }

        [TestMethod]
        public async Task StartTaskWorkflow_HonorsAgentConfirm_AndSkipsWhenStarted()
        {
            _settings.AgentConfirm = true;
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("start_task_workflow", new AIFunctionArguments()), "declined");
            Assert.AreEqual(0, _host.WorkflowStarts);
            _host.ConfirmResult = true;
            StringAssert.Contains(await Invoke("start_task_workflow", new AIFunctionArguments()), "started-by-host");
            Assert.AreEqual(1, _host.WorkflowStarts);
            StringAssert.Contains(await Invoke("start_task_workflow", new AIFunctionArguments()), "already started");
            Assert.AreEqual(1, _host.WorkflowStarts);
            Assert.AreEqual(2, _host.Confirmations);
            foreach (bool en in new[] { false, true })
                StringAssert.Contains(Prompts.AgentSystem(en, DateTime.Now, "", null), "start_task_workflow");
        }

        [TestMethod]
        public async Task RestartForTesting_ValidatesPrebuildsThenSchedules()
        {
            StringAssert.Contains(await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = " ", ["taskId"] = 0 }), "empty");
            _host.RestartBlocked = "not under a debugger";
            StringAssert.Contains(await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = "- [ ] a", ["taskId"] = 5 }), "not under a debugger");
            Assert.AreEqual(0, _host.Prebuilds);
            _host.RestartBlocked = null;
            _host.Build = new SelfRestartBuild { Ok = false, Summary = "CS1002 missing semicolon" };
            string failed = await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = "- [ ] a", ["taskId"] = 5 });
            StringAssert.Contains(failed, "CS1002");
            StringAssert.Contains(failed, "restart cancelled");
            Assert.AreEqual(0, _host.Scheduled.Count);
            _settings.AgentConfirm = true;
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = "- [ ] a", ["taskId"] = 5 }), "declined");
            Assert.AreEqual(1, _host.Prebuilds);
            _host.ConfirmResult = true;
            _host.Build = new SelfRestartBuild { Ok = true, Summary = "build ok" };
            StringAssert.Contains(await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = " - [ ] a ", ["taskId"] = 5 }), "scheduled");
            CollectionAssert.AreEqual(new[] { "- [ ] a|5|build ok" }, _host.Scheduled);
            foreach (bool en in new[] { false, true })
            {
                StringAssert.Contains(Prompts.AgentSystem(en, DateTime.Now, "", null), "restart_vsmanager_for_testing");
                StringAssert.Contains(Prompts.AgentSystem(en, DateTime.Now, "", null), "mark_test_item");
            }
        }

        [TestMethod]
        public async Task SelfIteration_StartsCountsRoundsAndStops()
        {
            _settings.AgentAutoFollowUp = false;
            StringAssert.Contains(await Invoke("start_self_iteration", new AIFunctionArguments { ["goal"] = "fix layout", ["maxRounds"] = 2 }), "auto follow-up");
            _settings.AgentAutoFollowUp = true;
            StringAssert.Contains(await Invoke("start_self_iteration", new AIFunctionArguments { ["goal"] = " ", ["maxRounds"] = 2 }), "goal");
            _host.RestartBlocked = "not under a debugger";
            StringAssert.Contains(await Invoke("start_self_iteration", new AIFunctionArguments { ["goal"] = "fix layout", ["maxRounds"] = 2 }), "not under a debugger");
            Assert.IsNull(SelfIteration.Load(DateTime.UtcNow));
            _host.RestartBlocked = null;
            _settings.AgentConfirm = true;
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("start_self_iteration", new AIFunctionArguments { ["goal"] = "fix layout", ["maxRounds"] = 2 }), "declined");
            _host.ConfirmResult = true;
            StringAssert.Contains(await Invoke("start_self_iteration", new AIFunctionArguments { ["goal"] = "fix layout", ["maxRounds"] = 2 }), "max 2 rounds");
            StringAssert.Contains(await Invoke("start_self_iteration", new AIFunctionArguments { ["goal"] = "other", ["maxRounds"] = 2 }), "already active");
            _settings.AgentConfirm = false;

            StringAssert.Contains(await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = "- [ ] a", ["taskId"] = 5 }), "round 1/2");
            StringAssert.Contains(await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = "- [ ] a", ["taskId"] = 5 }), "round 2/2");
            int prebuilds = _host.Prebuilds;
            StringAssert.Contains(await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = "- [ ] a", ["taskId"] = 5 }), "round limit");
            Assert.AreEqual(prebuilds, _host.Prebuilds);
            Assert.AreEqual(2, _host.Scheduled.Count);

            var state = SelfIteration.Load(DateTime.UtcNow);
            Assert.AreEqual(2, state.Round);
            string notice = SelfRestart.NoticeContent(new SelfRestartHandoff { TestPlan = "- [ ] a", TaskId = 5 }, new SelfRestartOutcome(), state);
            StringAssert.Contains(notice, "2/2");
            StringAssert.Contains(notice, "stop_self_iteration");
            Assert.IsFalse(SelfIteration.NoticeBlock(state).Contains("send_task"), "the last round must not ask for another fix");
            state.Round = 1;
            StringAssert.Contains(SelfIteration.NoticeBlock(state), "send_task");
            Assert.IsFalse(SelfRestart.NoticeContent(new SelfRestartHandoff { TestPlan = "- [ ] a" }, new SelfRestartOutcome()).Contains("Self-iteration"));

            StringAssert.Contains(await Invoke("stop_self_iteration", new AIFunctionArguments { ["summary"] = "all passed" }), "2/2");
            Assert.IsNull(SelfIteration.Load(DateTime.UtcNow));
            StringAssert.Contains(await Invoke("stop_self_iteration", new AIFunctionArguments()), "No self-iteration");
            StringAssert.Contains(await Invoke("restart_vsmanager_for_testing", new AIFunctionArguments { ["testPlan"] = "- [ ] a", ["taskId"] = 5 }), "scheduled");
            foreach (bool en in new[] { false, true })
                StringAssert.Contains(Prompts.AgentSystem(en, DateTime.Now, "", null), "start_self_iteration");
        }

        [TestMethod]
        public async Task SkillGapLoop_StartsDedicatedIterationOncePerItemAndIsDocumented()
        {
            var args = new Func<AIFunctionArguments>(() => new AIFunctionArguments { ["taskId"] = 5, ["item"] = 1, ["skill"] = "list_tray_menu: reads the tray menu items", ["reason"] = "no tool reads menus" });
            _settings.AgentAutoFollowUp = false;
            StringAssert.Contains(await Invoke("start_skill_gap_loop", args()), "auto follow-up");
            _settings.AgentAutoFollowUp = true;
            StringAssert.Contains(await Invoke("start_skill_gap_loop", new AIFunctionArguments { ["taskId"] = 5, ["item"] = 1, ["skill"] = " ", ["reason"] = "x" }), "Describe the skill");
            StringAssert.Contains(await Invoke("start_skill_gap_loop", new AIFunctionArguments { ["taskId"] = 9, ["item"] = 1, ["skill"] = "s", ["reason"] = "r" }), "No task #9");
            _settings.AgentConfirm = true;
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("start_skill_gap_loop", args()), "declined");
            Assert.IsNull(_host.GapItem.Skill, "拒绝后撤销登记 / Declining releases the claim");
            Assert.IsNull(SelfIteration.Load(DateTime.UtcNow));
            _host.ConfirmResult = true;
            string ok = await Invoke("start_skill_gap_loop", args());
            _settings.AgentConfirm = false;
            StringAssert.Contains(ok, "Skill-gap loop started (max 3 rounds)");
            var state = SelfIteration.Load(DateTime.UtcNow);
            Assert.IsTrue(state.IsSkillGap);
            Assert.AreEqual(5, state.TargetTaskId);
            Assert.AreEqual(1, state.TargetItem);
            Assert.IsTrue(TaskTestChecklist.CanAiCheck(_host.GapItem), "目标项可由 AI 用新 skill 勾选 / The target item can be checked with the new skill");
            StringAssert.Contains(await Invoke("start_self_iteration", new AIFunctionArguments { ["goal"] = "g" }), "already active");
            StringAssert.Contains(await Invoke("start_skill_gap_loop", args()), "already active");
            string block = SelfIteration.NoticeBlock(state);
            StringAssert.Contains(block, "Skill-gap loop");
            StringAssert.Contains(block, "item 1 of task #5");
            StringAssert.Contains(block, "original task's problem");
            await Invoke("stop_self_iteration", new AIFunctionArguments());
            StringAssert.Contains(await Invoke("start_skill_gap_loop", args()), "one per item");
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                StringAssert.Contains(prompt, "start_skill_gap_loop");
                StringAssert.Contains(prompt, en ? "Skill-gap loop vs self-iteration" : "补 skill 闭环与自迭代的关系");
                StringAssert.Contains(prompt, en ? "no user reminder needed" : "不需要用户提醒");
            }
        }

        [TestMethod]
        public void Prompt_TellsCadProjectsNotToStartCadThemselves()
        {
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                StringAssert.Contains(prompt, en ? "Do not start CAD to test it yourself" : "不需要你启动 CAD 自行测试");
                StringAssert.Contains(prompt, en ? "ai.agent will run the CAD tests afterwards" : "CAD 中的测试由 ai.agent 后续完成");
            }
        }

        [TestMethod]
        public void Prompt_RestartIgnoresTasksInOtherVs()
        {
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                StringAssert.Contains(prompt, en ? "tasks running in other VS instances are ignored and never waited for" : "其他 VS 中正在执行的任务一律忽略、不必等它们完成");
                StringAssert.Contains(prompt, en ? "re-delivered automatically afterwards" : "期间到达的完成通知在重启后自动补发");
                Assert.IsFalse(prompt.Contains(en ? "Never restart while a task is still running." : "任务仍在执行时不要重启。"));
            }
        }

        [TestMethod]
        public void Prompt_DecidesAutonomouslyWhenUserAway()
        {
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                StringAssert.Contains(prompt, en ? "18. Keep going when the user is away" : "18. 用户不在时自主推进");
                StringAssert.Contains(prompt, en ? "do not stop to ask and wait" : "不要停下来提问等待");
                StringAssert.Contains(prompt, en ? "what the user needs to provide" : "需要用户提供什么");
                StringAssert.Contains(prompt, en ? "acting on your own never counts as authorization" : "自主推进不构成授权");
            }
        }

        [TestMethod]
        public void CarriedNotices_RoundTripOnceAndSkipStale()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vsm-notices-" + Guid.NewGuid().ToString("N") + ".json");
            var now = DateTime.UtcNow;
            try
            {
                Assert.IsNull(CarriedNotices.Save(new[]
                {
                    new CarriedNotice { Display = "d1", Content = "c1", Scope = "ProjA" },
                    new CarriedNotice { Display = "d2", Content = "", Scope = null },
                    new CarriedNotice { Display = "d3", Content = "c3", Scope = null }
                }, now, path));
                var list = CarriedNotices.Take(now.AddMinutes(1), out string error, path);
                Assert.IsNull(error);
                Assert.AreEqual(2, list.Count);
                Assert.AreEqual("c1", list[0].Content);
                Assert.AreEqual("ProjA", list[0].Scope);
                Assert.IsNull(list[1].Scope);
                Assert.IsFalse(System.IO.File.Exists(path));
                Assert.AreEqual(0, CarriedNotices.Take(now, out error, path).Count);

                Assert.IsNull(CarriedNotices.Save(new[] { new CarriedNotice { Display = "d", Content = "c" } }, now, path));
                Assert.AreEqual(0, CarriedNotices.Take(now + CarriedNotices.MaxAge + TimeSpan.FromMinutes(1), out error, path).Count);
                Assert.IsFalse(System.IO.File.Exists(path));

                System.IO.File.WriteAllText(path, "x");
                Assert.IsNull(CarriedNotices.Save(new CarriedNotice[0], now, path));
                Assert.IsFalse(System.IO.File.Exists(path));
            }
            finally { CarriedNotices.Discard(path); }
        }

        [TestMethod]
        public void SelfIteration_ClampsRoundsAndExpires()
        {
            Assert.AreEqual(SelfIteration.DefaultRounds, SelfIteration.ClampRounds(0));
            Assert.AreEqual(SelfIteration.MaxRoundsLimit, SelfIteration.ClampRounds(99));
            Assert.IsNull(SelfIteration.Save(new SelfIterationState { Goal = "g", MaxRounds = 3, StartedUtc = DateTime.UtcNow.AddHours(-30) }));
            Assert.IsNull(SelfIteration.Load(DateTime.UtcNow));
            Assert.IsFalse(System.IO.File.Exists(SelfIteration.FilePath));
        }

        [TestMethod]
        public async Task MarkTestItem_RequiresEvidence_HonorsConfirm()
        {
            StringAssert.Contains(await Invoke("mark_test_item", new AIFunctionArguments { ["taskId"] = 5, ["item"] = 1, ["passed"] = true, ["evidence"] = " " }), "evidence");
            StringAssert.Contains(await Invoke("mark_test_item", new AIFunctionArguments { ["taskId"] = 5, ["item"] = 0, ["passed"] = true, ["evidence"] = "list_vs ok" }), "from 1");
            _settings.AgentConfirm = true;
            _host.ConfirmResult = false;
            StringAssert.Contains(await Invoke("mark_test_item", new AIFunctionArguments { ["taskId"] = 5, ["item"] = 1, ["passed"] = true, ["evidence"] = "list_vs ok" }), "declined");
            _host.ConfirmResult = true;
            StringAssert.Contains(await Invoke("mark_test_item", new AIFunctionArguments { ["taskId"] = 5, ["item"] = 2, ["passed"] = true, ["evidence"] = "list_vs ok" }), "marked");
            CollectionAssert.AreEqual(new[] { "5:2:True:list_vs ok" }, _host.Marks);
        }

        [TestMethod]
        public async Task ListTestChecklists_ReturnsHostTextAndIsInPrompt()
        {
            StringAssert.Contains(await Invoke("list_test_checklists", new AIFunctionArguments { ["taskId"] = 0 }), "checklists:0");
            StringAssert.Contains(await Invoke("list_test_checklists", new AIFunctionArguments { ["taskId"] = 9 }), "checklists:9");
            Assert.IsTrue(ToolClaimCheck.QueueReadTools.Contains("list_test_checklists"));
            Assert.IsFalse(ToolClaimCheck.EnqueueTools.Contains("list_test_checklists"));
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                StringAssert.Contains(prompt, "list_test_checklists");
                StringAssert.Contains(prompt, en ? "Why you must not publish a task for this" : "为什么不能自己发布这类任务");
            }
        }

        [TestMethod]
        public async Task UiProbeTools_UseHostAndAreDocumentedInPrompt()
        {
            StringAssert.Contains(await Invoke("get_window_state", new AIFunctionArguments()), "window-state");
            string handoff = await Invoke("read_restart_handoff", new AIFunctionArguments());
            StringAssert.Contains(handoff, "consumed-at-startup");
            StringAssert.Contains(handoff, "【restart-handoff.json】");
            StringAssert.Contains(await Invoke("get_foreground_window", new AIFunctionArguments()), "/");
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                foreach (string tool in new[] { "get_window_state", "get_foreground_window", "list_tray_icons", "read_restart_handoff" })
                    StringAssert.Contains(prompt, tool);
            }
            StringAssert.Contains(SelfRestart.NoticeContent(new SelfRestartHandoff { TestPlan = "- [ ] a" }, new SelfRestartOutcome()), "list_tray_icons");
        }

        [TestMethod]
        public async Task PrepareRestartScenario_ValidatesAndReachesHostAndIsDocumented()
        {
            string ok = await Invoke("prepare_restart_scenario", new AIFunctionArguments { ["vs"] = "Demo", ["window"] = "最大化", ["foreground"] = "other" });
            StringAssert.Contains(ok, "scenario:");
            StringAssert.Contains(ok, "page=vs（Demo）");
            StringAssert.Contains(ok, "window=maximize");
            StringAssert.Contains(ok, "foreground=other");
            string bad = await Invoke("prepare_restart_scenario", new AIFunctionArguments { ["window"] = "sideways" });
            StringAssert.Contains(bad, "Invalid scenario");
            StringAssert.Contains(await Invoke("prepare_restart_scenario", new AIFunctionArguments()), "at least one");
            foreach (bool en in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(en, DateTime.Now, "", null);
                StringAssert.Contains(prompt, "prepare_restart_scenario");
                StringAssert.Contains(prompt, en ? "Verifiability re-judging" : "可验证性重判");
            }
            string notice = SelfRestart.NoticeContent(new SelfRestartHandoff { TestPlan = "- [ ] a", Scenario = "前台=其他应用 / foreground=other" }, new SelfRestartOutcome());
            StringAssert.Contains(notice, "Pre-restart scenario: 前台=其他应用");
        }

        private sealed class FileHost : IAgentHost, IAgentNotebookHost, IAgentTaskResultHost, IAgentWorkflowHost, IAgentSelfRestartHost, IAgentChecklistHost, IAgentUiProbeHost, IAgentScenarioHost, IAgentSkillGapHost
        {
            public TaskTestItem GapItem = new TaskTestItem { Text = "设置页的保存路径显示为默认值", By = TaskTestChecklist.ByUser };
            public Task<Tuple<string, string>> ClaimSkillGap(int taskId, int item, string skill)
            {
                if (taskId != 5 || item != 1) return Task.FromResult(Tuple.Create<string, string>(null, "No task #" + taskId));
                string refusal = TaskTestChecklist.SkillGapRefusal(GapItem);
                if (refusal != null) return Task.FromResult(Tuple.Create<string, string>(null, refusal));
                GapItem.Skill = skill;
                return Task.FromResult(Tuple.Create<string, string>(GapItem.Text, null));
            }
            public Task ReleaseSkillGap(int taskId, int item) { GapItem.Skill = null; return Task.FromResult(true); }
            public RestartScenario Scenario;
            public Task<string> PrepareRestartScenario(RestartScenario s) { Scenario = s; return Task.FromResult("scenario:" + s.Describe()); }
            public Task<string> GetWindowState() => Task.FromResult("window-state");
            public Task<string> RestartConsumption() => Task.FromResult("consumed-at-startup");
            public Task<UiSelfInfo> SelfInfo() => Task.FromResult(new UiSelfInfo { TrayText = "t", TrayVisible = true });
            public Task<string> ListTestChecklists(int taskId) => Task.FromResult("checklists:" + taskId);
            internal string RestartBlocked;
            internal SelfRestartBuild Build = new SelfRestartBuild { Ok = true, Summary = "ok" };
            internal int Prebuilds;
            internal readonly List<string> Scheduled = new List<string>();
            internal readonly List<string> Marks = new List<string>();
            public Task<string> CheckSelfRestart() => Task.FromResult(RestartBlocked);
            public Task<SelfRestartBuild> PrebuildSelf(CancellationToken cancellationToken) { Prebuilds++; return Task.FromResult(Build); }
            public Task<string> ScheduleSelfRestart(string testPlan, int taskId, string scope, string buildSummary) { Scheduled.Add(testPlan + "|" + taskId + "|" + buildSummary); return Task.FromResult("scheduled"); }
            public Task<string> SetTestItem(int taskId, int item, bool passed, string evidence) { Marks.Add(taskId + ":" + item + ":" + passed + ":" + evidence); return Task.FromResult("marked"); }
            internal int WorkflowStarts;
            public bool WorkflowStarted { get; private set; }
            public Task<string> StartWorkflow() { WorkflowStarts++; WorkflowStarted = true; return Task.FromResult("started-by-host"); }
            internal readonly List<string> ResultEdits = new List<string>();
            public Task<string> EditTaskResult(int id, string text) { ResultEdits.Add(id + ":" + text); return Task.FromResult("edited"); }
            internal readonly List<string> NotebookChanges = new List<string>();
            internal bool? ConfirmResult;
            internal int Confirmations;
            public void NotebookChanged(string pageId) => NotebookChanges.Add(pageId);
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
            public Task<bool> Confirm(string title, string detail)
            {
                if (!ConfirmResult.HasValue) throw new NotSupportedException();
                Confirmations++;
                return Task.FromResult(ConfirmResult.Value);
            }
            public Task<string> DockPanes() => throw new NotSupportedException();
            public Task<string> ArrangeCopilotPanes(IList<VsInstance> targets, int screen, PaneArrangement arrangement, bool minimize) => throw new NotSupportedException();
            public Task<string> RestoreCopilotLayout() => throw new NotSupportedException();
            public Task<string> ParkTask(SolutionEntry e, string text) => throw new NotSupportedException();
            public Task<string> LaunchSolution(string path) => throw new NotSupportedException();
            public Task<string> CheckCanClose(VsInstance v) => throw new NotSupportedException();
            public Task<string> CloseVs(VsInstance v) => throw new NotSupportedException();
        }
    }
}
