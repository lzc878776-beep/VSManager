using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VSManager.AiHost;
using VSManager.CadAgent;

namespace VSManager.Tests
{
    /// <summary>
    /// CAD 动作执行器：协议、适配包、ai.exe 调度、VSManager 中转与 AI 工具。
    /// CAD action executor: protocol, adapters, ai.exe dispatch, the VSManager broker and the AI tool.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class CadActionTests
    {
        private TempDataFolder _data;
        private string _adapters;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _adapters = _data.File("adapters");
            Directory.CreateDirectory(_adapters);
            CadAdapterStore.RootsProvider = () => new[] { _adapters };
        }

        [TestCleanup]
        public void Cleanup()
        {
            CadAdapterStore.RootsProvider = null;
            _data.Dispose();
        }

        private CadAdapter WriteAdapter(string name, string json)
        {
            string dir = Path.Combine(_adapters, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, CadAdapter.FileName), json, new UTF8Encoding(true));
            return CadAdapter.Load(Path.Combine(dir, CadAdapter.FileName));
        }

        private const string DemoAdapter = "{\"name\":\"Demo\",\"match\":[\"Demo.Plugin*\"],\"host\":\"AutoCAD\",\"commands\":{\"make\":\"DEMOMAKE\",\"regen\":\"_.REGEN\"}}";

        // ---------------- 协议 / Protocol ----------------

        [TestMethod]
        public void Protocol_RoundTripsUnifiedInputAndOutput()
        {
            var req = new CadActionRequest { Vs = "1", TimeoutMs = 5000, Action = "runCommand", Args = new Dictionary<string, string> { ["command"] = "make" } };
            var back = CadJson.Deserialize<CadActionRequest>(CadJson.Serialize(req));
            Assert.AreEqual("1", back.Vs);
            Assert.AreEqual(5000, back.TimeoutMs);
            Assert.AreEqual("make", back.Arg("COMMAND"));

            var res = CadActionResult.Success("done", new CadArtifact { Kind = CadArtifact.Log, Name = "a.log", Content = "中文 line" });
            res.DurationMs = 12;
            string json = CadJson.Serialize(res);
            foreach (var key in new[] { "\"ok\":true", "\"message\":", "\"artifacts\":", "\"durationMs\":12" }) StringAssert.Contains(json, key);
            var r2 = CadJson.Deserialize<CadActionResult>(json);
            Assert.AreEqual("中文 line", r2.Artifacts.Single().Content);
            StringAssert.Contains(CadJson.Serialize(CadActionResult.Fail(CadErrors.Timeout, "x")), "\"errorCode\":\"TIMEOUT\"");
        }

        [TestMethod]
        public void Protocol_TimeoutDefaultsAndClamps_ActionsNormalize()
        {
            Assert.AreEqual(60000, new CadActionRequest().EffectiveTimeoutMs);
            Assert.AreEqual(1000, new CadActionRequest { TimeoutMs = 10 }.EffectiveTimeoutMs);
            Assert.AreEqual(600000, new CadActionRequest { TimeoutMs = int.MaxValue }.EffectiveTimeoutMs);
            Assert.AreEqual(8, CadActions.All.Count);
            Assert.AreEqual(CadActions.GetEntityCount, CadActions.Normalize(" GETENTITYCOUNT "));
            Assert.IsNull(CadActions.Normalize("shell"));
            Assert.IsTrue(CadErrors.IsRetryable(CadErrors.Transport));
            Assert.IsFalse(CadErrors.IsRetryable(CadErrors.Timeout));
            Assert.IsFalse(CadErrors.IsRetryable(CadErrors.ActionFailed));
            Assert.AreEqual(1, new CadSequence().EffectiveRetries);
        }

        [TestMethod]
        public void Protocol_ParsesArrayOrObject()
        {
            var a = CadJson.ParseActions("[{\"action\":\"openDrawing\"},{\"action\":\"screenshot\",\"timeoutMs\":3000}]");
            Assert.AreEqual(2, a.Count);
            Assert.AreEqual(3000, a[1].TimeoutMs);
            var b = CadJson.ParseActions("\uFEFF{\"actions\":[{\"action\":\"getLog\",\"args\":{\"lines\":\"50\"}}]}");
            Assert.AreEqual(50, b.Single().Number("lines", 0));
        }

        // ---------------- 适配包 / Adapters ----------------

        [TestMethod]
        public void Adapter_MatchesSolutionOrStem_SkipsTemplates_LocalWins()
        {
            WriteAdapter("Demo", DemoAdapter);
            WriteAdapter("_template", "{\"name\":\"Template\",\"match\":[\"*\"]}");
            var other = _data.File("other");
            Directory.CreateDirectory(Path.Combine(other, "Demo"));
            File.WriteAllText(Path.Combine(other, "Demo", CadAdapter.FileName), "{\"name\":\"Demo\",\"match\":[\"Nothing\"]}");

            var hit = CadAdapterStore.Match(new[] { @"C:\Work\Demo.Plugin.sln" });
            Assert.AreEqual("Demo", hit?.Name);
            Assert.IsTrue(hit.Matches("demo.plugin.tests.csproj"));
            Assert.IsNull(CadAdapterStore.Match(new[] { @"C:\Work\Pipe.sln" }));
            Assert.AreEqual(1, CadAdapterStore.LoadAll().Count, "_template must be skipped");
            // 后面的根目录优先。/ Later roots win.
            Assert.IsNull(CadAdapterStore.Match(new[] { "Demo.Plugin.sln" }, new[] { _adapters, other }));
        }

        [TestMethod]
        public void Adapter_CommandMap_RejectsUnlistedAndMultiline()
        {
            var a = WriteAdapter("Demo", DemoAdapter);
            Assert.AreEqual("DEMOMAKE", a.ResolveCommand("MAKE", out _));
            Assert.AreEqual("_.REGEN", a.ResolveCommand("_.regen", out _));
            Assert.IsNull(a.ResolveCommand("SHELL", out string err));
            StringAssert.Contains(err, "make");
            Assert.IsNull(a.ResolveCommand("make\nSHELL", out _));
            Assert.IsNull(a.ResolveCommand("", out _));
            a.AllowRawCommands = true;
            Assert.AreEqual("LINE", a.ResolveCommand("LINE", out _));
        }

        [TestMethod]
        public void Adapter_ResolvesNewestLogWithEnvironmentVariables()
        {
            string logs = _data.File("logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "app-1.log"), "old");
            File.SetLastWriteTimeUtc(Path.Combine(logs, "app-1.log"), DateTime.UtcNow.AddHours(-2));
            File.WriteAllText(Path.Combine(logs, "app-2.log"), "new");
            Environment.SetEnvironmentVariable("VSM_TEST_LOGS", logs);
            try
            {
                var a = new CadAdapter { Name = "x", Logs = new List<string> { "%VSM_TEST_LOGS%\\app-*.log", "%VSM_TEST_LOGS%\\missing.log" } };
                CollectionAssert.AreEqual(new[] { Path.Combine(logs, "app-2.log") }, a.ResolveLogs());
            }
            finally { Environment.SetEnvironmentVariable("VSM_TEST_LOGS", null); }
        }

        [TestMethod]
        public void RepoTemplate_IsValidAndBilingual()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adapters", "_template");
            if (!Directory.Exists(dir)) Assert.Inconclusive("template not copied to the test output");
            var a = CadAdapter.Load(Path.Combine(dir, CadAdapter.FileName));
            Assert.IsFalse(string.IsNullOrWhiteSpace(a.Name));
            string skill = File.ReadAllText(Path.Combine(dir, "SKILL.md"));
            foreach (var action in CadActions.All) StringAssert.Contains(skill, action);
            StringAssert.Contains(skill, "适配包");
            StringAssert.Contains(skill, "adapter");
        }

        // ---------------- ai.exe 调度 / ai.exe dispatch ----------------

        private sealed class ScriptedChannel : ICadChannel
        {
            public readonly List<CadActionRequest> Sent = new List<CadActionRequest>();
            public Func<CadActionRequest, int, CadActionResult> Reply = (r, n) => CadActionResult.Success("ok");
            public CadActionResult Send(CadActionRequest request, CancellationToken ct)
            {
                Sent.Add(request);
                return Reply(request, Sent.Count);
            }
        }

        private static CadSequence Seq(params string[] actions) => new CadSequence
        {
            Vs = "42", Actions = actions.Select(a => new CadActionRequest { Action = a }).ToList(),
        };

        [TestMethod]
        public void Runner_TimeoutStopsSequence_AndMarksRestSkipped()
        {
            var ch = new ScriptedChannel { Reply = (r, n) => r.Action == CadActions.RunCommand ? CadActionResult.Fail(CadErrors.Timeout, "slow") : CadActionResult.Success("ok") };
            var seq = Seq("openDrawing", "runCommand", "screenshot", "getLog");
            seq.Actions[1].SetArg("command", "x");
            var r = new SequenceRunner(ch) { RetryDelayMs = 0 }.Run(seq);
            Assert.IsFalse(r.Ok);
            Assert.AreEqual(2, r.StoppedAt);
            Assert.AreEqual(2, ch.Sent.Count, "a timeout is not retried and stops the sequence");
            CollectionAssert.AreEqual(new[] { null, CadErrors.Timeout, CadErrors.Skipped, CadErrors.Skipped }, r.Results.Select(x => x.ErrorCode).ToArray());
            StringAssert.Contains(r.Message, "超时");
            Assert.AreEqual(60000, ch.Sent[0].TimeoutMs);
            Assert.AreEqual("42", ch.Sent[0].Vs);
            Assert.AreEqual("1", ch.Sent[0].Id);
        }

        [TestMethod]
        public void Runner_RetriesTransportOnce_ButNotActionFailures()
        {
            var ch = new ScriptedChannel { Reply = (r, n) => n == 1 ? CadActionResult.Fail(CadErrors.Transport, "reset") : CadActionResult.Success("ok") };
            var r = new SequenceRunner(ch) { RetryDelayMs = 0 }.Run(Seq("screenshot"));
            Assert.IsTrue(r.Ok, r.Message);
            Assert.AreEqual(2, r.Results[0].Attempts);

            ch = new ScriptedChannel { Reply = (x, n) => CadActionResult.Fail(CadErrors.Transport, "down") };
            r = new SequenceRunner(ch) { RetryDelayMs = 0 }.Run(Seq("screenshot"));
            Assert.AreEqual(2, ch.Sent.Count, "only one retry");
            Assert.AreEqual(CadErrors.Transport, r.Results[0].ErrorCode);

            ch = new ScriptedChannel { Reply = (x, n) => CadActionResult.Fail(CadErrors.ActionFailed, "boom") };
            new SequenceRunner(ch) { RetryDelayMs = 0 }.Run(Seq("screenshot", "getLog"));
            Assert.AreEqual(1, ch.Sent.Count);
        }

        [TestMethod]
        public void Runner_MapsAdapterAliases_AndRejectsUnlistedWithoutSending()
        {
            var adapter = WriteAdapter("Demo", DemoAdapter);
            var ch = new ScriptedChannel();
            var seq = Seq("runCommand", "runCommand");
            seq.Actions[0].SetArg("command", "make");
            seq.Actions[1].SetArg("command", "SHELL");
            var r = new SequenceRunner(ch, adapter) { RetryDelayMs = 0 }.Run(seq);
            Assert.AreEqual("DEMOMAKE", ch.Sent.Single().Arg("command"));
            Assert.AreEqual(CadErrors.InvalidArgs, r.Results[1].ErrorCode);
            Assert.AreEqual("Demo", r.Adapter);

            ch = new ScriptedChannel();
            r = new SequenceRunner(ch).Run(Seq("format_disk"));
            Assert.AreEqual(CadErrors.UnknownAction, r.Results[0].ErrorCode);
            Assert.AreEqual(0, ch.Sent.Count);
        }

        [TestMethod]
        public void Executor_DeadlineCoversEveryActionAndRetry()
        {
            var seq = Seq("screenshot", "getLog");
            seq.Actions[0].TimeoutMs = 90000;
            Assert.AreEqual(TimeSpan.FromMilliseconds((110000 + 80000) * 2 + 30000), CadExecutor.Deadline(seq));
        }

        // ---------------- VSManager 中转 / VSManager broker ----------------

        private static CadActionBroker NewBroker(Func<int, bool> alive = null) => new CadActionBroker { ProcessAlive = alive ?? (_ => true) };

        /// <summary>在后台模拟 CAD 代理：领取一条动作并用 reply 回传。/ Simulates a CAD agent in the background: takes one action and posts reply.</summary>
        private static Task<CadActionRequest> AgentOnce(CadActionBroker broker, string agentId, Func<CadActionRequest, CadActionResult> reply) => Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                var next = await broker.NextAsync(new CadAgentMessage { AgentId = agentId, WaitMs = 200 });
                if (next.Request == null) continue;
                var r = reply(next.Request);
                if (r != null) { r.Id = next.Request.Id; broker.Result(new CadAgentMessage { AgentId = agentId, Result = r }); }
                return next.Request;
            }
            return null;
        });

        [TestMethod]
        public async Task Broker_DeliversActionAndResult_FillsRecordedDrawing()
        {
            var broker = NewBroker();
            var old = VsCadDebug.DrawingLookup;
            VsCadDebug.DrawingLookup = s => s.EndsWith("Demo.Plugin.sln") ? @"C:\Work\Plan.dwg" : null;
            try
            {
                var hello = broker.Hello(new CadAgentHello { Pid = 1234, Host = "AutoCAD", Solution = @"C:\Work\Demo.Plugin.sln" });
                Assert.IsTrue(hello.Ok);
                StringAssert.Contains(broker.StatusText, "pid=1234");
                var agent = AgentOnce(broker, hello.AgentId, q => CadActionResult.Success("opened " + q.Arg("path")));
                var r = await broker.ExecAsync(new CadActionRequest { Action = "OPENDRAWING", TimeoutMs = 5000 }, @"C:\Work\Demo.Plugin.sln");
                Assert.IsTrue(r.Ok, r.Message);
                Assert.AreEqual(@"opened C:\Work\Plan.dwg", r.Message);
                Assert.AreEqual(CadActions.OpenDrawing, (await agent).Action);

                var wrong = await broker.ExecAsync(new CadActionRequest { Action = "screenshot" }, @"C:\Work\Other.sln");
                Assert.AreEqual(CadErrors.CadUnavailable, wrong.ErrorCode);
            }
            finally { VsCadDebug.DrawingLookup = old; }
        }

        [TestMethod]
        public async Task Broker_TimesOut_DropsLateResult_AndRejectsSecondCad()
        {
            var broker = NewBroker();
            var hello = broker.Hello(new CadAgentHello { Pid = 1234 });
            Assert.IsFalse(broker.Hello(new CadAgentHello { Pid = 5678 }).Ok, "v1 drives one CAD at a time");
            CadActionRequest taken = null;
            var agent = AgentOnce(broker, hello.AgentId, q => { taken = q; return null; });
            var r = await broker.ExecAsync(new CadActionRequest { Action = "runCommand", TimeoutMs = 1000 }, null);
            Assert.AreEqual(CadErrors.Timeout, r.ErrorCode);
            await agent;
            var late = broker.Result(new CadAgentMessage { AgentId = hello.AgentId, Result = new CadActionResult { Id = taken.Id, Ok = true } });
            StringAssert.Contains(late.Message, "Stale");
        }

        [TestMethod]
        public async Task Broker_ReportsCadGone_AndUnavailable_WithoutRestart()
        {
            Assert.AreEqual(CadErrors.CadUnavailable, (await NewBroker().ExecAsync(new CadActionRequest { Action = "screenshot" }, null)).ErrorCode);

            bool alive = true;
            var broker = NewBroker(_ => alive);
            var hello = broker.Hello(new CadAgentHello { Pid = 1234 });
            var agent = AgentOnce(broker, hello.AgentId, q => { alive = false; return null; });
            var r = await broker.ExecAsync(new CadActionRequest { Action = "screenshot", TimeoutMs = 10000 }, null);
            Assert.AreEqual(CadErrors.CadGone, r.ErrorCode);
            StringAssert.Contains(r.Message, "未自动重启");
            await agent;
            Assert.IsNull(broker.Agent);
            Assert.IsTrue(broker.NextAsync(new CadAgentMessage { AgentId = "unknown" }).Result.Rehello);
        }

        [TestMethod]
        public void Broker_WritesConnectionFileInDataFolder()
        {
            string file = CadActionBroker.WriteConnection(new AppSettings { WebEnabled = true, WebPort = 9123, WebToken = "tok" });
            Assert.AreEqual(Path.Combine(AppPaths.DataFolder, CadAgentConnection.FileName), file);
            var c = CadJson.Deserialize<CadAgentConnection>(File.ReadAllText(file));
            Assert.AreEqual("http://127.0.0.1:9123/", c.Url);
            Assert.AreEqual("tok", c.Token);
            Assert.IsTrue(c.Enabled);
        }

        [TestMethod]
        public void WebRemote_CadRoutesAreLocalOnly()
        {
            Assert.IsNull(WebRemote.LocalDenial("127.0.0.1"));
            Assert.IsNull(WebRemote.LocalDenial("::1"));
            Assert.IsNull(WebRemote.LocalDenial("::ffff:127.0.0.1"));
            Assert.IsNotNull(WebRemote.LocalDenial("192.168.1.20"));
            Assert.IsNotNull(WebRemote.LocalDenial(null));
        }

        // ---------------- 调试集成 / Debug integration ----------------

        [TestMethod]
        public void DebugPlan_ScriptAddsStartupCommands_AndInjectStripsExtraArgs()
        {
            string s = CadDebugPlan.BuildScript(new[] { @"C:\Work\Extra.dll", @"C:\Work\Plugin.dll" }, new[] { "MYINIT", " ", "_.ZOOM _E" });
            Assert.AreEqual("(command \"_.NETLOAD\" \"C:/Work/Extra.dll\")\r\n(command \"_.NETLOAD\" \"C:/Work/Plugin.dll\")\r\nMYINIT\r\n_.ZOOM _E\r\n", s);
            Assert.ThrowsException<ArgumentException>(() => CadDebugPlan.BuildScript(new string[0], new[] { "A\nB" }));

            string script = Path.Combine(VsCadDebug.ScriptRoot, "x.scr");
            string injected = CadDebugPlan.Inject("/p Mine", script, @"C:\Work\Plan.dwg", "/nologo");
            Assert.AreEqual("\"C:\\Work\\Plan.dwg\" /b \"" + script + "\" /nologo /p Mine", injected);
            Assert.AreEqual("/p Mine", CadDebugPlan.StripInjected(injected, "/nologo"));
            Assert.AreEqual(injected, CadDebugPlan.Inject(injected, script, @"C:\Work\Plan.dwg", "/nologo"));
        }

        [TestMethod]
        public void Boot_SourceUsesPresetAndAdapterOverrides()
        {
            var adapter = WriteAdapter("Demo", DemoAdapter);
            File.WriteAllText(Path.Combine(adapter.Directory, CadAdapter.ActionsFileName),
                "// sample\r\nusing System.Text;\r\nnamespace VSManagerCadBoot.Custom { public sealed class Actions : VSManager.CadAgent.ICadActionOverride {\r\n" +
                "public bool TryExecute(VSManager.CadAgent.CadActionRequest r, VSManager.CadAgent.CadActionContext c, out VSManager.CadAgent.CadActionResult res) { res = null; return false; } } }\r\n");
            var o = new CadBootOptions { Host = "AutoCAD", Adapter = adapter, ConnectionFile = @"C:\Data\cad-agent.json", Solution = "C:\\Work\\Demo \"q\".sln" };
            string src = CadBootCompiler.GenerateSource(o, CadBootCompiler.ResolveApi(o.Host, adapter));
            Assert.IsTrue(src.StartsWith("using System.Text;"));
            StringAssert.Contains(src, "Autodesk.AutoCAD.ApplicationServices");
            StringAssert.Contains(src, "handler.AddOverride(new VSManagerCadBoot.Custom.Actions());");
            StringAssert.Contains(src, "@\"C:\\Work\\Demo \"\"q\"\".sln\"");
            StringAssert.Contains(src, CadBootCompiler.StatusCommand);
            Assert.IsFalse(src.Contains("$"), "all placeholders must be replaced");

            var bad = new CadApiConfig { Namespace = "A;B", ApplicationClass = "X" };
            Assert.ThrowsException<InvalidOperationException>(() => CadBootCompiler.GenerateSource(o, bad));
            Assert.IsNull(CadBootCompiler.ResolveReferences(@"C:\NoSuchCad\acad.exe", CadApiConfig.Preset("AutoCAD"), out string err));
            Assert.IsNotNull(err);
        }

        /// <summary>本机装有 .NET Framework 版 AutoCAD 时，真实编译引导 DLL。/ Really compiles the boot DLL when a .NET Framework AutoCAD is installed.</summary>
        [TestMethod]
        [TestCategory(TestKind.Console)]
        public void Boot_CompilesAgainstInstalledAutoCad()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string exe = Enumerable.Range(2019, 6).Reverse().Select(y => Path.Combine(root, "Autodesk", "AutoCAD " + y, "acad.exe")).FirstOrDefault(File.Exists);
            if (exe == null) Assert.Inconclusive("No .NET Framework AutoCAD (2019–2024) installed");
            var adapter = WriteAdapter("Demo", DemoAdapter);
            string cache = _data.File("boot");
            var r = CadBootCompiler.Compile(new CadBootOptions { CadExe = exe, Host = "AutoCAD", Adapter = adapter, ConnectionFile = _data.File("cad-agent.json"), CacheRoot = cache });
            Assert.IsTrue(r.Ok, r.Error);
            Assert.IsTrue(File.Exists(r.BootDll));
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(r.BootDll), "VSManager.CadAgent.dll")));
            Assert.IsTrue(CadBootCompiler.Compile(new CadBootOptions { CadExe = exe, Host = "AutoCAD", Adapter = adapter, ConnectionFile = _data.File("cad-agent.json"), CacheRoot = cache }).Cached);
        }

        // ---------------- CAD 侧通用规则 / CAD-side shared rules ----------------

        private sealed class DirectUi : ISynchronizeInvoke
        {
            public bool InvokeRequired => false;
            public IAsyncResult BeginInvoke(Delegate method, object[] args) => throw new NotSupportedException();
            public object EndInvoke(IAsyncResult result) => throw new NotSupportedException();
            public object Invoke(Delegate method, object[] args) => method.DynamicInvoke(args);
        }

        internal sealed class FakeCad : CadActionHandlerBase
        {
            public FakeCad() : base(new DirectUi()) { }
            public readonly List<string> Calls = new List<string>();
            public bool Busy, Drawing = true;
            protected override bool ActivateOpen(string path) { Calls.Add("activate " + Path.GetFileName(path)); return false; }
            protected override string OpenDrawing(string path, bool readOnly) { Calls.Add("open " + Path.GetFileName(path)); return path; }
            protected override string NewDrawing(string template) { Calls.Add("new"); Drawing = true; return "Drawing1.dwg"; }
            protected override string SwitchDrawing(string name) => name == "A.dwg" ? name : null;
            protected override int CloseAllDrawings() { Calls.Add("closeAll"); return 2; }
            protected override string ListDrawings() => "A.dwg";
            protected override bool HasActiveDrawing() => Drawing;
            protected override void SendCommand(string command) { Calls.Add("cmd " + command); }
            protected override bool CommandActive() => Busy;
            protected override string GetSystemVariable(string name) => name == "CMDACTIVE" ? "0" : throw new ArgumentException("bad var");
            protected override int CountEntities(string dxfType, string layer, out string breakdown) { breakdown = "LINE=3"; return 3; }
        }

        private static CadActionRequest Req(string action, params string[] args)
        {
            var r = new CadActionRequest { Action = action, TimeoutMs = 2000 };
            for (int i = 0; i + 1 < args.Length; i += 2) r.SetArg(args[i], args[i + 1]);
            return r;
        }

        [TestMethod]
        public void Handler_MissingDrawingOpensNewAndExplains()
        {
            var cad = new FakeCad();
            var r = cad.Execute(Req("openDrawing", "path", _data.File("missing.dwg")), CancellationToken.None);
            Assert.IsTrue(r.Ok);
            StringAssert.Contains(r.Message, "未找到图纸「missing.dwg」，已按规则打开新图");
            StringAssert.Contains(r.Artifacts.Single().Content, "newDrawing=true");
            Assert.IsFalse(r.Message.Contains(_data.Path), "only the file name is echoed");

            string dwg = _data.File("Plan.dwg");
            File.WriteAllText(dwg, "x");
            Assert.IsTrue(cad.Execute(Req("openDrawing", "path", dwg), CancellationToken.None).Ok);
            CollectionAssert.AreEqual(new[] { "new", "activate Plan.dwg", "open Plan.dwg" }, cad.Calls);
            File.WriteAllText(_data.File("notes.txt"), "x");
            Assert.AreEqual(CadErrors.InvalidArgs, cad.Execute(Req("openDrawing", "path", _data.File("notes.txt")), CancellationToken.None).ErrorCode);
        }

        [TestMethod]
        public void Handler_CommandRules_CloseNeedsDiscard_TimeoutMarked()
        {
            var cad = new FakeCad { Adapter = WriteAdapter("Demo", DemoAdapter) };
            Assert.AreEqual(CadErrors.InvalidArgs, cad.Execute(Req("runCommand", "command", "SHELL"), CancellationToken.None).ErrorCode);
            var ok = cad.Execute(Req("runCommand", "command", "make", "settleMs", "0"), CancellationToken.None);
            Assert.IsTrue(ok.Ok, ok.Message);
            CollectionAssert.Contains(cad.Calls, "cmd DEMOMAKE");

            cad.Busy = true;
            Assert.AreEqual(CadErrors.Busy, cad.Execute(Req("runCommand", "command", "make"), CancellationToken.None).ErrorCode);

            Assert.AreEqual(CadErrors.InvalidArgs, cad.Execute(Req("closeAllDrawings"), CancellationToken.None).ErrorCode);
            Assert.IsTrue(cad.Execute(Req("closeAllDrawings", "discard", "true"), CancellationToken.None).Ok);

            Assert.AreEqual(CadErrors.NotFound, cad.Execute(Req("switchDrawing", "name", "B.dwg"), CancellationToken.None).ErrorCode);
            StringAssert.Contains(cad.Execute(Req("getParam", "name", "CMDACTIVE, NOPE"), CancellationToken.None).Message, "CMDACTIVE = 0");
            StringAssert.Contains(cad.Execute(Req("getEntityCount", "type", "LINE"), CancellationToken.None).Message, ": 3");
            Assert.AreEqual(CadErrors.NotFound, cad.Execute(Req("getLog"), CancellationToken.None).ErrorCode, "adapter declares no logs");
        }

        [TestMethod]
        public void Handler_CommandThatNeverEnds_IsMarkedTimeout()
        {
            var cad = new BusyAfterSend { Adapter = WriteAdapter("Demo", DemoAdapter) };
            var r = cad.Execute(Req("runCommand", "command", "make", "settleMs", "0"), CancellationToken.None);
            Assert.AreEqual(CadErrors.Timeout, r.ErrorCode);
            Assert.IsTrue(r.DurationMs >= 1900, r.DurationMs.ToString());
        }

        private sealed class BusyAfterSend : CadActionHandlerBase
        {
            private bool _sent;
            public BusyAfterSend() : base(new DirectUi()) { }
            protected override bool ActivateOpen(string path) => false;
            protected override string OpenDrawing(string path, bool readOnly) => path;
            protected override string NewDrawing(string template) => "new";
            protected override string SwitchDrawing(string name) => null;
            protected override int CloseAllDrawings() => 0;
            protected override string ListDrawings() => "";
            protected override bool HasActiveDrawing() => true;
            protected override void SendCommand(string command) { _sent = true; }
            protected override bool CommandActive() => _sent;
            protected override string GetSystemVariable(string name) => "";
            protected override int CountEntities(string dxfType, string layer, out string breakdown) { breakdown = null; return 0; }
        }

        [TestMethod]
        public void Handler_GetLogReadsOnlyAdapterLogsTail()
        {
            string log = _data.File("plugin.log");
            File.WriteAllLines(log, Enumerable.Range(1, 50).Select(i => "line " + i));
            var cad = new FakeCad { Adapter = new CadAdapter { Name = "x", Logs = new List<string> { log } } };
            var r = cad.Execute(Req("getLog", "lines", "5"), CancellationToken.None);
            Assert.IsTrue(r.Ok, r.Message);
            var art = r.Artifacts.Single();
            Assert.AreEqual(CadArtifact.Log, art.Kind);
            StringAssert.Contains(art.Content, "line 50");
            Assert.IsFalse(art.Content.Contains("line 40"));
            Assert.AreEqual(CadErrors.NotFound, cad.Execute(Req("getLog", "name", "other"), CancellationToken.None).ErrorCode);
        }

        // ---------------- AI 工具 / AI tool ----------------

        private (AgentService agent, AgentDesktopTests.DesktopHost host, AppSettings settings, List<CadSequence> runs) NewAgent()
        {
            var host = new AgentDesktopTests.DesktopHost();
            host.Instances.Add(new VsInstance { Pid = 42, SolutionPath = _data.File("Demo.Plugin.sln"), Key = "demo" });
            host.Instances.Add(new VsInstance { Pid = 43, SolutionPath = _data.File("Pipe.sln"), Key = "pipe" });
            var settings = new AppSettings { WebEnabled = true, AgentConfirm = false };
            var agent = new AgentService(host, () => settings);
            var runs = new List<CadSequence>();
            agent.CadRunner = (seq, ct) =>
            {
                runs.Add(seq);
                return Task.FromResult(new CadSequenceResult
                {
                    Ok = true, Message = "All actions ran", Vs = seq.Vs,
                    Results = seq.Actions.Select(a => a.Action == CadActions.Screenshot
                        ? CadActionResult.Success("shot", new CadArtifact { Kind = CadArtifact.Image, Name = "cad.png", Data = Convert.ToBase64String(new byte[] { 137, 80 }), Width = 800, Height = 600 })
                        : a.Action == CadActions.GetLog
                            ? CadActionResult.Success("log", new CadArtifact { Kind = CadArtifact.Log, Name = "plugin.log", Content = "ERROR something" })
                            : CadActionResult.Success(a.Action + " ok")).ToList(),
                });
            };
            return (agent, host, settings, runs);
        }

        private static async Task<string> Invoke(AgentService agent, string tool, AIFunctionArguments args)
        {
            var field = typeof(AgentService).GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var f = ((IEnumerable<AITool>)field.GetValue(agent)).OfType<AIFunction>().Single(t => t.Name == tool);
            return (await f.InvokeAsync(args))?.ToString();
        }

        private const string Acceptance = "[{\"action\":\"openDrawing\"},{\"action\":\"runCommand\",\"args\":{\"command\":\"make\"}},{\"action\":\"screenshot\"},{\"action\":\"getLog\"}]";

        [TestMethod]
        public async Task Tool_RunsSequence_ShowsArtifacts_AndFormatsForModel()
        {
            WriteAdapter("Demo", DemoAdapter);
            var (agent, host, settings, runs) = NewAgent();
            using (agent)
            {
                string text = await Invoke(agent, "run_cad_actions", new AIFunctionArguments { ["vs"] = "1", ["actions"] = Acceptance });
                StringAssert.Contains(text, "✅");
                StringAssert.Contains(text, "🖼");
                StringAssert.Contains(text, "ERROR something");
                StringAssert.Contains(text, "不要宣称已通过");
                Assert.AreEqual("42", runs.Single().Vs);
                Assert.AreEqual("Demo", runs.Single().Adapter);
                Assert.AreEqual(1, host.CadShown.Count);
                Assert.AreEqual(0, host.Confirmations);
            }
        }

        [TestMethod]
        public async Task Tool_HonorsConfirmation_AdapterAndWebChecks()
        {
            WriteAdapter("Demo", DemoAdapter);
            var (agent, host, settings, runs) = NewAgent();
            using (agent)
            {
                settings.AgentConfirm = true;
                host.ConfirmResult = false;
                StringAssert.Contains(await agent.RunCadActions("1", Acceptance), "declined");
                Assert.AreEqual(1, host.Confirmations);

                settings.AgentConfirm = false;
                StringAssert.Contains(await agent.RunCadActions("2", Acceptance), "No CAD adapter");
                StringAssert.Contains(await agent.RunCadActions("1", "[{\"action\":\"runCommand\",\"args\":{\"command\":\"SHELL\"}}]"), "command map");
                StringAssert.Contains(await agent.RunCadActions("1", "[{\"action\":\"format\"}]"), "Unknown action");
                StringAssert.Contains(await agent.RunCadActions("1", "not json"), "Invalid action JSON");
                settings.WebEnabled = false;
                StringAssert.Contains(await agent.RunCadActions("1", Acceptance), "Web remote is off");
                Assert.AreEqual(0, runs.Count);
            }
        }

        [TestMethod]
        public async Task Tool_AcceptsNonStringArgValues()
        {
            WriteAdapter("Demo", DemoAdapter);
            var (agent, host, settings, runs) = NewAgent();
            using (agent)
            {
                string text = await agent.RunCadActions("1", "[{\"action\":\"getLog\",\"args\":{\"lines\":100}},{\"action\":\"closeAllDrawings\",\"args\":{\"discard\":true}}]");
                StringAssert.Contains(text, "✅", text);
                Assert.AreEqual("100", runs.Single().Actions[0].Arg("lines"));
                Assert.IsTrue(runs.Single().Actions[1].Flag("discard"));
            }
        }

        [TestMethod]
        public void FormatResult_MarksTimeoutAndSkipped_TruncatesLogs()
        {
            var result = new CadSequenceResult
            {
                Ok = false, Message = "stopped", StoppedAt = 1,
                Results = new List<CadActionResult>
                {
                    new CadActionResult { Action = "runCommand", ErrorCode = CadErrors.Timeout, Message = "slow", DurationMs = 60000,
                        Artifacts = new List<CadArtifact> { new CadArtifact { Kind = CadArtifact.Log, Name = "a.log", Content = new string('x', 9000) + "TAIL" } } },
                    new CadActionResult { Action = "screenshot", ErrorCode = CadErrors.Skipped },
                },
            };
            string text = AgentService.FormatCadResult(result, new List<CadActionRequest>());
            StringAssert.Contains(text, "❌");
            StringAssert.Contains(text, "⏱ runCommand [TIMEOUT]");
            StringAssert.Contains(text, "– screenshot [SKIPPED]");
            StringAssert.Contains(text, "TAIL");
            Assert.IsTrue(text.Length < 7500, text.Length.ToString());
        }

        [TestMethod]
        public async Task ListAdapters_ShowsCommandsAndStatus()
        {
            WriteAdapter("Demo", DemoAdapter);
            var (agent, host, settings, runs) = NewAgent();
            using (agent)
            {
                string text = await Invoke(agent, "list_cad_adapters", new AIFunctionArguments());
                StringAssert.Contains(text, "Demo");
                StringAssert.Contains(text, "make→DEMOMAKE");
                StringAssert.Contains(text, "CAD");
            }
        }

        // ---------------- 端到端 / End to end ----------------

        /// <summary>
        /// ai.exe → 本地 Web API → CAD 代理（进程内模拟 CAD）→ 回传：验证打开图纸 → 命令 → 截图 → 日志、缺失图纸开新图与超时停止。
        /// ai.exe → local Web API → CAD agent (simulated CAD in-process) → results: open → command → screenshot → log, a missing drawing opening a new one, and a timeout stopping the sequence.
        /// </summary>
        [TestMethod]
        [TestCategory(TestKind.Console)]
        public async Task EndToEnd_AiExeDrivesAgentThroughWebApi()
        {
            if (!File.Exists(CadExecutor.ExePath)) Assert.Inconclusive("ai.exe not in the test output");
            string log = _data.File("plugin.log");
            File.WriteAllText(log, "INFO start\r\nERROR sample failure\r\n");
            string adapterDir = Path.Combine(_adapters, "Demo");
            WriteAdapter("Demo", DemoAdapter.Replace("\"commands\"", "\"logs\":[" + CadJson.Serialize(log) + "],\"commands\""));

            int port;
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var settings = new AppSettings { WebEnabled = true, WebPort = port, WebToken = "e2e-token" };
            string solution = _data.File("Demo.Plugin.sln");
            var host = new RemoteHost(solution);
            var broker = new CadActionBroker();
            var cad = new SlowCommandCad();
            using (var web = new WebRemote(host, () => settings) { Cad = broker })
            {
                web.Apply();
                Assert.IsTrue(web.Running, web.Status);
                string conn = CadActionBroker.WriteConnection(settings);
                CadAgentHost.Start(cad, new CadAgentStart { ConnectionFile = conn, AdapterDirectory = adapterDir, Host = "AutoCAD", Solution = solution });
                try
                {
                    for (int i = 0; i < 100 && broker.Agent == null; i++) await Task.Delay(100);
                    Assert.IsNotNull(broker.Agent, CadAgentHost.Status);

                    var seq = new CadSequence
                    {
                        Vs = "1", Actions = CadJson.ParseActions("[{\"action\":\"openDrawing\",\"args\":{\"path\":" + CadJson.Serialize(_data.File("missing.dwg")) + "}},{\"action\":\"runCommand\",\"args\":{\"command\":\"make\",\"settleMs\":\"0\"}},{\"action\":\"getParam\",\"args\":{\"name\":\"CMDACTIVE\"}},{\"action\":\"getLog\"}]"),
                    };
                    var r = await CadExecutor.RunAsync(seq, settings, CancellationToken.None);
                    Assert.IsTrue(r.Ok, r.Message + " " + string.Join(" | ", r.Items.Select(x => x.ErrorCode + " " + x.Message)));
                    StringAssert.Contains(r.Results[0].Message, "已按规则打开新图");
                    CollectionAssert.Contains(cad.Calls, "cmd DEMOMAKE");
                    StringAssert.Contains(r.Results[3].Artifacts.Single().Content, "ERROR sample failure");

                    cad.HangNext();
                    seq = new CadSequence { Vs = "1", Actions = CadJson.ParseActions("[{\"action\":\"runCommand\",\"timeoutMs\":1500,\"args\":{\"command\":\"make\",\"settleMs\":\"0\"}},{\"action\":\"getLog\"}]") };
                    r = await CadExecutor.RunAsync(seq, settings, CancellationToken.None);
                    Assert.IsFalse(r.Ok);
                    Assert.AreEqual(1, r.StoppedAt);
                    Assert.AreEqual(CadErrors.Timeout, r.Results[0].ErrorCode);
                    Assert.AreEqual(CadErrors.Skipped, r.Results[1].ErrorCode);
                }
                finally { CadAgentHost.Stop(); }
            }
        }

        private sealed class SlowCommandCad : CadActionHandlerBase
        {
            private volatile bool Hang;
            public void HangNext() { _sent = false; Hang = true; }
            private bool _sent;
            public readonly List<string> Calls = new List<string>();
            public SlowCommandCad() : base(new DirectUi()) { }
            protected override bool ActivateOpen(string path) => false;
            protected override string OpenDrawing(string path, bool readOnly) => path;
            protected override string NewDrawing(string template) { Calls.Add("new"); return "Drawing1.dwg"; }
            protected override string SwitchDrawing(string name) => null;
            protected override int CloseAllDrawings() => 0;
            protected override string ListDrawings() => "";
            protected override bool HasActiveDrawing() => true;
            protected override void SendCommand(string command) { lock (Calls) Calls.Add("cmd " + command); _sent = true; }
            protected override bool CommandActive() => Hang && _sent;
            protected override string GetSystemVariable(string name) => "0";
            protected override int CountEntities(string dxfType, string layer, out string breakdown) { breakdown = null; return 0; }
        }

        private sealed class RemoteHost : IRemoteHost
        {
            public RemoteHost(string solution) { Instances = new List<VsInstance> { new VsInstance { Pid = 4242, SolutionPath = solution } }; }
            public IList<VsInstance> Instances { get; }
            public string NameOf(VsInstance v) => "Demo";
            public string NoteOf(VsInstance v) => "";
            public Task<string> SetNote(VsInstance v, string note) => Task.FromResult("");
            public ChatTranscript CachedChat(VsInstance v) => null;
            public Task<string> SendChat(VsInstance v, string text) => Task.FromResult("");
            public Task<string> InvokeChatButton(VsInstance v, string automationId, string name) => Task.FromResult("");
            public void Log(string s) { }
            public void FocusChat(int pid) { }
            public Task<string> DockPanes() => Task.FromResult("");
            public Task<string> ErrorList(VsInstance v, int max) => Task.FromResult("");
            public Task<byte[]> CaptureScreenshot(VsInstance v, bool requirePreview, CancellationToken cancellationToken) => Task.FromResult<byte[]>(null);
        }
    }
}
