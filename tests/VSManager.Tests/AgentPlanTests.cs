using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>
    /// 执行计划缓存：创建 / 更新 / 读回 / 释放、跨重启持久化、工具注册与提示词规则。
    /// Execution plan cache: create / update / read / release, persistence across restarts, tool registration and prompt rules.
    /// </summary>
    [TestClass]
    public class AgentPlanTests
    {
        private string _dir;
        private string _path;
        private static readonly DateTime Now = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsm-plan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "agent-plans.json");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string Create(string title = "补 skill 闭环", string scope = "", params string[] steps) =>
            AgentPlans.Create(title, "目标 / goal", steps.Length > 0 ? steps : new[] { "写代码", "编译", "重启验证" }, scope, Now, _path);

        [TestMethod]
        public void Create_PersistsSteps_AndSurvivesReload()
        {
            string r = Create();
            StringAssert.Contains(r, "#1");
            Assert.IsTrue(File.Exists(_path));
            // 模拟新进程：重新从磁盘读取 / Simulate a new process: read again from disk
            var plans = AgentPlans.Active(out string error, _path);
            Assert.IsNull(error);
            Assert.AreEqual(1, plans.Count);
            CollectionAssert.AreEqual(new[] { "写代码", "编译", "重启验证" }, plans[0].Steps.Select(s => s.Title).ToArray());
            Assert.IsTrue(plans[0].Steps.All(s => s.Status == AgentPlans.Pending));
        }

        [TestMethod]
        public void Create_StripsNumbering_AndSplitsLines()
        {
            Create("t", "", "1. 第一步\n2) 第二步", "- 第三步");
            var plan = AgentPlans.Active(out _, _path).Single();
            CollectionAssert.AreEqual(new[] { "第一步", "第二步", "第三步" }, plan.Steps.Select(s => s.Title).ToArray());
        }

        [TestMethod]
        public void Create_RejectsSimpleOrDuplicate()
        {
            StringAssert.StartsWith(Create("t", "", "只有一步"), "⚠");
            Assert.IsFalse(File.Exists(_path));
            Create("dup");
            StringAssert.StartsWith(Create("dup"), "⚠");
            Assert.AreEqual(1, AgentPlans.Active(out _, _path).Count);
        }

        [TestMethod]
        public void Create_EnforcesActiveLimit()
        {
            for (int i = 0; i < AgentPlans.MaxActivePlans; i++) StringAssert.StartsWith(Create("p" + i), "已创建");
            StringAssert.StartsWith(Create("overflow"), "⚠");
        }

        [TestMethod]
        public void UpdateStep_ChangesStatus_AcceptsAliases_AndPointsToNext()
        {
            Create();
            string r = AgentPlans.UpdateStep(0, 1, "完成", "编译通过", Now.AddMinutes(1), _path);
            StringAssert.Contains(r, "已更新");
            StringAssert.Contains(r, "编译");
            var plan = AgentPlans.Active(out _, _path).Single();
            Assert.AreEqual(AgentPlans.Done, plan.Steps[0].Status);
            Assert.AreEqual("编译通过", plan.Steps[0].Note);
            Assert.AreEqual(Now.AddMinutes(1), plan.UpdatedUtc);
            Assert.AreEqual(AgentPlans.InProgress, AgentPlans.NormalizeStatus("进行中"));
            Assert.AreEqual(AgentPlans.Skipped, AgentPlans.NormalizeStatus("SKIPPED"));
            Assert.IsNull(AgentPlans.NormalizeStatus("whatever"));
            StringAssert.StartsWith(AgentPlans.UpdateStep(0, 1, "whatever", null, Now, _path), "⚠");
            StringAssert.StartsWith(AgentPlans.UpdateStep(0, 9, "done", null, Now, _path), "⚠");
        }

        [TestMethod]
        public void UpdateStep_AllFinished_HintsCompletePlan()
        {
            Create("t", "", "a", "b");
            AgentPlans.UpdateStep(0, 1, "done", null, Now, _path);
            StringAssert.Contains(AgentPlans.UpdateStep(0, 2, "skipped", null, Now, _path), "complete_plan");
        }

        [TestMethod]
        public void PlanIdZero_IsAmbiguousWithTwoPlans()
        {
            Create("a");
            Create("b");
            StringAssert.StartsWith(AgentPlans.UpdateStep(0, 1, "done", null, Now, _path), "⚠");
            StringAssert.Contains(AgentPlans.UpdateStep(2, 1, "done", null, Now, _path), "已更新");
            Assert.AreEqual(AgentPlans.Done, AgentPlans.Active(out _, _path).Single(p => p.Id == 2).Steps[0].Status);
        }

        [TestMethod]
        public void Complete_RefusesUnfinished_UnlessForced()
        {
            Create();
            StringAssert.StartsWith(AgentPlans.Complete(0, "x", false, _path), "⚠");
            Assert.AreEqual(1, AgentPlans.Active(out _, _path).Count);
            StringAssert.Contains(AgentPlans.Complete(0, "用户取消", true, _path), "forced");
            Assert.AreEqual(0, AgentPlans.Active(out _, _path).Count);
        }

        [TestMethod]
        public void Complete_ReleasesFinishedPlan_AndIdsKeepIncreasing()
        {
            Create("t", "", "a", "b");
            AgentPlans.UpdateStep(0, 1, "done", null, Now, _path);
            AgentPlans.UpdateStep(0, 2, "done", null, Now, _path);
            StringAssert.Contains(AgentPlans.Complete(0, "ok", false, _path), "released");
            Assert.AreEqual(0, AgentPlans.Active(out _, _path).Count);
            StringAssert.Contains(Create("next"), "#2");
        }

        [TestMethod]
        public void CorruptFile_ReportsError_AndIsLeftUnchanged()
        {
            File.WriteAllText(_path, "{ not json");
            AgentPlans.Active(out string error, _path);
            Assert.IsNotNull(error);
            StringAssert.StartsWith(Create(), "⚠");
            Assert.AreEqual("{ not json", File.ReadAllText(_path));
        }

        [TestMethod]
        public void ResumeNotice_AndPromptSummary_ListNextStep()
        {
            Create();
            AgentPlans.UpdateStep(0, 1, "done", null, Now, _path);
            var plans = AgentPlans.Active(out _, _path);
            string notice = AgentPlans.ResumeNotice(plans);
            StringAssert.Contains(notice, "[执行计划恢复");
            StringAssert.Contains(notice, "read_plan");
            StringAssert.Contains(notice, "编译");
            StringAssert.Contains(AgentPlans.PromptSummary(plans), "编译");
            Assert.IsNull(AgentPlans.PromptSummary(new List<AgentPlan>()));
        }

        [TestMethod]
        public void RestartProbe_DescribesResidentPlanFile()
        {
            StringAssert.Contains(RestartProbe.DescribePlans(_path), "Not present");
            Create();
            StringAssert.Contains(RestartProbe.DescribePlans(_path), "#1 补 skill 闭环（0/3）");
        }

        [TestMethod]
        public async Task Tools_AreRegistered_Core_AndWorkThroughAgent()
        {
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
            {
                agent.PlanFilePath = _path;
                var tools = ((IEnumerable<AITool>)typeof(AgentService).GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(agent))
                    .OfType<AIFunction>().ToDictionary(t => t.Name);
                var advertised = agent.ToolsForRound().Select(t => t.Name).ToList();
                foreach (var name in new[] { "create_plan", "update_plan_step", "read_plan", "complete_plan" })
                {
                    Assert.IsTrue(tools.ContainsKey(name), name);
                    CollectionAssert.Contains(advertised, name);
                }

                string r = (await tools["create_plan"].InvokeAsync(new AIFunctionArguments { ["title"] = "多阶段验证", ["steps"] = new[] { "a", "b" } }))?.ToString();
                StringAssert.Contains(r, "#1");
                r = (await tools["update_plan_step"].InvokeAsync(new AIFunctionArguments { ["step"] = 1, ["status"] = "done", ["note"] = "ok" }))?.ToString();
                StringAssert.Contains(r, "已更新");
                r = (await tools["read_plan"].InvokeAsync(new AIFunctionArguments()))?.ToString();
                StringAssert.Contains(r, "多阶段验证");

                string prompt = (string)typeof(AgentService).GetMethod("SystemPrompt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(agent, null);
                StringAssert.Contains(prompt, "进行中的执行计划");
                StringAssert.Contains(prompt, "多阶段验证");

                r = (await tools["complete_plan"].InvokeAsync(new AIFunctionArguments { ["summary"] = "s" }))?.ToString();
                StringAssert.StartsWith(r, "⚠");
                await tools["update_plan_step"].InvokeAsync(new AIFunctionArguments { ["step"] = 2, ["status"] = "done" });
                r = (await tools["complete_plan"].InvokeAsync(new AIFunctionArguments { ["summary"] = "s" }))?.ToString();
                StringAssert.Contains(r, "released");
                prompt = (string)typeof(AgentService).GetMethod("SystemPrompt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(agent, null);
                Assert.IsFalse(prompt.Contains("进行中的执行计划"));

                string step = (string)typeof(AgentService).GetMethod("DescribeCall", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(agent, new object[] { new FunctionCallContent("s", "create_plan", new Dictionary<string, object> { ["title"] = "多阶段验证" }) });
                StringAssert.Contains(step, "创建执行计划「多阶段验证」");
            }
        }

        [TestMethod]
        public void Prompt_ContainsPlanRule_InBothLanguages()
        {
            string zh = Prompts.AgentSystem(false, DateTime.Today, "", "");
            StringAssert.Contains(zh, "20. 执行计划");
            StringAssert.Contains(zh, "简单请求");
            string en = Prompts.AgentSystem(true, DateTime.Today, "", "");
            StringAssert.Contains(en, "20. Execution plans");
            StringAssert.Contains(en, "Do not create plans for simple requests");
        }
    }
}
