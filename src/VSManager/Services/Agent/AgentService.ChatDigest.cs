using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace VSManager
{
    public sealed partial class AgentService
    {
        /// <summary>对话记录文件路径（测试可替换）。/ Chat record file path (replaceable in tests).</summary>
        internal Func<string> ChatLogPath = () => AgentChatLog.FilePath;

        /// <summary>plan.log 的候选路径，取第一个存在的（测试可替换）。/ Candidate plan.log paths; the first existing one is used (replaceable in tests).</summary>
        internal Func<IReadOnlyList<string>> PlanLogPaths = () => new[] { AppLog.PathOf(AgentPlans.LogFile), Path.Combine(AppPaths.DataFolder, AgentPlans.LogFile) };

        [Description("读取 AI 总控助手自己的对话记录与启动恢复记录（只读，已脱敏）。第一部分：末尾若干轮的轮次序号、时间、用户消息 / 通知摘要，以及该轮实际调用的工具名（如 create_plan、update_plan_step、read_plan、send_task）；" +
            "第二部分：本进程及历史各次启动时读回的执行计划（启动时间、读回计划数、计划编号与标题、是否发出「执行计划恢复」通知）。数据来自 %APPDATA%\\VSManager\\agent-chat.jsonl 与 plan.log。" +
            "用于核对某轮是否真的调用过某工具、重启后是否读回计划并发出通知。" +
            " / Reads the AI assistant's own chat record and startup-resume records (read-only, redacted). Part 1: the last rounds with round number, time, user message / notice summary " +
            "and the tools actually called in that round (e.g. create_plan, update_plan_step, read_plan, send_task); Part 2: the execution plans read back at startup by this process and earlier ones " +
            "(startup time, plans read back, plan ids and titles, whether a \"Plan resumed\" notice was sent). Sources: %APPDATA%\\VSManager\\agent-chat.jsonl and plan.log. " +
            "Use it to check whether a round really called a tool, and whether a restart read the plans back and sent the notice.")]
        internal Task<string> ReadAgentChat(
            [Description("读取末尾轮数 1–200，默认 20 / Number of last rounds 1–200, default 20")] int lines = AgentChatDigest.DefaultRounds,
            [Description("可选，只返回该时间之后的轮次与启动记录，格式 yyyy-MM-dd HH:mm（本地时间）/ Optional: only rounds and startup records after this time, format yyyy-MM-dd HH:mm (local time)")] string since = null)
        {
            string chat = ChatLogPath();
            var planLogs = PlanLogPaths();
            var settings = _settings();
            int max = MaxToolText;
            return Task.Run(() =>
            {
                DateTime? started = null;
                int pid;
                using (var self = Process.GetCurrentProcess())
                {
                    pid = self.Id;
                    try { started = self.StartTime; } catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException) { }
                }
                return AgentChatDigest.Read(lines, since, chat, planLogs, text => AgentFileService.RedactText(text, settings), pid, started, max);
            });
        }
    }
}
