using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class PublishRegistryPrivacyTests
    {
        private TempDataFolder _data;
        private string _repo;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _repo = _data.File("repository");
            Directory.CreateDirectory(_repo);
            Git("init -q");
            Git("config core.hooksPath .disabled-hooks");
            Git("config user.name \"Registry tests\"");
            Git("config user.email registry-tests@users.noreply.github.com");
            Git("config commit.gpgsign false");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (_data == null) return;
            foreach (var file in Directory.GetFiles(_data.Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            _data.Dispose();
        }

        [DataTestMethod]
        [DataRow("solutions.json")]
        [DataRow("nested/SOLUTIONS.JSON")]
        [DataRow("nested\\SoLuTiOnS.JsOn.bAk")]
        [DataRow("nested/solutions.json.corrupt-123")]
        [DataRow("nested/solutions.json.TMP")]
        [DataRow("nested/solutions.json.temp-123")]
        [DataRow("nested/solutions.json.bak.tmp")]
        [DataRow("nested/solutions.json.123.tmp")]
        [DataRow("nested/solutions.json~")]
        [DataRow("nested/solutions.json-backup")]
        [DataRow("nested/solutions.json_backup")]
        public void RegistryNames_AreBlockedWithoutReadingContents(string path)
        {
            Assert.IsTrue(PublishScanner.IsPrivateFile(path));
            var findings = PublishScanner.ScanFiles(_repo, new[] { path }, null, out int scanned);
            Assert.AreEqual(0, scanned);
            Assert.AreEqual(1, findings.Count);
            Assert.AreEqual(path, findings[0].File);
        }

        [DataTestMethod]
        [DataRow("solutions.json")]
        [DataRow("nested/SOLUTIONS.JSON")]
        [DataRow("nested/solutions.json.tmp-123")]
        [DataRow("nested/Solutions.Json.CORRUPT-123.tmp")]
        [DataRow("nested/solutions.json~")]
        [DataRow("nested/solutions.json_backup")]
        [DataRow("nested/solutions.json-backup")]
        public void EnsureGitIgnore_ProtectsNestedCaseAndSidecarVariants(string path)
        {
            GitHubPublisher.EnsureGitIgnore(_repo);
            Assert.AreEqual(0, GitHubPublisher.EnsureGitIgnore(_repo));
            Write(path, "{}");
            Git("check-ignore --no-index -- \"" + path + "\"");
            Assert.AreEqual(".gitignore", Git("ls-files --others --exclude-standard"));
        }

        [DataTestMethod]
        [DataRow("solutions.cs")]
        [DataRow("my-solutions.json")]
        [DataRow("solutions.jsonschema")]
        public void UnrelatedNames_AreNotRegistryFiles(string path)
        {
            Assert.IsFalse(PublishScanner.IsSolutionRegistryFile(path));
        }

        [TestMethod]
        public void ScanOnly_FindsForcedTrackedIgnoredRegistry()
        {
            GitHubPublisher.EnsureGitIgnore(_repo);
            Write("nested/SOLUTIONS.JSON.tmp", "{}");
            Git("add -f -- nested/SOLUTIONS.JSON.tmp");
            var result = Publisher().ScanOnly();
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Findings.Any(f => f.File == "nested/SOLUTIONS.JSON.tmp"));
        }

        [TestMethod]
        public void Run_BlocksTrackedRegistryBeforeNetworkAndCannotBeConfirmed()
        {
            GitHubPublisher.EnsureGitIgnore(_repo);
            Write("solutions.json.bak", "{}");
            Git("add -f -- solutions.json.bak");
            string before = Git("ls-files --stage");
            bool confirmed = false;
            var result = Publisher(() => confirmed = true).Run();
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Aborted);
            StringAssert.Contains(result.Error, "Solution registry must not be published");
            Assert.IsFalse(confirmed);
            Assert.AreEqual(before, Git("ls-files --stage"));
            Assert.AreEqual("", Git("log --all --format=%H"));
            Assert.AreEqual("", Git("remote"));
        }

        [TestMethod]
        public void Run_BlocksRegistryDeletedFromCurrentTreeButPresentInHistory()
        {
            Write("nested/Solutions.Json.corrupt-old", "{}");
            Git("add -f -- nested/Solutions.Json.corrupt-old");
            Git("commit -q -m registry-fixture");
            Git("rm -q -- nested/Solutions.Json.corrupt-old");
            Git("commit -q -m remove-fixture");
            Assert.AreEqual("", Git("ls-files"));
            string head = Git("rev-parse HEAD");
            var publisher = Publisher();
            var scan = publisher.ScanOnly();
            Assert.IsTrue(scan.Findings.Any(f => f.File == "nested/Solutions.Json.corrupt-old"));
            var result = publisher.Run();
            Assert.IsTrue(result.Aborted);
            Assert.AreEqual(head, Git("rev-parse HEAD"));
            Assert.AreEqual("", Git("remote"));
        }

        [TestMethod]
        public void RegistryGuard_RechecksIndexAfterInitialCleanCheck()
        {
            var publisher = Publisher();
            var guard = typeof(GitHubPublisher).GetMethod("CheckRegistryPrivacy", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(guard);
            Assert.IsTrue((bool)guard.Invoke(publisher, new object[] { new PublishResult() }));
            Write("nested/solutions.json.temporary", "{}");
            Git("add -f -- nested/solutions.json.temporary");
            var result = new PublishResult();
            Assert.IsFalse((bool)guard.Invoke(publisher, new object[] { result }));
            Assert.IsTrue(result.Aborted);
        }

        [TestMethod]
        public void IgnoredUntrackedRegistry_RemainsLocalWithoutBlockingCleanScan()
        {
            GitHubPublisher.EnsureGitIgnore(_repo);
            Write("nested/SoLuTiOnS.JsOn.tmp", "{}");
            var result = Publisher().ScanOnly();
            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsFalse(result.Findings.Any(f => PublishScanner.IsSolutionRegistryFile(f.File)));
            Assert.IsTrue(File.Exists(Path.Combine(_repo, "nested", "SoLuTiOnS.JsOn.tmp")));
        }

        private GitHubPublisher Publisher(Action confirm = null)
            => new GitHubPublisher(new PublishOptions { RepoPath = _repo, RepoName = "registry-tests" }, null,
                findings => { confirm?.Invoke(); return true; });

        private void Write(string relativePath, string content)
        {
            string path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        private string Git(string arguments)
        {
            var start = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = _repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                    Assert.Fail("测试仓库 git 超时 / Test repository git timed out");
                }
                Assert.AreEqual(0, process.ExitCode, error.Result);
                return output.Result.Trim();
            }
        }
    }
}
