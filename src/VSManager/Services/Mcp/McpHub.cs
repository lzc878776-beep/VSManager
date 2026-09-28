using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>注册给 AI 助手的 MCP 工具（带服务器信息与模型可见的名称）。/ An MCP tool registered for the assistant, with server info and the model-facing name.</summary>
    public sealed class McpRegisteredTool
    {
        /// <summary>模型看到的函数名，形如 mcp_服务器_工具。/ Function name seen by the model, such as mcp_server_tool.</summary>
        public string FunctionName;
        public string Server;
        public McpToolInfo Tool;
        /// <summary>调用该工具（后台线程执行）。/ Calls the tool (runs on a background thread).</summary>
        public Func<IDictionary<string, object>, CancellationToken, Task<McpCallResult>> Invoke;
    }

    /// <summary>单个服务器的连接状态。/ Connection status of one server.</summary>
    public sealed class McpServerStatus
    {
        public string Name;
        public string Endpoint;
        /// <summary>disabled / connecting / connected / failed / off。/ disabled / connecting / connected / failed / off.</summary>
        public string State;
        public string Error;
        public string ServerInfo;
        public string[] Tools = new string[0];

        public string Describe()
        {
            string head = Name + "：";
            switch (State)
            {
                case "connected": return head + "已连接 / connected，" + Tools.Length + " 个工具 / tools" + (string.IsNullOrEmpty(ServerInfo) ? "" : "（" + ServerInfo + "）");
                case "connecting": return head + "连接中… / connecting…";
                case "disabled": return head + "已停用 / disabled";
                case "off": return head + "MCP 未启用 / MCP is off";
                default: return head + "失败 / failed：" + (Error ?? "");
            }
        }
    }

    /// <summary>
    /// 管理全部 MCP 服务器连接，并提供可注册给 AI 助手的工具快照。连接在后台线程完成，不阻塞界面。
    /// Manages all MCP server connections and provides a tool snapshot for the assistant. Connections run in the background without blocking the UI.
    /// </summary>
    public sealed class McpHub : IDisposable
    {
        public static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(120);
        public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(180);

        private sealed class Conn
        {
            public McpServerSpec Spec;
            public McpClient Client;
            public McpServerStatus Status;
            public List<McpToolInfo> Tools = new List<McpToolInfo>();
        }

        private readonly object _gate = new object();
        private readonly Func<McpServerSpec, IMcpTransport> _transportFactory;
        private List<Conn> _conns = new List<Conn>();
        private IReadOnlyList<McpRegisteredTool> _tools = new McpRegisteredTool[0];
        private string _appliedKey;
        private bool _enabled;
        private string _configError;
        private int _generation;

        /// <summary>状态或工具变化（任意线程触发）。/ Raised when status or tools change (on any thread).</summary>
        public event Action Changed;

        public McpHub(Func<McpServerSpec, IMcpTransport> transportFactory = null)
        {
            _transportFactory = transportFactory ?? DefaultTransport;
        }

        private static IMcpTransport DefaultTransport(McpServerSpec spec) =>
            spec.IsHttp ? (IMcpTransport)new McpHttpTransport(spec) : McpStdioTransport.Launch(spec);

        public bool Enabled { get { lock (_gate) return _enabled; } }

        /// <summary>当前已连接服务器的工具快照。/ Tool snapshot of the connected servers.</summary>
        public IReadOnlyList<McpRegisteredTool> Tools { get { lock (_gate) return _tools; } }

        public IReadOnlyList<McpServerStatus> Status
        {
            get { lock (_gate) return _conns.Select(c => c.Status).ToList(); }
        }

        /// <summary>给界面与 AI 的多行状态文字。/ Multi-line status text for the UI and the assistant.</summary>
        public string StatusText()
        {
            lock (_gate)
            {
                if (!_enabled) return "MCP 未启用 / MCP is off";
                if (_configError != null) return "配置错误 / Configuration error：" + _configError;
                if (_conns.Count == 0) return "未配置 MCP 服务器 / No MCP servers configured";
                return string.Join("\r\n", _conns.Select(c => c.Status.Describe()));
            }
        }

        /// <summary>
        /// 应用设置：配置或开关变化时断开旧连接并在后台重连；未变化时不做任何事（force 强制重连）。
        /// Applies settings: on any change old connections close and reconnect in the background; unchanged settings do nothing (force reconnects).
        /// </summary>
        public Task Apply(bool enabled, string json, bool force = false)
        {
            string key = (enabled ? "1|" : "0|") + (json ?? "");
            List<Conn> old;
            List<Conn> fresh = new List<Conn>();
            int gen;
            lock (_gate)
            {
                if (!force && key == _appliedKey) return Task.CompletedTask;
                _appliedKey = key;
                _enabled = enabled;
                _configError = null;
                old = _conns;
                gen = ++_generation;
                if (enabled)
                {
                    var specs = McpConfig.Parse(json, out string error);
                    if (specs == null) _configError = error;
                    else
                        foreach (var s in specs)
                            fresh.Add(new Conn
                            {
                                Spec = s,
                                Status = new McpServerStatus { Name = s.Name, Endpoint = s.Describe(), State = s.Disabled ? "disabled" : "connecting" },
                            });
                }
                _conns = fresh;
                _tools = new McpRegisteredTool[0];
            }
            foreach (var c in old) Close(c);
            Changed?.Invoke();
            var work = fresh.Where(c => !c.Spec.Disabled).Select(c => Task.Run(() => Connect(c, gen))).ToArray();
            return Task.WhenAll(work);
        }

        /// <summary>按当前配置强制重连全部服务器。/ Force-reconnects all servers with the current configuration.</summary>
        public Task Reconnect()
        {
            bool enabled; string json;
            lock (_gate)
            {
                string key = _appliedKey ?? "0|";
                enabled = key.StartsWith("1|", StringComparison.Ordinal);
                json = key.Substring(2);
            }
            return Apply(enabled, json, true);
        }

        private async Task Connect(Conn c, int gen)
        {
            McpClient client = null;
            try
            {
                client = new McpClient(_transportFactory(c.Spec));
                await client.InitializeAsync(InitTimeout, CancellationToken.None).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(InitTimeout, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    if (gen != _generation) { client.Dispose(); return; }
                    c.Client = client;
                    c.Tools = tools;
                    c.Status = new McpServerStatus
                    {
                        Name = c.Spec.Name, Endpoint = c.Spec.Describe(), State = "connected",
                        ServerInfo = ((client.ServerName ?? "") + " " + (client.ServerVersion ?? "")).Trim(),
                        Tools = tools.Select(t => t.Name).ToArray(),
                    };
                    RebuildTools();
                }
                var captured = client;
                client.Closed += reason => OnDisconnected(c, captured, reason);
                client.ToolsChanged += () => _ = RefreshTools(c, captured);
                if (client.IsClosed) OnDisconnected(c, client, client.ClosedReason);
                AppLog.Write("mcp.log", "[" + c.Spec.Name + "] connected, " + tools.Count + " tools");
            }
            catch (Exception ex)
            {
                string detail = Detail(ex, client);
                client?.Dispose();
                lock (_gate)
                {
                    if (gen != _generation) return;
                    c.Status = new McpServerStatus { Name = c.Spec.Name, Endpoint = c.Spec.Describe(), State = "failed", Error = detail };
                }
                AppLog.Write("mcp.log", "[" + c.Spec.Name + "] failed: " + detail);
            }
            Changed?.Invoke();
        }

        private static string Detail(Exception ex, McpClient client)
        {
            var e = ex is AggregateException ae && ae.InnerExceptions.Count == 1 ? ae.InnerException : ex;
            string msg = e.Message;
            if (e is System.ComponentModel.Win32Exception) msg = "无法启动命令（请检查 command 与 PATH）/ Cannot start the command (check command and PATH): " + msg;
            return msg.Length > 300 ? msg.Substring(0, 300) + "…" : msg;
        }

        private async Task RefreshTools(Conn c, McpClient client)
        {
            try
            {
                var tools = await client.ListToolsAsync(InitTimeout, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    if (c.Client != client || !_conns.Contains(c)) return;
                    c.Tools = tools;
                    c.Status.Tools = tools.Select(t => t.Name).ToArray();
                    RebuildTools();
                }
                Changed?.Invoke();
            }
            catch (Exception ex) { AppLog.Error("mcp.log", "MCP tools refresh", ex); }
        }

        private void OnDisconnected(Conn c, McpClient client, string reason)
        {
            lock (_gate)
            {
                if (c.Client != client || !_conns.Contains(c)) return;
                c.Client = null;
                c.Tools = new List<McpToolInfo>();
                c.Status = new McpServerStatus { Name = c.Spec.Name, Endpoint = c.Spec.Describe(), State = "failed", Error = "连接已断开 / Disconnected：" + reason };
                RebuildTools();
            }
            Changed?.Invoke();
        }

        private void RebuildTools()
        {
            var list = new List<McpRegisteredTool>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _conns)
            {
                var client = c.Client;
                if (client == null) continue;
                foreach (var t in c.Tools)
                {
                    string fn = FunctionName(c.Spec.Name, t.Name);
                    for (int i = 2; !used.Add(fn); i++) fn = Fit(FunctionName(c.Spec.Name, t.Name), "_" + i);
                    var tool = t;
                    list.Add(new McpRegisteredTool
                    {
                        FunctionName = fn, Server = c.Spec.Name, Tool = t,
                        Invoke = (args, ct) => Task.Run(() => client.CallToolAsync(tool.Name, args, CallTimeout, ct), ct),
                    });
                }
            }
            _tools = list;
        }

        private static readonly Regex Unsafe = new Regex(@"[^A-Za-z0-9_\-]", RegexOptions.Compiled);

        /// <summary>
        /// 模型可见函数名：mcp_服务器_工具，只含 [A-Za-z0-9_-]，最长 64 个字符（超长时加哈希后缀）。
        /// Model-facing function name mcp_server_tool using only [A-Za-z0-9_-], at most 64 characters (hash suffix when too long).
        /// </summary>
        public static string FunctionName(string server, string tool)
        {
            string raw = "mcp_" + server + "_" + tool;
            string safe = Unsafe.Replace(raw, "_");
            if (safe == raw && safe.Length <= 64) return safe;
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return Fit(safe, "_" + BitConverter.ToString(h, 0, 3).Replace("-", "").ToLowerInvariant());
            }
        }

        private static string Fit(string name, string suffix) =>
            (name.Length + suffix.Length > 64 ? name.Substring(0, 64 - suffix.Length) : name) + suffix;

        private static void Close(Conn c)
        {
            var client = c.Client;
            c.Client = null;
            if (client != null) Task.Run(() => client.Dispose());
        }

        public void Dispose()
        {
            List<Conn> old;
            lock (_gate)
            {
                old = _conns;
                _conns = new List<Conn>();
                _tools = new McpRegisteredTool[0];
                _generation++;
                _appliedKey = null;
            }
            foreach (var c in old) c.Client?.Dispose();
        }
    }
}
