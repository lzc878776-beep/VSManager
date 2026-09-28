using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VSManager.Verify;

namespace VSManager
{
    /// <summary>
    /// 读取 VS 调试器附加进程的宿主能力，用于把目标项目的验证端点匹配到 VS。
    /// Host capability that reads the processes a VS debugger is attached to, used to match target-project verification endpoints to a VS.
    /// </summary>
    public interface IAgentVerifyHost
    {
        Task<IList<int>> DebuggedProcessIds(VsInstance vs);
    }

    public sealed partial class AgentService
    {
        /// <summary>
        /// 运行 ai.exe verify（参数、标准输入、期限）并返回标准输出（缺省启动真实 ai.exe；测试可替换）。
        /// Runs ai.exe verify (args, stdin, deadline) and returns stdout (starts the real ai.exe by default; replaceable by tests).
        /// </summary>
        internal Func<string[], string, TimeSpan, CancellationToken, Task<string>> VerifyRunner;

        private const string VerifySdkHint =
            "目标项目尚未提供验证接口：把程序目录 sdk\\VsmVerify.cs 加入项目（或引用 VSManager.CadAgent.dll），启动时调用 VerifyEndpoint.Start(\"名称\", \"解决方案名\") 并用 Register(\"检查名\", \"说明\", ctx => VerifyOutcome.Pass(\"…\")) 登记检查项，再在 VS 中调试启动；否则请改用 run_cad_actions。 " +
            "The target project exposes no verification interface: add sdk\\VsmVerify.cs from the program folder (or reference VSManager.CadAgent.dll), call VerifyEndpoint.Start(\"name\", \"solution\") at startup, register checks with Register(\"check\", \"description\", ctx => VerifyOutcome.Pass(\"...\")) and debug-start it from VS; otherwise use run_cad_actions.";

        private Task<string> RunVerify(string[] args, string stdin, TimeSpan deadline, CancellationToken ct) =>
            VerifyRunner != null ? VerifyRunner(args, stdin, deadline, ct) : VerifyExecutor.RunAsync(args, stdin, deadline, ct);

