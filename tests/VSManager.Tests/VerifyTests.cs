using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VSManager.AiHost;
using VSManager.Verify;

namespace VSManager.Tests
{
    /// <summary>
    /// 目标项目验证接口：命名管道端点、ai.exe verify 与 AI 工具 list_verify_checks / run_verify_check。
    /// Target-project verification interface: the named-pipe endpoint, ai.exe verify and the AI tools list_verify_checks / run_verify_check.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class VerifyTests
    {
        private static int SelfPid { get { using (var p = System.Diagnostics.Process.GetCurrentProcess()) return p.Id; } }

        [TestCleanup]
        public void Cleanup()
        {
            VerifyEndpoint.Current?.Dispose();
            VerifyCommand.Call = VerifyClient.Call;
            VerifyCommand.Discover = VerifyClient.Discover;
        }

        private static VerifyEndpoint StartDemo()
        {
            VerifyEndpoint.Current?.Dispose();
            Thread.Sleep(50);
            return VerifyEndpoint.Start("Demo plugin", "Demo.Plugin")
                .Register("count", "统计实体 / Count entities", ctx =>
                {
                    int n = int.Parse(ctx.Arg("n") ?? "3");
                    return (n > 0 ? VerifyOutcome.Pass("有实体 / has entities") : VerifyOutcome.Fail("没有实体 / no entities")).With("count", n.ToString()).Detail("layer=0");
                }, "n=数量 / count")
                .Register("boom", "抛异常 / Throws", ctx => throw new InvalidOperationException("kaput"))
                .Register("slow", "慢检查 / Slow", ctx => { Thread.Sleep(1500); return VerifyOutcome.Pass("late"); });
        }

        private static VerifyResponse CallWithRetry(int pid, VerifyRequest req, int timeout)
        {
            VerifyResponse r = null;
            for (int i = 0; i < 20; i++)
            {
                r = VerifyClient.Call(pid, req, timeout);
                if (r.Ok || r.ErrorCode != VerifyErrors.NoEndpoint) return r;
                Thread.Sleep(100);
            }
            return r;
        }

        [TestMethod]
        public void Endpoint_OverRealPipe_ListsAndRunsChecks()
        {
            StartDemo();
            int pid = SelfPid;
            var list = CallWithRetry(pid, new VerifyRequest { Op = VerifyRequest.List }, 5000);
            Assert.IsTrue(list.Ok, list.Message);
            Assert.AreEqual(VerifyProtocol.Version, list.Protocol);
            Assert.AreEqual("Demo.Plugin", list.Solution);
            CollectionAssert.AreEqual(new[] { "boom", "count", "slow" }, list.Checks.Select(c => c.Name).ToArray());
            CollectionAssert.Contains(VerifyClient.Discover(), pid);

            var pass = VerifyClient.Call(pid, new VerifyRequest { Op = VerifyRequest.Run, Check = "COUNT", Args = new Dictionary<string, string> { ["N"] = "5" } }, 5000);
            Assert.IsTrue(pass.Ok, pass.Message);
            Assert.AreEqual(VerifyStatus.Pass, pass.Status);
            Assert.AreEqual("count", pass.Check);
            Assert.AreEqual("5", pass.Evidence["count"]);
            Assert.AreEqual("layer=0", pass.Details.Single());

            var fail = VerifyClient.Call(pid, new VerifyRequest { Op = VerifyRequest.Run, Check = "count", Args = new Dictionary<string, string> { ["n"] = "0" } }, 5000);
            Assert.AreEqual(VerifyStatus.Fail, fail.Status);

            var boom = VerifyClient.Call(pid, new VerifyRequest { Op = VerifyRequest.Run, Check = "boom" }, 5000);
            Assert.IsFalse(boom.Ok);
            Assert.AreEqual(VerifyErrors.CheckException, boom.ErrorCode);
            StringAssert.Contains(boom.Message, "kaput");

            var unknown = VerifyClient.Call(pid, new VerifyRequest { Op = VerifyRequest.Run, Check = "nope" }, 5000);
            Assert.AreEqual(VerifyErrors.UnknownCheck, unknown.ErrorCode);
        }

        [TestMethod]
        public void Endpoint_TimeoutKeepsGateUntilCheckFinishes()
        {
            var ep = StartDemo();
            var timeout = ep.Handle(new VerifyRequest { Op = VerifyRequest.Run, Check = "slow", TimeoutMs = 1000 });
            Assert.AreEqual(VerifyErrors.Timeout, timeout.ErrorCode);
            var busy = ep.Handle(new VerifyRequest { Op = VerifyRequest.Run, Check = "count" });
            Assert.AreEqual(VerifyErrors.Busy, busy.ErrorCode);
            Thread.Sleep(1000);
            Assert.AreEqual(VerifyStatus.Pass, ep.Handle(new VerifyRequest { Op = VerifyRequest.Run, Check = "count" }).Status);
            Assert.AreEqual(VerifyErrors.InvalidRequest, ep.Handle(new VerifyRequest { Op = "delete" }).ErrorCode);
        }

        [TestMethod]
        public void Client_NoEndpoint_ReturnsErrorInsteadOfThrowing()
        {
            var r = VerifyClient.Call(int.MaxValue - 7, new VerifyRequest { Op = VerifyRequest.Ping }, 1000);
            Assert.IsFalse(r.Ok);
            Assert.AreEqual(VerifyErrors.NoEndpoint, r.ErrorCode);
        }

        [TestMethod]
        public void AiVerifyCommand_ListAndRun_UseExitCodes()
        {
            var calls = new List<VerifyRequest>();
            VerifyCommand.Discover = () => new List<int> { 10, 11 };
            VerifyCommand.Call = (pid, req, t) =>
            {
                calls.Add(req);
                if (pid == 11) return VerifyResponse.Failure(VerifyErrors.NoEndpoint, "gone");
                if (req.Op == VerifyRequest.List) return new VerifyResponse { Ok = true, Checks = new List<VerifyCheckInfo> { new VerifyCheckInfo { Name = "count" } } };
                return new VerifyResponse { Ok = true, Status = req.Args != null && req.Args["n"] == "0" ? VerifyStatus.Fail : VerifyStatus.Pass };
            };

            var output = new StringWriter();
            Assert.AreEqual(0, VerifyCommand.Run(new[] { "verify", "list" }, new StringReader(""), output));
            var discovery = VerifyWire.Deserialize<VerifyDiscovery>(output.ToString().Trim());
            Assert.AreEqual(10, discovery.Endpoints.Single().Pid);
            StringAssert.Contains(discovery.Message, "11: gone");

            output = new StringWriter();
            Assert.AreEqual(0, VerifyCommand.Run(new[] { "verify", "run", "-" }, new StringReader("{\"pid\":10,\"check\":\"count\",\"timeoutMs\":99999999}"), output));
            Assert.AreEqual(VerifyProtocol.MaxTimeoutMs, calls.Last().TimeoutMs);
            Assert.AreEqual(1, VerifyCommand.Run(new[] { "verify", "run" }, new StringReader("{\"pid\":10,\"check\":\"count\",\"args\":{\"n\":\"0\"}}"), new StringWriter()));
            Assert.AreEqual(2, VerifyCommand.Run(new[] { "verify", "run" }, new StringReader("{\"pid\":11,\"check\":\"count\"}"), new StringWriter()));
            Assert.AreEqual(2, VerifyCommand.Run(new[] { "verify", "run" }, new StringReader("not json"), new StringWriter()));
            Assert.AreEqual(2, VerifyCommand.Run(new[] { "verify", "list", "abc" }, new StringReader(""), new StringWriter()));
            Assert.AreEqual(2, VerifyCommand.Run(new[] { "verify" }, new StringReader(""), new StringWriter()));
        }

        // ---------------- AI 工具 / AI tools ----------------

        private sealed class Fake
        {
            public readonly List<string[]> Args = new List<string[]>();
            public readonly List<string> Inputs = new List<string>();
            public VerifyDiscovery Discovery = new VerifyDiscovery { Ok = true };
            public VerifyResponse RunReply = new VerifyResponse { Ok = true, Status = VerifyStatus.Pass, Message = "all good", Evidence = new Dictionary<string, string> { ["count"] = "5" } };

            public Task<string> Run(string[] args, string stdin, TimeSpan deadline, CancellationToken ct)
            {
                Args.Add(args);
                Inputs.Add(stdin);
                return Task.FromResult(args[1] == "list" ? VerifyWire.Serialize(Discovery) : VerifyWire.Serialize(RunReply));
            }
        }

        private static VerifyResponse Ep(int pid, string solution, params string[] checks) => new VerifyResponse
        {
            Ok = true, Pid = pid, Solution = solution, Process = "acad", Endpoint = "Demo plugin",
            Checks = checks.Select(c => new VerifyCheckInfo { Name = c, Description = c + " desc" }).ToList(),
        };

        private static (AgentService agent, AgentDesktopTests.DesktopHost host, AppSettings settings, Fake fake) NewAgent()
        {
            var host = new AgentDesktopTests.DesktopHost();
            host.Instances.Add(new VsInstance { Pid = 42, SolutionPath = Path.Combine(Path.GetTempPath(), "Demo.Plugin.sln"), Key = "demo" });
            host.Instances.Add(new VsInstance { Pid = 43, SolutionPath = Path.Combine(Path.GetTempPath(), "Other.sln"), Key = "other" });
            var settings = new AppSettings { AgentConfirm = false };
            var agent = new AgentService(host, () => settings);
            var fake = new Fake();
            agent.VerifyRunner = fake.Run;
            return (agent, host, settings, fake);
        }

        private static async Task<string> Invoke(AgentService agent, string tool, AIFunctionArguments args)
        {
            var field = typeof(AgentService).GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var f = ((IEnumerable<AITool>)field.GetValue(agent)).OfType<AIFunction>().Single(t => t.Name == tool);
            return (await f.InvokeAsync(args))?.ToString();
        }

        [TestMethod]
        public async Task Tools_MatchEndpointsByDebuggedPidOrSolution()
        {
            var (agent, host, settings, fake) = NewAgent();
            using (agent)
            {
                fake.Discovery.Endpoints = new List<VerifyResponse> { Ep(100, "demo.plugin", "count"), Ep(200, null, "shape"), Ep(300, "Unrelated", "x") };
                host.Debugged[42] = new List<int> { 200 };
                string text = await Invoke(agent, "list_verify_checks", new AIFunctionArguments { ["vs"] = "1" });
                StringAssert.Contains(text, "100");
                StringAssert.Contains(text, "200");
                StringAssert.Contains(text, "count desc");
                Assert.IsFalse(text.Contains("300"), text);

                string none = await Invoke(agent, "list_verify_checks", new AIFunctionArguments { ["vs"] = "2" });
                StringAssert.Contains(none, "VsmVerify.cs");
                StringAssert.Contains(none, "run_cad_actions");
            }
        }

        [TestMethod]
        public async Task Tools_RunCheck_SendsRequest_FormatsAndGuardsTicking()
        {
            var (agent, host, settings, fake) = NewAgent();
            using (agent)
            {
                fake.Discovery.Endpoints = new List<VerifyResponse> { Ep(100, "Demo.Plugin", "count") };
                string text = await Invoke(agent, "run_verify_check", new AIFunctionArguments { ["vs"] = "1", ["check"] = "COUNT", ["args"] = "{\"n\":5,\"layer\":\"0\"}", ["timeoutMs"] = 5000 });
                StringAssert.Contains(text, "✅");
                StringAssert.Contains(text, "all good");
                StringAssert.Contains(text, "count = 5");
                StringAssert.Contains(text, "mark_test_item");
                CollectionAssert.AreEqual(new[] { "verify", "run", "-" }, fake.Args.Last());
                var sent = VerifyWire.Deserialize<VerifyRunRequest>(fake.Inputs.Last());
                Assert.AreEqual(100, sent.Pid);
                Assert.AreEqual("count", sent.Check);
                Assert.AreEqual("5", sent.Args["n"]);
                Assert.AreEqual(5000, sent.TimeoutMs);

                fake.RunReply = new VerifyResponse { Ok = true, Status = VerifyStatus.Fail, Message = "wrong" };
                StringAssert.Contains(await Invoke(agent, "run_verify_check", new AIFunctionArguments { ["vs"] = "1", ["check"] = "count" }), "❌");
                fake.RunReply = VerifyResponse.Failure(VerifyErrors.Busy, "busy");
                StringAssert.Contains(await Invoke(agent, "run_verify_check", new AIFunctionArguments { ["vs"] = "1", ["check"] = "count" }), "BUSY");

                int runs = fake.Args.Count(a => a[1] == "run");
                StringAssert.Contains(await Invoke(agent, "run_verify_check", new AIFunctionArguments { ["vs"] = "1", ["check"] = "missing" }), "count desc");
                StringAssert.Contains(await Invoke(agent, "run_verify_check", new AIFunctionArguments { ["vs"] = "1", ["check"] = "count", ["args"] = "[1]" }), "JSON");
                Assert.AreEqual(runs, fake.Args.Count(a => a[1] == "run"));
            }
        }

        [TestMethod]
        public async Task Tools_RunCheck_AmbiguityAndConfirmation()
        {
            var (agent, host, settings, fake) = NewAgent();
            using (agent)
            {
                fake.Discovery.Endpoints = new List<VerifyResponse> { Ep(100, "Demo.Plugin", "count"), Ep(101, "Demo.Plugin", "count") };
                string ambiguous = await Invoke(agent, "run_verify_check", new AIFunctionArguments { ["vs"] = "1", ["check"] = "count" });
                StringAssert.Contains(ambiguous, "100");
                StringAssert.Contains(ambiguous, "101");
                Assert.IsFalse(fake.Args.Any(a => a[1] == "run"));

                settings.AgentConfirm = true;
                host.ConfirmResult = false;
                string declined = await Invoke(agent, "run_verify_check", new AIFunctionArguments { ["vs"] = "1", ["check"] = "count", ["pid"] = 101 });
                StringAssert.Contains(declined, "declined");
                Assert.AreEqual(1, host.Confirmations);
                Assert.IsFalse(fake.Args.Any(a => a[1] == "run"));

                host.ConfirmResult = true;
                await Invoke(agent, "run_verify_check", new AIFunctionArguments { ["vs"] = "1", ["check"] = "count", ["pid"] = 101 });
                Assert.AreEqual(101, VerifyWire.Deserialize<VerifyRunRequest>(fake.Inputs.Last()).Pid);
            }
        }
    }
}
