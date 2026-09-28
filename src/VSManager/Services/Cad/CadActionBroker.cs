using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VSManager.CadAgent;

namespace VSManager
{
    /// <summary>
    /// CAD 动作中转：登记 CAD 代理（v1 同一时刻只驱动一个 CAD 实例）、把动作交给代理长轮询领取、等待结果或超时。产物只在内存中传递。
    /// CAD action broker: registers the CAD agent (v1 drives one CAD instance at a time), hands actions to the agent's long poll and waits for the result or a timeout. Artifacts stay in memory.
    /// </summary>
    public sealed class CadActionBroker
    {
        public static readonly CadActionBroker Default = new CadActionBroker();

        /// <summary>代理多久未轮询视为断开。/ How long without a poll before the agent counts as disconnected.</summary>
        internal TimeSpan StaleAfter = TimeSpan.FromSeconds(60);
        /// <summary>测试可替换的进程存活检查。/ Process liveness check, replaceable by tests.</summary>
        internal Func<int, bool> ProcessAlive = DefaultAlive;

        public sealed class AgentInfo
        {
            public string Id;
            public int Pid;
            public string Host, Adapter, Solution, Version;
            public DateTime Since, LastSeen;
        }

        private sealed class Pending
        {
            public CadActionRequest Request;
            public readonly TaskCompletionSource<CadActionResult> Done = new TaskCompletionSource<CadActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Taken;
        }

        private readonly object _lock = new object();
        private readonly SemaphoreSlim _exec = new SemaphoreSlim(1, 1);
        private AgentInfo _agent;
        private Pending _pending;
        private TaskCompletionSource<bool> _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action Changed;

        private static bool DefaultAlive(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch { return false; }
        }

        public AgentInfo Agent
        {
            get { lock (_lock) return Alive(_agent) ? _agent : null; }
        }

        private bool Alive(AgentInfo a) => a != null && DateTime.UtcNow - a.LastSeen < StaleAfter && ProcessAlive(a.Pid);

        public string StatusText
        {
            get
            {
                var a = Agent;
                return a == null ? "CAD 代理未连接 / CAD agent not connected"
                    : "CAD 代理已连接 / CAD agent connected: " + (a.Host ?? "CAD") + " pid=" + a.Pid + (string.IsNullOrEmpty(a.Adapter) ? "" : " adapter=" + a.Adapter)
                      + (string.IsNullOrEmpty(a.Solution) ? "" : " solution=" + SafeName(a.Solution));
            }
        }

        private static string SafeName(string p)
        {
            try { return Path.GetFileName(p); } catch (ArgumentException) { return p; }
        }

