using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>MCP 服务器暴露的一个工具。/ One tool exposed by an MCP server.</summary>
    public sealed class McpToolInfo
    {
        public string Name;
        public string Title;
        public string Description;
        /// <summary>输入参数 JSON Schema。/ Input JSON Schema.</summary>
        public JsonElement InputSchema;
        /// <summary>服务器声明只读（annotations.readOnlyHint）。/ Declared read-only by the server (annotations.readOnlyHint).</summary>
        public bool ReadOnly;
    }

    /// <summary>tools/call 的结果（已转换为文字）。/ Result of tools/call, converted to text.</summary>
    public sealed class McpCallResult
    {
        public string Text;
        public bool IsError;
    }

    /// <summary>MCP 服务器返回的 JSON-RPC 错误。/ JSON-RPC error returned by an MCP server.</summary>
    public sealed class McpException : Exception
    {
        public int Code { get; }
        public McpException(int code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// 最小 MCP 客户端：握手、列出工具、调用工具（JSON-RPC 2.0）。
    /// Minimal MCP client: handshake, list tools and call tools (JSON-RPC 2.0).
    /// </summary>
    public sealed class McpClient : IDisposable
    {
        public const string ProtocolVersion = "2025-06-18";
        public const int MaxResultChars = 16000;

        private readonly IMcpTransport _transport;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>();
        private long _nextId;
        private volatile string _closedReason;

        public string ServerName { get; private set; }
        public string ServerVersion { get; private set; }
        public string NegotiatedVersion { get; private set; }
        public string Instructions { get; private set; }
        public bool IsClosed => _closedReason != null;
        public string ClosedReason => _closedReason;
        /// <summary>服务器通知工具列表已变化。/ Raised when the server reports that its tool list changed.</summary>
        public event Action ToolsChanged;
        public event Action<string> Closed;

        public McpClient(IMcpTransport transport)
        {
            _transport = transport;
            _transport.MessageReceived += OnMessage;
            _transport.Closed += OnClosed;
        }

        /// <summary>启动传输并完成 initialize 握手。/ Starts the transport and completes the initialize handshake.</summary>
        public async Task InitializeAsync(TimeSpan timeout, CancellationToken ct)
        {
            await _transport.StartAsync(ct).ConfigureAwait(false);
            var result = await RequestAsync("initialize", new Dictionary<string, object>
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new Dictionary<string, object>(),
                ["clientInfo"] = new Dictionary<string, object> { ["name"] = "VSManager", ["version"] = typeof(McpClient).Assembly.GetName().Version.ToString() },
            }, timeout, ct).ConfigureAwait(false);
            NegotiatedVersion = Str(result, "protocolVersion") ?? ProtocolVersion;
            _transport.ProtocolVersion = NegotiatedVersion;
            if (result.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                ServerName = Str(info, "name");
                ServerVersion = Str(info, "version");
            }
            Instructions = Str(result, "instructions");
            await NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
        }

        /// <summary>列出全部工具（自动翻页）。/ Lists all tools (follows pagination).</summary>
        public async Task<List<McpToolInfo>> ListToolsAsync(TimeSpan timeout, CancellationToken ct)
        {
            var tools = new List<McpToolInfo>();
            string cursor = null;
            for (int page = 0; page < 50; page++)
            {
                var p = new Dictionary<string, object>();
                if (cursor != null) p["cursor"] = cursor;
                var result = await RequestAsync("tools/list", p, timeout, ct).ConfigureAwait(false);
                if (result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var t in arr.EnumerateArray())
                    {
                        string name = Str(t, "name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var schema = t.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object ? s.Clone() : EmptySchema();
                        bool ro = t.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object
                            && a.TryGetProperty("readOnlyHint", out var r) && r.ValueKind == JsonValueKind.True;
                        string title = Str(t, "title");
                        if (title == null && a.ValueKind == JsonValueKind.Object) title = Str(a, "title");
                        tools.Add(new McpToolInfo { Name = name, Title = title, Description = Str(t, "description") ?? "", InputSchema = schema, ReadOnly = ro });
                    }
                cursor = Str(result, "nextCursor");
                if (string.IsNullOrEmpty(cursor)) break;
            }
            return tools;
        }

        /// <summary>调用工具并把结果内容转换为文字。/ Calls a tool and converts its content to text.</summary>
        public async Task<McpCallResult> CallToolAsync(string name, IDictionary<string, object> arguments, TimeSpan timeout, CancellationToken ct)
        {
            var result = await RequestAsync("tools/call", new Dictionary<string, object>
            {
                ["name"] = name,
                ["arguments"] = arguments ?? new Dictionary<string, object>(),
            }, timeout, ct).ConfigureAwait(false);
            return new McpCallResult { Text = ContentText(result), IsError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True };
        }

        /// <summary>把 tools/call 结果中的 content 转成文字；图片与二进制只给出摘要。/ Converts tools/call content to text; images and binaries are summarised.</summary>
        internal static string ContentText(JsonElement result)
        {
            var sb = new StringBuilder();
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var c in content.EnumerateArray())
                {
                    string type = Str(c, "type") ?? "";
                    string piece;
                    switch (type)
                    {
                        case "text": piece = Str(c, "text") ?? ""; break;
                        case "image":
                        case "audio":
                            piece = "[" + type + ": " + (Str(c, "mimeType") ?? "?") + "，约 / about " + ((Str(c, "data") ?? "").Length * 3 / 4) + " bytes，未传给模型 / not passed to the model]";
                            break;
                        case "resource":
                            var r = c.TryGetProperty("resource", out var rr) ? rr : default;
                            piece = r.ValueKind == JsonValueKind.Object ? (Str(r, "text") ?? "[resource: " + (Str(r, "uri") ?? "?") + "]") : "";
                            break;
                        case "resource_link": piece = "[link: " + (Str(c, "name") ?? "") + " " + (Str(c, "uri") ?? "") + "]"; break;
                        default: piece = c.GetRawText(); break;
                    }
                    if (piece.Length == 0) continue;
                    if (sb.Length > 0) sb.Append("\n");
                    sb.Append(piece);
                }
            if (sb.Length == 0 && result.TryGetProperty("structuredContent", out var sc)) sb.Append(sc.GetRawText());
            string text = sb.Length == 0 ? "（工具没有返回内容 / The tool returned no content）" : sb.ToString();
            return text.Length > MaxResultChars ? text.Substring(0, MaxResultChars) + "\n…（已截断 / truncated）" : text;
        }

        private static JsonElement EmptySchema()
        {
            using (var d = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}")) return d.RootElement.Clone();
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public async Task<JsonElement> RequestAsync(string method, object parameters, TimeSpan timeout, CancellationToken ct)
        {
            if (_closedReason != null) throw new IOException("MCP 连接已关闭 / MCP connection closed: " + _closedReason);
            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                var msg = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
                if (parameters != null) msg["params"] = parameters;
                await _transport.SendAsync(JsonSerializer.Serialize(msg), ct).ConfigureAwait(false);
                using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timer.CancelAfter(timeout);
                    var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timer.Token)).ConfigureAwait(false);
                    if (done != tcs.Task)
                    {
                        _ = NotifyAsync("notifications/cancelled", new Dictionary<string, object> { ["requestId"] = id, ["reason"] = "timeout or cancelled" }, CancellationToken.None)
                            .ContinueWith(t => t.Exception?.Handle(_ => true));
                        ct.ThrowIfCancellationRequested();
                        throw new TimeoutException("MCP 请求超时 / MCP request timed out: " + method);
                    }
                }
                return await tcs.Task.ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
        }

        public Task NotifyAsync(string method, object parameters, CancellationToken ct)
        {
            var msg = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["method"] = method };
            if (parameters != null) msg["params"] = parameters;
            return _transport.SendAsync(JsonSerializer.Serialize(msg), ct);
        }

        private void OnMessage(string json)
        {
            JsonElement root;
            try { using (var doc = JsonDocument.Parse(json)) root = doc.RootElement.Clone(); }
            catch (JsonException) { return; }
            if (root.ValueKind != JsonValueKind.Object) return;
            bool hasId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null;
            string method = Str(root, "method");
            if (method != null)
            {
                if (hasId) _ = AnswerServerRequest(idEl.Clone(), method);
                else if (method == "notifications/tools/list_changed") ToolsChanged?.Invoke();
                return;
            }
            if (!hasId || !idEl.TryGetInt64(out long id) || !_pending.TryGetValue(id, out var tcs)) return;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                int code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out int ci) ? ci : 0;
                tcs.TrySetException(new McpException(code, Str(err, "message") ?? "MCP error " + code));
            }
            else tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r : default);
        }

        /// <summary>响应服务器请求：ping 返回空对象，其他请求（采样、询问等）第一版不支持。/ Answers server requests: ping returns {}, others (sampling, elicitation...) are unsupported in this version.</summary>
        private async Task AnswerServerRequest(JsonElement id, string method)
        {
            var msg = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id };
            if (method == "ping") msg["result"] = new Dictionary<string, object>();
            else msg["error"] = new Dictionary<string, object> { ["code"] = -32601, ["message"] = "Method not supported by VSManager: " + method };
            try { await _transport.SendAsync(JsonSerializer.Serialize(msg), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is System.Net.Http.HttpRequestException) { }
        }

        private void OnClosed(string reason)
        {
            _closedReason = string.IsNullOrWhiteSpace(reason) ? "closed" : reason;
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException("MCP 连接已关闭 / MCP connection closed: " + _closedReason));
            Closed?.Invoke(_closedReason);
        }

        public void Dispose()
        {
            _transport.MessageReceived -= OnMessage;
            try { _transport.Dispose(); } catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException) { }
            if (_closedReason == null) OnClosed("已断开 / Disconnected");
        }
    }
}
