using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class VsDocumentCleanupTests
    {
        private static VsDocumentCleanupResult Run(CleanupDte dte, int threshold = 0,
            CleanupFallback fallback = null, Func<VsInstance, bool> ownerReady = null) =>
            VsDocumentCleanup.Run(new VsInstance { Dte = dte }, threshold, null,
                fallback ?? new CleanupFallback(), ownerReady ?? (_ => true));

        [TestMethod]
        public void ThresholdEquality_DoesNotClose()
        {
            var dte = new CleanupDte(10);
            var result = Run(dte, 10);
            Assert.AreEqual(10, result.InitialTabCount);
            Assert.IsFalse(result.ThresholdExceeded);
            Assert.AreEqual(0, result.Attempted);
            Assert.IsTrue(dte.Documents.All(d => d.Windows[0].Arguments.Count == 0));
        }

        [TestMethod]
        public void ThresholdExceeded_OnlyClosesSavedAndReportsUnsavedAndUnknown()
        {
            var dte = new CleanupDte(3);
            dte.Documents[1].SavedValue = false;
            dte.Documents[2].SavedError = true;
            var result = Run(dte, 2);
            Assert.AreEqual(3, result.InitialTabCount);
            Assert.AreEqual(1, result.Closed);
            Assert.AreEqual(1, result.SkippedUnsaved);
            Assert.AreEqual(1, result.Unknown);
            CollectionAssert.AreEqual(new[] { "Document1.cs" }, result.UnsavedNames);
            CollectionAssert.AreEqual(new[] { "Document0.cs" }, result.ClosedNames);
            StringAssert.Contains(result.Diagnostics.Last(), "Saved=未知 / Unknown");
        }

        [TestMethod]
        public void CountsDocumentWindowsNotDocuments_AndAlwaysUsesPromptZero()
        {
            var dte = new CleanupDte(1);
            var doc = dte.Documents[0];
            var first = doc.Windows[0];
            var second = new CleanupWindow(doc) { Caption = "Second view" };
            doc.Windows.Add(second);
            var result = Run(dte, 1);
            Assert.AreEqual(2, result.InitialTabCount);
            Assert.AreEqual(2, result.Closed);
            CollectionAssert.AreEqual(new[] { 0 }, first.Arguments);
            CollectionAssert.AreEqual(new[] { 0 }, second.Arguments);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(2)]
        [DataRow(3)]
        public void DebugOrUnknownMode_SkipsEverything(int mode)
        {
            var dte = new CleanupDte(2);
            dte.Debugger.Mode = mode;
            var result = Run(dte);
            Assert.IsTrue(result.DebuggingOrUnknown);
            Assert.AreEqual(0, result.Attempted);
        }

        [TestMethod]
        public void UnreadableDebugger_SkipsEverything()
        {
            var dte = new CleanupDte(1);
            dte.Debugger.Error = true;
            Assert.IsTrue(Run(dte).DebuggingOrUnknown);
        }

        [TestMethod]
        public void MidRunDebugging_StopsBeforeNextClose()
        {
            var dte = new CleanupDte(2);
            dte.Documents[0].Windows[0].BeforeClose = () => dte.Debugger.Mode = 2;
            var result = Run(dte);
            Assert.AreEqual(1, result.Closed);
            Assert.AreEqual(1, result.Attempted);
            Assert.IsTrue(result.DebuggingOrUnknown);
        }

        [TestMethod]
        public void DirtyBetweenEnumerationAndClose_IsSkipped()
        {
            var dte = new CleanupDte(1);
            dte.Documents[0].ReadSaved = count => count == 1;
            var result = Run(dte);
            Assert.AreEqual(0, result.Attempted);
            Assert.AreEqual(1, result.SkippedUnsaved);
        }

        [TestMethod]
        public void DebugModeChangesDuringPreflight_PreventsClose()
        {
            var dte = new CleanupDte(1);
            var result = Run(dte, ownerReady: _ => { dte.Debugger.Mode = 3; return true; });
            Assert.AreEqual(0, result.Attempted);
        }

        [TestMethod]
        public void NonDocumentOrMismatchedDocumentWindow_IsNeverClosed()
        {
            var dte = new CleanupDte(3);
            var tool = dte.Documents[0].Windows[0];
            tool.Type = 15;
            var mismatch = dte.Documents[1].Windows[0];
            mismatch.Document = dte.Documents[2];
            var result = Run(dte);
            Assert.AreEqual(1, result.InitialTabCount);
            Assert.AreEqual(1, result.Closed);
            Assert.AreEqual(0, tool.Arguments.Count);
            Assert.AreEqual(0, mismatch.Arguments.Count);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(16)]
        public void DocumentBoundCodeDesignerAndDocumentWindows_AreEligible(int type)
        {
            var dte = new CleanupDte(1);
            dte.Documents[0].Windows[0].Type = type;
            Assert.AreEqual(1, Run(dte).Closed);
        }

        [TestMethod]
        public void DteCloseHasNoEffect_UsesVerifiedFallback()
        {
            var dte = new CleanupDte(1);
            dte.Documents[0].Windows[0].RemoveOnClose = false;
            var fallback = new CleanupFallback { RemoveWindow = true };
            Assert.AreEqual(1, Run(dte, fallback: fallback).Closed);
            Assert.AreEqual(1, fallback.GuardedInvocations);
        }

        [TestMethod]
        public void CloseThrows_FallbackRequiresFreshGuardAndCountsConfirmedClosure()
        {
            var dte = new CleanupDte(1);
            var window = dte.Documents[0].Windows[0];
            window.ThrowOnClose = true;
            var fallback = new CleanupFallback { RemoveWindow = true };
            var result = Run(dte, fallback: fallback);
            Assert.AreEqual(1, fallback.Calls);
            Assert.AreEqual(1, fallback.GuardedInvocations);
            Assert.AreEqual(1, result.Closed);
            Assert.AreEqual(0, result.Failed);
            CollectionAssert.AreEqual(new[] { 0 }, window.Arguments);
        }

        [TestMethod]
        public void RequestedCloseWithoutRemoval_IsNotCounted()
        {
            var dte = new CleanupDte(1);
            dte.Documents[0].Windows[0].RemoveOnClose = false;
            var result = Run(dte);
            Assert.AreEqual(0, result.Closed);
            Assert.AreEqual(1, result.Attempted);
            Assert.AreEqual(1, result.Failed);
        }

        [TestMethod]
        public void FallbackRequestedClickWithoutRemoval_IsNotCounted()
        {
            var dte = new CleanupDte(1);
            dte.Documents[0].Windows[0].ThrowOnClose = true;
            var result = Run(dte, fallback: new CleanupFallback());
            Assert.AreEqual(0, result.Closed);
            Assert.AreEqual(1, result.Failed);
        }

        [TestMethod]
        public void FallbackFailure_DoesNotStopLaterDocuments()
        {
            var dte = new CleanupDte(2);
            dte.Documents[0].Windows[0].ThrowOnClose = true;
            var result = Run(dte, fallback: new CleanupFallback { Throw = true });
            Assert.AreEqual(1, result.Failed);
            Assert.AreEqual(1, result.Closed);
        }

        [TestMethod]
        public void FallbackRefusal_DoesNotStopLaterDocuments()
        {
            var dte = new CleanupDte(2);
            dte.Documents[0].Windows[0].ThrowOnClose = true;
            var result = Run(dte, fallback: new CleanupFallback { Refuse = true });
            Assert.AreEqual(1, result.Failed);
            Assert.AreEqual(1, result.Closed);
        }

        [TestMethod]
        public void CollidingTitles_NeverUseFallback()
        {
            var dte = new CleanupDte(2);
            foreach (var doc in dte.Documents)
            {
                doc.Windows[0].Caption = "Duplicate.cs";
                doc.Windows[0].ThrowOnClose = true;
            }
            var fallback = new CleanupFallback { RemoveWindow = true };
            var result = Run(dte, fallback: fallback);
            Assert.AreEqual(0, fallback.Calls);
            Assert.AreEqual(0, result.Closed);
        }

        [TestMethod]
        public void DirtyAfterDteFailure_PreventsFallback()
        {
            var dte = new CleanupDte(1);
            var doc = dte.Documents[0];
            doc.Windows[0].BeforeClose = () => doc.SavedValue = false;
            doc.Windows[0].ThrowOnClose = true;
            var fallback = new CleanupFallback { RemoveWindow = true };
            var result = Run(dte, fallback: fallback);
            Assert.AreEqual(0, fallback.Calls);
            Assert.AreEqual(0, result.Closed);
            Assert.AreEqual(1, result.SkippedUnsaved);
            CollectionAssert.AreEqual(new[] { "Document0.cs" }, result.UnsavedNames);
        }

        [TestMethod]
        public void DirtyImmediatelyBeforeUiaInvoke_PreventsClick()
        {
            var dte = new CleanupDte(1);
            var doc = dte.Documents[0];
            doc.Windows[0].ThrowOnClose = true;
            var fallback = new CleanupFallback { RemoveWindow = true, BeforeGuard = () => doc.SavedValue = false };
            var result = Run(dte, fallback: fallback);
            Assert.AreEqual(1, fallback.Calls);
            Assert.AreEqual(0, fallback.GuardedInvocations);
            Assert.AreEqual(0, result.Closed);
        }

        [TestMethod]
        public void ModalOwner_PreventsDteAndFallback()
        {
            var dte = new CleanupDte(1);
            var result = Run(dte, ownerReady: _ => false);
            Assert.AreEqual(0, result.Attempted);
            Assert.AreEqual(0, result.Closed);
        }

        [TestMethod]
        public void DebuggingImmediatelyBeforeUiaInvoke_PreventsClick()
        {
            var dte = new CleanupDte(1);
            dte.Documents[0].Windows[0].ThrowOnClose = true;
            var fallback = new CleanupFallback { RemoveWindow = true, BeforeGuard = () => dte.Debugger.Mode = 2 };
            var result = Run(dte, fallback: fallback);
            Assert.AreEqual(1, fallback.Calls);
            Assert.AreEqual(0, fallback.GuardedInvocations);
            Assert.AreEqual(0, result.Closed);
        }

        [TestMethod]
        public void NewDuplicateTitleAfterDteFailure_PreventsFallback()
        {
            var dte = new CleanupDte(1);
            var doc = dte.Documents[0];
            doc.Windows[0].ThrowOnClose = true;
            doc.Windows[0].BeforeClose = () =>
            {
                var duplicate = new CleanupDocument { FullName = doc.FullName };
                duplicate.Windows.Add(new CleanupWindow(duplicate));
                dte.Documents.Add(duplicate);
            };
            var fallback = new CleanupFallback { RemoveWindow = true };
            Assert.AreEqual(0, Run(dte, fallback: fallback).Closed);
            Assert.AreEqual(0, fallback.Calls);
        }

        [TestMethod]
        public void RenamedWindowAfterDteFailure_PreventsFallback()
        {
            var dte = new CleanupDte(1);
            var window = dte.Documents[0].Windows[0];
            window.ThrowOnClose = true;
            window.BeforeClose = () => window.Caption = "Renamed.cs";
            var fallback = new CleanupFallback { RemoveWindow = true };
            Assert.AreEqual(0, Run(dte, fallback: fallback).Closed);
            Assert.AreEqual(0, fallback.Calls);
        }

        [TestMethod]
        public void WindowMovedToOtherDocument_IsNotCountedAsClosed()
        {
            var dte = new CleanupDte(1);
            var doc = dte.Documents[0];
            var window = doc.Windows[0];
            window.RemoveOnClose = false;
            window.BeforeClose = () =>
            {
                var other = new CleanupDocument { FullName = "Other.cs" };
                doc.Windows.Remove(window);
                other.Windows.Add(window);
                window.Document = other;
                dte.Documents.Add(other);
            };
            Assert.AreEqual(0, Run(dte).Closed);
        }

        [TestMethod]
        public void NoDte_RefusesRatherThanInferringStateFromTitles()
        {
            var result = VsDocumentCleanup.Run(new VsInstance(), 0, null);
            Assert.AreEqual(1, result.Unknown);
            StringAssert.Contains(result.Diagnostics[0], "DTE unavailable");
        }

        [TestMethod]
        public void RealUia_RejectsSyntheticToolWindowCloseButton()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var form = new Form { Text = "合成工具窗口 / Synthetic tool window" })
                    using (var button = new Button { Text = "关闭 / Close", Name = "CloseButton" })
                    {
                        int clicked = 0;
                        button.Click += (s, e) => clicked++;
                        form.Controls.Add(button);
                        form.Show();
                        Application.DoEvents();
                        var target = new DocumentTabTarget { Hwnd = button.Handle, Title = button.Text, FullName = "Synthetic.cs" };
                        var vs = new VsInstance { MainHwnd = form.Handle, Pid = Process.GetCurrentProcess().Id };
                        Assert.IsFalse(new DocumentTabUiaFallback().TryClose(vs, target, () => true, out string reason));
                        Assert.AreEqual(0, clicked);
                        StringAssert.Contains(reason, "Refused");
                    }
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "合成 UIA 检查超时 / Synthetic UIA check timed out");
            Assert.IsNull(failure, failure?.ToString());
        }
    }

    public sealed class CleanupDte
    {
        public CleanupDebugger Debugger { get; } = new CleanupDebugger();
        public List<CleanupDocument> Documents { get; } = new List<CleanupDocument>();
        public CleanupDte(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var doc = new CleanupDocument { FullName = "Document" + i + ".cs" };
                doc.Windows.Add(new CleanupWindow(doc));
                Documents.Add(doc);
            }
        }
    }

    public sealed class CleanupDebugger
    {
        public int Mode = 1;
        public bool Error;
        public int CurrentMode => Error ? throw new COMException("合成失败 / Synthetic failure") : Mode;
    }

    public sealed class CleanupDocument
    {
        public bool SavedValue = true;
        public bool SavedError;
        public Func<int, bool> ReadSaved;
        private int reads;
        public bool Saved => SavedError ? throw new COMException("合成失败 / Synthetic failure") : ReadSaved?.Invoke(++reads) ?? SavedValue;
        public string FullName { get; set; }
        public List<CleanupWindow> Windows { get; } = new List<CleanupWindow>();
    }

    public sealed class CleanupWindow
    {
        public CleanupWindow(CleanupDocument document) { Document = document; Caption = document.FullName; }
        public CleanupDocument Document { get; set; }
        public int Type { get; set; } = 16;
        public string Caption { get; set; }
        public int HWnd => 1;
        public bool ThrowOnClose;
        public bool RemoveOnClose = true;
        public Action BeforeClose;
        public List<int> Arguments { get; } = new List<int>();
        public void Close(int saveChanges)
        {
            Arguments.Add(saveChanges);
            BeforeClose?.Invoke();
            if (ThrowOnClose) throw new COMException("合成失败 / Synthetic failure");
            if (RemoveOnClose) Document.Windows.Remove(this);
        }
    }

    internal sealed class CleanupFallback : IDocumentTabFallback
    {
        internal int Calls;
        internal int GuardedInvocations;
        internal bool RemoveWindow;
        internal bool Throw;
        internal bool Refuse;
        internal Action BeforeGuard;
        public bool TryClose(VsInstance vs, DocumentTabTarget target, Func<bool> mayInvoke, out string reason)
        {
            Calls++;
            reason = "合成后备 / Synthetic fallback";
            if (Throw) throw new COMException("合成失败 / Synthetic failure");
            if (Refuse) return false;
            BeforeGuard?.Invoke();
            if (!mayInvoke()) return false;
            GuardedInvocations++;
            if (RemoveWindow) ((CleanupDocument)target.Document).Windows.Remove((CleanupWindow)target.Window);
            return true;
        }
    }
}
