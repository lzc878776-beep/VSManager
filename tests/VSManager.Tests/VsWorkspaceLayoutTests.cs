using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>仅使用内存后端和测试自己创建的窗口，不连接真实 VS。/ Only fake backends and test-owned windows; never connects to real VS.</summary>
    [TestClass]
    public class VsWorkspaceLayoutTests
    {
        private static VsWorkspacePlacement Target(int pid = 10) => new VsWorkspacePlacement
        {
            Name = "Test" + pid,
            Vs = new VsInstance { Pid = pid, StartTicks = 100, MainHwnd = new IntPtr(pid), Dte = new object() },
            MainBounds = new Rectangle(-1200, 30, 800, 600),
            CopilotBounds = new Rectangle(-400, 30, 400, 600),
            OutputBounds = new Rectangle(-1200, 630, 800, 300),
            ErrorListBounds = new Rectangle(-400, 630, 400, 300)
        };

        [TestMethod]
        public void Arrange_CapturesEverySelectedPaneBeforeMovingOwner_AndReportsEach()
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            string result = engine.Arrange(new[] { target }, true, true, "chat");
            CollectionAssert.AreEqual(new[] { "capture-main", "capture-Copilot", "capture-Output", "capture-ErrorList", "move-main", "move-Copilot", "move-Output", "move-ErrorList" }, backend.Events);
            foreach (string pane in new[] { "Main", "Copilot", "Output", "ErrorList" }) StringAssert.Contains(result, pane + ": 已验证 / verified");
            Assert.AreEqual(target.MainBounds, backend.MainBounds);
            Assert.AreEqual(1, engine.SavedCount);
        }

        [TestMethod]
        public void Arrange_SolutionExplorer_IsCapturedMovedAndRestored()
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            target.SolutionExplorerBounds = new Rectangle(-400, 930, 400, 200);
            string result = engine.Arrange(new[] { target }, true, true, "chat", true);
            CollectionAssert.AreEqual(new[] { "capture-main", "capture-Copilot", "capture-Output", "capture-ErrorList", "capture-SolutionExplorer",
                "move-main", "move-Copilot", "move-Output", "move-ErrorList", "move-SolutionExplorer" }, backend.Events);
            StringAssert.Contains(result, "SolutionExplorer: 已验证 / verified");
            Assert.IsTrue(backend.Panes[WorkspacePane.SolutionExplorer].Floating);
            engine.Restore(new[] { target.Vs });
            Assert.IsFalse(backend.Panes[WorkspacePane.SolutionExplorer].Floating);
            Assert.IsTrue(backend.Panes[WorkspacePane.SolutionExplorer].AutoHides);
            Assert.AreEqual(0, engine.SavedCount);
            Assert.AreEqual(VsService.SolutionExplorerKind, WindowsWorkspaceBackend.KindOf(WorkspacePane.SolutionExplorer));
        }

        [TestMethod]
        public void Arrange_OptionalPanesAreNotReadOrChanged()
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            target.SolutionExplorerBounds = new Rectangle(-400, 930, 400, 200);
            engine.Arrange(new[] { target }, false, false, null);
            Assert.IsFalse(backend.Events.Any(e => e.Contains("Output") || e.Contains("ErrorList") || e.Contains("SolutionExplorer")));
        }

        [TestMethod]
        public void RepeatedArrange_RestoresEarliestMainAndPaneStates()
        {
            var backend = new FakeBackend();
            var originalMain = backend.MainBounds;
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            engine.Arrange(new[] { target }, true, false, null);
            target.MainBounds = new Rectangle(200, 300, 900, 700);
            engine.Arrange(new[] { target }, true, false, null);
            engine.Restore(new[] { target.Vs });
            Assert.AreEqual(originalMain, backend.MainBounds);
            Assert.IsFalse(backend.Panes[WorkspacePane.Copilot].Floating);
            Assert.IsTrue(backend.Panes[WorkspacePane.Copilot].AutoHides);
            Assert.IsFalse(backend.Panes[WorkspacePane.Copilot].Visible);
            Assert.IsFalse(backend.Panes[WorkspacePane.Copilot].Linkable);
            Assert.AreEqual(1, backend.Events.Count(e => e == "capture-main"));
            Assert.AreEqual(0, engine.SavedCount);
        }

        [TestMethod]
        public void RepeatedArrange_RejectsDifferentPaneIdentityUntilRestored()
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            engine.Arrange(new[] { target }, false, false, null);
            backend.Panes[WorkspacePane.Copilot].Kind = VsService.OutputKind;
            string result = engine.Arrange(new[] { target }, false, false, "another pane");
            StringAssert.Contains(result, "pane identity changed; restore first");
            Assert.AreEqual(1, backend.Events.Count(e => e == "move-Copilot"));
            Assert.AreEqual(1, engine.SavedCount);
        }

        [TestMethod]
        public void UnknownPaneState_SkipsOnlyThatPane_WithoutInventingSnapshot()
        {
            var backend = new FakeBackend { CaptureFailure = WorkspacePane.Output };
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            var result = engine.Arrange(new[] { target }, true, true, null);
            StringAssert.Contains(result, "Output: 跳过 / skipped");
            StringAssert.Contains(result, "ErrorList: 已验证 / verified");
            Assert.IsFalse(backend.Events.Contains("move-Output"));
            engine.Restore(new[] { target.Vs });
            Assert.IsFalse(backend.Events.Contains("restore-Output"));
        }

        [TestMethod]
        public void NoInterfaceCastFailure_IsReportedPerTarget_AndOtherTargetsStillArrange()
        {
            var backend = new FakeBackend { CastFailurePid = 11 };
            var engine = new WorkspaceLayoutEngine(backend);
            string result = engine.Arrange(new[] { Target(11), Target(12) }, true, true, null);
            StringAssert.Contains(result, "Test11 / Main: 跳过 / skipped: VS 自动化接口不可用（E_NOINTERFACE");
            StringAssert.Contains(result, "Test11 / Copilot: 跳过：目标不安全 / skipped: target unsafe");
            foreach (string pane in new[] { "Main", "Copilot", "Output", "ErrorList" }) StringAssert.Contains(result, "Test12 / " + pane + ": 已验证 / verified");
        }

        [TestMethod]
        public void NoInterfaceCastFailureOnPane_SkipsOnlyThatPane()
        {
            var backend = new FakeBackend { CastCaptureFailure = WorkspacePane.ErrorList };
            var engine = new WorkspaceLayoutEngine(backend);
            string result = engine.Arrange(new[] { Target() }, true, true, null);
            StringAssert.Contains(result, "ErrorList: 跳过 / skipped: VS 自动化接口不可用（E_NOINTERFACE");
            foreach (string pane in new[] { "Main", "Copilot", "Output" }) StringAssert.Contains(result, pane + ": 已验证 / verified");
        }

        [TestMethod]
        public void LaterPaneChangesEarlierPane_FinalVerificationReportsFailure()
        {
            var backend = new FakeBackend { DriftCopilotWhenOutputMoves = true };
            var engine = new WorkspaceLayoutEngine(backend);
            string result = engine.Arrange(new[] { Target() }, true, false, null);
            StringAssert.Contains(result, "Copilot: 最终验证失败");
            Assert.IsFalse(result.Contains("Copilot: 已验证 / verified"));
            StringAssert.Contains(result, "Output: 已验证 / verified");
            Assert.AreEqual(1, engine.SavedCount);
        }

        [TestMethod]
        public void PartialMutationFailure_IsReportedAndRemainsRestorable()
        {
            var backend = new FakeBackend { MoveFailure = WorkspacePane.Copilot };
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            string result = engine.Arrange(new[] { target }, true, false, null);
            Assert.IsTrue(backend.Panes[WorkspacePane.Copilot].Floating);
            StringAssert.Contains(result, "Copilot: 失败，保留快照 / failed");
            StringAssert.Contains(result, "Output: 已验证 / verified");
            engine.Restore(new[] { target.Vs });
            Assert.IsFalse(backend.Panes[WorkspacePane.Copilot].Floating);
            Assert.AreEqual(0, engine.SavedCount);
        }

        [TestMethod]
        public void FailedRestore_RetainsOnlyPendingPane_AndCanRetry()
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            engine.Arrange(new[] { target }, true, false, null);
            backend.RestoreFailure = WorkspacePane.Copilot;
            string result = engine.Restore(new[] { target.Vs });
            StringAssert.Contains(result, "Copilot: 还原失败，保留待重试 / restore failed");
            Assert.AreEqual(1, engine.SavedCount);
            backend.RestoreFailure = null;
            engine.Restore(new[] { target.Vs });
            Assert.AreEqual(1, backend.Events.Count(e => e == "restore-Output"));
            Assert.AreEqual(1, backend.Events.Count(e => e == "restore-main"));
            Assert.AreEqual(2, backend.Events.Count(e => e == "restore-Copilot"));
            Assert.AreEqual(0, engine.SavedCount);
        }

        [TestMethod]
        public void FailedMainRestore_RetainsPaneSnapshotUntilOwnerRestorationSucceeds()
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            engine.Arrange(new[] { target }, false, false, null);
            backend.FailMainRestore = true;
            engine.Restore(new[] { target.Vs });
            Assert.AreEqual(1, engine.SavedCount);
            backend.FailMainRestore = false;
            engine.Restore(new[] { target.Vs });
            Assert.AreEqual(2, backend.Events.Count(e => e == "restore-Copilot"));
            Assert.AreEqual(2, backend.Events.Count(e => e == "restore-main"));
            Assert.AreEqual(0, engine.SavedCount);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void Restore_RejectsReusedPidOrChangedMainHandle_WithoutLosingSnapshot(bool reusedPid)
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            engine.Arrange(new[] { target }, false, false, null);
            var wrong = Target().Vs;
            if (reusedPid) wrong.StartTicks++;
            else wrong.MainHwnd = new IntPtr(999);
            string result = engine.Restore(new[] { wrong });
            StringAssert.Contains(result, "retained for retry");
            Assert.IsFalse(backend.Events.Any(e => e.StartsWith("restore-", StringComparison.Ordinal)));
            Assert.AreEqual(1, engine.SavedCount);
            engine.Restore(new[] { target.Vs });
            Assert.AreEqual(0, engine.SavedCount);
        }

        [TestMethod]
        public void TargetBecomesUnsafeAfterCapture_NoMutationIsAttempted()
        {
            var backend = new FakeBackend { InvalidateAfterCapture = true };
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            string result = engine.Arrange(new[] { target }, false, false, null);
            StringAssert.Contains(result, "failed");
            Assert.IsFalse(backend.Events.Any(e => e.StartsWith("move-", StringComparison.Ordinal)));
            Assert.AreEqual(1, engine.SavedCount);
        }

        [TestMethod]
        public void InvalidBoundsAndDuplicates_AreExplicitlySkipped()
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            target.CopilotBounds = new Rectangle(10, 10, 0, 100);
            string result = engine.Arrange(new[] { target, target }, false, false, null);
            StringAssert.Contains(result, "invalid physical bounds");
            StringAssert.Contains(result, "duplicate target");
            Assert.AreEqual(1, backend.Events.Count(e => e == "move-main"));
            Assert.IsFalse(backend.Events.Contains("move-Copilot"));
        }

        [TestMethod]
        public void EmptyPaneBounds_LeavePaneUntouched()
        {
            var backend = new FakeBackend();
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            target.CopilotBounds = Rectangle.Empty;
            target.OutputBounds = Rectangle.Empty;
            string result = engine.Arrange(new[] { target }, true, false, null);
            Assert.AreEqual(1, backend.Events.Count(e => e == "move-main"));
            Assert.IsFalse(backend.Events.Any(e => e.StartsWith("move-", StringComparison.Ordinal) && e != "move-main"));
            Assert.IsFalse(result.Contains("invalid physical bounds"));
        }

        [TestMethod]
        public void UnknownMainSnapshot_PreventsAllMutationsAndSnapshots()
        {
            var backend = new FakeBackend { FailMainCapture = true };
            var engine = new WorkspaceLayoutEngine(backend);
            string result = engine.Arrange(new[] { Target() }, true, true, null);
            StringAssert.Contains(result, "Main: 跳过 / skipped");
            Assert.IsFalse(backend.Events.Any(e => e.StartsWith("move-", StringComparison.Ordinal)));
            Assert.AreEqual(0, engine.SavedCount);
        }

        [TestMethod]
        public void FloatingSnapshot_PreservesOriginalPlacement()
        {
            var backend = new FakeBackend();
            backend.Panes[WorkspacePane.Copilot].Floating = true;
            backend.Panes[WorkspacePane.Copilot].AutoHides = false;
            backend.Panes[WorkspacePane.Copilot].Placement = FakeBackend.Placement(new Rectangle(-900, 200, 420, 700));
            var original = backend.Panes[WorkspacePane.Copilot].Placement.rcNormalPosition;
            var engine = new WorkspaceLayoutEngine(backend);
            var target = Target();
            engine.Arrange(new[] { target }, false, false, null);
            engine.Restore(new[] { target.Vs });
            Assert.AreEqual(original.Left, backend.Panes[WorkspacePane.Copilot].Placement.rcNormalPosition.Left);
            Assert.AreEqual(original.Bottom, backend.Panes[WorkspacePane.Copilot].Placement.rcNormalPosition.Bottom);
            Assert.IsTrue(backend.Panes[WorkspacePane.Copilot].Floating);
        }

        [TestMethod]
        public void AutomationId_RequiresExactGuid_NotCaptionOrSubstring()
        {
            string kind = VsService.OutputKind;
            Assert.IsTrue(WindowsWorkspaceBackend.MatchesAutomationId(kind.ToLowerInvariant(), kind));
            Assert.IsTrue(WindowsWorkspaceBackend.MatchesAutomationId("ViewPresenter_" + kind, kind));
            Assert.IsTrue(WindowsWorkspaceBackend.MatchesAutomationId("ST:0:0:" + kind, kind));
            Assert.IsFalse(WindowsWorkspaceBackend.MatchesAutomationId("ST:x:0:" + kind, kind));
            Assert.IsFalse(WindowsWorkspaceBackend.MatchesAutomationId("ST:0:0:" + kind + ":extra", kind));
            Assert.IsFalse(WindowsWorkspaceBackend.MatchesAutomationId("Output", kind));
            Assert.IsFalse(WindowsWorkspaceBackend.MatchesAutomationId("x" + kind + "suffix", kind));
            Assert.IsFalse(WindowsWorkspaceBackend.MatchesAutomationId(WindowsWorkspaceBackend.ErrorListKind, kind));
            Assert.IsFalse(WindowsWorkspaceBackend.MatchesAutomationId(null, kind));
        }

        [TestMethod]
        public void BoundsMatch_AllowsOnlyTwoPixelDpiRounding()
        {
            var expected = new Rectangle(-1200, 10, 800, 600);
            Assert.IsTrue(WindowsWorkspaceBackend.BoundsMatch(new Rectangle(-1198, 12, 800, 600), expected));
            Assert.IsFalse(WindowsWorkspaceBackend.BoundsMatch(new Rectangle(-1197, 10, 800, 600), expected));
            Assert.IsFalse(WindowsWorkspaceBackend.BoundsMatch(new Rectangle(-1200, 10, 803, 600), expected));
            Assert.IsFalse(WindowsWorkspaceBackend.BoundsMatch(new Rectangle(-1200, 10, 800, 603), expected));
        }

        [TestMethod]
        [TestCategory(TestKind.Ui)]
        public void NativeIdentityAndHostGuards_UseOnlySyntheticWindows()
        {
            OnSta(() =>
            {
                using (var main = new QuietForm())
                using (var host = new QuietForm())
                using (var unrelated = new QuietForm())
                using (var process = Process.GetCurrentProcess())
                {
                    main.Show();
                    host.Show(main);
                    unrelated.Show();
                    var vs = new VsInstance { Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks,
                        MainHwnd = main.Handle, Dte = new SyntheticDte { MainWindow = new SyntheticWindow { HWnd = main.Handle } } };
                    var backend = new WindowsWorkspaceBackend();
                    backend.Validate(vs);
                    backend.ValidateHost(vs, host.Handle);
                    Assert.ThrowsException<InvalidOperationException>(() => backend.ValidateHost(vs, main.Handle));
                    Assert.ThrowsException<InvalidOperationException>(() => backend.ValidateHost(vs, unrelated.Handle));
                    main.Enabled = false;
                    Assert.ThrowsException<InvalidOperationException>(() => backend.Validate(vs));
                    main.Enabled = true;
                    vs.StartTicks++;
                    Assert.ThrowsException<InvalidOperationException>(() => backend.Validate(vs));
                    vs.StartTicks--;
                    ((SyntheticDte)vs.Dte).MainWindow.HWnd = unrelated.Handle;
                    Assert.ThrowsException<InvalidOperationException>(() => backend.Validate(vs));
                }
            });
        }

        [DataTestMethod]
        [TestCategory(TestKind.Ui)]
        [DataRow(false)]
        [DataRow(true)]
        public void NativeMainPlacement_RoundTripsOnlyTestOwnedWindow(bool minimized)
        {
            OnSta(() =>
            {
                using (var main = new QuietForm())
                using (var process = Process.GetCurrentProcess())
                {
                    main.Show();
                    var vs = new VsInstance { Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks,
                        MainHwnd = main.Handle, Dte = new SyntheticDte { MainWindow = new SyntheticWindow { HWnd = main.Handle } } };
                    var backend = new WindowsWorkspaceBackend();
                    if (minimized) main.WindowState = FormWindowState.Minimized;
                    var original = backend.CaptureMain(vs);
                    var work = Screen.FromHandle(main.Handle).WorkingArea;
                    var target = new Rectangle(work.X + 40, work.Y + 40, 440, 300);
                    backend.PlaceMain(vs, target);
                    Assert.IsFalse(Native.IsIconic(main.Handle));
                    backend.RestoreMain(vs, original);
                    var restored = backend.CaptureMain(vs);
                    Assert.AreEqual(original.showCmd, restored.showCmd);
                    Assert.AreEqual(original.rcNormalPosition.Left, restored.rcNormalPosition.Left);
                    Assert.AreEqual(original.rcNormalPosition.Right, restored.rcNormalPosition.Right);
                }
            });
        }

        [TestMethod]
        [TestCategory(TestKind.Ui)]
        public void NativeFloatingOutput_RoundTripsOnlyTestOwnedHost()
        {
            OnSta(() =>
            {
                using (var main = new QuietForm())
                using (var host = new QuietForm())
                using (var process = Process.GetCurrentProcess())
                {
                    main.Show();
                    host.Show(main);
                    var tool = new SyntheticToolWindow { Host = host };
                    var dte = new SyntheticDte { MainWindow = new SyntheticWindow { HWnd = main.Handle } };
                    dte.Windows.Add(tool);
                    var vs = new VsInstance { Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks,
                        MainHwnd = main.Handle, Dte = dte };
                    var backend = new WindowsWorkspaceBackend();
                    var original = backend.CapturePane(vs, WorkspacePane.Output, null);
                    var work = Screen.FromHandle(main.Handle).WorkingArea;
                    backend.PlacePane(vs, original, new Rectangle(work.X + 80, work.Y + 80, 460, 320));
                    backend.RestorePane(vs, original);
                    var restored = backend.CapturePane(vs, WorkspacePane.Output, null);
                    Assert.AreEqual(original.Placement.rcNormalPosition.Left, restored.Placement.rcNormalPosition.Left);
                    Assert.AreEqual(original.Placement.rcNormalPosition.Bottom, restored.Placement.rcNormalPosition.Bottom);
                    Assert.IsTrue(restored.Floating);
                    Assert.IsTrue(restored.Visible);
                    Assert.IsFalse(Native.IsIconic(main.Handle));
                }
            });
        }

        [TestMethod]
        [TestCategory(TestKind.Ui)]
        public void NativeNoInterfaceDte_FallsBackToHandle_AndSkipsUnreadableDocuments()
        {
            OnSta(() =>
            {
                using (var main = new QuietForm())
                using (var host = new QuietForm())
                using (var process = Process.GetCurrentProcess())
                {
                    main.Show();
                    host.Show(main);
                    var dte = new NoInterfaceDte();
                    dte.Windows.Add(new NoInterfaceDocumentWindow());
                    dte.Windows.Add(new SyntheticToolWindow { Host = host, ThrowOnDocument = true });
                    var vs = new VsInstance { Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks,
                        MainHwnd = main.Handle, Dte = dte };
                    var backend = new WindowsWorkspaceBackend();
                    backend.Validate(vs);
                    backend.CaptureMain(vs);
                    var pane = backend.CapturePane(vs, WorkspacePane.Output, null);
                    Assert.IsTrue(pane.Floating);
                }
            });
        }

        private static void OnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic window test timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation => true;
            public QuietForm() { ShowInTaskbar = true; StartPosition = FormStartPosition.Manual; Bounds = new Rectangle(50, 50, 400, 280); }
        }

        public sealed class SyntheticDte
        {
            public SyntheticWindow MainWindow { get; set; }
            public List<object> Windows { get; } = new List<object>();
        }
        public sealed class SyntheticWindow { public IntPtr HWnd { get; set; } }
        private static InvalidCastException NoInterface() =>
            new InvalidCastException("QueryInterface IVsPersistDocData E_NOINTERFACE (synthetic)");
        public sealed class NoInterfaceDte
        {
            public object MainWindow => throw NoInterface();
            public List<object> Windows { get; } = new List<object>();
        }
        public sealed class NoInterfaceDocumentWindow
        {
            public string Kind => "Document";
            public string ObjectKind => throw NoInterface();
            public object Document => throw NoInterface();
        }
        public sealed class SyntheticToolWindow
        {
            public Form Host { get; set; }
            public bool ThrowOnDocument { get; set; }
            public IntPtr HWnd => Host.Handle;
            public string Kind => "Tool";
            public string ObjectKind => VsService.OutputKind;
            public object Document { get { if (ThrowOnDocument) throw NoInterface(); return null; } }
            public bool IsFloating { get; set; } = true;
            public bool Linkable { get; set; } = true;
            public bool AutoHides { get; set; }
            public bool Visible { get => Host.Visible; set => Host.Visible = value; }
        }

        private sealed class FakeBackend : IWorkspaceLayoutBackend
        {
            internal readonly List<string> Events = new List<string>();
            internal readonly Dictionary<WorkspacePane, WorkspacePaneSnapshot> Panes = new Dictionary<WorkspacePane, WorkspacePaneSnapshot>();
            internal Rectangle MainBounds = new Rectangle(10, 20, 700, 500);
            internal WorkspacePane? CaptureFailure, MoveFailure, RestoreFailure, CastCaptureFailure;
            internal int CastFailurePid;
            internal bool FailMainCapture, FailMainRestore, InvalidateAfterCapture, DriftCopilotWhenOutputMoves;
            private bool valid = true;

            internal FakeBackend()
            {
                foreach (WorkspacePane pane in Enum.GetValues(typeof(WorkspacePane)))
                    Panes.Add(pane, new WorkspacePaneSnapshot { Pane = pane, AutoHides = true, Linkable = false,
                        Kind = WindowsWorkspaceBackend.KindOf(pane) });
            }

            public void Validate(VsInstance vs)
            {
                if (!valid) throw new InvalidOperationException("synthetic modal or identity change");
                if (vs != null && vs.Pid == CastFailurePid) throw NoInterface();
            }
            public Native.WINDOWPLACEMENT CaptureMain(VsInstance vs)
            {
                Events.Add("capture-main");
                if (FailMainCapture) throw new InvalidOperationException("unknown main placement");
                return Placement(MainBounds);
            }
            public WorkspacePaneSnapshot CapturePane(VsInstance vs, WorkspacePane pane, string keyword)
            {
                Events.Add("capture-" + pane);
                if (CaptureFailure == pane) throw new InvalidOperationException("unknown pane state");
                if (CastCaptureFailure == pane) throw NoInterface();
                if (InvalidateAfterCapture) valid = false;
                return Clone(Panes[pane]);
            }
            public void PlaceMain(VsInstance vs, Rectangle bounds) { Events.Add("move-main"); MainBounds = bounds; }
            public void PlacePane(VsInstance vs, WorkspacePaneSnapshot pane, Rectangle bounds)
            {
                Events.Add("move-" + pane.Pane);
                var current = Panes[pane.Pane];
                current.Floating = true;
                current.Visible = true;
                current.AutoHides = false;
                current.Linkable = true;
                current.Placement = Placement(bounds);
                if (DriftCopilotWhenOutputMoves && pane.Pane == WorkspacePane.Output) Panes[WorkspacePane.Copilot].Visible = false;
                if (MoveFailure == pane.Pane) throw new InvalidOperationException("requested bounds not honored");
            }
            public void VerifyMain(VsInstance vs, Rectangle bounds)
            {
                Validate(vs);
                if (MainBounds != bounds) throw new InvalidOperationException("final main bounds mismatch");
            }
            public void VerifyPane(VsInstance vs, WorkspacePaneSnapshot pane, Rectangle bounds)
            {
                Validate(vs);
                var current = Panes[pane.Pane];
                var rect = current.Placement.rcNormalPosition;
                if (!current.Floating || !current.Visible || Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) != bounds)
                    throw new InvalidOperationException("final pane bounds mismatch");
            }
            public void RestoreMain(VsInstance vs, Native.WINDOWPLACEMENT placement)
            {
                Events.Add("restore-main");
                if (FailMainRestore) throw new InvalidOperationException("main restore blocked");
                var rect = placement.rcNormalPosition;
                MainBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            }
            public void RestorePane(VsInstance vs, WorkspacePaneSnapshot pane)
            {
                Events.Add("restore-" + pane.Pane);
                if (RestoreFailure == pane.Pane) throw new InvalidOperationException("pane restore blocked");
                Panes[pane.Pane] = Clone(pane);
            }
            internal static Native.WINDOWPLACEMENT Placement(Rectangle bounds) => new Native.WINDOWPLACEMENT
            {
                showCmd = 1,
                rcNormalPosition = new Native.RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom }
            };
            private static WorkspacePaneSnapshot Clone(WorkspacePaneSnapshot pane) => new WorkspacePaneSnapshot
            {
                Pane = pane.Pane, Kind = pane.Kind, Keyword = pane.Keyword, Floating = pane.Floating,
                AutoHides = pane.AutoHides, Linkable = pane.Linkable, Visible = pane.Visible, Placement = pane.Placement
            };
        }
    }
}
