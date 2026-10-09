using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>AI 助手对话按进程筛选：记录写入进程、接续时保留进程、下拉框只显示所选进程。/ Filtering the AI chat by process: records carry the process, resume keeps it, the dropdown shows only the chosen process.</summary>
    [TestClass]
    public class AgentChatProcessFilterTests
    {
        private TempDataFolder _data;
        private IDisposable _chatPath;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _chatPath = AgentChatLog.OverrideFilePath(_data.File(AgentChatLog.FileName));
        }

        [TestCleanup]
        public void Cleanup()
        {
            _chatPath.Dispose();
            _data.Dispose();
        }

        [TestMethod]
        public void ChatLog_OverrideRestoresFollowingTheDataFolder_AndNeverTouchesRealAppData()
        {
            string real = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VSManager");
            _chatPath.Dispose();
            using (var inner = new TempDataFolder())
            {
                // 恢复后跟随数据目录，而不是固定为某个路径 / After restoring it follows the data folder instead of a pinned path
                Assert.AreEqual(inner.File(AgentChatLog.FileName), AgentChatLog.FilePath);
                using (AgentChatLog.OverrideFilePath(inner.File("x.jsonl")))
                    Assert.AreEqual(inner.File("x.jsonl"), AgentChatLog.FilePath);
                Assert.AreEqual(inner.File(AgentChatLog.FileName), AgentChatLog.FilePath);
            }
            // 程序集级兜底：测试期间数据目录从不指向真实 %APPDATA% / Assembly safety net: the data folder never points at the real %APPDATA% in tests
            Assert.IsFalse(AppPaths.DataFolder.StartsWith(real, StringComparison.OrdinalIgnoreCase), AppPaths.DataFolder);
            Assert.IsFalse(AgentChatLog.FilePath.StartsWith(real, StringComparison.OrdinalIgnoreCase), AgentChatLog.FilePath);
            _chatPath = AgentChatLog.OverrideFilePath(_data.File(AgentChatLog.FileName));
        }

        [TestMethod]
        public void ChatLog_RecordsAndReadsTheWritingProcess()
        {
            AgentChatLog.Append(AgentChatLog.RoleUser, "now");
            AgentChatLog.Append(new AgentChatRecord { Time = DateTime.Now, Role = "assistant", Text = "earlier", Pid = 42, ProcessStart = new DateTime(2026, 10, 8, 9, 0, 0) });
            var all = AgentChatLog.ReadAll();
            Assert.AreEqual(AgentChatLog.CurrentPid, all[0].Pid);
            Assert.AreEqual(AgentChatLog.CurrentProcessKey, all[0].ProcessKey);
            Assert.AreEqual("42@20261008090000", all[1].ProcessKey);
            StringAssert.Contains(AgentChatLog.DescribeProcess(all[1].ProcessKey), "PID 42 · 10-08 09:00");
            StringAssert.Contains(AgentChatLog.DescribeProcess(AgentChatLog.CurrentProcessKey), "当前");
            Assert.AreEqual("", AgentChatLog.Parse("{\"time\":\"2026-10-08T09:00:00\",\"role\":\"user\",\"text\":\"old\"}").ProcessKey);
            CollectionAssert.AreEqual(new[] { "b", "", "a" }, ProcessFilterCombo.Ordered(new[] { "a", "b", "a", "", "b" }).ToArray());
        }

        [TestMethod]
        public void AssistantLabel_ShowsTheProcessName()
        {
            AgentChatLog.Append(AgentChatLog.RoleAssistant, "now");
            var read = AgentChatLog.ReadAll().Single();
            Assert.AreEqual(AgentChatLog.CurrentProcessName, read.ProcessName);
            string current = AgentPanel.AssistantLabelOf(new ChatMessage { Role = ChatRole.Assistant });
            Assert.AreEqual("AI 助手 · " + AgentChatLog.CurrentProcessName + " · PID " + AgentChatLog.CurrentPid, current);
            Assert.AreEqual("AI 助手 · VSManager · PID 42",
                AgentPanel.AssistantLabelOf(new ChatMessage { Role = ChatRole.Assistant, Process = "42@20261008090000", ProcessName = "VSManager" }));
            Assert.AreEqual("AI 助手 · VSManager · PID 42",
                AgentPanel.AssistantLabelOf(new ChatMessage { Role = ChatRole.Assistant, Process = "42@20261008090000" }), "older records without pname");
            StringAssert.Contains(AgentPanel.AssistantLabelOf(new ChatMessage { Role = ChatRole.Assistant, Process = "" }), "未记录进程");
        }

        [TestMethod]
        public void Panel_DropdownShowsOnlyTheChosenProcess()
        {
            var records = new List<AgentChatRecord>
            {
                new AgentChatRecord { Time = DateTime.Now, Role = "user", Text = "question-old", Pid = 7, ProcessStart = new DateTime(2026, 10, 8, 9, 0, 0) },
                new AgentChatRecord { Time = DateTime.Now, Role = "assistant", Text = "answer-old", Pid = 7, ProcessStart = new DateTime(2026, 10, 8, 9, 0, 0) },
                new AgentChatRecord { Time = DateTime.Now, Role = "user", Text = "question-legacy" },
            };
            OnUi(() =>
            {
                using (var agent = new AgentService(null, () => new AppSettings()))
                using (var panel = new AgentPanel())
                {
                    Assert.AreEqual(3, agent.RestoreConversation(records));
                    Assert.AreEqual("7@20261008090000", agent.Transcript.Messages[0].Process);
                    panel.Bind(agent);
                    var combo = panel.ProcessFilter;
                    // 全部 + 当前进程（接续提示）+ 旧记录 + 进程 7 / All + current (resumed notice) + legacy + process 7
                    Assert.AreEqual(4, combo.Items.Count);
                    Assert.AreEqual(ProcessFilterCombo.AllText, combo.Items[0].ToString());
                    StringAssert.Contains(combo.Items[1].ToString(), "当前");
                    Assert.IsNull(combo.SelectedKey);
                    combo.Select("7@20261008090000");
                    Assert.AreEqual("7@20261008090000", combo.SelectedKey);
                    var shown = AgentPanel.FilterByProcess(agent.Transcript.Messages, combo.SelectedKey);
                    CollectionAssert.AreEqual(new[] { "question-old", "answer-old" }, shown.Select(m => m.Parts[0].Text).ToArray());
                    Assert.AreEqual("question-legacy", AgentPanel.FilterByProcess(agent.Transcript.Messages, "").Single().Parts[0].Text);
                    Assert.AreEqual(AgentService.ResumedNotice, AgentPanel.FilterByProcess(agent.Transcript.Messages, AgentChatLog.CurrentProcessKey).Single().Parts[0].Text);
                    combo.Select(null);
                    Assert.IsNull(combo.SelectedKey);
                    Assert.AreEqual(4, AgentPanel.FilterByProcess(agent.Transcript.Messages, combo.SelectedKey).Count);
                }
            });
        }

        private static void OnUi(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "UI test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
