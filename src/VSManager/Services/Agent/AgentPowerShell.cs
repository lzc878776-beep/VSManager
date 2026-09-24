using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace VSManager
{
    internal sealed class PowerShellResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        public bool OutputTruncated { get; set; }
        public string StandardOutput { get; set; } = "";
        public string StandardError { get; set; } = "";
    }

    // This is process lifetime containment, not a security sandbox. Scripts have current-user rights.
    internal static class AgentPowerShell
    {
        internal const int MaxScriptChars = 12000;
        private const int MaxOutputChars = 16000;
        private const int CleanupMilliseconds = 5000;

        // Only this trusted bootstrap runs before assignment to the job. The script is sent through
        // stdin afterwards, avoiding both command-line length limits and quoting/interpolation issues.
        private const string Bootstrap = @"
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
if ([Console]::In.ReadLine() -cne 'RUN') { exit 125 }
try {
    $source = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String([Console]::In.ReadLine()))
    $block = [ScriptBlock]::Create($source)
    $ErrorActionPreference = 'Stop'
    $global:LASTEXITCODE = 0
    $global:__agentNativeFailure = 0
    $Error.Clear()
    # Remember a failed native command even if a later native command succeeds (PowerShell 5.1).
    Set-PSBreakpoint -Command '*' -Action {
        if ($global:LASTEXITCODE -ne 0) { $global:__agentNativeFailure = $global:LASTEXITCODE }
    } | Out-Null
    # Flush deferred object formatting before exit without buffering all output in one string.
    & $block | Out-String -Stream | ForEach-Object { [Console]::WriteLine($_) }
    $succeeded = $?
    if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
    if ($global:__agentNativeFailure -ne 0) { exit $global:__agentNativeFailure }
    if (-not $succeeded -or $Error.Count -ne 0) { exit 1 }
    exit 0
} catch {
    [Console]::Error.WriteLine(($_ | Out-String))
    exit 1
}
";

        internal static async Task<PowerShellResult> RunAsync(string script, string workingDirectory,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(script))
                throw new ArgumentException("A nonempty PowerShell script is required.", nameof(script));
            if (script.Length > MaxScriptChars)
                throw new ArgumentException("The PowerShell script exceeds the length limit.", nameof(script));
            if (string.IsNullOrWhiteSpace(workingDirectory) || !IsAbsoluteDirectory(workingDirectory)
                || !Directory.Exists(workingDirectory))
                throw new ArgumentException("An absolute, existing working directory is required.", nameof(workingDirectory));
            if (timeoutSeconds < 1 || timeoutSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 120 seconds.");
            if (cancellationToken.IsCancellationRequested)
                return new PowerShellResult { ExitCode = -1, Cancelled = true };

            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("System Windows PowerShell is unavailable.", executable);

            var result = new PowerShellResult { ExitCode = -1 };
            var output = new BoundedOutput();
            var error = new BoundedOutput();
            using (var job = CreateKillOnCloseJob())
            using (var process = new Process())
            using (var timer = new CancellationTokenSource())
            {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand "
                        + Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap)),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                process.EnableRaisingEvents = true;
                process.Exited += (sender, args) => exited.TrySetResult(true);
                bool started = false;
                bool assigned = false;
                Task execution = null;
                Task drains = null;
                var deadline = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), timer.Token);
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    try
                    {
                        if (!process.Start())
                            throw new InvalidOperationException("Windows PowerShell did not start.");
                        started = true;
                        if (!AssignProcessToJobObject(job, process.Handle))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain Windows PowerShell in a job; script was not released.");
                        assigned = true;
                        drains = Task.WhenAll(output.ReadAsync(process.StandardOutput), error.ReadAsync(process.StandardError));
                        ObserveFault(drains);
                        if (process.HasExited) exited.TrySetResult(true);
                        execution = cancellationToken.IsCancellationRequested ? exited.Task : SendAndWaitAsync(process, script, exited.Task);
                        ObserveFault(execution);
                        Task winner = await Task.WhenAny(execution, deadline, cancelled.Task).ConfigureAwait(false);
                        if (winner == execution)
                            await execution.ConfigureAwait(false);
                        else
                        {
                            result.Cancelled = cancellationToken.IsCancellationRequested;
                            result.TimedOut = !result.Cancelled;
                            if (!TerminateJobObject(job, 1))
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate the PowerShell process job.");
                        }

                        // Close on normal completion too: descendants must not outlive this invocation
                        // or hold inherited output pipe handles open indefinitely.
                        job.Dispose();
                        if (await Task.WhenAny(exited.Task, Task.Delay(CleanupMilliseconds)).ConfigureAwait(false) != exited.Task)
                            throw new TimeoutException("Windows PowerShell did not exit after its job was terminated.");
                        result.ExitCode = process.ExitCode;
                        if (await Task.WhenAny(drains, Task.Delay(CleanupMilliseconds)).ConfigureAwait(false) == drains)
                            await drains.ConfigureAwait(false);
                        else
                            result.OutputTruncated = true;
                        result.StandardOutput = output.Snapshot();
                        result.StandardError = error.Snapshot();
                        result.OutputTruncated |= output.Truncated || error.Truncated;
                        return result;
                    }
                    finally
                    {
                        timer.Cancel();
                        job.Dispose();
                        if (started)
                        {
                            // On assignment failure only the gated root exists; never kill by name.
                            if (!assigned && !process.HasExited) process.Kill();
                            if (!process.HasExited) process.WaitForExit(CleanupMilliseconds);
                            // Do not flush a partially sent script into a killed process during cleanup.
                            process.StandardInput.BaseStream.Dispose();
                            process.StandardOutput.Dispose();
                            process.StandardError.Dispose();
                        }
                    }
                }
            }
        }

        private static bool IsAbsoluteDirectory(string path)
        {
            // Path.IsPathRooted alone also accepts drive-relative C:foo and root-relative \foo.
            return (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
                    && (path[2] == '\\' || path[2] == '/'))
                || (path.Length > 2 && path[0] == '\\' && path[1] == '\\');
        }

        private static async Task SendAndWaitAsync(Process process, string script, Task exited)
        {
            await process.StandardInput.WriteLineAsync("RUN").ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(Convert.ToBase64String(Encoding.Unicode.GetBytes(script))).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            // Keep stdin open until exit; user scripts reading stdin remain subject to the timeout.
            await exited.ConfigureAwait(false);
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private sealed class BoundedOutput
        {
            private readonly StringBuilder _text = new StringBuilder();
            private readonly object _sync = new object();
            internal bool Truncated { get; private set; }

            internal async Task ReadAsync(StreamReader reader)
            {
                var buffer = new char[2048];
                int count;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    lock (_sync)
                    {
                        int accepted = Math.Min(count, MaxOutputChars - _text.Length);
                        _text.Append(buffer, 0, accepted);
                        if (accepted < count) Truncated = true;
                    }
                }
            }

            internal string Snapshot()
            {
                lock (_sync) return _text.ToString();
            }
        }

        private static SafeFileHandle CreateKillOnCloseJob()
        {
            SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "Could not create the PowerShell process job.");
            }
            var limits = new JobExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobExtendedLimitInformation))))
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "Could not configure the PowerShell process job.");
            }
            return job;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize;
            internal UIntPtr MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimitInformation
        {
            internal JobBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
            ref JobExtendedLimitInformation information, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
