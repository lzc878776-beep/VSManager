using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AIMessage = Microsoft.Extensions.AI.ChatMessage;
using AIRole = Microsoft.Extensions.AI.ChatRole;

namespace VSManager
{
    /// <summary>
    /// 会话层隔离的纯逻辑：模型上下文按「轮」归属项目（一轮 = 一条用户 / 通知消息及其后的助手与工具消息），
    /// 项目轮只看到全局轮与本项目轮，全局轮只看到全局轮和各项目的简短摘要，避免不同项目的结论、文件路径与错误信息互相串扰。
    /// Pure logic of session-level isolation: the model context is attributed to projects per round (one user / notice message
    /// plus the assistant and tool messages after it). A project round sees only global rounds and its own project's rounds; a
    /// global round sees only global rounds plus a short digest per project, so conclusions, file paths and errors of different
    /// projects do not leak into each other.
    /// </summary>
    public static class AgentSessionScopes
    {
        /// <summary>全局轮看到的每个项目摘要的最大字数。/ Max characters of each project digest shown to a global round.</summary>
        public const int DigestLength = 240;

        /// <summary>
        /// 选出本轮可见的上下文：按顺序遍历，遇到用户消息时切换当前轮的归属，只保留归属为全局或等于 <paramref name="scope"/> 的轮。
        /// Selects the context visible to this round: walks in order, switching the owning scope at each user message, and keeps
        /// only rounds that are global or belong to <paramref name="scope"/>.
        /// </summary>
        public static List<AIMessage> Select(IReadOnlyList<AIMessage> history, Func<AIMessage, string> scopeOf, string scope)
        {
            var list = new List<AIMessage>();
            string current = null;
            foreach (var m in history ?? new AIMessage[0])
            {
                if (m == null) continue;
                if (m.Role == AIRole.User) current = scopeOf(m);
                if (current == null || Same(current, scope)) list.Add(m);
            }
            return list;
        }

        /// <summary>
        /// 各项目最近一条助手文字结论（截断），按项目最近活动排序；不含 <paramref name="exclude"/>。
        /// The latest assistant text conclusion of each project (clipped), most recently active first; <paramref name="exclude"/> is skipped.
        /// </summary>
        public static List<KeyValuePair<string, string>> Digests(IReadOnlyList<AIMessage> history, Func<AIMessage, string> scopeOf,
            string exclude = null, int maxLength = DigestLength)
        {
            var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            string current = null;
            foreach (var m in history ?? new AIMessage[0])
            {
                if (m == null) continue;
                if (m.Role == AIRole.User)
                {
                    current = scopeOf(m);
                    if (current == null || Same(current, exclude)) continue;
                    order.RemoveAll(s => Same(s, current));
                    order.Add(current);
                    if (!latest.ContainsKey(current)) latest[current] = "";
                    continue;
                }
                if (current == null || Same(current, exclude) || m.Role != AIRole.Assistant) continue;
                string text = OneLine(m.Text);
                if (text.Length > 0) latest[current] = text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";
            }
            order.Reverse();
            return order.Select(s => new KeyValuePair<string, string>(s, latest[s])).ToList();
        }

        /// <summary>项目轮的隔离说明。/ Isolation note for a project round.</summary>
        public static string ProjectNote(string scope) =>
            "【会话隔离】本轮属于项目「" + scope + "」的独立会话：上下文只包含全局对话和该项目的对话，其他项目的对话已隔离。"
            + "不要引用、假设或混用其他项目的结论、文件路径与错误信息；确实需要其他项目的情况时用工具查询（list_tasks、read_task_reply 等）。"
            + "\n[Session isolation] This round belongs to the separate session of project \"" + scope + "\": the context holds only the global conversation and this project's conversation; other projects are isolated. "
            + "Do not cite, assume or mix in other projects' conclusions, file paths or errors; when another project's state is really needed, query it with tools (list_tasks, read_task_reply, ...).";

