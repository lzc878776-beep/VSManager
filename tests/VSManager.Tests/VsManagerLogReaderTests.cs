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
    [TestClass]
    public class VsManagerLogReaderTests
    {
        private static readonly DateTime Today = new DateTime(2026, 9, 29, 10, 0, 0);
        private string _root, _archive, _logs;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "vsm-logtest-" + Guid.NewGuid().ToString("N"));
            _archive = Path.Combine(_root, "archive", "logs");
            _logs = Path.Combine(_root, "logs");
            Directory.CreateDirectory(_archive);
            Directory.CreateDirectory(_logs);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private IReadOnlyList<string> Folders => new[] { _archive, _logs };

        private string Read(string name, int lines = 200, string date = null, int max = 16000) =>
            VsManagerLogReader.Read(name, lines, date, Folders, Today, t => AgentFileService.RedactText(t, new AppSettings()), max);

        private void Write(string folder, string file, IEnumerable<string> lines) =>
            File.WriteAllText(Path.Combine(folder, file), string.Join("\r\n", lines) + "\r\n", new System.Text.UTF8Encoding(false));

        [TestMethod]
        public void Tasks_ReturnsPathTotalAndTail()
        {
            Write(_logs, "tasks.log", Enumerable.Range(1, 300).Select(i => "2026-09-29 09:00:00 第 " + i + " 行")
                .Concat(new[] { "2026-09-29 09:10:00 已按测试项文字重判 3 个测试项的可验证性" }));
            string r = Read("tasks", 5);
            StringAssert.Contains(r, VsManagerLogReader.FriendlyPath(Path.Combine(_logs, "tasks.log")));
            StringAssert.Contains(r, "总行数 / Total lines: 301");
            StringAssert.Contains(r, "已按测试项文字重判 3 个测试项的可验证性");
            StringAssert.Contains(r, "第 297 行");
            Assert.IsFalse(r.Contains("第 296 行"), r);
        }

        [TestMethod]
        public void DefaultLines_Is200()
        {
            Write(_logs, "watchdog.log", Enumerable.Range(1, 250).Select(i => "L" + i.ToString("000")));
            string r = Read("watchdog.log", 0);
            StringAssert.Contains(r, "Last 200");
            StringAssert.Contains(r, "L051");
            Assert.IsFalse(r.Contains("L050"));
        }

        [TestMethod]
        public void Send_UsesArchiveDatedFile_AndLastVolume()
        {
            Write(_archive, "send-20260929.log", new[] { "old volume" });
            Write(_archive, "send-20260929.2.log", new[] { "【执行方式】请按自动推荐的提示词执行" });
            Write(_archive, "send-20260928.log", new[] { "yesterday" });
            string r = Read("send");
            StringAssert.Contains(r, "send-20260929.2.log");
            StringAssert.Contains(r, "【执行方式】请按自动推荐的提示词执行");
            StringAssert.Contains(Read("send", 200, "2026-09-28"), "yesterday");
            StringAssert.Contains(Read("send-20260928.log"), "yesterday");
        }

        [TestMethod]
        public void UndatedLog_WithDate_KeepsThatDaysRecordsAndContinuations()
        {
            Write(_logs, "tasks.log", new[]
            {
                "2026-09-28 23:59:00 前一天 / previous day",
                "2026-09-29 08:00:00 对话窗格停留在聊天历史列表",
                "   at Stack.Frame()",
                "2026-09-30 00:00:01 后一天 / next day",
            });
            string r = Read("tasks", 200, "2026-09-29");
            StringAssert.Contains(r, "对话窗格停留在聊天历史列表");
            StringAssert.Contains(r, "at Stack.Frame()");
            StringAssert.Contains(r, "Total lines: 4");
            Assert.IsFalse(r.Contains("前一天"));
            Assert.IsFalse(r.Contains("后一天"));
            // 不给日期时读整个文件末尾 / Without a date the whole file's tail is read
            StringAssert.Contains(Read("tasks"), "前一天");
        }

        [TestMethod]
        public void Output_IsRedacted()
        {
            Write(_logs, "mcp.log", new[] { "2026-09-29 08:00:00 token=abc123secret", "Authorization: Bearer abcdefghijklmnop", "key sk-abcdefghijklmnopqrstuv" });
            string r = Read("mcp");
            Assert.IsFalse(r.Contains("abc123secret"), r);
            Assert.IsFalse(r.Contains("abcdefghijklmnop"), r);
            Assert.IsFalse(r.Contains("sk-abcdefghijklmnopqrstuv"), r);
            StringAssert.Contains(r, "REDACTED");
        }

        [DataTestMethod]
        [DataRow(@"..\settings.json")]
        [DataRow(@"C:\Windows\win.ini")]
        [DataRow("logs/tasks.log")]
        [DataRow("agent-chat.jsonl")]
        public void RejectsPathsAndNonLogFiles(string name)
        {
            string r = Read(name);
            Assert.IsTrue(r.Contains("not a path") || r.Contains("Only .log"), r);
        }

        [DataTestMethod]
        [DataRow("agent")]
        [DataRow("agent.log")]
        [DataRow("file-audit")]
        public void DeniedLogs_AreRefused(string name)
        {
            Write(_logs, "agent.log", new[] { "secret conversation" });
            Write(_logs, "file-audit.log", new[] { "audit" });
            string r = Read(name);
            StringAssert.Contains(r, "not available to the AI");
            Assert.IsFalse(r.Contains("secret conversation"));
        }

        [TestMethod]
        public void Missing_ListsAvailableLogs_AndBadDateIsReported()
        {
            Write(_logs, "tasks.log", new[] { "x" });
            Write(_logs, "agent.log", new[] { "x" });
            Write(_archive, "send-20260927.log", new[] { "x" });
            string r = Read("nothing");
            StringAssert.Contains(r, "Log not found");
            StringAssert.Contains(r, "tasks");
            StringAssert.Contains(r, "send");
            Assert.IsFalse(r.Contains("agent"), r);
            StringAssert.Contains(Read("tasks", 200, "2026/09/29"), "Invalid date");
        }

        [TestMethod]
        public void LongOutput_KeepsNewestLines()
        {
            Write(_logs, "tasks.log", Enumerable.Range(1, 2000).Select(i => "2026-09-29 09:00:00 记录 " + i.ToString("0000") + new string('x', 40)));
            string r = Read("tasks", 2000, null, 3000);
            Assert.IsTrue(r.Length <= 3000, r.Length.ToString());
            StringAssert.Contains(r, "记录 2000");
            StringAssert.Contains(r, "truncated");
            Assert.IsFalse(r.Contains("记录 0001"));
        }

        [TestMethod]
        public void FriendlyPath_HidesUserFolders()
        {
            string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Assert.AreEqual(@"%APPDATA%\VSManager\logs\tasks.log", VsManagerLogReader.FriendlyPath(Path.Combine(app, "VSManager", "logs", "tasks.log")));
        }

        [TestMethod]
        public async Task Tool_IsRegistered_AndReadsThroughAgent()
        {
            Write(_logs, "tasks.log", new[] { "2026-09-29 08:00:00 hello log" });
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
            {
                agent.LogFolders = () => Folders;
                var tool = ((IEnumerable<AITool>)typeof(AgentService).GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(agent))
                    .OfType<AIFunction>().Single(t => t.Name == "read_vsmanager_log");
                CollectionAssert.Contains(agent.ToolsForRound().Select(t => t.Name).ToList(), "read_vsmanager_log");
                StringAssert.Contains(tool.Description, "archive\\logs");
                string r = (await tool.InvokeAsync(new AIFunctionArguments { ["name"] = "tasks" }))?.ToString();
                StringAssert.Contains(r, "hello log");
                StringAssert.Contains(r, "Total lines: 1");
                string step = (string)typeof(AgentService).GetMethod("DescribeCall", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(agent, new object[] { new FunctionCallContent("s", "read_vsmanager_log", new Dictionary<string, object> { ["name"] = "tasks" }) });
                StringAssert.Contains(step, "读取 VSManager 日志「tasks」");
            }
        }

        [TestMethod]
        public void Prompt_MentionsLogTool_InBothLanguages()
        {
            StringAssert.Contains(Prompts.AgentSystem(false, DateTime.Today, "", ""), "用 read_vsmanager_log 读取对应日志末尾");
            StringAssert.Contains(Prompts.AgentSystem(true, DateTime.Today, "", ""), "read the tail of the matching log with read_vsmanager_log");
        }
    }
}
