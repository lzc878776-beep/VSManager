using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using VSManager.CadAgent;

namespace VSManager
{
    /// <summary>
    /// 手机网页遥控：在局域网内提供一个网页，可查看各 VS 的 Copilot 对话、发送消息、执行调试 / 生成。
    /// 使用 TcpListener 实现极简 HTTP 服务（HttpListener 监听非本机地址需要管理员权限）。所有 API 需要访问密钥。
    /// </summary>
    public class WebRemote : IDisposable
    {
        private readonly IRemoteHost _host;
        private readonly Func<AppSettings> _settings;
        private TcpListener _listener;
        private int _port;
        private long _lastApi;
        private readonly Dictionary<string, (int fails, DateTime since)> _fails = new Dictionary<string, (int, DateTime)>();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static byte[] _page;

        public event Action StatusChanged;
        public string Status { get; private set; } = "未启用";
        public bool Running => _listener != null;
        public int Port => _port;

        /// <summary>最近 90 秒内有手机端访问。</summary>
        public bool Active => Running && DateTime.Now.Ticks - Interlocked.Read(ref _lastApi) < TimeSpan.FromSeconds(90).Ticks;

        public WebRemote(IRemoteHost host, Func<AppSettings> settings)
        {
            _host = host;
            _settings = settings;
        }

        public void Apply()
        {
            var s = _settings();
            if (string.IsNullOrEmpty(s.WebToken)) { s.WebToken = AppSettings.NewToken(); s.Save(); }
            int port = s.WebPort >= 1024 && s.WebPort <= 65535 ? s.WebPort : 8765;
            if (s.WebEnabled && _listener != null && port == _port) return;
            Stop();
            if (!s.WebEnabled) { SetStatus("未启用"); return; }
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                _listener = l;
                _port = port;
                Task.Run(() => AcceptLoop(l));
                SetStatus("运行中");
            }
            catch (Exception ex)
            {
                _listener = null;
                SetStatus($"端口 {port} 启动失败：{ex.Message}");
            }
        }

        private void Stop()
        {
            var l = _listener;
            _listener = null;
            try { l?.Stop(); } catch { }
        }

        public void Dispose() => Stop();

        private void SetStatus(string s)
        {
            Status = s;
            StatusChanged?.Invoke();
        }

