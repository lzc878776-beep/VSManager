using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AgentPowerShellTests
    {
        private string _directory;

        [TestInitialize]
        public void Initialize()
        {
            _directory = Path.Combine(Directory.GetCurrentDirectory(), ".agent-powershell-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [TestMethod]
        public async Task UnicodeMultilineScript_PreservesTextAndWorkingDirectory()
        {
            var result = await Run("$text = @'\n你好 — café 😀\n'quotes' \"double\" $literal `backtick\n'@\n[Console]::WriteLine($text)\n[Console]::WriteLine((Get-Location).Path)");
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            StringAssert.Contains(result.StandardOutput, "你好 — café 😀");
            StringAssert.Contains(result.StandardOutput, "'quotes' \"double\" $literal `backtick");
            StringAssert.Contains(result.StandardOutput, _directory);
            Assert.IsFalse(result.TimedOut);
            Assert.IsFalse(result.Cancelled);
            Assert.IsFalse(result.OutputTruncated);
        }

        [DataTestMethod]
        [DataRow("Ready")]
        [DataRow("就绪 — café 😀")]
        public async Task CustomObject_FormatsPropertyHeadersAndValues(string status)
        {
            var result = await Run("[pscustomobject]@{ Status=" + Quote(status) + "; Count=3 }");
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            StringAssert.Matches(result.StandardOutput, new System.Text.RegularExpressions.Regex(@"Status\s+Count"));
            StringAssert.Contains(result.StandardOutput, status);
            StringAssert.Matches(result.StandardOutput, new System.Text.RegularExpressions.Regex(@"(?m)^\s*"
                + System.Text.RegularExpressions.Regex.Escape(status) + @"\s+3\s*$"));
        }

        [TestMethod]
        public async Task CurrentProcess_FormatsPropertyHeadersAndActualProcessValues()
        {
            string pidFile = Path.Combine(_directory, "runner-pid.txt");
            var result = await Run("[IO.File]::WriteAllText(" + Quote(pidFile)
                + ", [string]$PID); Get-Process -Id $PID | Select-Object Id,ProcessName");
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            int pid = int.Parse(File.ReadAllText(pidFile));
            Assert.IsTrue(pid > 0);
            StringAssert.Matches(result.StandardOutput, new System.Text.RegularExpressions.Regex(@"Id\s+ProcessName"));
            StringAssert.Matches(result.StandardOutput, new System.Text.RegularExpressions.Regex(@"(?m)^\s*"
                + pid + @"\s+powershell\s*$"));
        }

        [DataTestMethod]
        [DataRow("throw 'expected failure'")]
        [DataRow("Write-Error 'expected failure'; 'must not succeed'")]
        [DataRow("Write-Error 'expected failure' -ErrorAction Continue; 'after error'")]
        [DataRow("Get-Item -LiteralPath '.nonexistent-agent-item' -ErrorAction SilentlyContinue; 'after error'")]
        public async Task PowerShellErrors_AreFailures(string script)
        {
            var result = await Run(script);
            Assert.AreNotEqual(0, result.ExitCode);
            Assert.IsFalse(result.TimedOut);
        }

        [TestMethod]
        public async Task TerminatingError_IsCapturedOnStderr()
        {
            var result = await Run("throw 'expected stderr message'");
            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.StandardError, "expected stderr message");
        }

        [DataTestMethod]
        [DataRow("& \"$env:SystemRoot\\System32\\cmd.exe\" /d /c exit 7; 'after native failure'")]
        [DataRow("& \"$env:SystemRoot\\System32\\cmd.exe\" /d /c exit 7; & \"$env:SystemRoot\\System32\\cmd.exe\" /d /c exit 0")]
        public async Task NativeFailure_IsPreserved(string script)
        {
            var result = await Run(script);
            Assert.AreEqual(7, result.ExitCode, result.StandardError);
        }

        [TestMethod]
        public async Task ExplicitExitCode_IsPreserved()
        {
            var result = await Run("[Console]::Error.WriteLine('native-style stderr'); exit 23");
            Assert.AreEqual(23, result.ExitCode);
            StringAssert.Contains(result.StandardError, "native-style stderr");
        }

        [TestMethod]
        public async Task ConcurrentLargeOutput_IsBoundedAndDrained()
        {
            var result = await Run("$s = 'x' * 2048; 1..100 | ForEach-Object { [Console]::Out.Write($s); [Console]::Error.Write($s) }; 'finished'");
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            Assert.AreEqual(16000, result.StandardOutput.Length);
            Assert.AreEqual(16000, result.StandardError.Length);
            Assert.IsTrue(result.OutputTruncated);
            Assert.IsFalse(result.TimedOut);
        }

        [TestMethod]
        public async Task MaximumLengthUnicodeScript_DoesNotExceedWindowsCommandLineLimit()
        {
            const string prefix = "[Console]::WriteLine('maximum length'); #";
            var result = await Run(prefix + new string('界', AgentPowerShell.MaxScriptChars - prefix.Length));
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            StringAssert.Contains(result.StandardOutput, "maximum length");
        }

        [TestMethod]
        public async Task Timeout_TerminatesSleepingProcess()
        {
            var watch = Stopwatch.StartNew();
            var result = await AgentPowerShell.RunAsync("Start-Sleep -Seconds 60", _directory, 2, CancellationToken.None);
            Assert.IsTrue(result.TimedOut);
            Assert.IsFalse(result.Cancelled);
            Assert.AreNotEqual(0, result.ExitCode);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), "Timeout cleanup should be bounded.");
        }

        [TestMethod]
        public async Task AlreadyCancelled_DoesNotRunScript()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var result = await AgentPowerShell.RunAsync("[IO.File]::WriteAllText('unexpected.txt', 'ran')", _directory, 10, cancellation.Token);
                Assert.IsTrue(result.Cancelled);
                Assert.IsFalse(result.TimedOut);
                Assert.AreEqual(-1, result.ExitCode);
                Assert.IsFalse(File.Exists(Path.Combine(_directory, "unexpected.txt")));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task TimeoutOrCancellation_KillsChildTree(bool cancel)
        {
            string ready = Path.Combine(_directory, "child-ready.txt");
            string marker = Path.Combine(_directory, "child-survived.txt");
            string child = "[IO.File]::WriteAllText(" + Quote(ready) + ", [string]$PID); Start-Sleep -Seconds 30; [IO.File]::WriteAllText(" + Quote(marker) + ", 'survived')";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(child));
            string parent = "$p = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoProfile -NonInteractive -EncodedCommand "
                + encoded + "' -NoNewWindow -PassThru; [Console]::WriteLine($p.Id); Start-Sleep -Seconds 60";
            using (var cancellation = new CancellationTokenSource())
            {
                Task<PowerShellResult> running = AgentPowerShell.RunAsync(parent, _directory, cancel ? 30 : 8, cancellation.Token);
                Process childProcess = null;
                try
                {
                    var wait = Stopwatch.StartNew();
                    while (!File.Exists(ready) && !running.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(15))
                        await Task.Delay(50);
                    Assert.IsTrue(File.Exists(ready), "The child must start before testing its cleanup.");
                    string pidText = File.ReadAllText(ready);
                    int pid;
                    while (!int.TryParse(pidText, out pid) && !running.IsCompleted)
                    {
                        await Task.Delay(25);
                        pidText = File.ReadAllText(ready);
                    }
                    Assert.IsTrue(int.TryParse(pidText, out pid), "Child PID was not recorded.");
                    childProcess = Process.GetProcessById(pid);
                    // Hold the process handle so a recycled PID cannot affect this assertion.
                    IntPtr handle = childProcess.Handle;
                    if (cancel) cancellation.Cancel();
                    var result = await running;
                    Assert.AreEqual(cancel, result.Cancelled);
                    Assert.AreEqual(!cancel, result.TimedOut);
                    Assert.IsTrue(childProcess.WaitForExit(5000), "The invocation's child survived job closure.");
                    Assert.IsFalse(File.Exists(marker));
                }
                finally
                {
                    cancellation.Cancel();
                    await running;
                    childProcess?.Dispose();
                }
            }
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow(" \r\n\t")]
        public async Task EmptyScript_IsRejected(string script)
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => Run(script));
        }

        [TestMethod]
        public async Task OversizedScript_IsRejected()
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => Run(new string('x', AgentPowerShell.MaxScriptChars + 1)));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow(".")]
        [DataRow("relative\\directory")]
        [DataRow("C:relative")]
        [DataRow("\\root-relative")]
        public async Task InvalidWorkingDirectory_IsRejected(string directory)
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => AgentPowerShell.RunAsync("'ok'", directory, 10, CancellationToken.None));
        }

        [TestMethod]
        public async Task MissingDirectoryAndFilePath_AreRejected()
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => AgentPowerShell.RunAsync("'ok'", Path.Combine(_directory, "missing"), 10, CancellationToken.None));
            string file = Path.Combine(_directory, "file.txt");
            File.WriteAllText(file, "not a directory");
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => AgentPowerShell.RunAsync("'ok'", file, 10, CancellationToken.None));
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(-1)]
        [DataRow(121)]
        public async Task InvalidTimeout_IsRejected(int timeout)
        {
            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => AgentPowerShell.RunAsync("'ok'", _directory, timeout, CancellationToken.None));
        }

        private Task<PowerShellResult> Run(string script) => AgentPowerShell.RunAsync(script, _directory, 20, CancellationToken.None);

        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    }
}
