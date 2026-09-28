using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// MCP 传输层：收发单条 JSON-RPC 消息文本。/ MCP transport: sends and receives single JSON-RPC message texts.
    /// </summary>
    public interface IMcpTransport : IDisposable
    {
        /// <summary>收到一条消息（可能在任意线程触发）。/ Raised for each received message (on any thread).</summary>
        event Action<string> MessageReceived;
        /// <summary>连接关闭，参数为原因。/ Raised when the connection closes, with the reason.</summary>
        event Action<string> Closed;
        Task StartAsync(CancellationToken ct);
        Task SendAsync(string json, CancellationToken ct);
        /// <summary>协商得到的协议版本（HTTP 需在请求头中带上）。/ Negotiated protocol version (HTTP sends it as a header).</summary>
        string ProtocolVersion { get; set; }
    }

    /// <summary>
    /// 基于文本流的换行分隔传输（stdio 与测试共用）。/ Newline-delimited transport over text streams (shared by stdio and tests).
    /// </summary>
    public class McpStreamTransport : IMcpTransport
    {
        private readonly TextReader _reader;
        private readonly TextWriter _writer;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private int _closed;

        public event Action<string> MessageReceived;
        public event Action<string> Closed;
        public string ProtocolVersion { get; set; }

        public McpStreamTransport(TextReader reader, TextWriter writer)
        {
            _reader = reader;
            _writer = writer;
        }

        public virtual Task StartAsync(CancellationToken ct)
        {
            var t = new Thread(ReadLoop) { IsBackground = true, Name = "MCP reader" };
            t.Start();
            return Task.CompletedTask;
        }

        private void ReadLoop()
        {
            string reason = "服务器已关闭连接 / The server closed the connection";
            try
            {
                string line;
                while ((line = _reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line[0] != '{') continue;
                    try { MessageReceived?.Invoke(line); }
                    catch (Exception ex) { AppLog.Error("mcp.log", "MCP message handler", ex); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException) { reason = ex.Message; }
            OnClosed(reason);
        }

        protected void OnClosed(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(reason);
        }

        public async Task SendAsync(string json, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _writer.WriteAsync(json.Replace("\r", "").Replace("\n", " ") + "\n").ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        public virtual void Dispose()
        {
            try { _writer.Dispose(); } catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
            try { _reader.Dispose(); } catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
            OnClosed("已断开 / Disconnected");
        }
    }

    /// <summary>
    /// 启动本地进程并通过 stdin/stdout 通信；子进程放入作业对象，VSManager 退出时一并结束。
    /// Launches a local process and talks over stdin/stdout; children join a job object so they end with VSManager.
    /// </summary>
    public sealed class McpStdioTransport : McpStreamTransport
    {
        private readonly Process _process;
        private readonly IntPtr _job;
        private readonly StringBuilder _stderr = new StringBuilder();

        private McpStdioTransport(Process p, IntPtr job)
            : base(p.StandardOutput, new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false })
        {
            _process = p;
            _job = job;
        }

        /// <summary>最近的标准错误输出（用于诊断，最多约 4 KB）。/ Recent stderr output for diagnostics (about 4 KB at most).</summary>
        public string RecentErrors { get { lock (_stderr) return _stderr.ToString(); } }

        public static McpStdioTransport Launch(McpServerSpec spec)
        {
            var (file, args) = ResolveCommand(Environment.ExpandEnvironmentVariables(spec.Command ?? ""),
                spec.Args.Select(Environment.ExpandEnvironmentVariables).ToArray());
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            string cwd = string.IsNullOrWhiteSpace(spec.Cwd) ? null : Environment.ExpandEnvironmentVariables(spec.Cwd);
            if (cwd != null)
            {
                if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException("cwd 不存在 / cwd not found");
                psi.WorkingDirectory = cwd;
            }
            foreach (var kv in spec.Env) psi.EnvironmentVariables[kv.Key] = Environment.ExpandEnvironmentVariables(kv.Value ?? "");
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Start();
            IntPtr job = CreateKillOnCloseJob(p);
            var t = new McpStdioTransport(p, job);
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (t._stderr)
                {
                    t._stderr.AppendLine(e.Data);
                    if (t._stderr.Length > 4096) t._stderr.Remove(0, t._stderr.Length - 4096);
                }
            };
            p.BeginErrorReadLine();
            return t;
        }

        /// <summary>
        /// 按 PATH / PATHEXT 解析命令；.cmd / .bat（例如 npx）通过 cmd.exe 启动。
        /// Resolves the command through PATH / PATHEXT; .cmd / .bat files (such as npx) start through cmd.exe.
        /// </summary>
        internal static (string File, string Args) ResolveCommand(string command, string[] args)
        {
            string cmd = (command ?? "").Trim().Trim('"');
            if (cmd.Length == 0) throw new ArgumentException("command 为空 / command is empty");
            string resolved = FindExecutable(cmd) ?? cmd;
            string joined = string.Join(" ", args.Select(QuoteArg));
            string ext = Path.GetExtension(resolved).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                string comspec = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrEmpty(comspec)) comspec = "cmd.exe";
                return (comspec, "/d /s /c \"" + QuoteArg(resolved) + (joined.Length > 0 ? " " + joined : "") + "\"");
            }
            return (resolved, joined);
        }

        private static string FindExecutable(string cmd)
        {
            string[] exts = Path.HasExtension(cmd) ? new[] { "" }
                : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> dirs = cmd.IndexOfAny(new[] { '\\', '/' }) >= 0 || Path.IsPathRooted(cmd)
                ? new[] { "" }
                : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim().Trim('"'));
            foreach (var d in dirs)
                foreach (var e in exts)
                {
                    try
                    {
                        string candidate = d.Length == 0 ? cmd + e : Path.Combine(d, cmd + e);
                        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
                }
            return null;
        }

        /// <summary>按 Windows 命令行规则为参数加引号。/ Quotes an argument using Windows command-line rules.</summary>
        internal static string QuoteArg(string a)
        {
            if (a == null) return "\"\"";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1);
                else sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        public override void Dispose()
        {
            base.Dispose();
            try { if (!_process.HasExited && !_process.WaitForExit(1500)) _process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
            if (_job != IntPtr.Zero) CloseHandle(_job);
            _process.Dispose();
        }

        private static IntPtr CreateKillOnCloseJob(Process p)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, 9, ptr, (uint)size) || !AssignProcessToJobObject(job, p.Handle))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>
    /// MCP Streamable HTTP 传输：每条消息 POST 一次，响应可为 JSON 或 SSE。
    /// MCP Streamable HTTP transport: one POST per message; responses may be JSON or SSE.
    /// </summary>
    public sealed class McpHttpTransport : IMcpTransport
    {
        private readonly HttpClient _http;
        private readonly Uri _url;
        private readonly Dictionary<string, string> _headers;
        private readonly CancellationTokenSource _life = new CancellationTokenSource();
        private string _session;
        private int _closed;

        public event Action<string> MessageReceived;
        public event Action<string> Closed;
        public string ProtocolVersion { get; set; }

        public McpHttpTransport(McpServerSpec spec, HttpMessageHandler handler = null)
        {
            _url = new Uri(Environment.ExpandEnvironmentVariables(spec.Url.Trim()));
            _headers = spec.Headers.ToDictionary(kv => kv.Key, kv => Environment.ExpandEnvironmentVariables(kv.Value ?? ""), StringComparer.OrdinalIgnoreCase);
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = Timeout.InfiniteTimeSpan;
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task SendAsync(string json, CancellationToken ct)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token))
            using (var req = new HttpRequestMessage(HttpMethod.Post, _url))
            {
                req.Content = new StringContent(json, new UTF8Encoding(false), "application/json");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                foreach (var kv in _headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                if (_session != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
                if (ProtocolVersion != null) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
                var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _session = ids.FirstOrDefault() ?? _session;
                if ((int)resp.StatusCode == 202 || resp.StatusCode == System.Net.HttpStatusCode.NoContent) { resp.Dispose(); return; }
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    resp.Dispose();
                    throw new HttpRequestException("HTTP " + code + (code == 401 || code == 403 ? "（未授权，请检查 headers / unauthorized, check headers）" : ""));
                }
                string media = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (media.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    // SSE 响应在后台读取，直到服务器结束该流。/ SSE responses are read in the background until the server ends the stream.
                    _ = Task.Run(() => ReadSse(resp));
                    return;
                }
                using (resp)
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Dispatch(body);
                }
            }
        }

        private async Task ReadSse(HttpResponseMessage resp)
        {
            try
            {
                using (resp)
                using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    var data = new StringBuilder();
                    string line;
                    while (!_life.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (line.Length == 0)
                        {
                            if (data.Length > 0) Dispatch(data.ToString());
                            data.Clear();
                        }
                        else if (line.StartsWith("data:", StringComparison.Ordinal))
                        {
                            if (data.Length > 0) data.Append('\n');
                            data.Append(line.Substring(5).TrimStart(' '));
                        }
                    }
                    if (data.Length > 0) Dispatch(data.ToString());
                }
            }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                if (!_life.IsCancellationRequested) AppLog.Error("mcp.log", "MCP SSE", ex);
            }
        }

        private void Dispatch(string body)
        {
            body = (body ?? "").Trim();
            if (body.Length == 0) return;
            if (body[0] == '[')
            {
                // JSON-RPC 批量响应拆成单条。/ Split JSON-RPC batch responses into single messages.
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                    foreach (var e in doc.RootElement.EnumerateArray()) MessageReceived?.Invoke(e.GetRawText());
            }
            else MessageReceived?.Invoke(body);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _life.Cancel();
            if (_session != null)
            {
                // 尽力通知服务器结束会话。/ Best-effort session termination.
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Delete, _url))
                    using (var cts = new CancellationTokenSource(2000))
                    {
                        req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
                        foreach (var kv in _headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        _http.SendAsync(req, cts.Token).ContinueWith(t => t.Exception?.Handle(_ => true)).Wait(2000);
                    }
                }
                catch (Exception ex) when (ex is AggregateException || ex is HttpRequestException || ex is OperationCanceledException) { }
            }
            _http.Dispose();
            Closed?.Invoke("已断开 / Disconnected");
        }
    }
}