        /// <summary>本机可用的局域网访问地址（含密钥）。</summary>
        public List<string> Urls()
        {
            var token = _settings().WebToken;
            var list = new List<(int rank, string url)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    bool virt = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("wsl");
                    foreach (var a in props.UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                        if (a.Address.ToString().StartsWith("169.254.")) continue;
                        int rank = (gw ? 0 : 2) + (virt ? 4 : 0);
                        list.Add((rank, $"http://{a.Address}:{_port}/#k={token}"));
                    }
                }
            }
            catch { }
            return list.OrderBy(x => x.rank).Select(x => x.url).Distinct().ToList();
        }

        #region HTTP

        private async Task AcceptLoop(TcpListener l)
        {
            while (_listener == l)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { if (_listener != l) return; await Task.Delay(200).ConfigureAwait(false); continue; }
                var _ = Task.Run(() => HandleClient(c));
            }
        }

        private async Task HandleClient(TcpClient c)
        {
            using (c)
            {
                try
                {
                    c.NoDelay = true;
                    var ns = c.GetStream();
                    var req = await ReadRequest(ns).ConfigureAwait(false);
                    if (req == null) return;
                    string ip = (c.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                    Response res;
                    try { res = await Route(req, ip).ConfigureAwait(false); }
                    catch (Exception ex) { res = JsonRes(new { ok = false, msg = "服务器错误：" + ex.Message }, 500); }
                    await WriteResponse(ns, res).ConfigureAwait(false);
                }
                catch { }
            }
        }

        private class Request
        {
            public string Method, Path;
            public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body = "";
        }

        private class Response
        {
            public int Code = 200;
            public string Type = "application/json; charset=utf-8";
            public byte[] Body;
        }

        private static async Task<Request> ReadRequest(NetworkStream ns)
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            int headerEnd = -1;
            var deadline = DateTime.Now.AddSeconds(15);
            while (headerEnd < 0)
            {
                if (ms.Length > 32 * 1024 || DateTime.Now > deadline) return null;
                int n = await ReadWithTimeout(ns, buf, deadline).ConfigureAwait(false);
                if (n <= 0) return null;
                ms.Write(buf, 0, n);
                headerEnd = IndexOf(ms.GetBuffer(), (int)ms.Length, new byte[] { 13, 10, 13, 10 });
            }
            var all = ms.ToArray();
            string head = Encoding.ASCII.GetString(all, 0, headerEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new Request { Method = first[0].ToUpperInvariant() };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            if (q >= 0)
                foreach (var kv in target.Substring(q + 1).Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e > 0) req.Query[Uri.UnescapeDataString(kv.Substring(0, e))] = Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
                }
            for (int i = 1; i < lines.Length; i++)
            {
                int k = lines[i].IndexOf(':');
                if (k > 0) req.Headers[lines[i].Substring(0, k).Trim()] = lines[i].Substring(k + 1).Trim();
            }
            if (req.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int len) && len > 0)
            {
                // CAD 代理回传的结果可含截图，允许更大的请求体与更长的上传时间。/ CAD agent results may carry screenshots, so they allow a larger body and a longer upload.
                bool large = req.Path == CadResultPath;
                if (len > (large ? CadResultMaxBytes : 256 * 1024)) return null;
                if (large) deadline = DateTime.Now.AddSeconds(120);
                var body = new MemoryStream();
                int have = all.Length - headerEnd - 4;
                body.Write(all, headerEnd + 4, have);
                while (body.Length < len)
                {
                    int n = await ReadWithTimeout(ns, buf, deadline).ConfigureAwait(false);
                    if (n <= 0) return null;
                    body.Write(buf, 0, n);
                }
                req.Body = Encoding.UTF8.GetString(body.ToArray(), 0, len);
            }
            return req;
        }

        private static async Task<int> ReadWithTimeout(NetworkStream ns, byte[] buf, DateTime deadline)
        {
            var read = ns.ReadAsync(buf, 0, buf.Length);
            var ms = Math.Max(1, (int)(deadline - DateTime.Now).TotalMilliseconds);
            if (await Task.WhenAny(read, Task.Delay(ms)).ConfigureAwait(false) != read) return -1;
            return await read.ConfigureAwait(false);
        }

        private static int IndexOf(byte[] a, int len, byte[] p)
        {
            for (int i = 0; i + p.Length <= len; i++)
            {
                int j = 0;
                while (j < p.Length && a[i + j] == p[j]) j++;
                if (j == p.Length) return i;
            }
            return -1;
        }

        private static async Task WriteResponse(NetworkStream ns, Response r)
        {
            var body = r.Body ?? new byte[0];
            string reason = r.Code == 200 ? "OK" : r.Code == 401 ? "Unauthorized" : r.Code == 404 ? "Not Found" : r.Code == 429 ? "Too Many Requests" : "Error";
            var head = $"HTTP/1.1 {r.Code} {reason}\r\nContent-Type: {r.Type}\r\nContent-Length: {body.Length}\r\n" +
                       "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            await ns.WriteAsync(hb, 0, hb.Length).ConfigureAwait(false);
            await ns.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await ns.FlushAsync().ConfigureAwait(false);
        }

        private static Response JsonRes(object o, int code = 200) =>
            new Response { Code = code, Body = Encoding.UTF8.GetBytes(Json.Serialize(o)) };

        #endregion

        #region 路由

        private static readonly HashSet<string> DebugActions = new HashSet<string>
            { "go", "run", "break", "stop", "restart", "build", "rebuild", "cancelbuild", "stepover", "stepinto", "stepout" };

        private async Task<Response> Route(Request req, string ip)
        {
            if (req.Method == "GET" && (req.Path == "/" || req.Path == "/index.html"))
                return new Response { Type = "text/html; charset=utf-8", Body = Page() };
            if (req.Path == "/favicon.ico") return new Response { Code = 404, Type = "text/plain", Body = new byte[0] };
            if (!req.Path.StartsWith("/api/")) return new Response { Code = 404, Type = "text/plain", Body = Encoding.ASCII.GetBytes("not found") };

            // ---- 鉴权（失败过多的地址暂时拒绝） ----
            lock (_fails)
            {
                if (_fails.TryGetValue(ip, out var f) && f.fails >= 20 && DateTime.Now - f.since < TimeSpan.FromMinutes(10))
                    return JsonRes(new { ok = false, msg = "尝试次数过多，请 10 分钟后再试" }, 429);
            }
            req.Headers.TryGetValue("X-Key", out var key);
            if (!TokenEquals(key, _settings().WebToken))
            {
                lock (_fails)
                {
                    _fails.TryGetValue(ip, out var f);
                    if (DateTime.Now - f.since > TimeSpan.FromMinutes(10)) f = (0, DateTime.Now);
                    _fails[ip] = (f.fails + 1, f.since);
                }
                return JsonRes(new { ok = false, msg = "访问密钥无效，请在 VSManager 中重新扫码" }, 401);
            }
            lock (_fails) _fails.Remove(ip);
            // CAD 动作通道只服务本机，且不计入手机端活动。/ The CAD action channel is local-only and does not count as phone activity.
            if (req.Path.StartsWith("/api/cad/", StringComparison.Ordinal)) return await RouteCad(req, ip).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastApi, DateTime.Now.Ticks);

            var body = string.IsNullOrEmpty(req.Body) ? new Dictionary<string, object>() : Json.Deserialize<Dictionary<string, object>>(req.Body) ?? new Dictionary<string, object>();
            string Arg(string name)
            {
                if (body.TryGetValue(name, out var v) && v != null) return Convert.ToString(v);
                return req.Query.TryGetValue(name, out var q) ? q : null;
            }
            var list = _host.Instances.ToList();
            VsInstance Vs()
            {
                if (int.TryParse(Arg("pid"), out int pid)) return list.FirstOrDefault(x => x.Pid == pid);
                string key = (Arg("vs") ?? "").Trim().TrimStart('#');
                if (key.Length == 0) return null;
                if (int.TryParse(key, out int idx)) return idx >= 1 && idx <= list.Count ? list[idx - 1] : null;
                return list.FirstOrDefault(x => _host.NameOf(x).IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            switch (req.Path)
            {
                case "/api/state":
                    return JsonRes(new { ok = true, vs = list.Select((v, i) => VsJson(v, i + 1)).ToList() });

                case "/api/chat":
                {
                    var v = Vs();
                    if (v == null) return JsonRes(new { ok = false, msg = "该 VS 已关闭" });
                    _host.FocusChat(v.Pid);
                    var t = _host.CachedChat(v);
                    string sig = Hash((t?.Signature ?? "") + "|" + (t?.PaneFound ?? false) + "|" + v.Copilot);
                    if (Arg("sig") == sig) return JsonRes(new { ok = true, same = true, sig });
                    return JsonRes(new
                    {
                        ok = true, sig, synced = t != null, found = t?.PaneFound ?? false, title = t?.Title ?? "", copilot = State(v),
                        messages = (t?.Messages ?? new List<ChatMessage>()).Skip(Math.Max(0, (t?.Messages.Count ?? 0) - 30)).Select(m => new
                        {
                            role = m.Role == ChatRole.Assistant ? "a" : "u",
                            parts = m.Parts.Select(p => new { s = p.IsStep, t = p.Text }).ToList()
                        }).ToList()
                    });
                }

                case "/api/send":
                {
                    if (req.Method != "POST") break;
                    var v = Vs();
                    string text = (Arg("text") ?? "").Trim();
                    if (v == null) return JsonRes(new { ok = false, msg = "该 VS 已关闭" });
                    if (text.Length == 0) return JsonRes(new { ok = false, msg = "消息为空" });
                    _host.Log($"{(req.Headers.ContainsKey("X-Client") ? req.Headers["X-Client"] : "手机网页")}：发送到「{_host.NameOf(v)}」");
                    string r = await _host.SendChat(v, text).ConfigureAwait(false);
                    _host.FocusChat(v.Pid);
                    return JsonRes(new { ok = !PushCheck.IsRejected(r), pushed = r.StartsWith("✅", StringComparison.Ordinal), msg = r });
                }

                case "/api/debug":
                {
                    if (req.Method != "POST") break;
                    var v = Vs();
                    string a = Arg("action");
                    if (v == null) return JsonRes(new { ok = false, msg = "该 VS 已关闭" });
                    if (!DebugActions.Contains(a ?? "")) return JsonRes(new { ok = false, msg = "未知操作" });
                    string r = await DteWorker.Run(() => VsService.DebugAction(v, a)).ConfigureAwait(false);
                    _host.Log($"手机网页：「{_host.NameOf(v)}」{r}");
                    return JsonRes(new { ok = true, msg = r });
                }

                case "/api/button":
                {
                    if (req.Method != "POST") break;
                    var v = Vs();
                    string id = Arg("id");
                    if (v == null) return JsonRes(new { ok = false, msg = "该 VS 已关闭" });
                    string name = id == "CancelButton" ? "停止 Copilot" : id == "createNewThread" ? "新建对话线程" : null;
                    if (name == null) return JsonRes(new { ok = false, msg = "未知操作" });
                    string r = await _host.InvokeChatButton(v, id, name).ConfigureAwait(false);
                    _host.FocusChat(v.Pid);
                    return JsonRes(new { ok = r.StartsWith("已"), msg = r });
                }

                case "/api/profiles":
                {
                    var v = Vs();
                    if (v == null) return JsonRes(new { ok = false, msg = "该 VS 已关闭" });
                    var p = await DteWorker.Run(() => VsService.GetLaunchProfiles(v)).ConfigureAwait(false);
                    return JsonRes(new { ok = true, project = p.Project ?? "", names = p.Names, active = p.Active ?? "", canSet = p.CanSet });
                }

                case "/api/profile":
                {
                    if (req.Method != "POST") break;
                    var v = Vs();
                    string name = Arg("name");
                    if (v == null) return JsonRes(new { ok = false, msg = "该 VS 已关闭" });
                    if (string.IsNullOrEmpty(name)) return JsonRes(new { ok = false, msg = "未指定配置" });
                    string r = await DteWorker.Run(() => VsService.SetLaunchProfile(v, name)).ConfigureAwait(false);
                    _host.Log($"手机网页：「{_host.NameOf(v)}」{r}");
                    return JsonRes(new { ok = true, msg = r });
                }
                    case "/api/dock":
                    {
                        if (req.Method != "POST") break;
                        string r = await _host.DockPanes().ConfigureAwait(false);
                        return JsonRes(new { ok = true, msg = r });
                    }

                    case "/api/note":
                    {
                        if (req.Method != "POST") break;
                        var v = Vs();
                        if (v == null) return JsonRes(new { ok = false, msg = "未找到该 VS" });
                        string note = (Arg("note") ?? "").Trim();
                        if (note.Length > 500) note = note.Substring(0, 500);
                        return JsonRes(new { ok = true, msg = await _host.SetNote(v, note).ConfigureAwait(false) });
                    }

                    case "/api/errors":
                    {
                        var v = Vs();
                        if (v == null) return JsonRes(new { ok = false, msg = "未找到该 VS" });
                        int.TryParse(Arg("max"), out int max);
                        string r = await _host.ErrorList(v, max <= 0 ? 50 : Math.Min(max, 500)).ConfigureAwait(false);
                        return JsonRes(new { ok = true, msg = r });
                    }

                    case "/api/reply":
                    {
                        var v = Vs();
                        if (v == null) return JsonRes(new { ok = false, msg = "未找到该 VS" });
                        _host.FocusChat(v.Pid);
                        return JsonRes(new { ok = true, copilot = State(v), reply = LastReply(v) });
                    }

                    case "/api/screenshot":
                    {
                        var s = _settings();
                        string denied = ScreenshotDenial(ip, s);
                        if (denied != null) return JsonRes(new { ok = false, msg = denied });
                        var v = Vs();
                        if (v == null) return JsonRes(new { ok = false, msg = "未找到该 VS / VS not found" });
                        byte[] png;
                        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                        {
                            try { png = await _host.CaptureScreenshot(v, s.AgentScreenshotRequirePreview, cts.Token).ConfigureAwait(false); }
                            catch (Exception ex) { return JsonRes(new { ok = false, msg = "截图失败 / Screenshot failed: " + ex.Message }); }
                        }
                        if (png == null) return JsonRes(new { ok = false, msg = "用户取消了截图共享 / The user cancelled screenshot sharing" });
                        if (png.Length == 0 || png.Length > ChatImage.MaxBytes) return JsonRes(new { ok = false, msg = "截图数据无效或过大 / Invalid or oversized screenshot" });
                        _host.Log("📷 AI Skill 已读取「" + _host.NameOf(v) + "」的截图 / AI Skill read a screenshot of this VS");
                        return JsonRes(new { ok = true, name = _host.NameOf(v), bytes = png.Length, png = Convert.ToBase64String(png) });
                    }

                    case "/api/wait":
                    {
                        var v = Vs();
                        if (v == null) return JsonRes(new { ok = false, msg = "未找到该 VS" });
                        int.TryParse(Arg("timeout"), out int timeout);
                        timeout = timeout <= 0 ? 600 : Math.Min(timeout, 3600);
                        _host.FocusChat(v.Pid);
                        var start = DateTime.Now;
                        bool sawBusy = v.Copilot == CopilotState.Busy;
                        while (true)
                        {
                            if (v.Copilot == CopilotState.Busy) sawBusy = true;
                            else if (sawBusy || DateTime.Now - start > TimeSpan.FromSeconds(10)) break;
                            if ((DateTime.Now - start).TotalSeconds > timeout)
                                return JsonRes(new { ok = false, timeout = true, copilot = State(v), msg = "等待超时，Copilot 仍在运行" });
                            await Task.Delay(1500).ConfigureAwait(false);
                        }
                        await Task.Delay(1500).ConfigureAwait(false);
                        return JsonRes(new
                        {
                            ok = true, completed = sawBusy, copilot = State(v),
                            seconds = sawBusy ? (int)v.LastDuration.TotalSeconds : 0, reply = LastReply(v)
                        });
                    }
                }
                return JsonRes(new { ok = false, msg = "不支持的请求" }, 404);
            }

            private string LastReply(VsInstance v)
            {
                var t = _host.CachedChat(v);
                var last = t?.Messages?.LastOrDefault(m => m.Role == ChatRole.Assistant);
                return last == null ? "" : string.Join("\n", last.Parts.Where(p => !p.IsStep).Select(p => p.Text));
            }

        private object VsJson(VsInstance v, int idx) => new
        {
            pid = v.Pid, idx, name = _host.NameOf(v), copilot = State(v),
            note = _host.NoteOf(v) ?? "", sln = v.SolutionPath ?? "",
            dbg = v.Building ? "生成中" : v.DebugMode == 3 ? "调试中" : v.DebugMode == 2 ? "已中断" : v.DebugMode == 1 ? "未调试" : "",
            mode = v.Building ? "build" : v.DebugMode == 3 ? "run" : v.DebugMode == 2 ? "break" : "design",
        };

        private static string State(VsInstance v) => v.Copilot == CopilotState.Busy ? "busy" : v.Copilot == CopilotState.Idle ? "idle" : "none";

        internal const string CadResultPath = "/api/cad/agent/result";
        internal const int CadResultMaxBytes = 32 * 1024 * 1024;

        /// <summary>CAD 动作中转（测试可替换）。/ CAD action broker (replaceable by tests).</summary>
        internal CadActionBroker Cad = CadActionBroker.Default;

        /// <summary>只允许本机客户端；允许时返回 null。/ Allows local clients only; returns null when allowed.</summary>
        internal static string LocalDenial(string ip)
        {
            if (!IPAddress.TryParse(ip ?? "", out var address) || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
                return "CAD 动作接口只允许本机调用 / The CAD action API is available to local clients only";
            return null;
        }

        private static Response CadRes(string json, int code = 200) =>
            new Response { Code = code, Body = Encoding.UTF8.GetBytes(json) };

        /// <summary>
        /// CAD 动作通道（复用本 Web API 与访问密钥）：ai.exe 调用 exec，CAD 代理调用 agent/hello、agent/next、agent/result。
        /// CAD action channel (reuses this Web API and its access key): ai.exe calls exec; the CAD agent calls agent/hello, agent/next and agent/result.
        /// </summary>
        private async Task<Response> RouteCad(Request req, string ip)
        {
            string deny = LocalDenial(ip);
            if (deny != null) return JsonRes(new { ok = false, msg = deny }, 403);
            if (req.Method != "POST" && req.Path != "/api/cad/status") return JsonRes(new { ok = false, msg = "请使用 POST / Use POST" }, 405);
            try
            {
                switch (req.Path)
                {
                    case "/api/cad/status":
                    {
                        var a = Cad.Agent;
                        return JsonRes(new { ok = true, connected = a != null, status = Cad.StatusText, pid = a?.Pid ?? 0, host = a?.Host ?? "", adapter = a?.Adapter ?? "" });
                    }
                    case "/api/cad/agent/hello":
                        return CadRes(CadJson.Serialize(Cad.Hello(CadJson.Deserialize<CadAgentHello>(req.Body))));
                    case "/api/cad/agent/next":
                        return CadRes(CadJson.Serialize(await Cad.NextAsync(CadJson.Deserialize<CadAgentMessage>(req.Body)).ConfigureAwait(false)));
                    case CadResultPath:
                        return CadRes(CadJson.Serialize(Cad.Result(CadJson.Deserialize<CadAgentMessage>(req.Body))));
                    case "/api/cad/exec":
                    {
                        var r = CadJson.Deserialize<CadActionRequest>(req.Body);
                        if (r == null) return CadRes(CadJson.Serialize(CadActionResult.Fail(CadErrors.InvalidArgs, "空请求 / Empty request")), 400);
                        string solution = null;
                        if (!string.IsNullOrWhiteSpace(r.Vs))
                        {
                            var v = ResolveCadTarget(r.Vs);
                            if (v == null) return CadRes(CadJson.Serialize(CadActionResult.Fail(CadErrors.NotFound, "未找到该 VS / VS not found: " + r.Vs)));
                            solution = v.SolutionPath;
                        }
                        return CadRes(CadJson.Serialize(await Cad.ExecAsync(r, solution).ConfigureAwait(false)));
                    }
                }
            }
            catch (System.Runtime.Serialization.SerializationException ex)
            {
                return CadRes(CadJson.Serialize(CadActionResult.Fail(CadErrors.InvalidArgs, "JSON 无效 / Invalid JSON: " + ex.Message)), 400);
            }
            return JsonRes(new { ok = false, msg = "不支持的请求" }, 404);
        }

        /// <summary>按进程 ID、编号或名称解析目标 VS。/ Resolves the target VS by process ID, number or name.</summary>
        internal VsInstance ResolveCadTarget(string vs)
        {
            var list = _host.Instances.ToList();
            string key = (vs ?? "").Trim().TrimStart('#');
            if (key.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)) key = key.Substring(4).Trim();
            if (key.Length == 0) return null;
            if (int.TryParse(key, out int n))
                return list.FirstOrDefault(x => x.Pid == n) ?? (n >= 1 && n <= list.Count ? list[n - 1] : null);
            return list.FirstOrDefault(x => _host.NameOf(x).IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 截图接口只服务本机客户端（AI Skill），且须开启「AI 截图」；允许时返回 null，否则返回拒绝原因。
        /// The screenshot API serves only local clients (AI Skill) and requires the AI screenshot setting; returns null when allowed, otherwise the reason.
        /// </summary>
        internal static string ScreenshotDenial(string ip, AppSettings settings)
        {
            if (!IPAddress.TryParse(ip ?? "", out var address) || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
                return "截图只允许本机调用 / Screenshots are available to local clients only";
            if (settings == null || !settings.AgentScreenshotEnabled)
                return "截图工具已关闭，请在 属性 → AI 助手 中开启 / Screenshot tool is disabled; enable it in Properties → AI assistant";
            return null;
        }

        private static bool TokenEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        private static byte[] Page()
        {
            if (_page != null) return _page;
            var asm = typeof(WebRemote).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("remote.html", StringComparison.OrdinalIgnoreCase));
            if (name == null) return Encoding.UTF8.GetBytes("<h1>页面资源缺失</h1>");
            using (var s = asm.GetManifestResourceStream(name))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return _page = ms.ToArray();
            }
        }

        #endregion
    }
}
