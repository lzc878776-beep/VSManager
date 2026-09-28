using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>CAD 调试图纸：启动参数注入、找不到时打开新图、设置与 AI 工具记录。/ CAD debug drawings: argument injection, new-drawing fallback, settings and AI tool recording.</summary>
    [TestClass]
    [DoNotParallelize]
    public class CadDebugDrawingTests
    {
        private string _dir;

        [TestInitialize] public void Init() { _dir = Path.Combine(Path.GetTempPath(), "VSManagerCadDrawing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
        [TestCleanup] public void Cleanup() { VsCadDebug.DrawingLookup = null; try { Directory.Delete(_dir, true); } catch { } }

        [TestMethod]
        public void Inject_PutsDrawingFirst_AndStripRemovesItWithScript()
        {
            string script = @"C:\Temp\VSManager\CadDebug\Plugin.scr";
            string injected = CadDebugPlan.Inject("/nologo", script, @"C:\Work\Test Plan.dwg");
            Assert.AreEqual("\"C:\\Work\\Test Plan.dwg\" /b \"" + script + "\" /nologo", injected);
            Assert.AreEqual("/nologo", CadDebugPlan.StripInjected(injected));
            Assert.AreEqual("/b \"" + script + "\" /nologo", CadDebugPlan.Inject(injected, script));
            Assert.AreEqual("/b \"" + script + "\"", CadDebugPlan.Inject("", script, @"C:\Work\notes.txt"));
            // 用户自己的图纸在本工具参数之后，不会被去除。/ The user's own drawing after our arguments is kept.
            Assert.AreEqual("\"C:\\Mine.dwg\"", CadDebugPlan.StripInjected("/b \"" + script + "\" \"C:\\Mine.dwg\""));
        }

        [DataTestMethod]
        [DataRow("/nologo \"C:\\A B\\x.dwg\"", true)]
        [DataRow("C:\\x.DXF /nologo", true)]
        [DataRow("/nologo", false)]
        [DataRow("/b \"C:\\s.scr\"", false)]
        [DataRow("\"C:\\d.dwg\" /b \"C:\\Temp\\VSManager\\CadDebug\\P.scr\"", false)]
        public void HasUserDrawing_DetectsOnlyUserArguments(string args, bool expected) =>
            Assert.AreEqual(expected, CadDebugPlan.HasUserDrawing(args));

        [DataTestMethod]
        [DataRow(@"C:\a.dwg", true)]
        [DataRow(@"C:\a.DXF", true)]
        [DataRow(@"C:\a.dwt", false)]
        [DataRow("C:\\a\"b.dwg", false)]
        [DataRow("", false)]
        public void IsDrawingPath(string path, bool expected) => Assert.AreEqual(expected, CadDebugPlan.IsDrawingPath(path));

        private VsInstance Make(string args, out VsCadDebugTests.Project project)
        {
            var dte = new VsCadDebugTests.Dte();
            var p = new VsCadDebugTests.Project { FullName = Path.Combine(_dir, "Plugin.csproj") };
            p.Properties.Set("OutputFileName", "Plugin.dll");
            p.ConfigurationManager.ActiveConfiguration.Properties
                .Set("StartAction", 1).Set("StartProgram", @"C:\Program Files\Autodesk\AutoCAD 2024\acad.exe").Set("StartArguments", args).Set("OutputPath", @"bin\Debug\");
            dte.Solution.Projects.Add(p);
            dte.Solution.SolutionBuild.StartupProjects = new object[] { p.UniqueName };
            project = p;
            return new VsInstance { Pid = 880000 + Environment.TickCount % 10000, Dte = dte, SolutionPath = Path.Combine(_dir, "Plugin.sln") };
        }

        private static object Args(VsCadDebugTests.Project p) => p.ConfigurationManager.ActiveConfiguration.Properties.Item("StartArguments").Value;

        [TestMethod]
        public void Prepare_OpensRecordedDrawing_ThenRestores()
        {
            string drawing = Path.Combine(_dir, "Debug Plan.dwg");
            File.WriteAllText(drawing, "");
            var vs = Make("/nologo", out var p);
            string seen = null;
            VsCadDebug.DrawingLookup = s => { seen = s; return drawing; };
            var session = VsCadDebug.Prepare(vs, out string note);
            try
            {
                Assert.AreEqual(vs.SolutionPath, seen);
                Assert.AreEqual(drawing, session.Drawing);
                Assert.IsNull(session.MissingDrawing);
                Assert.AreEqual("\"" + drawing + "\" /b \"" + session.Script + "\" /nologo", Args(p));
                StringAssert.Contains(note, "打开图纸 Debug Plan.dwg");
                StringAssert.Contains(note, "Debug Plan.dwg will be opened");
            }
            finally { VsCadDebug.Finish(session); try { File.Delete(session.Script); } catch { } }
            Assert.AreEqual("/nologo", Args(p));
        }

        [TestMethod]
        public void Prepare_MissingDrawing_FallsBackToNewDrawing()
        {
            var vs = Make("", out var p);
            VsCadDebug.DrawingLookup = s => Path.Combine(_dir, "gone.dwg");
            var session = VsCadDebug.Prepare(vs, out string note);
            try
            {
                Assert.IsNull(session.Drawing);
                StringAssert.EndsWith(session.MissingDrawing, "gone.dwg");
                Assert.AreEqual("/b \"" + session.Script + "\"", Args(p));
                StringAssert.Contains(note, "改为打开新图");
                StringAssert.Contains(note, "a new drawing is used");
            }
            finally { VsCadDebug.Finish(session); try { File.Delete(session.Script); } catch { } }
        }

        [TestMethod]
        public void Prepare_UserDrawingInArguments_WinsOverRecordedDrawing()
        {
            string drawing = Path.Combine(_dir, "a.dwg");
            File.WriteAllText(drawing, "");
            var vs = Make("\"C:\\Mine.dwg\"", out var p);
            VsCadDebug.DrawingLookup = s => drawing;
            var session = VsCadDebug.Prepare(vs, out _);
            try
            {
                Assert.IsNull(session.Drawing);
                Assert.AreEqual("/b \"" + session.Script + "\" \"C:\\Mine.dwg\"", Args(p));
            }
            finally { VsCadDebug.Finish(session); try { File.Delete(session.Script); } catch { } }
            Assert.AreEqual("\"C:\\Mine.dwg\"", Args(p));
        }

        [TestMethod]
        public void Settings_RecordAndClearPerSolution()
        {
            var s = new AppSettings();
            s.SetCadDrawing(@"C:\Src\A.sln", @"C:\Work\a.dwg");
            s.SetCadDrawing(@"C:\Src\B.sln", @"C:\Work\b.dwg");
            s.SetCadDrawing(@"c:\src\a.SLN", @"C:\Work\a2.dwg");
            Assert.AreEqual(@"C:\Work\a2.dwg", s.GetCadDrawing(@"C:\Src\A.sln"));
            Assert.AreEqual(2, s.CadDebugDrawings.Count);
            s.SetCadDrawing(@"C:\Src\A.sln", "");
            Assert.IsNull(s.GetCadDrawing(@"C:\Src\A.sln"));
            Assert.IsNull(s.GetCadDrawing(null));
        }

        private static async Task<string> Invoke(AgentService agent, string name, AIFunctionArguments arguments)
        {
            var tools = (IEnumerable<AITool>)typeof(AgentService).GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(agent);
            return (await tools.OfType<AIFunction>().Single(t => t.Name == name).InvokeAsync(arguments))?.ToString();
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, true)]
        [DataRow(true, false)]
        public async Task Tool_RecordsDrawing_RespectingApproval(bool confirm, bool approve)
        {
            string drawing = Path.Combine(_dir, "plan.dwg");
            File.WriteAllText(drawing, "");
            var host = new AgentDesktopTests.DesktopHost { ConfirmResult = approve };
            var vs = new VsInstance { Pid = 100, Key = "one", SolutionPath = @"C:\Src\Plugin.sln" };
            host.Instances.Add(vs);
            using (var agent = new AgentService(host, () => new AppSettings { AgentConfirm = confirm }))
            {
                string r = await Task.Run(() => Invoke(agent, "set_cad_debug_drawing", new AIFunctionArguments { ["vs"] = "1", ["drawing"] = drawing }));
                bool allowed = !confirm || approve;
                Assert.AreEqual(allowed ? drawing : null, host.GetCadDrawing(vs), r);
                Assert.AreEqual(confirm ? 1 : 0, host.Confirmations);
                string shown = await Invoke(agent, "get_cad_debug_drawing", new AIFunctionArguments { ["vs"] = "1" });
                StringAssert.Contains(shown, allowed ? drawing : "No debug drawing recorded");
                if (allowed)
                {
                    await Invoke(agent, "set_cad_debug_drawing", new AIFunctionArguments { ["vs"] = "1", ["drawing"] = "" });
                    Assert.IsNull(host.GetCadDrawing(vs));
                }
            }
        }

        [TestMethod]
        public async Task Tool_RejectsInvalidDrawings_WithoutRecording()
        {
            string text = Path.Combine(_dir, "readme.txt");
            File.WriteAllText(text, "");
            var host = new AgentDesktopTests.DesktopHost { ConfirmResult = true };
            var vs = new VsInstance { Pid = 100, Key = "one", SolutionPath = @"C:\Src\Plugin.sln" };
            host.Instances.Add(vs);
            host.Instances.Add(new VsInstance { Pid = 101, Key = "two" });
            using (var agent = new AgentService(host, () => new AppSettings { AgentConfirm = true }))
            {
                StringAssert.Contains(await Invoke(agent, "set_cad_debug_drawing", new AIFunctionArguments { ["vs"] = "1", ["drawing"] = text }), "Only .dwg");
                StringAssert.Contains(await Invoke(agent, "set_cad_debug_drawing", new AIFunctionArguments { ["vs"] = "1", ["drawing"] = Path.Combine(_dir, "none.dwg") }), "not found");
                StringAssert.Contains(await Invoke(agent, "set_cad_debug_drawing", new AIFunctionArguments { ["vs"] = "2", ["drawing"] = Path.Combine(_dir, "none.dwg") }), "no solution open");
                agent.RememberAttachments(new[] { new AttachmentRef { Id = "20250101-000000-abcd", Name = "shot.png", Kind = AttachmentKind.Image, Ext = ".png" } });
                StringAssert.Contains(await Invoke(agent, "set_cad_debug_drawing", new AIFunctionArguments { ["vs"] = "1", ["drawing"] = "last" }), "No .dwg");
                Assert.AreEqual(0, host.CadDrawings.Count);
                Assert.AreEqual(0, host.Confirmations);
            }
        }

        [TestMethod]
        public void PromptAndSteps_MentionDrawingTools()
        {
            foreach (bool english in new[] { false, true })
            {
                string prompt = Prompts.AgentSystem(english, DateTime.Today, "", "");
                StringAssert.Contains(prompt, "set_cad_debug_drawing");
                StringAssert.Contains(prompt, "get_cad_debug_drawing");
            }
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
                foreach (var name in new[] { "set_cad_debug_drawing", "get_cad_debug_drawing" })
                {
                    string step = (string)typeof(AgentService).GetMethod("DescribeCall", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(agent, new object[] { new FunctionCallContent("step", name, new Dictionary<string, object> { ["drawing"] = @"C:\Work\plan.dwg" }) });
                    Assert.IsTrue(step.Contains(" / "), step);
                    Assert.IsFalse(step.Contains(@"C:\Work"), step);
                }
        }
    }
}
