using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using VSManager.CadAgent;

namespace VSManager.AiHost
{
    /// <summary>动作下发通道。/ Channel that delivers one action.</summary>
    public interface ICadChannel
    {
        CadActionResult Send(CadActionRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 哑调度器：逐条下发（缺省超时 60 秒、可重试错误重试 1 次），超时标记并停止序列，失败即停止；不做判定。
    /// Dumb dispatcher: sends actions one by one (60 s default timeout, one retry for retryable errors), marks a timeout and stops the sequence, stops on failure; it never judges results.
    /// </summary>
    public sealed class SequenceRunner
    {
        private readonly ICadChannel _channel;
        private readonly CadAdapter _adapter;
        /// <summary>重试前等待（测试可设为 0）。/ Wait before a retry (tests may set 0).</summary>
        public int RetryDelayMs { get; set; } = 2000;

        public SequenceRunner(ICadChannel channel, CadAdapter adapter = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _adapter = adapter;
        }

        public CadSequenceResult Run(CadSequence seq, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var summary = new CadSequenceResult { Vs = seq?.Vs, Adapter = _adapter?.Name ?? seq?.Adapter, Results = new List<CadActionResult>() };
            var actions = seq?.Actions ?? new List<CadActionRequest>();
            if (actions.Count == 0) return Finish(summary, sw, false, "动作序列为空 / The action sequence is empty");
            if (actions.Count > CadSequence.MaxActions) return Finish(summary, sw, false, "动作过多（最多 " + CadSequence.MaxActions + " 条）/ Too many actions");
            for (int i = 0; i < actions.Count; i++)
            {
                var req = Prepare(actions[i], seq, i);
                CadActionResult r = Validate(req);
                if (r == null)
                {
                    int attempts = 0;
                    while (true)
                    {
                        if (ct.IsCancellationRequested) { r = CadActionResult.Fail(CadErrors.Cancelled, "已取消 / Cancelled"); break; }
                        attempts++;
                        r = SafeSend(req, ct);
                        if (r.Ok || !CadErrors.IsRetryable(r.ErrorCode) || attempts > seq.EffectiveRetries) break;
                        if (RetryDelayMs > 0 && ct.WaitHandle.WaitOne(RetryDelayMs)) continue;
                    }
                    r.Attempts = attempts;
                }
                r.Id = req.Id;
                r.Action = req.Action;
                summary.Results.Add(r);
                if (!r.Ok)
                {
                    summary.StoppedAt = i + 1;
                    for (int j = i + 1; j < actions.Count; j++)
                        summary.Results.Add(new CadActionResult { Id = (j + 1).ToString(), Action = actions[j]?.Action, ErrorCode = CadErrors.Skipped, Message = "前序动作失败，未执行 / Skipped after an earlier failure" });
                    string why = r.ErrorCode == CadErrors.Timeout ? "第 " + (i + 1) + " 条动作超时，已停止序列 / Action timed out; sequence stopped"
                        : "第 " + (i + 1) + " 条动作失败，已停止序列 / Action failed; sequence stopped";
                    return Finish(summary, sw, false, why);
                }
            }
            return Finish(summary, sw, true, "全部 " + actions.Count + " 条动作已执行 / All actions ran");
        }

        private static CadSequenceResult Finish(CadSequenceResult s, Stopwatch sw, bool ok, string message)
        {
            s.Ok = ok;
            s.Message = message;
            s.DurationMs = sw.ElapsedMilliseconds;
            return s;
        }

        private CadActionRequest Prepare(CadActionRequest source, CadSequence seq, int index)
        {
            var req = (source ?? new CadActionRequest()).Clone();
            req.Id = string.IsNullOrWhiteSpace(req.Id) ? (index + 1).ToString() : req.Id.Trim();
            req.Action = CadActions.Normalize(req.Action) ?? req.Action;
            if (string.IsNullOrWhiteSpace(req.Vs)) req.Vs = seq.Vs;
            if (req.TimeoutMs <= 0) req.TimeoutMs = seq.TimeoutMs > 0 ? seq.TimeoutMs : CadActionRequest.DefaultTimeoutMs;
            req.TimeoutMs = req.EffectiveTimeoutMs;
            // 命令别名按适配包映射（数据驱动，CAD 侧再次校验）。/ Command aliases are mapped by the adapter (data-driven; CAD re-checks).
            if (req.Action == CadActions.RunCommand && _adapter != null)
            {
                string cmd = _adapter.ResolveCommand(req.Arg("command"), out _);
                if (cmd != null) req.SetArg("command", cmd);
            }
            return req;
        }

        private CadActionResult Validate(CadActionRequest req)
        {
            if (CadActions.Normalize(req.Action) == null) return CadActionResult.Fail(CadErrors.UnknownAction, "未知动作 / Unknown action: " + req.Action + "（可用 / available: " + string.Join(", ", CadActions.All) + "）");
            if (req.Action == CadActions.RunCommand && _adapter != null && _adapter.ResolveCommand(req.Arg("command"), out string error) == null)
                return CadActionResult.Fail(CadErrors.InvalidArgs, error);
            return null;
        }

        private CadActionResult SafeSend(CadActionRequest req, CancellationToken ct)
        {
            try { return _channel.Send(req, ct) ?? CadActionResult.Fail(CadErrors.Transport, "没有返回结果 / No result"); }
            catch (OperationCanceledException) { return CadActionResult.Fail(CadErrors.Cancelled, "已取消 / Cancelled"); }
            catch (Exception ex) { return CadActionResult.Fail(CadErrors.Transport, ex.Message); }
        }
    }

    /// <summary>
    /// 复用 VSManager 本地 Web API（POST /api/cad/exec，X-Key 令牌）。/ Reuses VSManager's local Web API (POST /api/cad/exec with the X-Key token).
    /// </summary>
    public sealed class WebApiChannel : ICadChannel
    {
        private readonly Uri _base;
        private readonly string _token;

        public WebApiChannel(string url, string token)
        {
            _base = new Uri(url);
            if (!_base.IsLoopback) throw new ArgumentException("只允许连接本机 VSManager / Only a local VSManager URL is allowed");
            _token = token ?? "";
        }

        public CadActionResult Send(CadActionRequest request, CancellationToken ct)
        {
            var req = (HttpWebRequest)WebRequest.Create(new Uri(_base, "/api/cad/exec"));
            req.Method = "POST";
            req.Proxy = null;
            req.KeepAlive = false;
            req.ContentType = "application/json; charset=utf-8";
            req.Headers["X-Key"] = _token;
            int wait = request.EffectiveTimeoutMs + 15000;
            req.Timeout = wait;
            req.ReadWriteTimeout = wait;
            byte[] body = Encoding.UTF8.GetBytes(CadJson.Serialize(request));
            req.ContentLength = body.Length;
            using (ct.Register(() => { try { req.Abort(); } catch { } }))
            {
                try
                {
                    using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                    using (var res = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                        return CadJson.Deserialize<CadActionResult>(sr.ReadToEnd());
                }
                catch (WebException ex) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ex.Message, ex, ct);
                }
                catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout)
                {
                    return CadActionResult.Fail(CadErrors.Timeout, "等待 VSManager 应答超时 / Timed out waiting for VSManager");
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse hr)
                {
                    string text = "";
                    try { using (var sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd(); } catch { }
                    var parsed = TryParse(text);
                    if (parsed != null) return parsed;
                    return CadActionResult.Fail(CadErrors.Transport, "HTTP " + (int)hr.StatusCode + " " + text);
                }
            }
        }

        private static CadActionResult TryParse(string text)
        {
            try { var r = CadJson.Deserialize<CadActionResult>(text); return r != null && (r.Ok || r.ErrorCode != null) ? r : null; }
            catch { return null; }
        }
    }
}
