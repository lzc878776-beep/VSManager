using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class WorkspaceLayoutPlanTests
    {
        internal static WorkspaceDisplaySnapshot Screens(int count)
        {
            var snapshot = new WorkspaceDisplaySnapshot { CurrentScreen = 1, ManagerScreen = 1 };
            for (int i = 0; i < count; i++)
                snapshot.Displays.Add(new WorkspaceDisplay { Number = i + 1, Primary = i == 0,
                    Bounds = new Rectangle((i - 1) * 1920, 0, 1920, 1080), WorkingArea = new Rectangle((i - 1) * 1920, 0, 1920, 1040) });
            return snapshot;
        }

        private static WorkspaceLayoutPlan Plan(WorkspaceDisplaySnapshot screens, int count = 3, int main = 0, int panes = 0, bool output = true, bool errors = true) =>
            WorkspaceLayoutPlan.Create(screens, Enumerable.Range(0, count).Select(i => new VsInstance { Pid = i + 1 }).ToList(),
                Enumerable.Range(0, count).Select(i => "VS " + (i + 1)).ToList(), main, panes, output, errors);

        [TestMethod]
        public void DiscoveryGeometry_MatchesLiveDisplaysWithoutMovingWindows()
        {
            var screens = ScreenHelper.Ordered();
            var snapshot = (WorkspaceDisplaySnapshot)typeof(MainForm).GetMethod("CaptureDisplayGeometry", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { screens });
            Assert.AreEqual(screens.Count, snapshot.Displays.Count);
            for (int i = 0; i < screens.Count; i++)
            {
                Assert.AreEqual(i + 1, snapshot.Displays[i].Number);
                Assert.AreEqual(screens[i].Bounds, snapshot.Displays[i].Bounds);
                Assert.AreEqual(screens[i].WorkingArea, snapshot.Displays[i].WorkingArea);
                Assert.AreEqual(screens[i].Primary, snapshot.Displays[i].Primary);
            }
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(5)]
        public void AutoPlan_AllWindowsInsideWorkAreasAndNonOverlapping(int screenCount)
        {
            var screens = Screens(screenCount);
            var plan = Plan(screens);
            Assert.AreEqual(3, plan.Placements.Count);
            var rectangles = plan.Placements.SelectMany(p => new[] { p.MainBounds, p.CopilotBounds, p.OutputBounds, p.ErrorListBounds }).ToList();
            foreach (var r in rectangles)
            {
                Assert.IsTrue(r.Width > 0 && r.Height > 0);
                Assert.IsTrue(screens.Displays.Any(s => s.WorkingArea.Contains(r)), r.ToString());
            }
            for (int i = 0; i < rectangles.Count; i++)
                for (int j = i + 1; j < rectangles.Count; j++) Assert.IsFalse(rectangles[i].IntersectsWith(rectangles[j]));
            Assert.AreEqual(screenCount == 1 ? 1 : 2, plan.PaneScreen);
        }

        [TestMethod]
        public void AutoPlan_UsesCurrentScreenAndLargestOtherWorkArea()
        {
            var screens = Screens(3);
            screens.CurrentScreen = 2;
            screens.Displays[2].Bounds = new Rectangle(1920, -120, 2560, 1440);
            screens.Displays[2].WorkingArea = new Rectangle(1920, -120, 2560, 1400);
            var plan = Plan(screens);
            Assert.AreEqual(2, plan.MainScreens[0]);
            Assert.AreEqual(3, plan.PaneScreen);
            Assert.AreEqual(1, plan.MainScreens[1]);
            StringAssert.Contains(screens.Describe(), "2560x1440");
            StringAssert.Contains(screens.Describe(), "Position=(1920,-120)");
            StringAssert.Contains(screens.Describe(), "Primary=True");
        }

        [TestMethod]
        public void ExplicitScreens_AndOptionalPanes_AreRespected()
        {
            var screens = Screens(3);
            var plan = Plan(screens, 1, 3, 1, false, false);
            CollectionAssert.AreEqual(new[] { 3 }, plan.MainScreens);
            Assert.AreEqual(1, plan.PaneScreen);
            Assert.AreEqual(screens.Displays[2].WorkingArea, plan.Placements[0].MainBounds);
            Assert.AreEqual(screens.Displays[0].WorkingArea, plan.Placements[0].CopilotBounds);
            Assert.AreEqual(Rectangle.Empty, plan.Placements[0].OutputBounds);
            Assert.AreEqual(Rectangle.Empty, plan.Placements[0].ErrorListBounds);
        }

        [TestMethod]
        public void SameScreen_SplitsWithoutOverlapAndWarnsWhenSmall()
        {
            var plan = Plan(Screens(2), 8, 2, 2);
            Assert.IsTrue(plan.Cramped);
            foreach (var p in plan.Placements) Assert.IsTrue(p.MainBounds.Right <= p.CopilotBounds.Left);
            StringAssert.Contains(plan.Describe(), "Limited space");
        }

        [TestMethod]
        public void Plan_RejectsReplacedProcessOrMainWindowIdentity()
        {
            var plan = Plan(Screens(2), 1);
            var vs = plan.Placements[0].Vs;
            Assert.IsTrue(plan.TargetsUnchanged);
            vs.StartTicks++;
            Assert.IsFalse(plan.TargetsUnchanged);
            vs.StartTicks--;
            vs.MainHwnd = new IntPtr(42);
            Assert.IsFalse(plan.TargetsUnchanged);
        }

        [TestMethod]
        public void InvalidScreens_AreRejectedAndUnknownCurrentUsesPrimary()
        {
            Assert.ThrowsException<ArgumentException>(() => Plan(Screens(0)));
            Assert.ThrowsException<ArgumentException>(() => Plan(Screens(2), main: -1));
            Assert.ThrowsException<ArgumentException>(() => Plan(Screens(2), panes: 3));
            var screens = Screens(2);
            screens.CurrentScreen = 0;
            Assert.AreEqual(1, Plan(screens).MainScreens[0]);
            string signature = screens.Signature;
            screens.CurrentScreen = 2;
            Assert.AreEqual(signature, screens.Signature);
            screens.Displays[0].WorkingArea = new Rectangle(-1920, 20, 1920, 1020);
            Assert.AreNotEqual(signature, screens.Signature);
        }
    }

    [TestClass]
    public class WorkspaceLayoutToolTests
    {
        private static async Task<string> Invoke(AgentService agent, string name, AIFunctionArguments arguments = null)
        {
            var tools = (IEnumerable<AITool>)typeof(AgentService).GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(agent);
            return (await tools.OfType<AIFunction>().Single(t => t.Name == name).InvokeAsync(arguments ?? new AIFunctionArguments()))?.ToString();
        }

        [TestMethod]
        public async Task Discovery_IsReadOnlyAndWorksFromBackgroundThread()
        {
            var host = new AgentDesktopTests.DesktopHost { Displays = WorkspaceLayoutPlanTests.Screens(2) };
            using (var agent = new AgentService(host, () => new AppSettings { AgentConfirm = true }))
            {
                var result = await Task.Run(() => Invoke(agent, "get_displays"));
                StringAssert.Contains(result, "Screen count: 2");
                StringAssert.Contains(result, "1920x1080");
                Assert.AreEqual(0, host.Confirmations);
                Assert.IsNull(host.LayoutPlan);
                Assert.AreEqual(0, host.LayoutRestores);
            }
        }

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(true, true)]
        [DataRow(false, false)]
        public async Task LayoutAndRestore_RespectApproval(bool confirm, bool approve)
        {
            var host = new AgentDesktopTests.DesktopHost { Displays = WorkspaceLayoutPlanTests.Screens(2), ConfirmResult = approve };
            host.Instances.Add(new VsInstance { Pid = 100, Key = "one" });
            using (var agent = new AgentService(host, () => new AppSettings { AgentConfirm = confirm }))
            {
                await Task.Run(() => Invoke(agent, "arrange_workspace_layout"));
                bool allowed = !confirm || approve;
                Assert.AreEqual(allowed, host.LayoutPlan != null);
                if (allowed)
                {
                    Assert.AreEqual(host.Displays.Signature, host.LayoutSignature);
                    Assert.IsTrue(host.LayoutPlan.IncludeOutput);
                    Assert.IsFalse(host.LayoutPlan.IncludeErrorList);
                    Assert.AreSame(host.Instances[0], host.LayoutPlan.Placements[0].Vs);
                }
                await Invoke(agent, "restore_workspace_layout");
                Assert.AreEqual(allowed ? 1 : 0, host.LayoutRestores);
                Assert.AreEqual(confirm ? 2 : 0, host.Confirmations);
            }
        }

        [TestMethod]
        public async Task InvalidTargetOrScreen_DoesNotApproveOrMove()
        {
            var host = new AgentDesktopTests.DesktopHost { Displays = WorkspaceLayoutPlanTests.Screens(2), ConfirmResult = true };
            host.Instances.Add(new VsInstance { Pid = 100, Key = "one" });
            using (var agent = new AgentService(host, () => new AppSettings { AgentConfirm = true }))
            {
                await Invoke(agent, "arrange_workspace_layout", new AIFunctionArguments { ["vs"] = "99" });
                await Invoke(agent, "arrange_workspace_layout", new AIFunctionArguments { ["paneScreen"] = 3 });
                Assert.IsNull(host.LayoutPlan);
                Assert.AreEqual(0, host.Confirmations);
                host.Instances.Clear();
                StringAssert.Contains(await Invoke(agent, "arrange_workspace_layout"), "No running");
            }
        }

        [TestMethod]
        public async Task TargetSelection_DeduplicatesAndKeepsRequestedPanes()
        {
            var host = new AgentDesktopTests.DesktopHost { Displays = WorkspaceLayoutPlanTests.Screens(2) };
            host.Instances.Add(new VsInstance { Pid = 100, Key = "one" });
            host.Instances.Add(new VsInstance { Pid = 101, Key = "two" });
            using (var agent = new AgentService(host, () => new AppSettings()))
            {
                await Invoke(agent, "arrange_workspace_layout", new AIFunctionArguments { ["vs"] = "2,2", ["mainScreen"] = 2, ["paneScreen"] = 1, ["includeOutput"] = false, ["includeErrorList"] = true });
                Assert.AreEqual(1, host.LayoutPlan.Placements.Count);
                Assert.AreSame(host.Instances[1], host.LayoutPlan.Placements[0].Vs);
                Assert.IsFalse(host.LayoutPlan.IncludeOutput);
                Assert.IsTrue(host.LayoutPlan.IncludeErrorList);
            }
        }

        [TestMethod]
        public void PromptAndSteps_ExplainDiscoveryLayoutAndRestore()
        {
            foreach (bool english in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(english, DateTime.Today, "", "");
                StringAssert.Contains(prompt, "get_displays");
                StringAssert.Contains(prompt, "arrange_workspace_layout");
                StringAssert.Contains(prompt, "restore_workspace_layout");
                StringAssert.Contains(prompt, "arrange_copilot_panes");
            }
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
                foreach (var name in new[] { "get_displays", "arrange_workspace_layout", "restore_workspace_layout" })
                {
                    string step = (string)typeof(AgentService).GetMethod("DescribeCall", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(agent, new object[] { new FunctionCallContent("step", name, new Dictionary<string, object>()) });
                    Assert.IsTrue(step.Contains(" / "), step);
                }
        }
    }
}