        /// <summary>全局轮的隔离说明与各项目摘要；没有项目会话时返回 null。/ Isolation note with project digests for a global round; null without project sessions.</summary>
        public static string GlobalNote(IList<KeyValuePair<string, string>> digests)
        {
            if (digests == null || digests.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            sb.Append("【会话隔离】各项目的对话已按项目隔离，本轮只看到全局对话。以下是各项目会话最近一条结论的摘要，仅供了解进度；需要细节时用工具查询，或让用户围绕单个项目提问，不要把一个项目的文件路径或错误套用到另一个项目。")
              .Append("\n[Session isolation] Conversations are isolated per project; this round sees only the global conversation. Below is the latest conclusion of each project session, for progress only; query tools for details or ask about one project at a time, and never apply one project's file paths or errors to another.");
            foreach (var d in digests)
                sb.Append("\n- ").Append(d.Key).Append("：").Append(string.IsNullOrEmpty(d.Value) ? "（尚无结论 / no conclusion yet）" : d.Value);
            return sb.ToString();
        }

        /// <summary>
        /// 从用户文字推断归属：文字中恰好提到一个已知项目（项目名或登记别名）时归属该项目，否则为全局（null）。
        /// Infers the owning scope from user text: exactly one known project mentioned (project name or registered alias) → that project; otherwise global (null).
        /// </summary>
        /// <param name="projects">(项目名, 可匹配的名称) 列表。/ (project, matchable name) pairs.</param>
        public static string Infer(string text, IEnumerable<KeyValuePair<string, string>> projects)
        {
            if (string.IsNullOrWhiteSpace(text) || projects == null) return null;
            var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in projects)
            {
                string name = (p.Value ?? "").Trim();
                if (string.IsNullOrEmpty(p.Key) || name.Length < 2) continue;
                if (text.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(p.Key.Trim());
            }
            return hits.Count == 1 ? hits.First() : null;
        }

        /// <summary>规范化项目名作为归属键；空白返回 null。/ Normalizes a project name as the scope key; blank → null.</summary>
        public static string Key(string project)
        {
            string s = (project ?? "").Trim();
            return s.Length == 0 ? null : s;
        }

        public static bool Same(string a, string b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string OneLine(string s) =>
            string.Join(" ", (s ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));
    }

    public sealed partial class AgentService
    {
        /// <summary>
        /// 模型消息的项目归属（只标在用户 / 通知消息上，其后的助手与工具消息继承）；未标记即全局。
        /// Project scope of model messages (tagged on user / notice messages only; later assistant and tool messages inherit); untagged = global.
        /// </summary>
        private static readonly ConditionalWeakTable<AIMessage, string> MessageScopes = new ConditionalWeakTable<AIMessage, string>();

        internal static string ScopeOf(AIMessage m) => m != null && MessageScopes.TryGetValue(m, out var s) ? s : null;

        internal static void SetScope(AIMessage m, string scope)
        {
            if (m == null) return;
            MessageScopes.Remove(m);
            scope = AgentSessionScopes.Key(scope);
            if (scope != null) MessageScopes.Add(m, scope);
        }

        /// <summary>是否启用会话层隔离（笔记助手不隔离）。/ Whether session isolation is on (never for the note assistant).</summary>
        private bool SessionIsolation => Profile != AgentProfile.Notes && (_settings()?.AgentSessionIsolation ?? true);

        /// <summary>当前（或最近）一轮的项目归属。/ Project scope of the current (or latest) round.</summary>
        internal string CurrentScope { get; private set; }

        /// <summary>用户轮：按文字中提到的唯一项目推断归属。/ User rounds: infer the scope from the single project the text mentions.</summary>
        private string InferScope(string text)
        {
            if (!SessionIsolation) return null;
            try
            {
                var names = new List<KeyValuePair<string, string>>();
                foreach (var v in _host.Instances.ToList())
                {
                    string display = _host.NameOf(v);
                    string project = TaskProjectContext.ProjectName(v.Key, display);
                    if (string.IsNullOrEmpty(project)) continue;
                    names.Add(new KeyValuePair<string, string>(project, project));
                    if (!string.IsNullOrEmpty(display)) names.Add(new KeyValuePair<string, string>(project, display));
                }
                foreach (var e in _host.Solutions.Items.ToList())
                {
                    string project = TaskProjectContext.ProjectName(e.Path, e.Alias);
                    if (string.IsNullOrEmpty(project)) continue;
                    names.Add(new KeyValuePair<string, string>(project, project));
                    names.Add(new KeyValuePair<string, string>(project, e.Alias));
                }
                return AgentSessionScopes.Infer(text, names);
            }
            catch (Exception ex)
            {
                AppLog.Write(LogFile, "推断会话归属失败 / Scope inference failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 本轮发给模型的上下文：隔离关闭时为完整历史；开启时只含全局轮与本项目轮，并在最前面加隔离说明（全局轮附各项目摘要）。
        /// Context sent to the model this round: the full history when isolation is off; otherwise only global rounds and this
        /// project's rounds, preceded by an isolation note (global rounds get per-project digests).
        /// </summary>
        private List<AIMessage> ContextForRound(string scope)
        {
            if (!SessionIsolation) return _history.ToList();
            var selected = AgentSessionScopes.Select(_history, ScopeOf, scope);
            string note = scope != null ? AgentSessionScopes.ProjectNote(scope)
                : AgentSessionScopes.GlobalNote(AgentSessionScopes.Digests(_history, ScopeOf));
            if (note != null) selected.Insert(0, new AIMessage(AIRole.System, note));
            return selected;
        }
    }
}
