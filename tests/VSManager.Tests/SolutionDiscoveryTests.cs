using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class SolutionDiscoveryTests
    {
        private TempDataFolder _data;
        [TestInitialize] public void Init() => _data = new TempDataFolder();
        [TestCleanup] public void Cleanup() => _data.Dispose();

        private string Solution(string relative)
        {
            string path = _data.File(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "");
            return path;
        }

        [TestMethod]
        public void Scan_DepthAndExcludedFolders_AreBounded_AndExtensionsAreExact()
        {
            var expected = new[] { Solution("Root.sln"), Solution(@"one\First.SLNX"), Solution(@"one\two\Second.sln"), Solution(@"one\two\three\Third.slnx") };
            Solution(@"one\two\three\four\TooDeep.sln");
            foreach (string folder in new[] { "bin", "OBJ", "node_modules", ".git", "packages", ".vs", "TestResults", "artifacts", "dist" })
                Solution(Path.Combine(folder, "Excluded.sln"));
            Solution("False.sln.bak");
            Solution("Project.csproj");
            var result = SolutionDirectoryScanner.Scan(_data.Path);
            CollectionAssert.AreEquivalent(expected, result.Files.ToArray());
            Assert.AreEqual(4, result.DirectoriesVisited);
            Assert.IsFalse(result.LimitReached);
            Assert.AreEqual(0, result.Warnings.Count);
            CollectionAssert.AreEqual(new[] { expected[0] }, SolutionDirectoryScanner.Scan(_data.Path, 0).Files.ToArray());
            Assert.IsFalse(File.Exists(SolutionRegistry.DefaultPath));
        }

        [TestMethod]
        public void Scan_InvalidInputAndCancellation_AreExplicit()
        {
            Assert.ThrowsException<ArgumentException>(() => SolutionDirectoryScanner.Scan(" "));
            Assert.ThrowsException<DirectoryNotFoundException>(() => SolutionDirectoryScanner.Scan(_data.File("missing")));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => SolutionDirectoryScanner.Scan(_data.Path, 4));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.ThrowsException<OperationCanceledException>(() => SolutionDirectoryScanner.Scan(_data.Path, cancellationToken: cancellation.Token));
            }
        }

        [TestMethod]
        public void Scan_ResultLimit_IsExplicit_NotSilentTruncation()
        {
            for (int i = 0; i <= SolutionDirectoryScanner.MaximumResults; i++) Solution("Solution" + i + ".sln");
            var result = SolutionDirectoryScanner.Scan(_data.Path, 0);
            Assert.AreEqual(SolutionDirectoryScanner.MaximumResults, result.Files.Count);
            Assert.IsTrue(result.LimitReached);
            Assert.IsTrue(result.Warnings.Any(w => w.Contains("results are incomplete")));
        }

        [TestMethod]
        public void ScanAndImport_ExpandConfiguredEnvironmentPaths()
        {
            string name = "VSMANAGER_DISCOVERY_TEST_" + Guid.NewGuid().ToString("N");
            string path = Solution("Order.slnx");
            Environment.SetEnvironmentVariable(name, _data.Path);
            try
            {
                CollectionAssert.AreEqual(new[] { path }, SolutionDirectoryScanner.Scan("%" + name + "%", 0).Files.ToArray());
                var registry = new SolutionRegistry();
                Assert.IsNull(registry.ImportPaths(new[] { "%" + name + "%\\Order.slnx" }, out var added));
                Assert.AreEqual(path, added.Single().Path);
                Assert.AreEqual(path, SolutionDirectoryScanner.ResolveSolutionPath("%" + name + "%\\Order.slnx"));
            }
            finally { Environment.SetEnvironmentVariable(name, null); }
        }

        [TestMethod]
        public void Import_SelectedOnly_DeduplicatesAndNumbersAliases_WithoutOverwritingExistingMetadata()
        {
            string existingPath = Solution(@"existing\Order.sln");
            string first = Solution(@"one\Order.slnx"), second = Solution(@"two\Order.sln");
            Solution("Unselected.sln");
            var registry = new SolutionRegistry();
            Assert.IsNull(registry.Upsert(new SolutionEntry { Alias = "Order", Path = existingPath, Description = "Generic order project", Synonyms = new List<string> { "ordering" }, DefaultVs = 2 }));
            int changes = 0;
            registry.Changed += () => changes++;
            Assert.IsNull(registry.ImportPaths(new[] { existingPath, first, first.ToUpperInvariant(), second }, out var added));
            Assert.AreEqual(1, changes);
            CollectionAssert.AreEqual(new[] { "Order (2)", "Order (3)" }, added.Select(e => e.Alias).ToArray());
            Assert.AreEqual(3, registry.Count);
            Assert.IsFalse(registry.Items.Any(e => e.FileName == "Unselected"));
            var restored = new SolutionRegistry().Load();
            Assert.AreEqual(3, restored.Count);
            var original = restored.Resolve("ordering").Hit;
            Assert.AreEqual("Generic order project", original.Description);
            Assert.AreEqual(2, original.DefaultVs);
            Assert.AreEqual(existingPath, original.Path);
            Assert.IsTrue(File.Exists(SolutionRegistry.DefaultPath + ".bak"));
            Assert.IsNull(registry.ImportPaths(new[] { first, second }, out var duplicates));
            Assert.AreEqual(0, duplicates.Count);
            Assert.AreEqual(1, changes);
        }

        [TestMethod]
        public void Import_EmptyOrPunctuationFileNames_GetResolvableAliases()
        {
            var registry = new SolutionRegistry();
            Assert.IsNull(registry.ImportPaths(new[] { Solution(".sln"), Solution("---.slnx") }, out var added));
            Assert.AreEqual(2, added.Count);
            foreach (var entry in added) Assert.AreEqual(entry.Path, registry.Resolve(entry.Alias).Hit.Path);
        }

        [TestMethod]
        public void Launch_RejectsNonSolutionFileBeforeStartingAnyProcess()
        {
            StringAssert.Contains(VsLifecycle.Launch(Solution("Wrong.txt"), null), "Only .sln / .slnx files are supported");
            StringAssert.Contains(VsLifecycle.Launch("Relative.sln", null), "fully qualified solution path is required");
        }

        [TestMethod]
        public void Import_InvalidOrDisappearedCandidate_DoesNotPartiallySave()
        {
            string good = Solution("Order.sln");
            var registry = new SolutionRegistry();
            Assert.IsNotNull(registry.ImportPaths(new[] { good, _data.File("Missing.sln") }, out var added));
            Assert.AreEqual(0, added.Count);
            Assert.AreEqual(0, registry.Count);
            Assert.IsFalse(File.Exists(SolutionRegistry.DefaultPath));
            Assert.IsNotNull(registry.ImportPaths(new[] { "Relative.sln" }, out _));
            Assert.IsNotNull(registry.ImportPaths(new[] { Solution("Wrong.txt") }, out _));
        }

        [TestMethod]
        public void Import_SaveFailure_LeavesMemoryAndDiskUnchanged()
        {
            string path = Solution("Order.sln");
            Directory.CreateDirectory(_data.File("blocked"));
            var registry = new SolutionRegistry(_data.File("blocked"));
            int changes = 0;
            registry.Changed += () => changes++;
            Assert.IsNotNull(registry.ImportPaths(new[] { path }, out var added));
            Assert.AreEqual(0, added.Count);
            Assert.AreEqual(0, registry.Count);
            Assert.AreEqual(0, changes);
            Assert.IsTrue(Directory.Exists(registry.FilePath));
        }

        [TestMethod]
        public void ManualConfiguration_ReloadsMultiplePathsAndOptionalFields()
        {
            var registry = new SolutionRegistry();
            Assert.IsNull(registry.Replace(new[]
            {
                new SolutionEntry { Alias = "订单项目", Path = Solution("Order.sln"), Synonyms = new List<string> { "订单" }, Description = "通用示例 / Generic example", DefaultVs = 1 },
                new SolutionEntry { Alias = "钢筋项目", Path = Solution("Rebar.slnx") }
            }));
            string json = File.ReadAllText(SolutionRegistry.DefaultPath);
            foreach (string field in new[] { "Solutions", "Alias", "Path", "Description", "Synonyms", "DefaultVs" }) StringAssert.Contains(json, "\"" + field + "\"");
            File.WriteAllText(SolutionRegistry.DefaultPath, json.Replace("订单项目", "订单管理"));
            registry.Load();
            Assert.AreEqual(2, registry.Count);
            Assert.AreEqual("订单管理", registry.Resolve("订单").Hit.Alias);
            Assert.AreEqual(0, registry.Resolve("钢筋项目").Hit.DefaultVs);
        }
    }
}
