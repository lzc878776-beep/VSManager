using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class CadDebugPlanTests
    {
        [DataTestMethod]
        [DataRow(@"C:\Program Files\Autodesk\AutoCAD 2024\acad.exe", "AutoCAD")]
        [DataRow("\"%ProgramFiles%\\Autodesk\\AutoCAD 2025\\ACAD.EXE\"", "AutoCAD")]
        [DataRow(@"C:\Program Files\ZWSOFT\ZWCAD 2024\ZWCAD.exe", "ZWCAD")]
        [DataRow(@"C:\Program Files\Gstarsoft\GstarCAD2024\gcad.exe", "GstarCAD")]
        [DataRow(@"C:\Program Files\Bricsys\BricsCAD V24\bricscad.exe", "BricsCAD")]
        public void DetectHost_RecognisesCadExecutables(string program, string host) =>
            Assert.AreEqual(host, CadDebugPlan.DetectHost(program));

        [DataTestMethod]
        [DataRow(null)] [DataRow("")] [DataRow(@"C:\Tools\notepad.exe")]
        [DataRow(@"C:\Program Files\Autodesk\AutoCAD LT 2024\acadlt.exe")]
        [DataRow(@"C:\acad.exe.bak")]
        public void DetectHost_IgnoresOtherPrograms(string program) =>
            Assert.IsNull(CadDebugPlan.DetectHost(program));

        [TestMethod]
        public void BuildScript_UsesLispNetloadWithForwardSlashes()
        {
            string s = CadDebugPlan.BuildScript(new[] { @"C:\Work\My Plugin\bin\Debug\Plugin.dll", @"C:\Work\My Plugin\bin\Debug\PLUGIN.dll", " " });
            Assert.AreEqual("(command \"_.NETLOAD\" \"C:/Work/My Plugin/bin/Debug/Plugin.dll\")\r\n", s);
            Assert.ThrowsException<ArgumentException>(() => CadDebugPlan.BuildScript(new[] { "C:\\a\"b.dll" }));
        }

        [TestMethod]
        public void Inject_PrependsScript_AndStripRestoresOriginal()
        {
            string script = @"C:\Temp\VSManager\CadDebug\Plugin.scr";
            string injected = CadDebugPlan.Inject("/nologo /p \"My Profile\"", script);
            Assert.AreEqual("/b \"" + script + "\" /nologo /p \"My Profile\"", injected);
            Assert.AreEqual("/nologo /p \"My Profile\"", CadDebugPlan.StripInjected(injected));
            Assert.AreEqual("/b \"" + script + "\"", CadDebugPlan.Inject("", script));
            // 残留的旧注入被替换而不是叠加。/ A leftover injection is replaced, not stacked.
            Assert.AreEqual(injected, CadDebugPlan.Inject(injected, script));
            Assert.IsFalse(CadDebugPlan.HasUserScript(injected));
        }

        [DataTestMethod]
        [DataRow("/b \"C:\\Work\\start.scr\"", true)]
        [DataRow("-b start.scr /nologo", true)]
        [DataRow("/nologo /product ACAD", false)]
        [DataRow("/bleed", false)]
        [DataRow(null, false)]
        public void HasUserScript_DetectsOnlyUserBScripts(string args, bool expected) =>
            Assert.AreEqual(expected, CadDebugPlan.HasUserScript(args));

        [TestMethod]
        public void ScriptFileName_IsSafe()
        {
            Assert.AreEqual("My_Plugin.scr", CadDebugPlan.ScriptFileName("My Plugin"));
            Assert.AreEqual("a_b.scr", CadDebugPlan.ScriptFileName("a|b"));
            Assert.AreEqual("project.scr", CadDebugPlan.ScriptFileName(null));
        }

        [TestMethod]
        public void ScriptRoot_MatchesInjectedMarker()
        {
            string root = VsCadDebug.ScriptRoot.TrimEnd('\\') + "\\";
            StringAssert.EndsWith(root, CadDebugPlan.ScriptFolderMarker);
            string injected = CadDebugPlan.Inject("/nologo", System.IO.Path.Combine(VsCadDebug.ScriptRoot, "x.scr"));
            Assert.AreEqual("/nologo", CadDebugPlan.StripInjected(injected));
        }
    }
}
