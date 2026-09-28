using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace VSManager.CadAgent
{
    /// <summary>引导 DLL 传给代理的启动信息。/ Startup information passed by the boot DLL.</summary>
    public sealed class CadAgentStart
    {
        /// <summary>cad-agent.json 完整路径。/ Full path of cad-agent.json.</summary>
        public string ConnectionFile;
        public string AdapterDirectory;
        public string Host;
        public string Solution;
    }

    /// <summary>
    /// CAD 进程内的代理：通过 VSManager 本地 Web API 上线、长轮询领取动作、执行后回传结果。只连本机回环地址。
    /// Agent inside the CAD process: says hello over VSManager's local Web API, long-polls for actions and posts results back. Connects to loopback only.
    /// </summary>
    public static class CadAgentHost
    {
        private static Thread _thread;
        private static volatile bool _stop;
        private static ICadActionHandler _handler;
        private static CadAgentStart _start;
        private static CadAdapter _adapter;
        public static string Status { get; private set; } = "未启动 / Not started";

        public static void Start(ICadActionHandler handler, CadAgentStart start)
        {
            if (_thread != null) return;
            _handler = handler;
            _start = start ?? new CadAgentStart();
            try
            {
                string file = string.IsNullOrEmpty(_start.AdapterDirectory) ? null : Path.Combine(_start.AdapterDirectory, CadAdapter.FileName);
                if (file != null && File.Exists(file)) _adapter = CadAdapter.Load(file);
            }
            catch (Exception ex) { Status = "适配包读取失败 / Adapter load failed: " + ex.Message; }
            if (handler is CadActionHandlerBase b) b.Adapter = _adapter;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "VSManager CAD agent" };
            _thread.Start();
        }

        public static void Stop() { _stop = true; }

        public static void ReportError(string stage, Exception ex) { Status = stage + ": " + ex.Message; }

        private static void Loop()
        {
            string agentId = null;
            int backoff = 1000;
            while (!_stop)
            {
                try
                {
                    var conn = ReadConnection();
                    if (conn == null || !conn.Enabled || string.IsNullOrEmpty(conn.Url))
                    {
                        Status = "等待 VSManager 开启 Web 远程 / Waiting for VSManager Web remote";
                        agentId = null;
                        Sleep(5000);
                        continue;
                    }
                    if (agentId == null)
                    {
                        var hello = new CadAgentHello
                        {
                            Pid = Process.GetCurrentProcess().Id, Host = _start.Host, Adapter = _adapter?.Name,
                            Solution = _start.Solution, Version = typeof(CadAgentHost).Assembly.GetName().Version.ToString(),
                        };
                        var r = Post<CadAgentReply>(conn, "/api/cad/agent/hello", CadJson.Serialize(hello), 15000);
                        if (r == null || !r.Ok || string.IsNullOrEmpty(r.AgentId))
                        {
                            Status = "VSManager 拒绝连接 / Rejected: " + (r?.Message ?? "");
                            Sleep(10000);
                            continue;
                        }
                        agentId = r.AgentId;
                        Status = "已连接 VSManager / Connected (" + agentId + ")";
                        backoff = 1000;
                    }
                    var next = Post<CadAgentReply>(conn, "/api/cad/agent/next", CadJson.Serialize(new CadAgentMessage { AgentId = agentId, WaitMs = 25000 }), 40000);
                    if (next == null || next.Rehello) { agentId = null; continue; }
                    if (next.Request == null) continue;
                    Status = "执行中 / Running " + next.Request.Action;
                    var result = _handler.Execute(next.Request, CancellationToken.None);
                    Post<CadAgentReply>(conn, "/api/cad/agent/result", CadJson.Serialize(new CadAgentMessage { AgentId = agentId, Result = result }), 60000);
                    Status = "已连接 VSManager / Connected (" + agentId + ")";
                }
                catch (Exception ex)
                {
                    Status = "连接 VSManager 失败，稍后重试 / Connection failed, retrying: " + ex.Message;
                    agentId = null;
                    Sleep(backoff);
                    backoff = Math.Min(15000, backoff * 2);
                }
            }
            Status = "已停止 / Stopped";
        }

        private static void Sleep(int ms)
        {
            for (int i = 0; i < ms && !_stop; i += 250) Thread.Sleep(250);
        }

        private static CadAgentConnection ReadConnection()
        {
            string file = _start.ConnectionFile;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                return CadJson.Deserialize<CadAgentConnection>(sr.ReadToEnd());
        }

        private static T Post<T>(CadAgentConnection conn, string path, string json, int timeoutMs) where T : class
        {
            var uri = new Uri(new Uri(conn.Url), path);
            if (!uri.IsLoopback) throw new InvalidOperationException("只允许连接本机 VSManager / Only a local VSManager is allowed");
            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.Method = "POST";
            req.Proxy = null;
            req.ContentType = "application/json; charset=utf-8";
            req.Headers["X-Key"] = conn.Token ?? "";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    return CadJson.Deserialize<T>(sr.ReadToEnd());
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse hr && (int)hr.StatusCode == 401)
            {
                throw new InvalidOperationException("访问令牌无效 / Invalid access token");
            }
        }
    }
}
