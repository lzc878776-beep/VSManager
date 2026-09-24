using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AIMessage = Microsoft.Extensions.AI.ChatMessage;

namespace VSManager.Tests
{
    [TestClass]
    public class AgentDesktopTests
    {
        private TempDataFolder _data;
        private DesktopHost _host;
        private AppSettings _settings;
        private VisionClient _vision;
        private AgentService _agent;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _host = new DesktopHost();
            _host.Instances.Add(new VsInstance { Pid = 42, SolutionPath = _data.Path, Key = "test" });
            _settings = new AppSettings { AgentEndpoint = "http://localhost:11434/v1", AgentModel = "test-vision", AgentConfirm = false };
            _host.Queue = new TaskQueue(_settings, new MemoryTaskStore(), new RecordingArchive(), () => DateTime.Now);
            _vision = new VisionClient();
            _agent = new AgentService(_host, () => _settings, _vision);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _agent.Dispose();
            if (Directory.Exists(_data.File("Project\\.git"))) WorktreeTests.MakeFixtureWritable(_data.Path);
            _data.Dispose();
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Script_StrictPolicy_RefusesWithoutApprovalOrExecution(bool enabled)
        {
            _settings.AgentPowerShellEnabled = enabled;
            _host.Approve = _ => Task.FromResult(true);
            string result = await _agent.RunPowerShell("1", "'no' | Set-Content -LiteralPath 'denied.txt'", "Create a test marker");
            StringAssert.Contains(result, "disabled");
            StringAssert.Contains(result, "find_files");
            Assert.AreEqual(0, _host.Approvals);
            Assert.IsNull(_host.Detail);
            Assert.IsFalse(File.Exists(_data.File("denied.txt")));
            Assert.IsFalse(_agent.AwaitingUser);
        }