        /// <summary>
        /// 发现属于该 VS 的验证端点：端点进程由该 VS 调试附加，或端点声明的解决方案名与该 VS 的解决方案文件名一致（不区分大小写）。
        /// Discovers the endpoints that belong to this VS: the endpoint process is attached to by its debugger, or the endpoint's declared solution equals the VS solution file name (case-insensitive).
        /// </summary>
        private async Task<(List<VerifyResponse> endpoints, string error)> DiscoverVerifyEndpoints(VsInstance target, CancellationToken ct)
        {
            string raw;
            try { raw = await RunVerify(new[] { "verify", "list" }, "", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (null, "验证端点发现失败 / Endpoint discovery failed: " + ex.Message); }
            VerifyDiscovery discovery;
            try { discovery = VerifyWire.Deserialize<VerifyDiscovery>((raw ?? "").Trim()); }
            catch (Exception ex) { return (null, "ai.exe 输出无法解析 / Cannot parse ai.exe output: " + ex.Message + " · " + OneLine(raw ?? "", 200)); }
            if (discovery == null) return (null, "ai.exe 没有输出 / ai.exe returned nothing");

            IList<int> debugged = new List<int>();
            if (_host is IAgentVerifyHost vh)
            {
                try { debugged = await vh.DebuggedProcessIds(target).ConfigureAwait(false) ?? new List<int>(); }
                catch (Exception ex) { Log("读取调试进程失败 / Reading debugged processes failed: " + ex.Message); }
            }
            string solution = string.IsNullOrWhiteSpace(target.SolutionPath) ? null : Path.GetFileNameWithoutExtension(target.SolutionPath);
            var mine = (discovery.Endpoints ?? new List<VerifyResponse>())
                .Where(e => e != null && e.Pid > 0 && (debugged.Contains(e.Pid)
                    || (solution != null && string.Equals((e.Solution ?? "").Trim(), solution, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(e => debugged.Contains(e.Pid)).ThenBy(e => e.Pid).ToList();
            return (mine, null);
        }

        private static string FormatEndpoint(VerifyResponse e)
        {
            var sb = new StringBuilder();
            sb.Append("· 进程 / process ").Append(e.Pid);
            if (!string.IsNullOrEmpty(e.Process)) sb.Append(" (").Append(e.Process).Append(')');
            if (!string.IsNullOrEmpty(e.Endpoint)) sb.Append(" · ").Append(e.Endpoint);
            if (!string.IsNullOrEmpty(e.Solution)) sb.Append(" · 解决方案 / solution ").Append(e.Solution);
            var checks = e.Checks ?? new List<VerifyCheckInfo>();
            if (checks.Count == 0) sb.Append("\r\n  (未登记检查项 / no checks registered)");
            foreach (var c in checks.Where(c => c != null))
            {
                sb.Append("\r\n  - ").Append(c.Name);
                if (!string.IsNullOrWhiteSpace(c.Description)) sb.Append("：").Append(OneLine(c.Description, 300));
                if (!string.IsNullOrWhiteSpace(c.Args)) sb.Append("（参数 / args: ").Append(OneLine(c.Args, 300)).Append('）');
            }
            return sb.ToString();
        }

        [Description("列出目标 VS 所调试项目提供的验证接口（本机命名管道 IPC，经 ai.exe 发现；不需要 Web 远程）及其检查项。执行「待验证」项前先调用：有匹配检查项时用 run_verify_check，没有时改用 run_cad_actions 或请用户确认。 " +
            "Lists the verification interface (local named-pipe IPC discovered through ai.exe; no Web remote needed) exposed by the project the target VS is debugging, with its checks. Call it before executing \"pending verification\" items: use run_verify_check when a check fits, otherwise run_cad_actions or ask the user.")]
        internal async Task<string> ListVerifyChecks(
            [Description("VS 编号（如 \"1\"）或名称 / VS number or name")] string vs,
            CancellationToken cancellationToken = default)
        {
            if (!Resolve(vs, out var target, out var error)) return error;
            var (endpoints, failure) = await DiscoverVerifyEndpoints(target, cancellationToken).ConfigureAwait(false);
            if (failure != null) return failure;
            if (endpoints.Count == 0) return "「" + _host.NameOf(target) + "」没有找到验证端点 / No verification endpoint found for this VS。" + VerifySdkHint;
            return "「" + _host.NameOf(target) + "」的验证端点 / Verification endpoints (" + endpoints.Count + ")：\r\n"
                + Truncate(string.Join("\r\n", endpoints.Select(FormatEndpoint)), MaxToolText);
        }

        [Description("通过 ai.exe 经本机命名管道调用目标项目登记的验证检查项，返回 pass / fail / inconclusive / error、说明、证据与明细。检查项在目标进程内执行（如 CAD 插件内部），用于自动执行任务清单中的「待验证」项；只有 pass 且对照判定依据成立时才可 mark_test_item 打勾，fail / inconclusive / error 不得打勾。先用 list_verify_checks 查看可用检查项与参数。 " +
            "Calls a verification check registered by the target project through ai.exe over a local named pipe and returns pass / fail / inconclusive / error with message, evidence and details. The check runs inside the target process (e.g. a CAD plug-in). Only tick mark_test_item on pass after judging against the criteria; never tick fail / inconclusive / error. Use list_verify_checks first.")]
        internal async Task<string> RunVerifyCheck(
            [Description("VS 编号（如 \"1\"）或名称 / VS number or name")] string vs,
            [Description("检查项名称（list_verify_checks 返回）/ Check name from list_verify_checks")] string check,
            [Description("可选：参数 JSON 对象，值为字符串，例如 {\"layer\":\"0\"} / Optional args JSON object with string values")] string args = null,
            [Description("可选：超时毫秒数，默认 60000，范围 1000–600000 / Optional timeout in ms, default 60000, range 1000–600000")] int timeoutMs = 0,
            [Description("可选：端点进程 ID，同一 VS 有多个端点提供同名检查时必填 / Optional endpoint process id, required when several endpoints offer the check")] int pid = 0,
            CancellationToken cancellationToken = default)
        {
            if (!Resolve(vs, out var target, out var error)) return error;
            check = (check ?? "").Trim();
            if (check.Length == 0) return "需要检查项名称 / A check name is required";
            if (!TryParseVerifyArgs(args, out var argMap, out string argError)) return argError;
            int timeout = VerifyProtocol.ClampTimeout(timeoutMs);

            var (endpoints, failure) = await DiscoverVerifyEndpoints(target, cancellationToken).ConfigureAwait(false);
            if (failure != null) return failure;
            if (endpoints.Count == 0) return "「" + _host.NameOf(target) + "」没有找到验证端点 / No verification endpoint found for this VS。" + VerifySdkHint;
            var candidates = endpoints.Where(e => (e.Checks ?? new List<VerifyCheckInfo>()).Any(c => c != null && string.Equals(c.Name, check, StringComparison.OrdinalIgnoreCase))).ToList();
            if (pid > 0) candidates = candidates.Where(e => e.Pid == pid).ToList();
            if (candidates.Count == 0)
                return "没有端点提供检查项「" + check + "」" + (pid > 0 ? "（进程 " + pid + "）" : "") + " / No endpoint offers this check。可用 / Available:\r\n"
                    + Truncate(string.Join("\r\n", endpoints.Select(FormatEndpoint)), MaxToolText);
            if (candidates.Count > 1)
                return "多个端点提供检查项「" + check + "」，请用 pid 指定 / Several endpoints offer this check; pass pid: " + string.Join(", ", candidates.Select(e => e.Pid + (string.IsNullOrEmpty(e.Process) ? "" : " (" + e.Process + ")")));
            var endpoint = candidates[0];
            string canonical = endpoint.Checks.First(c => c != null && string.Equals(c.Name, check, StringComparison.OrdinalIgnoreCase)).Name;

            if (_settings().AgentConfirm)
            {
                string detail = "进程 / process " + endpoint.Pid + (string.IsNullOrEmpty(endpoint.Process) ? "" : " (" + endpoint.Process + ")")
                    + "\r\n检查项 / check: " + canonical
                    + (argMap.Count == 0 ? "" : "\r\n参数 / args: " + string.Join(", ", argMap.Select(kv => kv.Key + "=" + OneLine(kv.Value ?? "", 60))))
                    + "\r\n超时 / timeout: " + timeout / 1000 + "s";
                if (!await ConfirmAsync("在「" + _host.NameOf(target) + "」的调试进程中执行验证检查「" + canonical + "」", detail))
                    return "用户拒绝了该操作。/ The user declined.";
            }

            var request = new VerifyRunRequest { Pid = endpoint.Pid, Check = canonical, Args = argMap.Count == 0 ? null : argMap, TimeoutMs = timeout };
            Log("验证检查 / Verify check: VS pid=" + target.Pid + " endpoint pid=" + endpoint.Pid + " check=" + canonical);
            string raw;
            try { raw = await RunVerify(new[] { "verify", "run", "-" }, VerifyWire.Serialize(request), TimeSpan.FromMilliseconds(timeout + 30000), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return "验证执行失败 / Verification run failed: " + ex.Message; }
            VerifyResponse reply;
            try { reply = VerifyWire.Deserialize<VerifyResponse>((raw ?? "").Trim()); }
            catch (Exception ex) { return "ai.exe 输出无法解析 / Cannot parse ai.exe output: " + ex.Message + " · " + OneLine(raw ?? "", 200); }
            if (reply == null) return "ai.exe 没有输出 / ai.exe returned nothing";
            return Truncate(FormatVerifyResult(reply, canonical, endpoint.Pid), MaxToolText);
        }

        internal static bool TryParseVerifyArgs(string json, out Dictionary<string, string> map, out string error)
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            error = null;
            json = (json ?? "").Trim().TrimStart('\uFEFF');
            if (json.Length == 0 || json == "null") return true;
            try
            {
                if (!(System.Text.Json.Nodes.JsonNode.Parse(json) is System.Text.Json.Nodes.JsonObject obj))
                {
                    error = "args 必须是 JSON 对象 / args must be a JSON object, e.g. {\"layer\":\"0\"}";
                    return false;
                }
                foreach (var kv in obj)
                {
                    string value = kv.Value == null ? null
                        : kv.Value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string s) ? s
                        : kv.Value.ToJsonString();
                    map[kv.Key] = value;
                }
                return true;
            }
            catch (System.Text.Json.JsonException ex)
            {
                error = "args JSON 无效 / Invalid args JSON: " + ex.Message;
                return false;
            }
        }

        internal static string FormatVerifyResult(VerifyResponse r, string check, int pid)
        {
            var sb = new StringBuilder();
            string status = r.Ok ? (r.Status ?? VerifyStatus.Error) : VerifyStatus.Error;
            string icon = status == VerifyStatus.Pass ? "✅" : status == VerifyStatus.Fail ? "❌" : status == VerifyStatus.Inconclusive ? "❔" : "⚠";
            sb.Append(icon).Append(" 检查 / check「").Append(r.Check ?? check).Append("」: ").Append(status);
            if (!r.Ok && !string.IsNullOrEmpty(r.ErrorCode)) sb.Append(" (").Append(r.ErrorCode).Append(')');
            sb.Append(" · 进程 / process ").Append(r.Pid > 0 ? r.Pid : pid);
            if (r.DurationMs > 0) sb.Append(" · ").Append(r.DurationMs).Append(" ms");
            if (!string.IsNullOrWhiteSpace(r.Message)) sb.Append("\r\n").Append(r.Message.Trim());
            if (r.Evidence != null && r.Evidence.Count > 0)
            {
                sb.Append("\r\n证据 / Evidence:");
                foreach (var kv in r.Evidence) sb.Append("\r\n  ").Append(kv.Key).Append(" = ").Append(OneLine(kv.Value ?? "", 500));
            }
            if (r.Details != null && r.Details.Count > 0)
            {
                sb.Append("\r\n明细 / Details:");
                foreach (var d in r.Details) sb.Append("\r\n  ").Append(OneLine(d ?? "", 500));
            }
            if (r.ErrorCode == VerifyErrors.Busy) sb.Append("\r\n端点仍在执行上一个检查（可能已超时），稍后重试 / The endpoint is still running a previous (possibly timed-out) check; retry later.");
            if (r.ErrorCode == VerifyErrors.Transport) sb.Append("\r\n若目标进程以管理员身份运行而 VSManager 不是（或反之），管道可能拒绝访问 / Access may be denied when only one side runs elevated.");
            sb.Append("\r\n\r\n请对照判定依据给出结论：只有 pass 且证据满足判定依据时才可用 mark_test_item 打勾；fail / inconclusive / error 不得打勾，应说明原因或请用户确认。 "
                + "Judge against the criteria: tick mark_test_item only on pass with matching evidence; never tick fail / inconclusive / error.");
            return sb.ToString();
        }
    }
}