        /// <summary>代理上线；已有另一个存活代理时拒绝（v1 只驱动一个 CAD）。/ Agent hello; rejected while another live agent exists (v1 drives one CAD).</summary>
        public CadAgentReply Hello(CadAgentHello hello)
        {
            if (hello == null || hello.Pid <= 0) return new CadAgentReply { Ok = false, Message = "无效的上线报文 / Invalid hello" };
            lock (_lock)
            {
                if (Alive(_agent) && _agent.Pid != hello.Pid)
                    return new CadAgentReply { Ok = false, Message = "已有 CAD 实例（pid=" + _agent.Pid + "）连接；v1 同一时刻只驱动一个 CAD / Another CAD (pid=" + _agent.Pid + ") is connected; v1 drives one CAD at a time" };
                _agent = new AgentInfo
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12), Pid = hello.Pid, Host = hello.Host, Adapter = hello.Adapter,
                    Solution = hello.Solution, Version = hello.Version, Since = DateTime.UtcNow, LastSeen = DateTime.UtcNow,
                };
                // 上一个代理领取但未回传的动作不会再有结果。/ An action taken by a previous agent will never get a result.
                if (_pending != null && _pending.Taken) _pending.Done.TrySetResult(CadActionResult.Fail(CadErrors.CadGone, "CAD 代理已重新连接，动作结果丢失 / The CAD agent reconnected; the action result was lost"));
                var reply = new CadAgentReply { Ok = true, AgentId = _agent.Id };
                AppLog.Write("cad.log", "代理上线 / Agent hello pid=" + hello.Pid + " host=" + hello.Host + " adapter=" + hello.Adapter);
                ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke());
                return reply;
            }
        }

        /// <summary>代理长轮询领取下一条动作。/ Agent long poll for the next action.</summary>
        public async Task<CadAgentReply> NextAsync(CadAgentMessage msg, CancellationToken ct = default)
        {
            int wait = Math.Max(0, Math.Min(30000, msg?.WaitMs ?? 0));
            var until = DateTime.UtcNow.AddMilliseconds(wait);
            while (true)
            {
                Task signal;
                lock (_lock)
                {
                    if (_agent == null || msg == null || msg.AgentId != _agent.Id) return new CadAgentReply { Ok = false, Rehello = true, Message = "请重新上线 / Say hello again" };
                    _agent.LastSeen = DateTime.UtcNow;
                    if (_pending != null && !_pending.Taken && !_pending.Done.Task.IsCompleted)
                    {
                        _pending.Taken = true;
                        return new CadAgentReply { Ok = true, AgentId = _agent.Id, Request = _pending.Request };
                    }
                    signal = _signal.Task;
                }
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return new CadAgentReply { Ok = true, AgentId = msg.AgentId };
                await Task.WhenAny(signal, Task.Delay(left, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }

        /// <summary>代理回传结果；迟到（已超时）的结果丢弃。/ Agent posts a result; late results (already timed out) are dropped.</summary>
        public CadAgentReply Result(CadAgentMessage msg)
        {
            lock (_lock)
            {
                if (_agent == null || msg == null || msg.AgentId != _agent.Id) return new CadAgentReply { Ok = false, Rehello = true };
                _agent.LastSeen = DateTime.UtcNow;
                var p = _pending;
                if (p == null || !p.Taken || msg.Result == null || msg.Result.Id != p.Request.Id)
                    return new CadAgentReply { Ok = true, AgentId = _agent.Id, Message = "结果已过期，已丢弃 / Stale result dropped" };
                p.Done.TrySetResult(msg.Result);
                return new CadAgentReply { Ok = true, AgentId = _agent.Id };
            }
        }

        /// <summary>
        /// 执行一条动作：检查代理与目标解决方案、补全调试图纸、等待结果；超时返回 TIMEOUT，CAD 退出返回 CAD_GONE（不自动重启）。
        /// Runs one action: checks the agent and target solution, fills in the debug drawing, waits for the result; TIMEOUT on timeout, CAD_GONE when CAD exited (never restarted).
        /// </summary>
        public async Task<CadActionResult> ExecAsync(CadActionRequest request, string targetSolution, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            CadActionResult Done(CadActionResult r)
            {
                r.Id = request?.Id;
                r.Action = request?.Action;
                if (r.DurationMs <= 0) r.DurationMs = sw.ElapsedMilliseconds;
                return r;
            }
            if (request == null) return Done(CadActionResult.Fail(CadErrors.InvalidArgs, "空请求 / Empty request"));
            string action = CadActions.Normalize(request.Action);
            if (action == null) return Done(CadActionResult.Fail(CadErrors.UnknownAction, "未知动作 / Unknown action: " + request.Action));
            var req = request.Clone();
            req.Action = action;
            req.Id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString("N").Substring(0, 8) : req.Id.Trim();
            req.TimeoutMs = req.EffectiveTimeoutMs;

            var agent = Agent;
            if (agent == null) return Done(CadActionResult.Fail(CadErrors.CadUnavailable, "没有已连接的 CAD：请用 VSManager 点击调试启动 CAD（需匹配的适配包并开启 Web 远程）/ No CAD connected: start CAD with VSManager's debug button (needs a matching adapter and Web remote)"));
            if (!string.IsNullOrEmpty(targetSolution) && !string.IsNullOrEmpty(agent.Solution) && !SamePath(targetSolution, agent.Solution))
                return Done(CadActionResult.Fail(CadErrors.CadUnavailable, "已连接的 CAD 属于另一个解决方案（" + SafeName(agent.Solution) + "）/ The connected CAD belongs to another solution"));
            if (action == CadActions.OpenDrawing && req.Arg("path") == null)
            {
                string recorded = null;
                try { recorded = string.IsNullOrEmpty(agent.Solution) ? null : VsCadDebug.DrawingLookup?.Invoke(agent.Solution); } catch { }
                if (!string.IsNullOrWhiteSpace(recorded)) req.SetArg("path", recorded.Trim());
            }

            if (!await _exec.WaitAsync(0).ConfigureAwait(false))
                return Done(CadActionResult.Fail(CadErrors.Busy, "CAD 正在执行另一条动作 / CAD is running another action"));
            var pending = new Pending { Request = req };
            try
            {
                TaskCompletionSource<bool> signal;
                lock (_lock)
                {
                    _pending = pending;
                    signal = _signal;
                    _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                signal.TrySetResult(true);
                var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
                while (true)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                    {
                        AppLog.Write("cad.log", "动作超时 / Action timeout " + action + " after " + req.TimeoutMs + " ms");
                        return Done(CadActionResult.Fail(CadErrors.Timeout, "动作在 " + req.TimeoutMs / 1000.0 + " 秒内未完成，已标记超时 / Action did not finish within the timeout"));
                    }
                    var step = left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1);
                    if (await Task.WhenAny(pending.Done.Task, Task.Delay(step, ct)).ConfigureAwait(false) == pending.Done.Task)
                    {
                        var r = pending.Done.Task.Result;
                        AppLog.Write("cad.log", "动作完成 / Action done " + action + " ok=" + r.Ok + (r.ErrorCode == null ? "" : " code=" + r.ErrorCode));
                        return Done(r);
                    }
                    if (ct.IsCancellationRequested) return Done(CadActionResult.Fail(CadErrors.Cancelled, "已取消 / Cancelled"));
                    lock (_lock)
                    {
                        if (_agent == null || _agent.Pid != agent.Pid || !ProcessAlive(agent.Pid))
                            return Done(CadActionResult.Fail(CadErrors.CadGone, "CAD 已退出或崩溃，未自动重启 / CAD exited or crashed; it is not restarted automatically"));
                        if (!pending.Taken && DateTime.UtcNow - _agent.LastSeen > StaleAfter)
                            return Done(CadActionResult.Fail(CadErrors.CadUnavailable, "CAD 代理已停止轮询 / The CAD agent stopped polling"));
                    }
                }
            }
            finally
            {
                lock (_lock) if (_pending == pending) _pending = null;
                pending.Done.TrySetResult(CadActionResult.Fail(CadErrors.Cancelled, "已结束 / Finished"));
                _exec.Release();
            }
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// 写入 CAD 代理连接文件（数据目录 cad-agent.json；与 settings.json 同目录，不新增暴露面）。
        /// Writes the CAD agent connection file (cad-agent.json next to settings.json in the data folder; no new exposure).
        /// </summary>
        public static string WriteConnection(AppSettings s)
        {
            string file = Path.Combine(AppPaths.DataFolder, CadAgentConnection.FileName);
            int port = s.WebPort >= 1024 && s.WebPort <= 65535 ? s.WebPort : 8765;
            var conn = new CadAgentConnection { Url = "http://127.0.0.1:" + port + "/", Token = s.WebToken ?? "", Enabled = s.WebEnabled };
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(file, CadJson.Serialize(conn), new System.Text.UTF8Encoding(false));
            return file;
        }
    }
}
