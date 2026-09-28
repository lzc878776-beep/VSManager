using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VSManager.CadAgent;

namespace VSManager
{
    /// <summary>
    /// 启动 ai.exe 执行动作序列：序列经标准输入传入，连接信息经环境变量传入（令牌不出现在命令行），结果 JSON 从标准输出读取。
    /// Starts ai.exe to run an action sequence: the sequence goes through stdin, connection details through environment variables (the token never appears on the command line), and the result JSON comes from stdout.
    /// </summary>
    public static class CadExecutor
    {
        public const string ExeName = "ai.exe";

        public static string ExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExeName);

        /// <summary>整个序列的时限：各动作超时 × (重试 + 1) 之和再加余量。/ Deadline for the whole sequence: sum of action timeouts × (retries + 1) plus slack.</summary>
        internal static TimeSpan Deadline(CadSequence seq)
        {
            long total = 0;
            foreach (var a in seq.Actions ?? Enumerable.Empty<CadActionRequest>())
            {
                int t = a.TimeoutMs > 0 ? a.EffectiveTimeoutMs : (seq.TimeoutMs > 0 ? new CadActionRequest { TimeoutMs = seq.TimeoutMs }.EffectiveTimeoutMs : CadActionRequest.DefaultTimeoutMs);
                total += (long)(t + 20000) * (seq.EffectiveRetries + 1);
            }
            return TimeSpan.FromMilliseconds(total + 30000);
        }

        public static async Task<CadSequenceResult> RunAsync(CadSequence seq, AppSettings settings, CancellationToken ct)
        {
            if (!settings.WebEnabled) return Fail("Web 远程未开启：CAD 动作通道复用本地 Web API，请在 属性 → Web 远程 中开启 / Web remote is off: the CAD action channel reuses the local Web API; enable it in Properties → Web remote");
            string exe = ExePath;
            if (!File.Exists(exe)) return Fail("找不到 ai.exe（应与 VSManager.exe 位于同一目录）/ ai.exe not found next to VSManager.exe");
            int port = settings.WebPort >= 1024 && settings.WebPort <= 65535 ? settings.WebPort : 8765;
            var psi = new ProcessStartInfo(exe, "run -")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(exe),
            };
            psi.EnvironmentVariables["VSM_URL"] = "http://127.0.0.1:" + port + "/";
            psi.EnvironmentVariables["VSM_TOKEN"] = settings.WebToken ?? "";
            using (var p = new Process { StartInfo = psi })
            {
                p.Start();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                byte[] input = Encoding.UTF8.GetBytes(CadJson.Serialize(seq));
                await p.StandardInput.BaseStream.WriteAsync(input, 0, input.Length, ct).ConfigureAwait(false);
                p.StandardInput.Close();
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                p.EnableRaisingEvents = true;
                p.Exited += (s, e) => exited.TrySetResult(true);
                if (p.HasExited) exited.TrySetResult(true);
                var done = await Task.WhenAny(exited.Task, Task.Delay(Deadline(seq), ct)).ConfigureAwait(false);
                if (done != exited.Task)
                {
                    try { p.Kill(); } catch { }
                    return Fail(ct.IsCancellationRequested ? "已取消 / Cancelled" : "ai.exe 执行超时，已终止 / ai.exe timed out and was stopped", ct.IsCancellationRequested ? CadErrors.Cancelled : CadErrors.Timeout);
                }
                string output = await stdout.ConfigureAwait(false);
                string err = await stderr.ConfigureAwait(false);
                CadSequenceResult result = null;
                try { result = CadJson.Deserialize<CadSequenceResult>(output); } catch { }
                if (result == null) return Fail("ai.exe 输出无法解析（退出码 " + p.ExitCode + "）/ ai.exe output could not be parsed: " + Trim(err.Length > 0 ? err : output));
                return result;
            }
        }

        private static string Trim(string s) => s == null ? "" : s.Length > 500 ? s.Substring(0, 500) + "…" : s;

        private static CadSequenceResult Fail(string message, string code = null) => new CadSequenceResult
        {
            Ok = false, Message = message,
            Results = code == null ? new System.Collections.Generic.List<CadActionResult>() : new System.Collections.Generic.List<CadActionResult> { CadActionResult.Fail(code, message) },
        };
    }
}
