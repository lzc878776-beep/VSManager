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
    /// read_agent_chat：对话轮次与实际调用的工具、plan.log 启动恢复记录、since 过滤、脱敏与工具注册。
    /// read_agent_chat: chat rounds with the tools actually called, plan.log startup records, since filter, redaction and tool registration.
    /// </summary>
    [TestClass]
    public class AgentChatDigestTests
    {
        private string _dir, _chat, _planLog;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsm-chat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _chat = Path.Combine(_dir, "agent-chat.jsonl");
            _planLog = Path.Combine(_dir, "plan.log");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static AgentChatRecord Rec(string role, string text, DateTime time, params string[] tools) => new AgentChatRecord
        {
            Role = role, Text = text, Time = time,
            Calls = tools.Select(t => new AgentToolCall { Name = t, Args = "{}", Result = "ok" }).ToList()
        };

        private void WriteChat(params AgentChatRecord[] records)
        {
            var old = AgentChatLog.FilePath;
            AgentChatLog.FilePath = _chat;
            try { foreach (var r in records) AgentChatLog.Append(r); }
            finally { AgentChatLog.FilePath = old; }
        }

        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 8, 0, 0);

        [TestMethod]
        public void Rounds_GroupRepliesAndTools_SkipLocalAndReset()
        {
            var rounds = AgentChatDigest.Rounds(new[]
            {
                Rec("user", "建计划", T0),
                Rec("assistant", "好", T0.AddSeconds(5), "create_plan", "update_plan_step", "update_plan_step"),
                new AgentChatRecord { Role = "notice", Text = "本机提示", Time = T0.AddSeconds(6), Local = true },
                new AgentChatRecord { Role = "notice", Text = "新对话", Time = T0.AddSeconds(7), Reset = true },
                Rec("notice", "📋 执行计划恢复", T0.AddMinutes(1)),
                Rec("assistant", "继续", T0.AddMinutes(1).AddSeconds(3), "read_plan"),
                Rec("assistant", "自行接续", T0.AddMinutes(2), "send_task"),
                Rec("user", "还在跑", T0.AddMinutes(3)),
            });
            Assert.AreEqual(4, rounds.Count);
            CollectionAssert.AreEqual(new[] { "create_plan", "update_plan_step", "update_plan_step" }, rounds[0].Tools);
            Assert.AreEqual("notice", rounds[1].Kind);
            CollectionAssert.AreEqual(new[] { "read_plan" }, rounds[1].Tools);
            Assert.AreEqual("assistant", rounds[2].Kind);
            Assert.IsFalse(rounds[3].HasReply);
            Assert.AreEqual(4, rounds[3].Index);
        }

        [TestMethod]
        public void Startups_ParseNewAndOldFormats_AndAttachNotices()
        {
            var lines = new[]
            {
                "2026-09-28 10:00:00 启动时读回执行计划 / Plans read back at startup: 1，续跑 / resuming 1（#1 #194 验证用计划）",
                "2026-09-28 10:00:00 重启完成通知附带执行计划 / Restart notice carries plans: #1",
                "2026-09-28 10:05:00 更新执行计划 / Plan step updated #1 第 2 步 / step → done：x",
                "2026-09-29 09:00:00 " + AgentPlans.StartupMarker + "2，续跑 / resuming 2（#2 补 skill、#3 自迭代） [PID 42]",
                "2026-09-29 09:00:10 " + AgentPlans.NoticeMarker + "#2 补 skill、#3 自迭代 [PID 42]",
                "2026-09-29 10:00:00 " + AgentPlans.StartupMarker + "0，续跑 / resuming 0 [PID 77]",
                "garbage",
            };
            var s = AgentChatDigest.Startups(lines);
            Assert.AreEqual(3, s.Count);
            CollectionAssert.AreEqual(new[] { "#1 #194 验证用计划" }, s[0].Plans);
            Assert.AreEqual(1, s[0].Notices.Count);
            StringAssert.Contains(s[0].Notices[0], "restart notice");
            Assert.AreEqual(42, s[1].Pid);
            CollectionAssert.AreEqual(new[] { "#2 补 skill", "#3 自迭代" }, s[1].Plans);
            StringAssert.Contains(s[1].Notices.Single(), "separate notice");
            Assert.AreEqual(0, s[2].ReadBack);
            Assert.AreEqual(0, s[2].Notices.Count);
        }

        [TestMethod]
        public void LogLines_RoundTripThroughParser()
        {
            var plans = new List<AgentPlan> { new AgentPlan { Id = 5, Title = "多阶段验证" } };
            var lines = new[]
            {
                "2026-09-29 09:00:00 " + AgentPlans.StartupLogLine(3, plans, 9),
                "2026-09-29 09:00:01 " + AgentPlans.NoticeLogLine(plans, true, 9),
            };
            var s = AgentChatDigest.Startups(lines).Single();
            Assert.AreEqual(3, s.ReadBack);
            Assert.AreEqual(1, s.Resuming);
            Assert.AreEqual(9, s.Pid);
            CollectionAssert.AreEqual(new[] { "#5 多阶段验证" }, s.Plans);
            Assert.AreEqual(1, s.Notices.Count);
        }

        [TestMethod]
        public void Read_FormatsBothParts_MarksThisProcess_AndLimitsRounds()
        {
            WriteChat(Rec("user", "第一轮", T0), Rec("assistant", "a", T0.AddSeconds(1), "send_task"),
                Rec("user", "第二轮", T0.AddMinutes(1)), Rec("assistant", "b", T0.AddMinutes(1).AddSeconds(1), "create_plan"),
                Rec("user", "第三轮", T0.AddMinutes(2)), Rec("assistant", "c", T0.AddMinutes(2).AddSeconds(1), "read_plan", "read_plan"));
            File.WriteAllLines(_planLog, new[] { "2026-09-29 08:30:00 " + AgentPlans.StartupMarker + "1，续跑 / resuming 1（#1 计划甲） [PID 123]" });
            string r = AgentChatDigest.Read(2, null, _chat, new[] { Path.Combine(_dir, "missing.log"), _planLog }, null, 123, null, 0);
            StringAssert.Contains(r, "total 3");
            Assert.IsFalse(r.Contains("第一轮"));
            StringAssert.Contains(r, "#2 2026-09-29 08:01:00 [用户 / user] 第二轮");
            StringAssert.Contains(r, "Tools: create_plan");
            StringAssert.Contains(r, "read_plan×2");
            StringAssert.Contains(r, "★本进程");
            StringAssert.Contains(r, "#1 计划甲");
            StringAssert.Contains(r, "none recorded");
        }

        [TestMethod]
        public void Read_SinceFilters_AndRejectsBadFormat()
        {
            WriteChat(Rec("user", "早", T0), Rec("user", "晚", T0.AddHours(2)));
            string r = AgentChatDigest.Read(20, "2026-09-29 09:00", _chat, new string[0], null, 1, null, 0);
            StringAssert.Contains(r, "晚");
            Assert.IsFalse(r.Contains("] 早"));
            StringAssert.Contains(r, "plan.log not found");
            StringAssert.StartsWith(AgentChatDigest.Read(20, "昨天", _chat, new string[0], null, 1, null, 0), "⚠");
        }

        [TestMethod]
        public void Read_RedactsAndTruncatesSummaries()
        {
            WriteChat(Rec("user", "secret-token " + new string('长', 400), T0));
            string r = AgentChatDigest.Read(20, null, _chat, new string[0], s => s.Replace("secret-token", "***"), 1, null, 0);
            Assert.IsFalse(r.Contains("secret-token"));
            StringAssert.Contains(r, "***");
            Assert.IsFalse(r.Contains(new string('长', AgentChatDigest.SummaryChars)));
        }

        [TestMethod]
        public async Task Tool_IsRegistered_Core_AndReadsThroughAgent()
        {
            WriteChat(Rec("user", "你好", T0), Rec("assistant", "hi", T0.AddSeconds(1), "update_plan_step"));
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => new AppSettings()))
            {
                agent.ChatLogPath = () => _chat;
                agent.PlanLogPaths = () => new[] { _planLog };
                var tool = ((IEnumerable<AITool>)typeof(AgentService).GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(agent))
                    .OfType<AIFunction>().Single(t => t.Name == "read_agent_chat");
                CollectionAssert.Contains(agent.ToolsForRound().Select(t => t.Name).ToList(), "read_agent_chat");
                StringAssert.Contains(tool.Description, "agent-chat.jsonl");
                string r = (await tool.InvokeAsync(new AIFunctionArguments { ["lines"] = 5 }))?.ToString();
                StringAssert.Contains(r, "update_plan_step");
                StringAssert.Contains(r, "你好");
                string step = (string)typeof(AgentService).GetMethod("DescribeCall", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(agent, new object[] { new FunctionCallContent("s", "read_agent_chat", new Dictionary<string, object>()) });
                StringAssert.Contains(step, "读取 AI 对话记录");
            }
        }

        [TestMethod]
        public void Prompt_MentionsTool_InBothLanguages()
        {
            StringAssert.Contains(Prompts.AgentSystem(false, DateTime.Today, "", ""), "用 read_agent_chat 读取");
            StringAssert.Contains(Prompts.AgentSystem(true, DateTime.Today, "", ""), "use read_agent_chat");
        }
    }
}
