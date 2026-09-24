using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class SolutionLifecycleTests
    {
        private static SolutionEntry Entry(string relative) => new SolutionEntry
        {
            Alias = "订单项目",
            Path = SolutionMatcherTests.P(relative)
        };

        [TestMethod]
        public void ExplicitUnregisteredPath_DoesNotFallThroughToFuzzyAlias()
        {
            var entry = Entry(@"Registered\Orders.sln");
            entry.Synonyms.Add("Orders");
            var result = SolutionMatcher.Resolve(new[] { entry }, SolutionMatcherTests.P(@"Other\Orders.sln"));
            Assert.IsFalse(result.Found);
            Assert.AreEqual(0, result.Candidates.Count);
            Assert.AreSame(entry, SolutionMatcher.Resolve(new[] { entry }, "Orders.sln").Hit);
        }

        [TestMethod]
        public void DuplicateExactPaths_ReturnAllCandidates()
        {
            var first = Entry("Orders.slnx");
            var second = Entry("Orders.slnx");
            second.Alias = "另一个别名";
            var result = SolutionMatcher.Resolve(new[] { first, second }, first.Path);
            Assert.IsTrue(result.Ambiguous);
            Assert.IsNull(result.Hit);
            Assert.AreEqual(2, result.Candidates.Count);
        }

        [TestMethod]
        public void OpenInstance_ExactCurrentPathWinsOverTitleDefaultAndStaleLaunchPath()
        {
            var entry = Entry("Orders.sln");
            entry.DefaultVs = 1;
            var unknown = new VsInstance { Title = "Orders - Microsoft Visual Studio" };
            var stale = new VsInstance { SolutionPath = SolutionMatcherTests.P("Other.sln"), LaunchPath = entry.Path };
            var exact = new VsInstance { SolutionPath = entry.Path };
            Assert.AreSame(exact, SolutionMatcher.FindOpenSolution(entry, new[] { unknown, stale, exact }, new[] { entry }));
            entry.DefaultVs = 0;
            Assert.IsNull(SolutionMatcher.FindOpenSolution(entry, new[] { stale }, new[] { entry }));
        }

        [TestMethod]
        public void OpenInstance_KnownLaunchPathWinsButDoesNotAllowWrongTitleOrDefaultFallback()
        {
            var entry = Entry("Orders.sln");
            var launched = new VsInstance { LaunchPath = entry.Path };
            Assert.AreSame(launched, SolutionMatcher.FindOpenSolution(entry, new[] { launched }, new[] { entry }));
            launched.LaunchPath = SolutionMatcherTests.P("Other.sln");
            launched.Title = "Orders - Microsoft Visual Studio";
            entry.DefaultVs = 1;
            Assert.IsNull(SolutionMatcher.FindOpenSolution(entry, new[] { launched }, new[] { entry }));
        }

        [TestMethod]
        public void OpenInstance_SharedRegisteredFilename_RejectsUnknownTitle()
        {
            var entry = Entry(@"One\Orders.sln");
            var other = Entry(@"Two\Orders.slnx");
            var unknown = new VsInstance { Title = "Orders - Microsoft Visual Studio" };
            Assert.IsNull(SolutionMatcher.FindOpenSolution(entry, new[] { unknown }, new[] { entry, other }));
            unknown.SolutionPath = entry.Path;
            Assert.AreSame(unknown, SolutionMatcher.FindOpenSolution(entry, new[] { unknown }, new[] { entry, other }));
        }

        [TestMethod]
        public void OpenInstance_TitleRequiresSingleInstance()
        {
            var entry = Entry("Orders.sln");
            var first = new VsInstance { Title = "Orders - Microsoft Visual Studio" };
            var second = new VsInstance { Title = first.Title };
            Assert.AreSame(first, SolutionMatcher.FindOpenSolution(entry, new[] { first }, new[] { entry }));
            Assert.IsNull(SolutionMatcher.FindOpenSolution(entry, new[] { first, second }, new[] { entry }));
            second.SolutionPath = SolutionMatcherTests.P(@"Other\Orders.sln");
            Assert.IsNull(SolutionMatcher.FindOpenSolution(entry, new[] { first, second }, new[] { entry }));
        }

        [TestMethod]
        public void OpenInstance_ExplicitDefaultOnlyAppliesToUnknownPaths()
        {
            var entry = Entry("Orders.sln");
            var unknown = new VsInstance { Title = "Other - Microsoft Visual Studio" };
            Assert.IsNull(SolutionMatcher.FindOpenSolution(entry, new[] { unknown }, new[] { entry }));
            entry.DefaultVs = 1;
            Assert.AreSame(unknown, SolutionMatcher.FindOpenSolution(entry, new[] { unknown }, new[] { entry }));
            unknown.SolutionPath = SolutionMatcherTests.P("Other.sln");
            Assert.IsNull(SolutionMatcher.FindOpenSolution(entry, new[] { unknown }, new[] { entry }));
        }

        [TestMethod]
        public void UnsavedItems_ReportsDocumentsProjectsAndSolution()
        {
            var dte = new LifecycleDte();
            dte.Documents = new[] { new LifecycleItem { Name = "Code.cs", SavedValue = false } };
            dte.Solution.Projects = new[] { new LifecycleItem { Name = "Project", SavedValue = false } };
            dte.Solution.SavedValue = false;
            var result = VsLifecycle.UnsavedItems(new VsInstance { Dte = dte }, out string reason);
            Assert.IsNull(reason);
            CollectionAssert.AreEqual(new[] { "文档 / Document：Code.cs", "项目 / Project：Project", "解决方案 / Solution：Orders.sln" }, result);
        }

        [TestMethod]
        public void UnsavedItems_AllSaved_ReturnsEmptyList()
        {
            var dte = new LifecycleDte();
            dte.Documents = new[] { new LifecycleItem() };
            dte.Solution.Projects = new[] { new LifecycleItem() };
            var result = VsLifecycle.UnsavedItems(new VsInstance { Dte = dte }, out string reason);
            Assert.IsNull(reason);
            Assert.AreEqual(0, result.Count);
        }

        [DataTestMethod]
        [DataRow("documents")]
        [DataRow("document-saved")]
        [DataRow("document-name")]
        [DataRow("projects")]
        [DataRow("project-saved")]
        [DataRow("project-name")]
        [DataRow("solution-path")]
        [DataRow("solution-saved")]
        public void UnsavedItems_AnyRequiredReadFailure_RefusesCheck(string failure)
        {
            var dte = new LifecycleDte();
            switch (failure)
            {
                case "documents": dte.Documents = new LifecycleBrokenCollection(); break;
                case "document-saved": dte.Documents = new[] { new LifecycleItem { ThrowSaved = true } }; break;
                case "document-name": dte.Documents = new[] { new LifecycleItem { SavedValue = false, ThrowName = true } }; break;
                case "projects": dte.Solution.Projects = new LifecycleBrokenCollection(); break;
                case "project-saved": dte.Solution.Projects = new[] { new LifecycleItem { ThrowSaved = true } }; break;
                case "project-name": dte.Solution.Projects = new[] { new LifecycleItem { SavedValue = false, ThrowName = true } }; break;
                case "solution-path": dte.Solution.ThrowPath = true; break;
                case "solution-saved": dte.Solution.ThrowSaved = true; break;
            }
            Assert.IsNull(VsLifecycle.UnsavedItems(new VsInstance { Dte = dte }, out string reason));
            StringAssert.Contains(reason, "Failed to check unsaved changes");
        }

        [TestMethod]
        public void UnsavedItems_MissingDte_RefusesCheck()
        {
            Assert.IsNull(VsLifecycle.UnsavedItems(new VsInstance(), out string reason));
            StringAssert.Contains(reason, "Cannot reach");
        }
    }

    public sealed class LifecycleDte
    {
        public IEnumerable Documents { get; set; } = new LifecycleItem[0];
        public LifecycleSolution Solution { get; set; } = new LifecycleSolution();
    }

    public class LifecycleItem
    {
        private string _name = "Item";
        public bool SavedValue { get; set; } = true;
        public bool ThrowSaved { get; set; }
        public bool ThrowName { get; set; }
        public bool Saved => ThrowSaved ? throw new InvalidOperationException("无法读取保存状态 / Cannot read saved state") : SavedValue;
        public string Name
        {
            get => ThrowName ? throw new InvalidOperationException("无法读取名称 / Cannot read name") : _name;
            set => _name = value;
        }
    }

    public sealed class LifecycleSolution : LifecycleItem
    {
        public IEnumerable Projects { get; set; } = new LifecycleItem[0];
        public bool ThrowPath { get; set; }
        public string FullName => ThrowPath ? throw new InvalidOperationException("无法读取路径 / Cannot read path") : SolutionMatcherTests.P("Orders.sln");
    }

    public sealed class LifecycleBrokenCollection : IEnumerable
    {
        public IEnumerator GetEnumerator() => throw new InvalidOperationException("无法枚举 / Cannot enumerate");
    }
}
