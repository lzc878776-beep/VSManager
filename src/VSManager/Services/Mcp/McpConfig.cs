using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 一个 MCP 服务器配置：本地命令（stdio）或远程地址（Streamable HTTP）。
    /// One MCP server configuration: a local command (stdio) or a remote address (Streamable HTTP).
    /// </summary>
    public sealed class McpServerSpec
    {
        public string Name;
        public string Command;
        public string[] Args = new string[0];
        public Dictionary<string, string> Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Cwd;
        public string Url;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Disabled;

        public bool IsHttp => !string.IsNullOrWhiteSpace(Url);

        /// <summary>确认弹窗与状态中显示的启动方式（不含环境变量与请求头的值）。/ Launch description for confirmations and status (never shows env or header values).</summary>
        public string Describe() => IsHttp ? Url : Command + (Args.Length == 0 ? "" : " " + string.Join(" ", Args));
    }

    /// <summary>
    /// 解析 MCP 服务器配置 JSON，兼容常见格式：{"mcpServers":{...}}、{"servers":{...}} 或直接的名称字典。
    /// Parses MCP server configuration JSON in the common shapes: {"mcpServers":{...}}, {"servers":{...}} or a bare name map.
    /// </summary>
    public static class McpConfig
    {
        private static readonly Regex ValidName = new Regex(@"^[A-Za-z0-9_\-]{1,32}$", RegexOptions.Compiled);

        /// <summary>留空配置时显示的示例（不预装任何服务器）。/ Example shown for an empty configuration (no server is preinstalled).</summary>
        public const string Example = "{\r\n  \"mcpServers\": {\r\n    \"example\": { \"command\": \"npx\", \"args\": [\"-y\", \"<mcp-server-package>\"], \"env\": { \"TOKEN_NAME\": \"%TOKEN_NAME%\" } },\r\n    \"remote\": { \"url\": \"https://example.com/mcp\", \"headers\": { \"Authorization\": \"Bearer %TOKEN_NAME%\" }, \"disabled\": true }\r\n  }\r\n}";

        /// <summary>解析配置；成功时 error 为 null。空文本表示没有服务器。/ Parses the configuration; error is null on success. Empty text means no servers.</summary>
        public static List<McpServerSpec> Parse(string json, out string error)
        {
            error = null;
            var list = new List<McpServerSpec>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) { error = "配置必须是 JSON 对象 / The configuration must be a JSON object"; return null; }
                    var servers = root;
                    if (root.TryGetProperty("mcpServers", out var m)) servers = m;
                    else if (root.TryGetProperty("servers", out var s)) servers = s;
                    if (servers.ValueKind != JsonValueKind.Object) { error = "mcpServers 必须是对象 / mcpServers must be an object"; return null; }
                    foreach (var p in servers.EnumerateObject())
                    {
                        if (!ValidName.IsMatch(p.Name)) { error = "服务器名只能含字母、数字、_ 或 -（最多 32 个字符）/ Server names may contain only letters, digits, _ or - (up to 32 characters): " + p.Name; return null; }
                        if (list.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) { error = "服务器名重复 / Duplicate server name: " + p.Name; return null; }
                        if (p.Value.ValueKind != JsonValueKind.Object) { error = "服务器配置必须是对象 / Server entry must be an object: " + p.Name; return null; }
                        var spec = new McpServerSpec { Name = p.Name };
                        var v = p.Value;
                        spec.Command = Str(v, "command");
                        spec.Url = Str(v, "url");
                        spec.Cwd = Str(v, "cwd");
                        spec.Disabled = v.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
                        if (v.TryGetProperty("args", out var a))
                        {
                            if (a.ValueKind != JsonValueKind.Array || a.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) { error = "args 必须是字符串数组 / args must be an array of strings: " + p.Name; return null; }
                            spec.Args = a.EnumerateArray().Select(x => x.GetString()).ToArray();
                        }
                        if (!Map(v, "env", spec.Env) || !Map(v, "headers", spec.Headers)) { error = "env / headers 必须是字符串字典 / env / headers must map names to strings: " + p.Name; return null; }
                        bool hasCommand = !string.IsNullOrWhiteSpace(spec.Command), hasUrl = !string.IsNullOrWhiteSpace(spec.Url);
                        if (hasCommand == hasUrl) { error = "每个服务器需且只能填写 command 或 url 之一 / Each server needs exactly one of command or url: " + p.Name; return null; }
                        if (hasUrl && (!Uri.TryCreate(spec.Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
                        { error = "url 必须是 http(s) 地址 / url must be an http(s) address: " + p.Name; return null; }
                        list.Add(spec);
                    }
                }
            }
            catch (JsonException ex) { error = "JSON 格式错误 / Invalid JSON: " + ex.Message; return null; }
            return list;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : null;

        private static bool Map(JsonElement e, string name, Dictionary<string, string> into)
        {
            if (!e.TryGetProperty(name, out var v)) return true;
            if (v.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in v.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) return false;
                into[p.Name] = p.Value.GetString() ?? "";
            }
            return true;
        }
    }
}
