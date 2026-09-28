using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace VSManager
{
    /// <summary>
    /// 提供 MCP 服务器状态与工具的宿主能力（由主窗体通过 McpHub 实现）。
    /// Host capability that provides MCP server status and tools (implemented by the main form through McpHub).
    /// </summary>
    public interface IAgentMcpHost
    {
        /// <summary>当前已连接服务器的工具快照。/ Tool snapshot of the connected servers.</summary>
        IReadOnlyList<McpRegisteredTool> McpTools();
        /// <summary>多行状态文字。/ Multi-line status text.</summary>
        string McpStatusText();
        /// <summary>按当前配置重连全部服务器，返回重连后的状态。/ Reconnects all servers with the current configuration and returns the resulting status.</summary>
        Task<string> ReconnectMcp();
    }

    public sealed partial class AgentService
    {
        private volatile Dictionary<string, McpRegisteredTool> _mcpRound = new Dictionary<string, McpRegisteredTool>();

        /// <summary>
        /// 本轮可用工具：公布的内置工具（按需加载时为核心 + 激活的分组）+ 总控助手已连接的 MCP 工具（笔记助手不挂载 MCP）。
        /// Tools for this round: advertised built-in tools (core plus active groups with on-demand loading) plus connected MCP tools for the manager assistant (the note assistant does not mount MCP).
        /// </summary>
        internal IList<AITool> ToolsForRound()
        {
            var mcp = Profile == AgentProfile.Notes ? null : (_host as IAgentMcpHost)?.McpTools();
            if (mcp == null || mcp.Count == 0)
            {
                _mcpRound = new Dictionary<string, McpRegisteredTool>();
                return AdvertisedBuiltIns();
            }
            var builtIn = new HashSet<string>(_tools.OfType<AIFunction>().Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<string, McpRegisteredTool>(StringComparer.Ordinal);
            var list = new List<AITool>(AdvertisedBuiltIns());
            foreach (var t in mcp)
            {
                if (builtIn.Contains(t.FunctionName) || map.ContainsKey(t.FunctionName)) continue;
                map[t.FunctionName] = t;
                list.Add(new McpFunction(this, t));
            }
            _mcpRound = map;
            return list;
        }

        /// <summary>MCP 工具调用的步骤文字；非 MCP 工具返回 null。/ Step text for MCP tool calls; null for other tools.</summary>
        private string DescribeMcpCall(string name)
        {
            if (name == null || !name.StartsWith("mcp_", StringComparison.Ordinal)) return null;
            return _mcpRound.TryGetValue(name, out var t)
                ? "调用 MCP 工具 / Call MCP tool「" + t.Server + " / " + (string.IsNullOrWhiteSpace(t.Tool.Title) ? t.Tool.Name : t.Tool.Title) + "」"
                : "调用 MCP 工具 / Call MCP tool「" + name + "」";
        }

        [Description("查看已挂载的 MCP（Model Context Protocol）外部服务器：连接状态、错误与各自提供的工具（工具以 mcp_服务器_工具 的名称注册）。用户询问外部工具、MCP 或某个 mcp_ 工具为何不可用时调用。/ Lists mounted MCP (Model Context Protocol) external servers: connection status, errors and their tools (registered as mcp_server_tool).")]
        private string ListMcpServers()
        {
            if (!(_host is IAgentMcpHost h)) return "当前环境不支持 MCP / MCP is not available here";
            var s = _settings();
            var sb = new StringBuilder(h.McpStatusText());
            var tools = h.McpTools();
            if (tools.Count > 0)
            {
                sb.Append("\r\n\r\n已注册工具 / Registered tools：");
                foreach (var t in tools)
                    sb.Append("\r\n- ").Append(t.FunctionName).Append(t.Tool.ReadOnly ? "（只读 / read-only）" : "")
                      .Append("：").Append(OneLine(t.Tool.Description ?? "", 120));
            }
            if (!s.McpEnabled) sb.Append("\r\n\r\n需要用户在「属性 → MCP 服务器」中启用并填写配置；AI 不能修改该配置。/ The user must enable MCP and edit its configuration in Settings → MCP servers; the AI cannot change it.");
            return sb.ToString();
        }

        [Description("按用户已保存的配置重新连接全部 MCP 服务器（会重启本地服务器进程），返回重连后的状态。仅在 MCP 工具失效或用户要求重连时调用。/ Reconnects all MCP servers with the saved configuration (restarting local server processes) and returns the new status. Call only when MCP tools fail or the user asks.")]
        private async Task<string> ReconnectMcpServers()
        {
            if (!(_host is IAgentMcpHost h)) return "当前环境不支持 MCP / MCP is not available here";
            if (!_settings().McpEnabled) return "MCP 未启用 / MCP is off：请用户在「属性 → MCP 服务器」中启用 / ask the user to enable it in Settings → MCP servers";
            if (_settings().AgentConfirm && !await ConfirmAsync("重新连接 MCP 服务器 / Reconnect MCP servers", "将断开并重新启动全部 MCP 服务器连接。/ All MCP server connections will be closed and restarted."))
                return "用户拒绝了本次操作 / The user declined";
            return await h.ReconnectMcp().ConfigureAwait(false);
        }

        /// <summary>
        /// 把一个 MCP 工具包装为 AI 函数：Schema 与说明来自服务器；有副作用（非只读）且开启「操作前确认」时先弹窗审批。
        /// Wraps one MCP tool as an AI function: schema and description come from the server; non-read-only calls ask for approval when confirmation is on.
        /// </summary>
        internal sealed class McpFunction : AIFunction
        {
            private readonly AgentService _owner;
            private readonly McpRegisteredTool _tool;

            public McpFunction(AgentService owner, McpRegisteredTool tool)
            {
                _owner = owner;
                _tool = tool;
            }

            public override string Name => _tool.FunctionName;

            public override string Description =>
                "[外部 MCP 工具 / External MCP tool · " + _tool.Server + "/" + _tool.Tool.Name + (_tool.Tool.ReadOnly ? " · 只读 / read-only" : "") + "] "
                + (_tool.Tool.Description ?? "");

            public override JsonElement JsonSchema => _tool.Tool.InputSchema;

            protected override async ValueTask<object> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            {
                var args = new Dictionary<string, object>(StringComparer.Ordinal);
                if (arguments != null) foreach (var kv in arguments) args[kv.Key] = kv.Value;
                if (_owner._settings().AgentConfirm && !_tool.Tool.ReadOnly)
                {
                    string detail;
                    try { detail = JsonSerializer.Serialize(args); }
                    catch (NotSupportedException) { detail = string.Join(", ", args.Keys); }
                    if (detail.Length > 1500) detail = detail.Substring(0, 1500) + "…";
                    if (!await _owner.ConfirmAsync("调用 MCP 工具 / Call MCP tool「" + _tool.Server + " / " + _tool.Tool.Name + "」", detail).ConfigureAwait(false))
                        return "用户拒绝了本次操作 / The user declined";
                }
                try
                {
                    var r = await _tool.Invoke(args, cancellationToken).ConfigureAwait(false);
                    return (r.IsError ? "MCP 工具返回错误 / MCP tool error：" : "") + r.Text;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    AppLog.Error("mcp.log", "MCP call " + _tool.FunctionName, ex);
                    return "MCP 调用失败 / MCP call failed：" + (ex is AggregateException ae && ae.InnerException != null ? ae.InnerException.Message : ex.Message);
                }
            }
        }
    }
}
