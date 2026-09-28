using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 启动 ai.exe verify 调用目标项目的验证接口（本机命名管道），请求经标准输入传入，结果 JSON 从标准输出读取；不需要 Web 远程与令牌。
    /// Starts ai.exe verify to call the target project's verification interface (local named pipe); the request goes through stdin and the result JSON comes from stdout. No Web remote or token needed.
    /// </summary>
    public static class VerifyExecutor
    {
        public static async Task<string> RunAsync(string[] args, string stdin, TimeSpan deadline, CancellationToken ct)
        {
            string exe = CadExecutor.ExePath;
            if (!File.Exists(exe)) throw new FileNotFoundException("找不到 ai.exe（应与 VSManager.exe 位于同一目录）/ ai.exe not found next to VSManager.exe", exe);
            var psi = new ProcessStartInfo(exe, string.Join(" ", args))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(exe),
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.Start();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                byte[] input = Encoding.UTF8.GetBytes(stdin ?? "");
                await p.StandardInput.BaseStream.WriteAsync(input, 0, input.Length, ct).ConfigureAwait(false);
                p.StandardInput.Close();
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                p.EnableRaisingEvents = true;
                p.Exited += (s, e) => exited.TrySetResult(true);
                if (p.HasExited) exited.TrySetResult(true);
                var done = await Task.WhenAny(exited.Task, Task.Delay(deadline, ct)).ConfigureAwait(false);
                if (done != exited.Task)
                {
                    try { p.Kill(); } catch { }
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("ai.exe verify 执行超时，已终止 / ai.exe verify timed out and was stopped");
                }
                string output = await stdout.ConfigureAwait(false);
                string err = await stderr.ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(output) ? err : output;
            }
        }
    }
}
