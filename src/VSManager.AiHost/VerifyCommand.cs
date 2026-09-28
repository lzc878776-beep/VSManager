using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VSManager.Verify;

namespace VSManager.AiHost
{
    /// <summary>
    /// ai verify：经本机命名管道调用目标项目的验证接口。list 发现端点与检查项，run 执行一个检查；不需要 Web 远程与令牌。
    /// ai verify: calls the target project's verification interface over local named pipes. list discovers endpoints and checks, run executes one check; no Web remote or token needed.
    /// </summary>
    internal static class VerifyCommand
    {
        internal const int ListTimeoutMs = 5000;

        /// <summary>端点调用（测试可替换）。/ Endpoint call (replaceable by tests).</summary>
        internal static Func<int, VerifyRequest, int, VerifyResponse> Call = VerifyClient.Call;
        internal static Func<List<int>> Discover = VerifyClient.Discover;

        internal static int Run(string[] args, TextReader input, TextWriter output)
        {
            string sub = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "";
            switch (sub)
            {
                case "list":
                {
                    var pids = new List<int>();
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (!int.TryParse(args[i], out int pid) || pid <= 0) return Write(output, new VerifyDiscovery { Ok = false, Message = "进程 ID 无效 / Invalid process id: " + args[i] }, 2);
                        pids.Add(pid);
                    }
                    var result = List(pids.Count > 0 ? pids : Discover());
                    return Write(output, result, 0);
                }
                case "run":
                {
                    VerifyRunRequest req;
                    try
                    {
                        string file = args.Length > 2 ? args[2] : "-";
                        string json = file == "-" ? input.ReadToEnd() : File.ReadAllText(file, Encoding.UTF8);
                        req = VerifyWire.Deserialize<VerifyRunRequest>(json);
                    }
                    catch (Exception ex) { return Write(output, VerifyResponse.Failure(VerifyErrors.InvalidRequest, "请求 JSON 无效 / Invalid request JSON: " + ex.Message), 2); }
                    if (req == null || req.Pid <= 0 || string.IsNullOrWhiteSpace(req.Check))
                        return Write(output, VerifyResponse.Failure(VerifyErrors.InvalidRequest, "需要 pid 与 check / pid and check are required"), 2);
                    var reply = RunCheck(req);
                    return Write(output, reply, reply.Ok ? (reply.Status == VerifyStatus.Pass ? 0 : 1) : 2);
                }
                default:
                    output.WriteLine("ai verify list [pid...]   列出验证端点与检查项 / List verification endpoints and checks");
                    output.WriteLine("ai verify run [file|-]    执行检查：{\"pid\":1234,\"check\":\"name\",\"args\":{},\"timeoutMs\":60000} / Run a check");
                    return 2;
            }
        }

        internal static VerifyDiscovery List(IEnumerable<int> pids)
        {
            var result = new VerifyDiscovery { Ok = true };
            var problems = new List<string>();
            foreach (int pid in pids.Distinct())
            {
                var reply = Call(pid, new VerifyRequest { Op = VerifyRequest.List }, ListTimeoutMs);
                if (reply != null && reply.Ok) { reply.Pid = pid; result.Endpoints.Add(reply); }
                else problems.Add(pid + ": " + (reply?.Message ?? "无应答 / no reply"));
            }
            result.Message = "找到 " + result.Endpoints.Count + " 个验证端点 / Found " + result.Endpoints.Count + " endpoint(s)"
                + (problems.Count > 0 ? "；不可用 / unavailable: " + string.Join("; ", problems) : "");
            return result;
        }

        internal static VerifyResponse RunCheck(VerifyRunRequest req)
        {
            int timeout = VerifyProtocol.ClampTimeout(req.TimeoutMs);
            var reply = Call(req.Pid, new VerifyRequest { Op = VerifyRequest.Run, Check = req.Check.Trim(), Args = req.Args, TimeoutMs = timeout }, timeout)
                ?? VerifyResponse.Failure(VerifyErrors.Transport, "无应答 / No reply");
            if (reply.Pid == 0) reply.Pid = req.Pid;
            if (string.IsNullOrEmpty(reply.Check)) reply.Check = req.Check.Trim();
            return reply;
        }

        private static int Write<T>(TextWriter output, T value, int code)
        {
            output.WriteLine(VerifyWire.Serialize(value));
            return code;
        }
    }
}
