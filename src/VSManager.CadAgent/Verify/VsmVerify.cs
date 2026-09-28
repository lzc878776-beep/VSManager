// VSManager 项目验证接口（服务端，单文件、只依赖 .NET Framework 4.x 自带程序集，C# 6 语法）。
// VSManager project verification interface (server side; single file, only .NET Framework 4.x assemblies, C# 6 syntax).
//
// 目标项目（CAD 插件、桌面程序等）把本文件复制进自己的项目（或引用 VSManager.CadAgent.dll），启动时注册检查项：
// A target project (CAD plug-in, desktop app, ...) copies this file into itself (or references VSManager.CadAgent.dll) and registers checks at startup:
//
//   var endpoint = VerifyEndpoint.Start("MyPlugin", "MyPlugin.sln");
//   endpoint.Register("layer.exists", "图层存在 / Layer exists", ctx =>
//   {
//       string layer = ctx.Arg("name", "0");
//       bool found = LayerExists(layer);   // 自己的业务判断 / your own logic
//       return (found ? VerifyOutcome.Pass("找到图层 / Layer found") : VerifyOutcome.Fail("缺少图层 / Layer missing"))
//           .With("layer", layer);
//   }, "name=图层名 / layer name");
//   // CAD 等要求主线程的宿主：设置 Invoker 把检查封送到主线程执行。
//   // Hosts that need the main thread (such as CAD): set Invoker to marshal checks onto it.
//   endpoint.Invoker = (check, timeoutMs) => RunOnMainThread(check, timeoutMs);
//
// VSManager 的 AI 助手通过 ai.exe 连接本机命名管道 \\.\pipe\VSManager.Verify.<进程 ID> 调用这些检查，结果回到任务清单的「待验证」判定。
// The VSManager AI assistant calls these checks through ai.exe over the local named pipe \\.\pipe\VSManager.Verify.<process id>; results feed the "pending verification" judgement.
// 管道只允许当前 Windows 用户访问并拒绝网络登录；每个连接一行 UTF-8 JSON 请求、一行 JSON 应答。
// The pipe only admits the current Windows user and denies network logons; each connection carries one UTF-8 JSON request line and one JSON reply line.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace VSManager.Verify
{
    /// <summary>检查结论。/ Check verdicts.</summary>
    public static class VerifyStatus
    {
        public const string Pass = "pass";
        public const string Fail = "fail";
        /// <summary>无法判定（前置条件不满足等）。/ Cannot decide (e.g. preconditions not met).</summary>
        public const string Inconclusive = "inconclusive";
        /// <summary>检查本身出错（异常、超时、未知检查）。/ The check itself failed (exception, timeout, unknown check).</summary>
        public const string Error = "error";

        public static bool IsKnown(string status)
        {
            return status == Pass || status == Fail || status == Inconclusive || status == Error;
        }
    }

    /// <summary>错误码。/ Error codes.</summary>
    public static class VerifyErrors
    {
        public const string UnknownCheck = "UNKNOWN_CHECK";
        public const string InvalidRequest = "INVALID_REQUEST";
        public const string Timeout = "TIMEOUT";
        public const string CheckException = "CHECK_EXCEPTION";
        public const string Busy = "BUSY";
        public const string NoEndpoint = "NO_ENDPOINT";
        public const string Transport = "TRANSPORT";
    }

    /// <summary>协议常量。/ Protocol constants.</summary>
    public static class VerifyProtocol
    {
        public const int Version = 1;
        public const string PipePrefix = "VSManager.Verify.";
        public const int MaxMessageBytes = 4 * 1024 * 1024;
        public const int DefaultTimeoutMs = 60000;
        public const int MinTimeoutMs = 1000;
        public const int MaxTimeoutMs = 600000;
        public const int MaxMessageChars = 4000;
        public const int MaxDetails = 200;
        public const int MaxEvidence = 100;
        public const int MaxValueChars = 2000;

        public static string PipeName(int pid) { return PipePrefix + pid; }

        /// <summary>从管道名解析进程 ID；不是验证管道时返回 0。/ Parses the process id from a pipe name; 0 when it is not a verification pipe.</summary>
        public static int ParsePid(string pipeName)
        {
            if (pipeName == null || !pipeName.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)) return 0;
            int pid;
            return int.TryParse(pipeName.Substring(PipePrefix.Length), out pid) && pid > 0 ? pid : 0;
        }

        public static int ClampTimeout(int timeoutMs)
        {
            return timeoutMs <= 0 ? DefaultTimeoutMs : Math.Max(MinTimeoutMs, Math.Min(MaxTimeoutMs, timeoutMs));
        }
    }

    /// <summary>请求：op = ping / list / run。/ Request: op = ping / list / run.</summary>
    [DataContract]
    public sealed class VerifyRequest
    {
        public const string Ping = "ping";
        public const string List = "list";
        public const string Run = "run";

        [DataMember(Name = "op")] public string Op { get; set; }
        [DataMember(Name = "check", EmitDefaultValue = false)] public string Check { get; set; }
        [DataMember(Name = "args", EmitDefaultValue = false)] public Dictionary<string, string> Args { get; set; }
        [DataMember(Name = "timeoutMs", EmitDefaultValue = false)] public int TimeoutMs { get; set; }
    }

    /// <summary>检查项说明。/ Check description.</summary>
    [DataContract]
    public sealed class VerifyCheckInfo
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "description", EmitDefaultValue = false)] public string Description { get; set; }
        /// <summary>参数说明（自由文本）。/ Argument help (free text).</summary>
        [DataMember(Name = "args", EmitDefaultValue = false)] public string Args { get; set; }
    }

    /// <summary>应答。/ Reply.</summary>
    [DataContract]
    public sealed class VerifyResponse
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "protocol")] public int Protocol { get; set; }
        [DataMember(Name = "pid", EmitDefaultValue = false)] public int Pid { get; set; }
        [DataMember(Name = "endpoint", EmitDefaultValue = false)] public string Endpoint { get; set; }
        [DataMember(Name = "solution", EmitDefaultValue = false)] public string Solution { get; set; }
        [DataMember(Name = "process", EmitDefaultValue = false)] public string Process { get; set; }
        [DataMember(Name = "checks", EmitDefaultValue = false)] public List<VerifyCheckInfo> Checks { get; set; }
        [DataMember(Name = "check", EmitDefaultValue = false)] public string Check { get; set; }
        [DataMember(Name = "status", EmitDefaultValue = false)] public string Status { get; set; }
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        [DataMember(Name = "evidence", EmitDefaultValue = false)] public Dictionary<string, string> Evidence { get; set; }
        [DataMember(Name = "details", EmitDefaultValue = false)] public List<string> Details { get; set; }
        [DataMember(Name = "errorCode", EmitDefaultValue = false)] public string ErrorCode { get; set; }
        [DataMember(Name = "durationMs")] public long DurationMs { get; set; }

        public static VerifyResponse Failure(string code, string message)
        {
            return new VerifyResponse { Ok = false, Protocol = VerifyProtocol.Version, ErrorCode = code, Status = VerifyStatus.Error, Message = message };
        }
    }

    /// <summary>检查的返回值。/ Value returned by a check.</summary>
    public sealed class VerifyOutcome
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public Dictionary<string, string> Evidence { get; private set; }
        public List<string> Details { get; private set; }

        public VerifyOutcome()
        {
            Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Details = new List<string>();
        }

        public static VerifyOutcome Pass(string message) { return new VerifyOutcome { Status = VerifyStatus.Pass, Message = message }; }
        public static VerifyOutcome Fail(string message) { return new VerifyOutcome { Status = VerifyStatus.Fail, Message = message }; }
        public static VerifyOutcome Inconclusive(string message) { return new VerifyOutcome { Status = VerifyStatus.Inconclusive, Message = message }; }

        /// <summary>附加一条证据（键值）。/ Adds one evidence entry (key / value).</summary>
        public VerifyOutcome With(string key, object value)
        {
            if (!string.IsNullOrWhiteSpace(key)) Evidence[key.Trim()] = value == null ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            return this;
        }

        /// <summary>附加一行明细。/ Adds one detail line.</summary>
        public VerifyOutcome Detail(string line)
        {
            if (line != null) Details.Add(line);
            return this;
        }
    }

    /// <summary>检查的调用上下文。/ Invocation context of a check.</summary>
    public sealed class VerifyContext
    {
        public string Check { get; internal set; }
        public IDictionary<string, string> Args { get; internal set; }
        public int TimeoutMs { get; internal set; }
        /// <summary>超时后置为取消；长检查应定期查看。/ Cancelled after the timeout; long checks should poll it.</summary>
        public CancellationToken Cancellation { get; internal set; }

        /// <summary>读取参数（不区分大小写、去空白）。/ Reads an argument (case-insensitive, trimmed).</summary>
        public string Arg(string name, string fallback = null)
        {
            if (Args != null)
                foreach (var kv in Args)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                        return kv.Value.Trim();
            return fallback;
        }
    }

    /// <summary>
    /// 进程内的验证端点：在命名管道上提供已注册的检查。每个进程一个端点，Start 重复调用返回同一实例。
    /// In-process verification endpoint serving registered checks over a named pipe. One endpoint per process; repeated Start calls return the same instance.
    /// </summary>
    public sealed class VerifyEndpoint : IDisposable
    {
        private sealed class Entry
        {
            public VerifyCheckInfo Info;
            public Func<VerifyContext, VerifyOutcome> Handler;
        }

        private static readonly object StartGate = new object();
        private static VerifyEndpoint _current;

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _checks = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly SemaphoreSlim _running = new SemaphoreSlim(1, 1);
        private readonly int _pid;
        private Thread _thread;

        public string Name { get; private set; }
        public string Solution { get; private set; }
        public string PipeName { get { return VerifyProtocol.PipeName(_pid); } }

        /// <summary>
        /// 可选：把检查封送到宿主要求的线程（如 CAD 主线程）。参数为要执行的检查与超时（毫秒）；为空时在后台线程直接执行。
        /// Optional: marshals a check onto the thread the host requires (such as the CAD main thread). Receives the check and the timeout (ms); when null the check runs on a background thread.
        /// </summary>
        public Func<Func<VerifyOutcome>, int, VerifyOutcome> Invoker { get; set; }

        /// <summary>最近一次管道错误（诊断用）。/ Last pipe error (diagnostics).</summary>
        public string LastError { get; private set; }

        private VerifyEndpoint(string name, string solution, int pid)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "endpoint" : name.Trim();
            Solution = string.IsNullOrWhiteSpace(solution) ? null : solution.Trim();
            _pid = pid;
        }

        /// <summary>启动（或返回已启动的）本进程验证端点。/ Starts (or returns the already running) endpoint of this process.</summary>
        public static VerifyEndpoint Start(string name, string solution = null)
        {
            lock (StartGate)
            {
                if (_current != null) return _current;
                int pid;
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) pid = p.Id;
                var endpoint = new VerifyEndpoint(name, solution, pid);
                endpoint._thread = new Thread(endpoint.Listen) { IsBackground = true, Name = "VSManager verify endpoint" };
                endpoint._thread.Start();
                _current = endpoint;
                return endpoint;
            }
        }

        /// <summary>当前进程的端点（未启动时为 null）。/ The endpoint of this process (null when not started).</summary>
        public static VerifyEndpoint Current { get { lock (StartGate) return _current; } }

        /// <summary>注册或替换一个检查。/ Registers or replaces a check.</summary>
        public VerifyEndpoint Register(string check, string description, Func<VerifyContext, VerifyOutcome> handler, string args = null)
        {
            if (string.IsNullOrWhiteSpace(check)) throw new ArgumentException("检查名不能为空 / Check name is required", "check");
            if (handler == null) throw new ArgumentNullException("handler");
            lock (_gate)
                _checks[check.Trim()] = new Entry
                {
                    Info = new VerifyCheckInfo { Name = check.Trim(), Description = description, Args = args },
                    Handler = handler,
                };
            return this;
        }

        public bool Unregister(string check)
        {
            lock (_gate) return check != null && _checks.Remove(check.Trim());
        }

        public void Dispose()
        {
            lock (StartGate)
            {
                _stop.Set();
                if (_current == this) _current = null;
            }
        }

        private void Listen()
        {
            while (!_stop.WaitOne(0))
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServer(PipeName);
                    var pending = server.BeginWaitForConnection(null, null);
                    if (WaitHandle.WaitAny(new[] { pending.AsyncWaitHandle, (WaitHandle)_stop }) == 1) { server.Dispose(); break; }
                    server.EndWaitForConnection(pending);
                    var connection = server;
                    server = null;
                    var worker = new Thread(() => Serve(connection)) { IsBackground = true, Name = "VSManager verify request" };
                    worker.Start();
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    if (server != null) server.Dispose();
                    if (_stop.WaitOne(1000)) break;
                }
            }
        }

        internal static NamedPipeServerStream CreateServer(string name)
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                VerifyResponse reply;
                try
                {
                    string line;
                    // 客户端 10 秒内不发完请求就断开，避免占住连接；读到请求后取消计时，长检查不受影响。
                    // Drop clients that do not finish the request within 10 s; the timer stops once the request is read, so long checks are unaffected.
                    using (new Timer(_ => { try { pipe.Dispose(); } catch { } }, null, 10000, Timeout.Infinite))
                        line = VerifyWire.ReadLine(pipe);
                    VerifyRequest request = null;
                    try { request = VerifyWire.Deserialize<VerifyRequest>(line); } catch (SerializationException) { }
                    reply = request == null ? VerifyResponse.Failure(VerifyErrors.InvalidRequest, "请求不是有效 JSON / The request is not valid JSON") : Handle(request);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidDataException)
                {
                    LastError = ex.Message;
                    return;
                }
                try { VerifyWire.WriteLine(pipe, VerifyWire.Serialize(reply)); }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { LastError = ex.Message; }
            }
        }

        /// <summary>处理一个请求（测试可直接调用）。/ Handles one request (tests may call it directly).</summary>
        internal VerifyResponse Handle(VerifyRequest request)
        {
            var sw = Stopwatch.StartNew();
            VerifyResponse reply;
            string op = (request.Op ?? "").Trim().ToLowerInvariant();
            if (op == VerifyRequest.Ping || op == VerifyRequest.List)
            {
                reply = new VerifyResponse { Ok = true };
                if (op == VerifyRequest.List)
                    lock (_gate)
                    {
                        reply.Checks = new List<VerifyCheckInfo>();
                        foreach (var e in _checks.Values) reply.Checks.Add(e.Info);
                        reply.Checks.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    }
            }
            else if (op == VerifyRequest.Run) reply = RunCheck(request);
            else reply = VerifyResponse.Failure(VerifyErrors.InvalidRequest, "未知操作 / Unknown op: " + request.Op + " (ping / list / run)");
            reply.Protocol = VerifyProtocol.Version;
            reply.Pid = _pid;
            reply.Endpoint = Name;
            reply.Solution = Solution;
            try { using (var p = System.Diagnostics.Process.GetCurrentProcess()) reply.Process = p.ProcessName; } catch (InvalidOperationException) { }
            reply.DurationMs = sw.ElapsedMilliseconds;
            return reply;
        }

        private VerifyResponse RunCheck(VerifyRequest request)
        {
            string name = (request.Check ?? "").Trim();
            Entry entry;
            lock (_gate) _checks.TryGetValue(name, out entry);
            if (entry == null)
            {
                var unknown = VerifyResponse.Failure(VerifyErrors.UnknownCheck, "未注册的检查 / Unknown check: " + name);
                unknown.Check = name;
                return unknown;
            }
            int timeout = VerifyProtocol.ClampTimeout(request.TimeoutMs);
            if (!_running.Wait(0))
            {
                var busy = VerifyResponse.Failure(VerifyErrors.Busy, "上一个检查仍在执行 / A previous check is still running");
                busy.Check = entry.Info.Name;
                return busy;
            }
            var cts = new CancellationTokenSource();
            var context = new VerifyContext
            {
                Check = entry.Info.Name, TimeoutMs = timeout, Cancellation = cts.Token,
                Args = new Dictionary<string, string>(request.Args ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            };
            VerifyOutcome outcome = null;
            Exception failure = null;
            var done = new ManualResetEvent(false);
            var invoker = Invoker;
            Func<VerifyOutcome> call = () => entry.Handler(context);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { outcome = invoker != null ? invoker(call, timeout) : call(); }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    // 检查真正结束才放行下一个，超时的检查仍会占用。/ Only a finished check releases the gate; a timed-out one keeps it.
                    _running.Release();
                    done.Set();
                }
            });
            VerifyResponse reply;
            if (!done.WaitOne(timeout))
            {
                cts.Cancel();
                reply = VerifyResponse.Failure(VerifyErrors.Timeout, "检查超过 " + timeout + " ms 未返回 / The check did not return within " + timeout + " ms");
            }
            else if (failure != null)
            {
                var inner = failure is System.Reflection.TargetInvocationException && failure.InnerException != null ? failure.InnerException : failure;
                reply = VerifyResponse.Failure(VerifyErrors.CheckException, "检查抛出异常 / The check threw: " + inner.GetType().Name + ": " + inner.Message);
            }
            else if (outcome == null || !VerifyStatus.IsKnown(outcome.Status))
                reply = VerifyResponse.Failure(VerifyErrors.CheckException, "检查没有返回有效结论（pass / fail / inconclusive / error）/ The check returned no valid status");
            else
            {
                reply = new VerifyResponse { Ok = true, Status = outcome.Status, Message = Cut(outcome.Message, VerifyProtocol.MaxMessageChars) };
                if (outcome.Evidence.Count > 0)
                {
                    reply.Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in outcome.Evidence)
                    {
                        if (reply.Evidence.Count >= VerifyProtocol.MaxEvidence) break;
                        reply.Evidence[kv.Key] = Cut(kv.Value, VerifyProtocol.MaxValueChars);
                    }
                }
                if (outcome.Details.Count > 0)
                {
                    reply.Details = new List<string>();
                    foreach (var d in outcome.Details)
                    {
                        if (reply.Details.Count >= VerifyProtocol.MaxDetails) break;
                        reply.Details.Add(Cut(d, VerifyProtocol.MaxValueChars));
                    }
                }
            }
            reply.Check = entry.Info.Name;
            return reply;
        }

        private static string Cut(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }

    /// <summary>一行 JSON 的读写与序列化。/ One-line JSON framing and serialization.</summary>
    public static class VerifyWire
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

        /// <summary>读取到换行为止（不含换行）；超过上限抛 InvalidDataException。/ Reads up to a newline (excluded); throws InvalidDataException past the limit.</summary>
        public static string ReadLine(Stream stream)
        {
            var buffer = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                int n = stream.Read(one, 0, 1);
                if (n <= 0) break;
                if (one[0] == (byte)'\n') break;
                buffer.WriteByte(one[0]);
                if (buffer.Length > VerifyProtocol.MaxMessageBytes) throw new InvalidDataException("消息过大 / Message too large");
            }
            return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
        }

        public static void WriteLine(Stream stream, string json)
        {
            // DataContractJsonSerializer 会转义字符串中的换行，一行即一条消息。/ DataContractJsonSerializer escapes newlines inside strings, so one line is one message.
            byte[] bytes = Encoding.UTF8.GetBytes((json ?? "").Replace("\r", "").Replace("\n", "") + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
