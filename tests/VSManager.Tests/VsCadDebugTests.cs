using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>用假 DTE 对象图验证 CAD 调试参数的注入、恢复与监视。/ Verifies CAD debug argument injection, restore and watching against a fake DTE object graph.</summary>
    [TestClass]
    public class VsCadDebugTests
    {
        public sealed class Prop { public object Value { get; set; } }
        public sealed class Props
        {
            private readonly Dictionary<string, Prop> _items = new Dictionary<string, Prop>(StringComparer.OrdinalIgnoreCase);
            public Props Set(string name, object value) { _items[name] = new Prop { Value = value }; return this; }
            public Prop Item(string name) => _items.TryGetValue(name, out var p) ? p : throw new ArgumentException(name);
        }
        public sealed class Config { public Props Properties { get; } = new Props(); }
        public sealed class ConfigManager { public Config ActiveConfiguration { get; } = new Config(); }
        public sealed class Project
        {
            public string UniqueName { get; set; } = "Plugin\\Plugin.csproj";
            public string Kind { get; set; } = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            public string Name { get; set; } = "Plugin";
            public string FullName { get; set; }
            public Props Properties { get; } = new Props();
            public ConfigManager ConfigurationManager { get; } = new ConfigManager();
        }
        public sealed class Build { public object StartupProjects { get; set; } public int BuildState { get; set; } = 3; }
        public sealed class Solution { public Build SolutionBuild { get; } = new Build(); public List<object> Projects { get; } = new List<object>(); }
        public sealed class Debugger { public int CurrentMode { get; set; } = 1; }
        public sealed class Dte { public Solution Solution { get; } = new Solution(); public Debugger Debugger { get; } = new Debugger(); }

        private static int _pid = 900000;
        private string _dir;

        [TestInitialize] public void Init() { _dir = Path.Combine(Path.GetTempPath(), "VSManagerCadTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
        [TestCleanup] public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

        private (VsInstance Vs, Dte Dte, Project Project) Make(string program, string args, int startAction = 1, string output = "Plugin.dll")
        {
            var dte = new Dte();
            var p = new Project { FullName = Path.Combine(_dir, "Plugin.csproj") };
            p.Properties.Set("OutputFileName", output);
            p.ConfigurationManager.ActiveConfiguration.Properties
                .Set("StartAction", startAction).Set("StartProgram", program).Set("StartArguments", args).Set("OutputPath", @"bin\Debug\");
            dte.Solution.Projects.Add(p);
            dte.Solution.SolutionBuild.StartupProjects = new object[] { p.UniqueName };
            return (new VsInstance { Pid = Interlocked.Increment(ref _pid), Dte = dte }, dte, p);
        }

        private static object Args(Project p) => p.ConfigurationManager.ActiveConfiguration.Properties.Item("StartArguments").Value;

        [TestMethod]
        public void ProjectProperties_InjectScript_ThenRestoreOriginal()
        {
            var (vs, _, p) = Make(@"C:\Program Files\Autodesk\AutoCAD 2024\acad.exe", "/nologo");
            var s = VsCadDebug.Prepare(vs, out string note);
            try
            {
                Assert.IsNotNull(s);
                Assert.AreEqual("AutoCAD", s.Host);
                Assert.AreEqual("project", s.Source);
                StringAssert.Contains(note, "NETLOAD Plugin.dll");
                Assert.AreEqual("/b \"" + s.Script + "\" /nologo", Args(p));
                string dll = Path.Combine(_dir, @"bin\Debug\Plugin.dll");
                Assert.AreEqual(dll, s.Dll);
                StringAssert.Contains(File.ReadAllText(s.Script, Encoding.Default), "(command \"_.NETLOAD\" \"" + dll.Replace('\\', '/') + "\")");
                // 同一 VS 在恢复前再次调试不会重复注入。/ Debugging the same VS again before restore does not inject twice.
                Assert.IsNull(VsCadDebug.Prepare(vs, out string again));
                StringAssert.Contains(again, "AutoCAD");
            }
            finally { VsCadDebug.Finish(s); }
            Assert.IsTrue(s.Restored);
            Assert.AreEqual("/nologo", Args(p));
            try { File.Delete(s.Script); } catch { }
        }

        [TestMethod]
        public void ProjectProperties_EditedDuringLaunch_OnlyStripsOwnArgument()
        {
            var (vs, _, p) = Make(@"C:\Program Files\ZWCAD\zwcad.exe", "");
            var s = VsCadDebug.Prepare(vs, out _);
            p.ConfigurationManager.ActiveConfiguration.Properties.Item("StartArguments").Value = Args(p) + " /p Custom";
            VsCadDebug.Finish(s);
            Assert.AreEqual("/p Custom", Args(p));
            try { File.Delete(s.Script); } catch { }
        }

        [TestMethod]
        public void NotCad_UserScript_OrExe_LeaveArgumentsUntouched()
        {
            var (a, _, pa) = Make(@"C:\Tools\host.exe", "/x");
            Assert.IsNull(VsCadDebug.Prepare(a, out string na)); Assert.IsNull(na); Assert.AreEqual("/x", Args(pa));

            var (b, _, pb) = Make(@"C:\CAD\acad.exe", "/x", startAction: 0);
            Assert.IsNull(VsCadDebug.Prepare(b, out _)); Assert.AreEqual("/x", Args(pb));

            var (c, _, pc) = Make(@"C:\CAD\acad.exe", "/b \"C:\\mine.scr\"");
            Assert.IsNull(VsCadDebug.Prepare(c, out string nc)); StringAssert.Contains(nc, "/b"); Assert.AreEqual("/b \"C:\\mine.scr\"", Args(pc));

            var (d, _, pd) = Make(@"C:\CAD\acad.exe", "", output: "Tool.exe");
            Assert.IsNull(VsCadDebug.Prepare(d, out string nd)); StringAssert.Contains(nd, "DLL"); Assert.AreEqual("", Args(pd));

            VsCadDebug.Enabled = false;
            try
            {
                var (e, _, pe) = Make(@"C:\CAD\acad.exe", "");
                Assert.IsNull(VsCadDebug.Prepare(e, out _)); Assert.AreEqual("", Args(pe));
            }
            finally { VsCadDebug.Enabled = true; }
        }

        [TestMethod]
        public void LaunchSettings_ExecutableProfile_InjectedThenRestoredByteForByte()
        {
            var (vs, _, p) = Make("", "", startAction: 0);
            Directory.CreateDirectory(Path.Combine(_dir, "Properties"));
            string file = Path.Combine(_dir, "Properties", "launchSettings.json");
            string original = "{\r\n  \"profiles\": {\r\n    \"CAD\": {\r\n      \"commandName\": \"Executable\",\r\n      \"executablePath\": \"%ProgramFiles%\\\\Autodesk\\\\AutoCAD 2025\\\\acad.exe\",\r\n      \"commandLineArgs\": \"/nologo\"\r\n    }\r\n  }\r\n}\r\n";
            File.WriteAllText(file, original, new UTF8Encoding(true));
            byte[] bytes = File.ReadAllBytes(file);
            var s = VsCadDebug.Prepare(vs, out _);
            Assert.IsNotNull(s);
            Assert.AreEqual("launchSettings", s.Source);
            string written = File.ReadAllText(file);
            StringAssert.Contains(written, "\"commandLineArgs\":\"/b \\\"");
            StringAssert.Contains(written, "CadDebug\\\\Plugin.scr\\\" /nologo\"");
            VsCadDebug.Finish(s);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(file));
            try { File.Delete(s.Script); } catch { }
        }

        [TestMethod]
        public void Watch_RestoresWhenDebuggerStartsRunning()
        {
            var (vs, dte, p) = Make(@"C:\CAD\gcad.exe", "/a");
            var s = VsCadDebug.Prepare(vs, out _);
            VsCadDebug.Watch(vs, s);
            Thread.Sleep(1200);
            Assert.IsFalse(s.Restored, "设计模式且未超时时保持注入 / Stays injected while in design mode");
            dte.Debugger.CurrentMode = 3;
            for (int i = 0; i < 50 && !s.Restored; i++) Thread.Sleep(100);
            Assert.IsTrue(s.Restored);
            Assert.AreEqual("/a", DteWorker.Run(() => Args(p)).Result);
            try { File.Delete(s.Script); } catch { }
        }
    }
}