        [TestMethod]
        public async Task Script_CancelledOrInvalid_StillNeverAsksForApproval()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                StringAssert.Contains(await _agent.RunPowerShell("missing", "", "", 121, cts.Token), "disabled");
                Assert.AreEqual(0, _host.Approvals);
                Assert.IsFalse(_agent.AwaitingUser);
            }
        }

        [TestMethod]
        public void Script_IsNotRegisteredAsAnAiTool()
        {
            var field = typeof(AgentService).GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var tools = (IEnumerable<AITool>)field.GetValue(_agent);
            Assert.IsFalse(tools.Any(t => t.Name == "run_powershell"));
        }

        [TestMethod]
        public async Task Screenshot_Denied_DoesNotCreateModelClient()
        {
            StringAssert.Contains(await _agent.CaptureVsScreenshot("1", "Describe the dialog"), "cancelled");
            Assert.AreEqual(1, _host.Captures);
            Assert.AreEqual(0, _vision.Created);
            StringAssert.Contains(_host.Destination, "test-vision");
            Assert.IsFalse(_agent.AwaitingUser);
        }

        [TestMethod]
        public async Task Screenshot_Approved_SendsImageToVisionOnly_AndReturnsObservation()
        {
            _host.Image = new byte[] { 137, 80, 78, 71 };
            string result = await _agent.CaptureVsScreenshot("1", "Describe the dialog");
            StringAssert.Contains(result, "observation only");
            StringAssert.Contains(result, "A dialog has an OK button");
            Assert.AreEqual(1, _vision.Created);
            Assert.AreEqual(1, _vision.Requests);
            var image = _vision.Messages.SelectMany(m => m.Contents).OfType<DataContent>().Single();
            Assert.AreEqual("image/png", image.MediaType);
            CollectionAssert.AreEqual(_host.Image, image.Data.ToArray());
            Assert.IsTrue(_vision.Options.Tools == null || _vision.Options.Tools.Count == 0);
            Assert.IsTrue(_vision.Disposed);
        }

        [TestMethod]
        public async Task Screenshot_Disabled_DoesNotCaptureOrSend()
        {
            _settings.AgentScreenshotEnabled = false;
            StringAssert.Contains(await _agent.CaptureVsScreenshot("1", "Describe"), "disabled");
            Assert.AreEqual(0, _host.Captures);
            Assert.AreEqual(0, _vision.Created);
        }

        [TestMethod]
        public async Task Screenshot_UnsupportedModel_ReturnsExplicitFailure()
        {
            _host.Image = new byte[] { 137, 80, 78, 71 };
            _vision.Error = new System.ClientModel.ClientResultException("Images not supported");
            StringAssert.Contains(await _agent.CaptureVsScreenshot("1", "Describe"), "vision support");
            Assert.AreEqual(1, _vision.Requests);
        }

        [TestMethod]
        public async Task OpenCopilot_IsRegistered_AndCallsHostForResolvedVs()
        {
            string result = (await InvokeTool("open_copilot", new AIFunctionArguments { ["vs"] = "1" }))?.ToString();
            StringAssert.Contains(result, "已打开对话助手");
            Assert.AreEqual(1, _host.PaneOpened.Count);
            Assert.AreSame(_host.Instances[0], _host.PaneOpened[0]);
        }

        [TestMethod]
        public async Task OpenCopilot_UnknownVs_DoesNotCallHost()
        {
            string result = await _agent.OpenCopilot("99");
            Assert.IsFalse(string.IsNullOrWhiteSpace(result));
            Assert.AreEqual(0, _host.PaneOpened.Count);
        }

        [TestMethod]
        public void Prompts_DescribeOpenCopilot()
        {
            foreach (bool english in new[] { false, true })
                StringAssert.Contains(Prompts.AgentSystem(english, DateTime.Now, "VS", ""), "open_copilot");
        }

        [TestMethod]
        public void Prompts_DescribeToolsAndApprovalBoundary()
        {
            foreach (bool english in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(english, DateTime.Now, "VS", "");
                StringAssert.Contains(prompt, "capture_vs_screenshot");
                StringAssert.Contains(prompt, "run_powershell");
                StringAssert.Contains(prompt, "disabled");
                foreach (string name in new[] { "find_files", "search_file_contents", "read_file", "list_directory" })
                    StringAssert.Contains(prompt, name);
                StringAssert.Contains(prompt, "untrusted data");
            }
        }

        [DataTestMethod]
        [DataRow(CopilotState.Idle)]
        [DataRow(CopilotState.Busy)]
        [DataRow(CopilotState.Unknown)]
        public async Task SendTaskTool_AlwaysEnqueues_AndDoesNotStartSending(CopilotState state)
        {
            _host.Instances[0].Copilot = state;
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = "First queued task" });
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = "Second queued task" });
            Assert.AreEqual(2, _host.Queue.Items.Count);
            Assert.IsTrue(_host.Queue.Items.All(t => t.Status == QueueStatus.Waiting && t.Attempts == 0 && t.Source == "AI"));
            Assert.IsTrue(_host.Queue.Items[0].Id < _host.Queue.Items[1].Id);
            Assert.IsNull(typeof(IAgentHost).GetMethod("SendTask"));
        }

        [TestMethod]
        public async Task ImprovementTool_QueuesEvenWhenBusy_InsteadOfSendingDirectly()
        {
            _host.Instances[0].SolutionPath = _data.File("VSManager.slnx");
            _host.Instances[0].Copilot = CopilotState.Busy;
            await InvokeTool("request_vsmanager_improvement", new AIFunctionArguments
            {
                ["capability"] = "Show task diagnostics", ["reason"] = "Missing diagnostic tool", ["suggestion"] = ""
            });
            var task = _host.Queue.Items.Single();
            Assert.AreEqual(QueueStatus.Waiting, task.Status);
            Assert.AreEqual(0, task.Attempts);
            StringAssert.Contains(task.Text, "Show task diagnostics");
            StringAssert.Contains(task.Text, "【开源约束】");
        }

        [TestMethod]
        public async Task SendTaskTool_WithAttachments_EnqueuesReferences()
        {
            var image = new AttachmentRef { Id = "20250101-101010123-abcdef", Name = "shot.png", Kind = AttachmentKind.Image, Ext = ".png", Size = 10, Sha256 = "aa" };
            var log = new AttachmentRef { Id = "20250101-101010124-abcdef", Name = "build.log", Kind = AttachmentKind.Text, Ext = ".log", Size = 5, Sha256 = "bb" };
            _agent.RememberAttachments(new[] { image, log });
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = "Fix the dialog", ["attachments"] = "last" });
            var task = _host.Queue.Items.Single();
            Assert.AreEqual(2, task.Attachments.Length);
            Assert.AreEqual("shot.png", task.Attachments[0].Name);
            Assert.AreEqual(QueueStatus.Waiting, task.Status);

            // 同一文字、不同附件不应复用 / Same text with different attachments must not be reused
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = "Fix the dialog", ["attachments"] = log.Id });
            Assert.AreEqual(2, _host.Queue.Items.Count);
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = "Fix the dialog", ["attachments"] = log.Id });
            Assert.AreEqual(2, _host.Queue.Items.Count);
        }

        [TestMethod]
        public async Task SendTaskTool_UnknownAttachment_IsRefused()
        {
            object result = await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = "x", ["attachments"] = "20250101-101010123-000000" });
            StringAssert.Contains(result.ToString(), "not found");
            Assert.AreEqual(0, _host.Queue.Items.Count);
            result = await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = "x", ["attachments"] = "last" });
            StringAssert.Contains(result.ToString(), "no attachments");
            Assert.AreEqual(0, _host.Queue.Items.Count);
        }

        [TestMethod]
        public async Task SendTaskTool_ClosedRegisteredTarget_IsParkedNotSent()
        {
            Assert.IsNull(_host.Solutions.Replace(new[] { new SolutionEntry { Alias = "Closed project", Path = _data.File("Closed.slnx") } }));
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "Closed project", ["task"] = "Queued for closed target" });
            var task = _host.Queue.Items.Single();
            Assert.AreEqual(QueueStatus.WaitingVs, task.Status);
            Assert.AreEqual(0, task.Attempts);
        }

        [DataTestMethod]
        [DataRow(false, "Project")]
        [DataRow(false, " project ")]
        [DataRow(true, "Project")]
        public async Task SendTaskTool_ExactMainAlias_NeverRoutesToOpenWorktree(bool mainOpen, string target)
        {
            var main = RegisterSolution("Project", "Project\\Project.slnx");
            var lane = RegisterSolution("Project.worktree.lane", "Project.worktree.lane\\Project.slnx");
            lane.Worktree = new WorktreeInfo { MainRoot = _data.File("Project"), Root = _data.File("Project.worktree.lane"),
                SolutionPath = lane.Path, MainBranch = "refs/heads/main", Branch = "refs/heads/task/lane" };
            Assert.IsNull(_host.Solutions.Upsert(lane));
            _host.Instances[0].SolutionPath = lane.Path;
            _host.Instances[0].Key = lane.Path;
            if (mainOpen) _host.Instances.Add(new VsInstance { Pid = 43, SolutionPath = main.Path, Key = main.Path });
            _host.Queue.ResolveWorktree = key => _host.Solutions.FindByPath(key)?.Worktree;

            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = target, ["task"] = "Main project task" });
            var task = _host.Queue.Items.Single();
            Assert.AreEqual(main.Path, task.VsKey);
            Assert.AreEqual(mainOpen ? QueueStatus.Waiting : QueueStatus.WaitingVs, task.Status);
            Assert.AreEqual(0, task.Attempts);
            Assert.IsNull(task.Worktree);
            Assert.IsFalse(task.WorktreeCounted);

            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = lane.Alias, ["task"] = "Isolated feature" });
            var isolated = _host.Queue.Items.Last();
            Assert.AreEqual(2, _host.Queue.Items.Count);
            Assert.AreEqual(lane.Path, isolated.VsKey);
            Assert.AreEqual(QueueStatus.Waiting, isolated.Status);
            Assert.AreEqual(lane.Worktree.Branch, isolated.Worktree.Branch);
        }

        [DataTestMethod]
        [DataRow("1")]
        [DataRow("#1")]
        [DataRow("1号")]
        [DataRow(" # 1号 ")]
        public async Task SendTaskTool_ExplicitNumber_TakesPrecedenceOverRegisteredAlias(string target)
        {
            RegisterSolution(target.Trim(), "Closed.slnx");
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = target, ["task"] = "Numbered target task" });
            var task = _host.Queue.Items.Single();
            Assert.AreEqual(_host.Instances[0].Key, task.VsKey);
            Assert.AreEqual(QueueStatus.Waiting, task.Status);
        }

        [DataTestMethod]
        [DataRow("Test VS")]
        [DataRow("Test")]
        [DataRow("Project")]
        public async Task SendTaskTool_WithoutExactAlias_PreservesNameAndPathMatching(string target)
        {
            RegisterSolution("Other", "Closed.slnx");
            _host.Instances[0].SolutionPath = _data.File("Project\\Project.slnx");
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = target, ["task"] = "Open target task" });
            var task = _host.Queue.Items.Single();
            Assert.AreEqual(_host.Instances[0].Key, task.VsKey);
            Assert.AreEqual(QueueStatus.Waiting, task.Status);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void QueuePrompts_DescribeCurrentPolicy_AndForbidBypasses(bool english)
        {
            string defaultPolicy = Prompts.AgentSystem(english, DateTime.Now, "VS", "");
            string strictPolicy = Prompts.AgentSystem(english, DateTime.Now, "VS", "", skipFailedPredecessors: false);
            StringAssert.Contains(defaultPolicy, "SkipFailedPredecessors=true");
            StringAssert.Contains(strictPolicy, "SkipFailedPredecessors=false");
            StringAssert.Contains(defaultPolicy, english ? "never writes directly" : "不直接写入");
            StringAssert.Contains(defaultPolicy, english ? "never deleted" : "历史不删除");
            StringAssert.Contains(defaultPolicy, english ? "scripts, UI typing" : "脚本、UI 输入");
            StringAssert.Contains(strictPolicy, english ? "pause successors" : "暂停同一 VS");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TaskWordingPrompts_OnlyPermitLanguageCleanup_AndRequireClarification(bool english)
        {
            string prompt = Prompts.AgentSystem(english, DateTime.Now, "VS", "");
            string[] required = english ? new[]
            {
                "language cleanup only", "independently executable Chinese", "one paragraph without line breaks",
                "correct spelling and grammar", "resolve subjects and references only", "confirmed context",
                "retain all explicit requirements and constraints as stated",
                "Forbidden: add unrequested features, requirements, acceptance criteria, technical solutions or technology recommendations, file scope or implementation details",
                "never omit explicit requirements, expand or narrow the change scope, or alter intent, goals or boundaries",
                "never turn questions into commands or split simple requests into multiple subtasks",
                "ask one key question and propose a recommended default", "wait for confirmation before dispatching",
                "never fill in missing intent yourself", "tool-appended open-source constraint",
                "only after the user explicitly requests or confirms improving"
            } : new[]
            {
                "只做语言梳理", "可独立执行的中文", "写成一段话、不要换行",
                "修正错别字与语病", "补全主语与指代", "已确认上下文",
                "要求与约束原样保留",
                "禁止：新增用户未提出的功能点、要求、验收标准、技术方案或技术选型建议、文件范围、实现细节",
                "不得删减明确要求，不得扩大或缩小改动范围，不得改变原意、目标与边界",
                "不得把疑问句改写成命令，不得把简单请求拆成多个子任务",
                "先只问一个关键问题，并给出推荐默认做法", "等待确认后再发布", "不得自行补全",
                "由工具自动附加的开源约束", "只有用户明确要求或确认完善助手能力后"
            };
            foreach (string text in required) StringAssert.Contains(prompt, text);
            StringAssert.Contains(prompt, english ? AgentService.OpenSourcePolicyEn : AgentService.OpenSourcePolicy);
            Assert.IsFalse(prompt.Contains(english ? "state your assumption" : "说明你的假设"));
            Assert.IsFalse(prompt.Contains(english ? "break development work into tasks" : "把开发任务拆解"));
            Assert.IsFalse(prompt.Contains(english ? "English instruction (goal, scope, constraints, acceptance criteria)" : "中文指令（目标、范围、约束、验收标准）"));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SendTask_PreservesQuestionsAndConstraints_OnlyNormalizesExistingFormatting(bool background)
        {
            _settings.BackgroundSend = background;
            string text = "  这个错误为什么会出现？只分析原因，不修改代码。\r\n 请保留现有行为，不新增功能。  ";
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = text });
            string expected = background ? "这个错误为什么会出现？只分析原因，不修改代码。 请保留现有行为，不新增功能。" : text.Trim();
            Assert.AreEqual(expected, _host.Queue.Items.Single().Text);
            Assert.AreEqual(QueueStatus.Waiting, _host.Queue.Items.Single().Status);
            Assert.AreEqual(0, _vision.Requests);
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task SendTask_PreservesBody_AndAppendsOpenSourceConstraintExactlyOnce(bool english, bool alreadyIncluded)
        {
            _settings.VoiceLanguage = english ? VoiceLanguages.English : VoiceLanguages.Chinese;
            _host.Instances[0].SolutionPath = _data.File("VSManager.slnx");
            string body = "仅修正文案中的错别字，不改行为。";
            string suffix = english ? AgentService.OpenSourceTaskSuffixEn : AgentService.OpenSourceTaskSuffix;
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = body + (alreadyIncluded ? suffix : "") });
            Assert.AreEqual(body + suffix, _host.Queue.Items.Single().Text);
            Assert.IsFalse(_host.Queue.Items.Single().Text.Contains("\n"));
        }

        [TestMethod]
        public async Task SendTask_OverLimit_RejectsWithoutTruncationOrAutomaticSplitting()
        {
            int limit = AppSettings.ClampQuota(nameof(AppSettings.AgentMaxTaskText), _settings.AgentMaxTaskText);
            object result = await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = "1", ["task"] = new string('x', limit + 1) });
            Assert.AreEqual(0, _host.Queue.Items.Count);
            StringAssert.Contains(result.ToString(), "不得自行删减要求或拆分任务");
            StringAssert.Contains(result.ToString(), "ask the user how to proceed");
        }

        [TestMethod]
        public void SendTaskSchema_DescribesFaithfulSingleParagraphWordingInBothLanguages()
        {
            string description = GetTool("send_task").JsonSchema.GetProperty("properties").GetProperty("task").GetProperty("description").GetString();
            StringAssert.Contains(description, "仅梳理语言");
            StringAssert.Contains(description, "不新增要求、验收标准、技术方案或范围");
            StringAssert.Contains(description, "language cleanup only");
            StringAssert.Contains(description, "one paragraph without line breaks");
            StringAssert.Contains(description, "clarify incomplete intent first");
        }

        [TestMethod]
        public async Task SolutionTools_ListRegisteredAliasesPathsAndOpenNumbers()
        {
            var entry = RegisterSolution("订单项目", "Orders.slnx", "下单");
            _host.Instances[0].SolutionPath = entry.Path;
            string solutions = (await InvokeTool("list_solutions", new AIFunctionArguments())).ToString();
            foreach (string text in new[] { entry.Alias, entry.Path, "下单", "#1" }) StringAssert.Contains(solutions, text);
            string instances = (await InvokeTool("list_vs", new AIFunctionArguments())).ToString();
            foreach (string text in new[] { entry.Alias, entry.Path, "#1" }) StringAssert.Contains(instances, text);
            Assert.AreEqual(0, _vision.Requests);
        }

        [TestMethod]
        public async Task OpenSolution_SynonymAlreadyOpen_ActivatesWithoutLaunching()
        {
            var entry = RegisterSolution("订单项目", "Orders.sln", "下单");
            _host.Instances[0].SolutionPath = entry.Path;
            string result = (await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = "下单" })).ToString();
            StringAssert.Contains(result, "未重复打开");
            Assert.AreSame(_host.Instances[0], _host.Activated.Single());
            Assert.AreEqual(0, _host.Launched.Count);
        }

        [DataTestMethod]
        [DataRow(".sln")]
        [DataRow(".slnx")]
        public async Task OpenSolution_RegisteredEnvironmentPath_ExpandsAndLaunches(string extension)
        {
            string variable = "VSM_TEST_SOLUTION_" + Guid.NewGuid().ToString("N");
            string path = _data.File("Environment" + extension);
            File.WriteAllText(path, "");
            Environment.SetEnvironmentVariable(variable, _data.Path);
            try
            {
                Assert.IsNull(_host.Solutions.Upsert(new SolutionEntry { Alias = "环境项目", Path = "\"%" + variable + "%\\Environment" + extension + "\"" }));
                _host.OpenAfterLaunch = true;
                string result = (await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = "环境项目" })).ToString();
                CollectionAssert.AreEqual(new[] { path }, _host.Launched);
                StringAssert.Contains(result, "已打开");
                Assert.AreEqual(0, _vision.Requests);
            }
            finally { Environment.SetEnvironmentVariable(variable, null); }
        }

        [TestMethod]
        public async Task OpenSolution_UnregisteredFullPath_LaunchesExactFileNotSimilarRegisteredName()
        {
            RegisterSolution("订单项目", "Orders.sln", "Orders");
            string path = _data.File("Other\\Orders.sln");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "");
            _host.OpenAfterLaunch = true;
            await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = path });
            CollectionAssert.AreEqual(new[] { path }, _host.Launched);
        }

        [TestMethod]
        public async Task OpenSolution_MissingOrUnsupportedOrIncompletePath_DoesNotLaunch()
        {
            RegisterSolution("订单项目", "Orders.sln", "Orders");
            string unsupported = _data.File("Orders.txt");
            File.WriteAllText(unsupported, "");
            string invalid = (await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = unsupported })).ToString();
            StringAssert.Contains(invalid, "Only .sln or .slnx");
            string relative = (await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = @"Other\Orders.sln" })).ToString();
            StringAssert.Contains(relative, "fully qualified");
            string registeredPath = _host.Solutions.Items.Single().Path;
            string rootRelative = "\\" + registeredPath.Substring(Path.GetPathRoot(registeredPath).Length);
            string rooted = (await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = rootRelative })).ToString();
            StringAssert.Contains(rooted, "fully qualified");
            string driveRelative = Path.GetPathRoot(registeredPath).TrimEnd('\\') + "Orders.sln";
            string drive = (await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = driveRelative })).ToString();
            StringAssert.Contains(drive, "fully qualified");
            string missing = (await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = _data.File("Missing\\Orders.sln") })).ToString();
            StringAssert.Contains(missing, "不存在");
            Assert.AreEqual(0, _host.Launched.Count);
            Assert.AreEqual(0, _host.Activated.Count);
        }

        [TestMethod]
        public async Task SolutionTools_AmbiguousAliasAndExactPath_DoNotOpenOrClose()
        {
            var first = RegisterSolution("订单统计", "Shared.sln", "订单");
            RegisterSolution("订单打印", "Shared.sln", "订单");
            _host.Instances[0].SolutionPath = first.Path;
            foreach (string query in new[] { "订单", first.Path, "Shared.sln" })
            foreach (string tool in new[] { "open_solution", "close_vs" })
            {
                string result = (await InvokeTool(tool, new AIFunctionArguments { [tool == "open_solution" ? "solution" : "target"] = query })).ToString();
                StringAssert.Contains(result, "订单统计");
                StringAssert.Contains(result, "订单打印");
                StringAssert.Contains(result, "请让用户选择");
            }
            Assert.AreEqual(0, _host.Launched.Count);
            Assert.AreEqual(0, _host.Activated.Count);
            Assert.AreEqual(0, _host.CloseChecks);
            Assert.AreEqual(0, _host.Closes);
        }

        [TestMethod]
        public async Task SolutionTools_NoMatch_ListsAliasesWithoutSideEffects()
        {
            RegisterSolution("订单项目", "Orders.sln", "下单");
            foreach (string tool in new[] { "open_solution", "close_vs" })
            {
                string result = (await InvokeTool(tool, new AIFunctionArguments { [tool == "open_solution" ? "solution" : "target"] = "天气预报" })).ToString();
                StringAssert.Contains(result, "订单项目");
                StringAssert.Contains(result, "下单");
            }
            Assert.AreEqual(0, _host.Launched.Count);
            Assert.AreEqual(0, _host.CloseChecks);
            Assert.AreEqual(0, _host.Closes);
        }

        [TestMethod]
        public async Task OpenSolution_LaunchFailure_IsReturnedWithoutPolling()
        {
            var entry = RegisterSolution("订单项目", "Orders.sln");
            File.WriteAllText(entry.Path, "");
            _host.LaunchError = "启动失败 / Launch failed";
            string result = (await InvokeTool("open_solution", new AIFunctionArguments { ["solution"] = entry.Alias })).ToString();
            Assert.AreEqual(_host.LaunchError, result);
            Assert.AreEqual(1, _host.Launched.Count);
        }

        [DataTestMethod]
        [DataRow("未保存 / Unsaved changes")]
        [DataRow("无法检查 / Cannot check unsaved changes")]
        public async Task CloseVs_UnsafeCheck_DoesNotConfirmOrClose(string refusal)
        {
            var entry = RegisterSolution("订单项目", "Orders.sln", "下单");
            _host.Instances[0].SolutionPath = entry.Path;
            _host.CloseRefusal = refusal;
            Assert.AreEqual(refusal, (await InvokeTool("close_vs", new AIFunctionArguments { ["target"] = "下单" })).ToString());
            Assert.AreEqual(1, _host.CloseChecks);
            Assert.AreEqual(0, _host.Confirmations);
            Assert.AreEqual(0, _host.Closes);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CloseVs_CheckedAndConfirmed_ClosesOnlyIfApproved(bool approved)
        {
            _settings.SolutionCloseConfirm = true;
            _host.ConfirmResult = approved;
            await InvokeTool("close_vs", new AIFunctionArguments { ["target"] = "1" });
            Assert.AreEqual(1, _host.CloseChecks);
            Assert.AreEqual(1, _host.Confirmations);
            Assert.AreEqual(approved ? 1 : 0, _host.Closes);
        }

        [TestMethod]
        public async Task CloseVs_RegisteredSynonym_ChecksAndClosesOpenTarget()
        {
            var entry = RegisterSolution("订单项目", "Orders.sln", "下单");
            _host.Instances[0].SolutionPath = entry.Path;
            _settings.SolutionCloseConfirm = false;
            Assert.AreEqual("已关闭 / Closed", (await InvokeTool("close_vs", new AIFunctionArguments { ["target"] = "下单" })).ToString());
            Assert.AreEqual(1, _host.CloseChecks);
            Assert.AreEqual(1, _host.Closes);
        }

        [TestMethod]
        public async Task CloseVs_ExplicitNumber_TakesPrecedenceOverNumericRegistryAlias()
        {
            RegisterSolution("1", "Closed.sln");
            _settings.SolutionCloseConfirm = false;
            await InvokeTool("close_vs", new AIFunctionArguments { ["target"] = "#1" });
            Assert.AreEqual(1, _host.CloseChecks);
            Assert.AreEqual(1, _host.Closes);
        }

        [TestMethod]
        public async Task CloseVs_ClosedAliasAndDifferentFullPath_NeverClosesSimilarlyNamedInstance()
        {
            var entry = RegisterSolution("订单项目", "Orders.sln", "下单");
            _host.Instances[0].SolutionPath = _data.File("Other\\Orders.sln");
            foreach (string target in new[] { "下单", entry.Path, _data.File("Missing\\Orders.sln") })
                await InvokeTool("close_vs", new AIFunctionArguments { ["target"] = target });
            Assert.AreEqual(0, _host.CloseChecks);
            Assert.AreEqual(0, _host.Closes);
        }

        [TestMethod]
        public async Task WorktreeTools_CreateRegisterOpenAndQueueToExactIsolatedSolution()
        {
            string main = _data.File("Project");
            Directory.CreateDirectory(main);
            WorktreeService.Git(main, "init", "-b", "main");
            var source = RegisterSolution("Project", "Project\\Project.slnx");
            File.WriteAllText(source.Path, "<Solution />");
            WorktreeService.Git(main, "add", "--", "Project.slnx");
            WorktreeService.Git(main, "commit", "-m", "initial");
            _settings.AgentFileRoots = new List<string> { _data.Path };
            _host.Instances[0].SolutionPath = source.Path;
            _host.OpenAfterLaunch = true;
            _host.Queue.ResolveWorktree = key => _host.Solutions.FindByPath(_host.Instances.FirstOrDefault(v => v.Key == key)?.SolutionPath ?? key)?.Worktree;
            string result = (await InvokeTool("create_worktree", new AIFunctionArguments { ["project"] = "Project", ["name"] = "lane" })).ToString();
            StringAssert.Contains(result, "Worktree registered");
            var lane = _host.Solutions.Items.Single(e => e.Worktree != null);
            Assert.AreEqual("Project.worktree.lane", lane.Alias);
            Assert.AreEqual(lane.Path, _host.Launched.Single());
            StringAssert.Contains((await InvokeTool("list_worktrees", new AIFunctionArguments())).ToString(), lane.Alias);
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = lane.Alias, ["task"] = "Implement isolated feature" });
            var task = _host.Queue.Items.Single();
            Assert.AreEqual(lane.Path, task.VsKey);
            Assert.AreEqual(lane.Worktree.Branch, task.Worktree.Branch);
            _host.Instances.RemoveAt(1);
            await InvokeTool("send_task", new AIFunctionArguments { ["vs"] = lane.Alias, ["task"] = "Next isolated feature" });
            Assert.AreEqual(QueueStatus.WaitingVs, _host.Queue.Items.Last().Status);
            Assert.AreEqual(lane.Path, _host.Queue.Items.Last().VsKey);
        }

        private SolutionEntry RegisterSolution(string alias, string filename, params string[] synonyms)
        {
            var entry = new SolutionEntry { Alias = alias, Path = _data.File(filename), Synonyms = synonyms.ToList() };
            Assert.IsNull(_host.Solutions.Upsert(entry));
            return entry;
        }

        private async Task<object> InvokeTool(string name, AIFunctionArguments arguments) => await GetTool(name).InvokeAsync(arguments);

        private AIFunction GetTool(string name)
        {
            var field = typeof(AgentService).GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var tools = (IEnumerable<AITool>)field.GetValue(_agent);
            return tools.OfType<AIFunction>().Single(t => t.Name == name);
        }

        private sealed class VisionClient : IAiClientFactory, IChatClient
        {
            internal int Created, Requests;
            internal bool Disposed;
            internal Exception Error;
            internal List<AIMessage> Messages;
            internal ChatOptions Options;
            public IChatClient Create(Uri endpoint, string model, string apiKey) { Created++; return this; }
            public Task<ChatResponse> GetResponseAsync(IEnumerable<AIMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                Requests++;
                Messages = messages.ToList(); Options = options;
                if (Error != null) throw Error;
                return Task.FromResult(new ChatResponse(new AIMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "A dialog has an OK button")));
            }
            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<AIMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { Disposed = true; }
        }

        private sealed class DesktopHost : IAgentHost, IAgentDesktopHost, IAgentCopilotPaneHost, IAgentAttachmentHost
        {
            public Task<string> QueueTask(VsInstance v, string text, AttachmentRef[] attachments) =>
                Task.FromResult("Queued @" + Queue.Add(v.Key, NameOf(v), text, "AI", attachments).Id);
            public Task<string> ParkTask(SolutionEntry e, string text, AttachmentRef[] attachments) =>
                Task.FromResult("Parked @" + Queue.Add(e.Path, e.Alias, text, "AI", attachments, parked: true).Id);
            internal readonly List<VsInstance> PaneOpened = new List<VsInstance>();
            public Task<string> OpenCopilotPane(VsInstance vs) { PaneOpened.Add(vs); return Task.FromResult("已打开对话助手 / Copilot chat opened"); }
            public IList<VsInstance> Instances { get; } = new List<VsInstance>();
            public SolutionRegistry Solutions { get; } = new SolutionRegistry();
            internal TaskQueue Queue;
            internal Func<string, Task<bool>> Approve = _ => Task.FromResult(false);
            internal int Approvals, Captures, Confirmations, CloseChecks, Closes;
            internal string Detail, Destination, LaunchError, CloseRefusal;
            internal bool OpenAfterLaunch;
            internal bool? ConfirmResult;
            internal readonly List<VsInstance> Activated = new List<VsInstance>();
            internal readonly List<string> Launched = new List<string>();
            internal byte[] Image;
            public Task<bool> ApprovePowerShell(string detail, CancellationToken cancellationToken) { Approvals++; Detail = detail; return Approve(detail); }
            public Task<byte[]> CaptureApprovedScreenshot(VsInstance vs, string destination, CancellationToken cancellationToken) { Captures++; Destination = destination; return Task.FromResult(Image); }
            public string NameOf(VsInstance v) => "Test VS";
            public string NoteOf(VsInstance v) => "";
            public Task<bool> Confirm(string title, string detail)
            {
                if (!ConfirmResult.HasValue) throw new AssertFailedException("General approval must not bypass desktop approval");
                Confirmations++;
                return Task.FromResult(ConfirmResult.Value);
            }
            public Task<string> SetNote(VsInstance v, string note) => throw new NotSupportedException();
            public Task<ChatTranscript> ReadChat(VsInstance v, int maxMessages) => throw new NotSupportedException();
            public Task<string> QueueTask(VsInstance v, string text) => Task.FromResult("Queued @" + Queue.Add(v.Key, NameOf(v), text, "AI").Id);
            public Task<string> ListTasks() => throw new NotSupportedException();
            public Task<string> CancelTask(int id) => throw new NotSupportedException();
            public Task<string> DebugAction(VsInstance v, string action) => throw new NotSupportedException();
            public Task<string> InvokeChatButton(VsInstance v, string automationId, string name) => throw new NotSupportedException();
            public Task<string> Activate(VsInstance v) { Activated.Add(v); return Task.FromResult("已激活 / Activated"); }
            public Task<string> ErrorList(VsInstance v, int max) => throw new NotSupportedException();
            public Task<string> DockPanes() => throw new NotSupportedException();
            public Task<string> ArrangeCopilotPanes(IList<VsInstance> targets, int screen, PaneArrangement arrangement, bool minimize) => throw new NotSupportedException();
            public Task<string> RestoreCopilotLayout() => throw new NotSupportedException();
            public VsInstance FindOpenSolution(SolutionEntry e) => SolutionMatcher.FindOpenSolution(e, Instances, Solutions.Items);
            public Task<string> ParkTask(SolutionEntry e, string text) => Task.FromResult("Parked @" + Queue.AddParked(e.Path, e.Alias, text, "AI").Id);
            public Task<string> LaunchSolution(string path)
            {
                Launched.Add(path);
                if (LaunchError == null && OpenAfterLaunch) Instances.Add(new VsInstance { SolutionPath = path, Key = path, Pid = 43 });
                return Task.FromResult(LaunchError);
            }
            public Task<string> CheckCanClose(VsInstance v) { CloseChecks++; return Task.FromResult(CloseRefusal); }
            public Task<string> CloseVs(VsInstance v) { Closes++; return Task.FromResult("已关闭 / Closed"); }
        }
    }
}
