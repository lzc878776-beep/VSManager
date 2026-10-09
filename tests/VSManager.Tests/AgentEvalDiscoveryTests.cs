using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>读取代码自动选择评测目录，以及全部问题清单。/ Selecting the evaluation folder from code, and the full issue list.</summary>
    [TestClass]
    [DoNotParallelize]
    public class AgentEvalDiscoveryTests
    {
        private const string Var = "VSM_EVAL_DISCOVERY_TEST";
        private string _root;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable(Var, Path.Combine(_root, "data"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            Environment.SetEnvironmentVariable(Var, null);
            try { Directory.Delete(_root, true); } catch { }
        }

        [TestMethod]
        public void Extract_ReadsEnvironmentFoldersFromScriptsAndCode()
        {
            CollectionAssert.Contains(AgentEvalDiscovery.Extract("$evalRoot = Join-Path $env:LOCALAPPDATA 'Tool\\Agent\\Eval'").ToList(), "%LOCALAPPDATA%\\Tool\\Agent\\Eval");
            CollectionAssert.Contains(AgentEvalDiscovery.Extract(
                "var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), \"Tool\", \"Eval\", \"runs\", DateTime.Now.ToString(\"yyyyMMdd\"));").ToList(),
                "%LOCALAPPDATA%\\Tool\\Eval");
            CollectionAssert.Contains(AgentEvalDiscovery.Extract("root = os.path.join(os.environ['APPDATA'], 'Tool', 'eval', 'result.json')").ToList(), "%APPDATA%\\Tool\\eval");
            CollectionAssert.Contains(AgentEvalDiscovery.Extract("// 结果写到 %LOCALAPPDATA%\\Tool\\Eval（每次一个子目录）").ToList(), "%LOCALAPPDATA%\\Tool\\Eval");
            Assert.AreEqual(0, AgentEvalDiscovery.Extract("var x = 100; // no folder here").Count());
        }

        [TestMethod]
        public void Discover_SelectsTheFolderWithRuns_AndKeepsTheVariableForm()
        {
            string repo = Path.Combine(_root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            Directory.CreateDirectory(Path.Combine(repo, "tests", "Eval"));
            Directory.CreateDirectory(Path.Combine(repo, "src", "App"));
            File.WriteAllText(Path.Combine(repo, "src", "App", "App.sln"), "");
            File.WriteAllText(Path.Combine(repo, "tests", "Eval", "Run-Eval.ps1"),
                "$evalRoot = Join-Path $env:" + Var + " 'Tool\\Eval'\r\n$logs = Join-Path $env:" + Var + " 'Tool\\Logs'\r\n$resultPath = Join-Path $runDirectory 'result.json'\r\n");
            // 不含评测标志的代码不读取 / Code without evaluation marks is ignored
            File.WriteAllText(Path.Combine(repo, "src", "App", "Other.cs"), "var p = Path.Combine(Environment.GetEnvironmentVariable(\"" + Var + "\"), \"Other\");");
            string runs = Path.Combine(_root, "data", "Tool", "Eval", "runs");
            Directory.CreateDirectory(Path.Combine(runs, "20260101-090000"));
            File.WriteAllText(Path.Combine(runs, "20260101-090000", "cases.json"), "{}");
            Directory.CreateDirectory(Path.Combine(_root, "data", "Other", "runs", "20260102-090000"));
            Directory.CreateDirectory(Path.Combine(_root, "data", "Tool", "Logs"));

            var roots = AgentEvalDiscovery.CodeRoots(new[] { Path.Combine(repo, "src", "App", "App.sln"), Path.Combine(repo, "tests") });
            CollectionAssert.AreEqual(new[] { repo }, roots, "解决方案上溯到仓库根并去重 / Solutions climb to the repo root, deduplicated");
            var found = AgentEvalDiscovery.Discover(roots);
            var best = AgentEvalDiscovery.Best(found);
            Assert.IsNotNull(best);
            Assert.AreEqual("%" + Var + "%\\Tool\\Eval", best.Display);
            Assert.AreEqual(1, best.RunCount);
            Assert.AreEqual(new DateTime(2026, 1, 1, 9, 0, 0), best.Latest);
            StringAssert.Contains(best.Sources.Single(), "Run-Eval.ps1");
            Assert.IsFalse(found.Any(c => c.Display.Contains("Other")));
            Assert.IsFalse(found.Single(c => c.Display.EndsWith("Logs")).HasRuns);
            string text = AgentEvalDiscovery.Describe(found, roots);
            StringAssert.Contains(text, "✓ %" + Var + "%\\Tool\\Eval");
            Assert.IsFalse(text.Contains(_root), "说明只用环境变量形式 / The description uses the variable form only");
        }

        [TestMethod]
        public void Discover_WithoutEvaluationCode_FindsNothing()
        {
            File.WriteAllText(Path.Combine(_root, "a.cs"), "class A {}");
            Assert.IsNull(AgentEvalDiscovery.Best(AgentEvalDiscovery.Discover(new[] { _root })));
            Assert.AreEqual(0, AgentEvalDiscovery.CodeRoots(new[] { "", Path.Combine(_root, "missing") }).Count);
        }

        [TestMethod]
        public void DescribeIssues_ListsEveryIssue_AndFiltersByState()
        {
            var runs = Enumerable.Range(0, 3).Select(r => new AgentEvalRun
            {
                Name = "run" + r, Time = new DateTime(2026, 1, 1).AddDays(r), State = AgentEvalState.Failed,
                Result = new AgentEvalResult
                {
                    Conversations = Enumerable.Range(0, 30).Select(c => new AgentEvalConversation
                    {
                        Name = "c" + c,
                        Turns = new[] { new AgentEvalTurn { Index = 1, Prompt = "q" + c, Passed = c % 2 == 0 && r == 0 ? false : c % 2 == 0, Error = "boom" } }.ToList()
                    }).ToList()
                }
            }).Reverse().ToList();
            string all = AgentEvalStore.DescribeIssues(runs);
            // 15 个一直失败 + 15 个后来恢复 / 15 always failing + 15 resolved later
            StringAssert.Contains(all, "问题共 30 个");
            for (int c = 0; c < 30; c++) StringAssert.Contains(all, "c" + c + "#1");
            string failing = AgentEvalStore.DescribeIssues(runs, "failing");
            Assert.AreEqual(15, failing.Split('\n').Count(l => l.Contains("[仍失败")));
            Assert.IsFalse(failing.Contains("[已恢复"));
        }

        [TestMethod]
        public void Page_ContinuesWithOffset_WithoutLosingText()
        {
            string text = string.Join("\n", Enumerable.Range(0, 400).Select(i => "line " + i));
            string first = AgentService.Page(text, 0, 600);
            StringAssert.Contains(first, "offset=");
            int offset = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"offset=(\d+)").Groups[1].Value);
            string rebuilt = first.Substring(0, first.IndexOf("\n……", StringComparison.Ordinal));
            while (true)
            {
                string part = AgentService.Page(text, offset, 600);
                var m = System.Text.RegularExpressions.Regex.Match(part, @"offset=(\d+)");
                if (!m.Success) { rebuilt += part; break; }
                rebuilt += part.Substring(0, part.IndexOf("\n……", StringComparison.Ordinal));
                offset = int.Parse(m.Groups[1].Value);
            }
            Assert.AreEqual(text.Replace("\n", ""), rebuilt.Replace("\n", ""));
        }

        [TestMethod]
        public void AgentTools_IncludeTheEvalSkill()
        {
            var settings = new AppSettings { AgentEndpoint = "http://localhost:11434/v1", AgentModel = "test", AgentConfirm = false };
            using (var manager = new AgentService(new AgentDesktopTests.DesktopHost(), () => settings))
            {
                var field = typeof(AgentService).GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var names = ((System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.AITool>)field.GetValue(manager)).Select(t => t.Name).ToList();
                CollectionAssert.IsSubsetOf(new[] { "find_agent_eval_folder", "list_agent_eval_issues" }, names);
            }
            Assert.AreEqual("eval", AgentToolGroups.GroupOf("find_agent_eval_folder"));
            CollectionAssert.Contains(AgentToolGroups.Triggered("看看 agent 自循环的问题").ToList(), "eval");
        }
    }
}
