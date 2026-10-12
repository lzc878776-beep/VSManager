using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    /// <summary>
    /// 可选宿主能力：按任务编号查询任务的目标 VS 名称，用于在「AI 助手」标题上显示消息发布到 / 结果来自哪个 VS；可从任意线程调用。
    /// Optional host capability: looks up a task's target VS name by task id, used to show which VS a message published to / got results from
    /// in the "AI 助手" title; callable from any thread.
    /// </summary>
    public interface IAgentTaskVsHost
    {
        /// <summary>任务的目标 VS 名称；找不到时返回 null。/ The task's target VS name; null when not found.</summary>
        string TaskVsName(int id);
    }

    /// <summary>
    /// 一次工具调用涉及的 VS：<see cref="Publish"/> 为 true 表示向它发布任务，否则表示从它取得结果。
    /// The VS involved in one tool call: <see cref="Publish"/> true means a task was published to it, otherwise results were taken from it.
    /// </summary>
    internal sealed class VsRoute
    {
        public bool Publish;
        public string Vs;
    }

    public sealed partial class AgentService
    {
        /// <summary>向 VS 发布任务的工具。/ Tools that publish a task to a VS.</summary>
        private static readonly HashSet<string> PublishTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "send_task", "continue_task", "retry_task", "retry_task_with_info", "request_vsmanager_improvement",
        };

        /// <summary>按任务编号读取结果的工具（结果来自任务所在 VS）。/ Tools reading results by task id (results come from the task's VS).</summary>
        private static readonly HashSet<string> TaskSourceTools = new HashSet<string>(StringComparer.Ordinal) { "read_task_reply" };

        /// <summary>工具返回首行出现这些文字时视为未成功（未发布 / 未取得结果）。/ A tool result whose first line contains any of these counts as unsuccessful.</summary>
        private static readonly string[] RouteFailureMarks =
        {
            "用户拒绝", "不支持", "没有找到", "未找到", "没有编号", "没有正在运行", "为空", "过长", "未入队", "未开启",
            "unavailable", "not found", "not queued", "cancelled", "is empty", "no running",
        };

        /// <summary>
        /// 判断工具调用涉及哪个 VS：带 vs 参数的按该 VS；按任务编号的工具查任务所在 VS；改进需求发往打开 VSManager 项目的 VS。不涉及 VS 时返回 null。
        /// Works out which VS a tool call involves: the vs argument; the task's VS for task-id tools; the VS with the VSManager project for
        /// improvement requests. Returns null when no VS is involved.
        /// </summary>
        internal VsRoute RouteOf(string name, IDictionary<string, object> args)
        {
            if (string.IsNullOrEmpty(name) || _host == null) return null;
            string Arg(string k) => args != null && args.TryGetValue(k, out var v) && v != null ? v.ToString().Trim() : "";
            bool publish = PublishTools.Contains(name);
            string vs = null;
            try
            {
                if (name == "request_vsmanager_improvement")
                {
                    var target = _host.Instances.ToList().FirstOrDefault(IsVsManager);
                    vs = target != null ? _host.NameOf(target) : null;
                }
                // 未在运行的 VS 中找到时，发布类工具可能按登记别名暂存，保留原文；读取类工具不计入。
                // Not a running VS: publishing tools may park the task by registry alias, so keep the text; reading tools record nothing.
                else if (Arg("vs").Length > 0) vs = Resolve(Arg("vs"), out var v, out _) ? _host.NameOf(v) : publish ? Arg("vs") : null;
                else if ((publish || TaskSourceTools.Contains(name)) && int.TryParse(Arg("id"), out int id) && _host is IAgentTaskVsHost tasks)
                    vs = tasks.TaskVsName(id);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException) { vs = null; }
            return string.IsNullOrWhiteSpace(vs) ? null : new VsRoute { Publish = publish, Vs = vs.Trim() };
        }

        /// <summary>
        /// 工具成功返回后把 VS 计入消息的「发布到」或「结果来自」；<paramref name="result"/> 为 null（异常）或表示失败时不计入。
        /// After a successful tool result, adds the VS to the message's "published to" or "results from"; nothing is added when
        /// <paramref name="result"/> is null (exception) or reports a failure.
        /// </summary>
        internal static void ApplyRoute(ChatMessage m, VsRoute route, string result)
        {
            if (m == null || route == null || !RouteSucceeded(result)) return;
            var list = route.Publish ? m.Targets : m.Sources;
            if (!list.Contains(route.Vs, StringComparer.OrdinalIgnoreCase)) list.Add(route.Vs);
        }

        /// <summary>工具返回是否表示成功（按首行判断）。/ Whether a tool result reports success (judged by its first line).</summary>
        internal static bool RouteSucceeded(string result)
        {
            string first = (result ?? "").TrimStart();
            if (first.Length == 0) return false;
            int nl = first.IndexOf('\n');
            if (nl >= 0) first = first.Substring(0, nl);
            if (first.StartsWith("❌", StringComparison.Ordinal) || first.StartsWith("⚠", StringComparison.Ordinal)) return false;
            return !RouteFailureMarks.Any(mark => first.IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
