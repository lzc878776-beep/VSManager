using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VSManager.CadAgent
{
    /// <summary>
    /// 动作协议 v1 的动作名。/ Action names of action protocol v1.
    /// </summary>
    public static class CadActions
    {
        public const string OpenDrawing = "openDrawing";
        public const string SwitchDrawing = "switchDrawing";
        public const string CloseAllDrawings = "closeAllDrawings";
        public const string RunCommand = "runCommand";
        public const string GetParam = "getParam";
        public const string Screenshot = "screenshot";
        public const string GetLog = "getLog";
        public const string GetEntityCount = "getEntityCount";

        public static readonly IReadOnlyList<string> All = new[]
        {
            OpenDrawing, SwitchDrawing, CloseAllDrawings, RunCommand, GetParam, Screenshot, GetLog, GetEntityCount,
        };

        /// <summary>按不区分大小写匹配返回规范动作名；未知时返回 null。/ Returns the canonical action name (case-insensitive), or null when unknown.</summary>
        public static string Normalize(string action)
        {
            string a = (action ?? "").Trim();
            return All.FirstOrDefault(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 统一错误码。/ Unified error codes.
    /// </summary>
    public static class CadErrors
    {
        public const string Timeout = "TIMEOUT";
        public const string CadUnavailable = "CAD_UNAVAILABLE";
        public const string CadGone = "CAD_GONE";
        public const string InvalidArgs = "INVALID_ARGS";
        public const string UnknownAction = "UNKNOWN_ACTION";
        public const string NotFound = "NOT_FOUND";
        public const string ActionFailed = "ACTION_FAILED";
        public const string Busy = "BUSY";
        public const string Cancelled = "CANCELLED";
        public const string Transport = "TRANSPORT";
        public const string Skipped = "SKIPPED";

        /// <summary>可重试的错误（连接类 / 忙）；超时与动作失败不重试。/ Retryable errors (connection / busy); timeouts and action failures are not retried.</summary>
        public static bool IsRetryable(string code) => code == Transport || code == Busy || code == CadUnavailable;
    }

    /// <summary>
    /// 单条动作请求：统一入参 vs / timeoutMs / action / args。/ One action request with the unified vs / timeoutMs / action / args input.
    /// </summary>
    [DataContract]
    public sealed class CadActionRequest
    {
        /// <summary>默认超时（毫秒）。/ Default timeout in milliseconds.</summary>
        public const int DefaultTimeoutMs = 60000;
        public const int MinTimeoutMs = 1000;
        public const int MaxTimeoutMs = 600000;

        [DataMember(Name = "id", EmitDefaultValue = false)] public string Id { get; set; }
        [DataMember(Name = "vs", EmitDefaultValue = false)] public string Vs { get; set; }
        [DataMember(Name = "timeoutMs", EmitDefaultValue = false)] public int TimeoutMs { get; set; }
        [DataMember(Name = "action")] public string Action { get; set; }
        [DataMember(Name = "args", EmitDefaultValue = false)] public Dictionary<string, string> Args { get; set; }

        /// <summary>规范化后的有效超时。/ Effective timeout after clamping.</summary>
        public int EffectiveTimeoutMs => TimeoutMs <= 0 ? DefaultTimeoutMs : Math.Max(MinTimeoutMs, Math.Min(MaxTimeoutMs, TimeoutMs));

        /// <summary>读取参数（不区分大小写，去空白）；缺失时返回 fallback。/ Reads an argument (case-insensitive, trimmed); returns fallback when missing.</summary>
        public string Arg(string name, string fallback = null)
        {
            if (Args != null)
                foreach (var kv in Args)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                        return kv.Value.Trim();
            return fallback;
        }

        public bool Flag(string name, bool fallback = false)
        {
            string v = Arg(name);
            if (v == null) return fallback;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public int Number(string name, int fallback) => int.TryParse(Arg(name), out int n) ? n : fallback;

        /// <summary>设置参数（保留原有键的大小写）。/ Sets an argument (keeps an existing key's casing).</summary>
        public void SetArg(string name, string value)
        {
            if (Args == null) Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string key = Args.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            Args[key] = value;
        }

        public CadActionRequest Clone() => new CadActionRequest
        {
            Id = Id, Vs = Vs, TimeoutMs = TimeoutMs, Action = Action,
            Args = Args == null ? null : new Dictionary<string, string>(Args, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// 回传产物：截图（base64 PNG）、日志或文字。只在内存中传递，不写入用户目录。
    /// Returned artifact: screenshot (base64 PNG), log or text. Passed in memory only and never written to user folders.
    /// </summary>
    [DataContract]
    public sealed class CadArtifact
    {
        public const string Image = "image";
        public const string Log = "log";
        public const string Text = "text";

        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "name", EmitDefaultValue = false)] public string Name { get; set; }
        [DataMember(Name = "mime", EmitDefaultValue = false)] public string Mime { get; set; }
        /// <summary>二进制内容的 base64（截图）。/ Base64 of binary content (screenshots).</summary>
        [DataMember(Name = "data", EmitDefaultValue = false)] public string Data { get; set; }
        [DataMember(Name = "content", EmitDefaultValue = false)] public string Content { get; set; }
        [DataMember(Name = "width", EmitDefaultValue = false)] public int Width { get; set; }
        [DataMember(Name = "height", EmitDefaultValue = false)] public int Height { get; set; }
    }

    /// <summary>
    /// 单条动作结果：统一返回 ok / errorCode / message / artifacts / durationMs。
    /// One action result with the unified ok / errorCode / message / artifacts / durationMs output.
    /// </summary>
    [DataContract]
    public sealed class CadActionResult
    {
        [DataMember(Name = "id", EmitDefaultValue = false)] public string Id { get; set; }
        [DataMember(Name = "action", EmitDefaultValue = false)] public string Action { get; set; }
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "errorCode", EmitDefaultValue = false)] public string ErrorCode { get; set; }
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        [DataMember(Name = "artifacts", EmitDefaultValue = false)] public List<CadArtifact> Artifacts { get; set; }
        [DataMember(Name = "durationMs")] public long DurationMs { get; set; }
        [DataMember(Name = "attempts", EmitDefaultValue = false)] public int Attempts { get; set; }

        public static CadActionResult Success(string message, params CadArtifact[] artifacts) => new CadActionResult
        {
            Ok = true, Message = message, Artifacts = artifacts == null || artifacts.Length == 0 ? null : artifacts.ToList(),
        };

        public static CadActionResult Fail(string code, string message) => new CadActionResult { Ok = false, ErrorCode = code, Message = message };

        public IEnumerable<CadArtifact> Items => Artifacts ?? Enumerable.Empty<CadArtifact>();
    }

    /// <summary>
    /// 动作序列（交给 ai.exe 执行）。/ Action sequence handed to ai.exe.
    /// </summary>
    [DataContract]
    public sealed class CadSequence
    {
        public const int MaxActions = 30;

        [DataMember(Name = "vs", EmitDefaultValue = false)] public string Vs { get; set; }
        [DataMember(Name = "adapter", EmitDefaultValue = false)] public string Adapter { get; set; }
        /// <summary>未单独指定超时的动作使用此默认值。/ Default for actions without their own timeout.</summary>
        [DataMember(Name = "timeoutMs", EmitDefaultValue = false)] public int TimeoutMs { get; set; }
        /// <summary>可重试错误的重试次数，缺省 1。/ Retries for retryable errors; default 1.</summary>
        [DataMember(Name = "retries", EmitDefaultValue = false)] public int? Retries { get; set; }
        [DataMember(Name = "actions")] public List<CadActionRequest> Actions { get; set; }

        public int EffectiveRetries => Math.Max(0, Math.Min(3, Retries ?? 1));
    }

    /// <summary>
    /// 动作序列汇总结果。/ Summary result of an action sequence.
    /// </summary>
    [DataContract]
    public sealed class CadSequenceResult
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "vs", EmitDefaultValue = false)] public string Vs { get; set; }
        [DataMember(Name = "adapter", EmitDefaultValue = false)] public string Adapter { get; set; }
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        /// <summary>停止序列的动作序号（从 1 开始）；0 表示全部执行。/ 1-based index of the action that stopped the sequence; 0 means all ran.</summary>
        [DataMember(Name = "stoppedAt", EmitDefaultValue = false)] public int StoppedAt { get; set; }
        [DataMember(Name = "results")] public List<CadActionResult> Results { get; set; }
        [DataMember(Name = "durationMs")] public long DurationMs { get; set; }

        public IEnumerable<CadActionResult> Items => Results ?? Enumerable.Empty<CadActionResult>();
    }

    /// <summary>CAD 代理上线报文。/ CAD agent hello message.</summary>
    [DataContract]
    public sealed class CadAgentHello
    {
        [DataMember(Name = "agentId", EmitDefaultValue = false)] public string AgentId { get; set; }
        [DataMember(Name = "pid")] public int Pid { get; set; }
        [DataMember(Name = "host", EmitDefaultValue = false)] public string Host { get; set; }
        [DataMember(Name = "adapter", EmitDefaultValue = false)] public string Adapter { get; set; }
        [DataMember(Name = "solution", EmitDefaultValue = false)] public string Solution { get; set; }
        [DataMember(Name = "version", EmitDefaultValue = false)] public string Version { get; set; }
    }

    /// <summary>CAD 代理领取动作 / 回传结果报文。/ CAD agent poll / result message.</summary>
    [DataContract]
    public sealed class CadAgentMessage
    {
        [DataMember(Name = "agentId")] public string AgentId { get; set; }
        [DataMember(Name = "waitMs", EmitDefaultValue = false)] public int WaitMs { get; set; }
        [DataMember(Name = "result", EmitDefaultValue = false)] public CadActionResult Result { get; set; }
    }

    /// <summary>VSManager 对代理报文的应答。/ VSManager reply to agent messages.</summary>
    [DataContract]
    public sealed class CadAgentReply
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "agentId", EmitDefaultValue = false)] public string AgentId { get; set; }
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        /// <summary>代理需要重新上线（未知或已被替换的代理）。/ The agent must say hello again (unknown or replaced agent).</summary>
        [DataMember(Name = "rehello", EmitDefaultValue = false)] public bool Rehello { get; set; }
        [DataMember(Name = "request", EmitDefaultValue = false)] public CadActionRequest Request { get; set; }
    }

    /// <summary>
    /// 代理连接配置（由 VSManager 写到数据目录 cad-agent.json，令牌不出现在命令行中）。
    /// Agent connection settings (written by VSManager to cad-agent.json in its data folder so the token never appears on a command line).
    /// </summary>
    [DataContract]
    public sealed class CadAgentConnection
    {
        public const string FileName = "cad-agent.json";

        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "token")] public string Token { get; set; }
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
    }

    /// <summary>
    /// 协议 JSON（DataContractJsonSerializer，可在 CAD 进程内安全使用）。/ Protocol JSON (DataContractJsonSerializer, safe inside CAD processes).
    /// </summary>
    public static class CadJson
    {
        private static readonly DataContractJsonSerializerSettings Settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        public static string Serialize<T>(T value)
        {
            var s = new DataContractJsonSerializer(typeof(T), Settings);
            using (var ms = new MemoryStream())
            {
                s.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var s = new DataContractJsonSerializer(typeof(T), Settings);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json.Trim().TrimStart('\uFEFF'))))
                return s.ReadObject(ms) as T;
        }

        /// <summary>解析动作列表：接受数组，或含 actions 字段的对象。/ Parses an action list: an array, or an object with an actions field.</summary>
        public static List<CadActionRequest> ParseActions(string json)
        {
            string t = (json ?? "").Trim().TrimStart('\uFEFF');
            if (t.StartsWith("[")) return Deserialize<List<CadActionRequest>>(t) ?? new List<CadActionRequest>();
            return Deserialize<CadSequence>(t)?.Actions ?? new List<CadActionRequest>();
        }
    }
}
