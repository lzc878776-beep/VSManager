using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class TranscriptMemoryTests
    {
        [TestMethod]
        public void StatusOnlyUpdates_DoNotRetransmitHistory_AndRepeatedValuesDoNotAdvanceRevision()
        {
            OnSta(() =>
            {
                using (var view = new TranscriptView())
                {
                    view.Render(Transcript(new string('x', 100000)), true);
                    var first = Update(view);
                    Assert.AreEqual(1, Messages(first).Length);
                    Commit(view);
                    view.SetActivity("working", "pending");
                    view.SetCompletion("done");
                    var status = Update(view);
                    Assert.AreEqual(0, Messages(status).Length);
                    Assert.IsFalse((bool)status["reset"]);
                    Assert.IsTrue(Payload(view).Length < 1024);
                    long revision = Revision(view);
                    view.SetActivity("working", "pending");
                    view.SetCompletion("done");
                    Assert.AreEqual(revision, Revision(view));
                }
            });
        }

        [TestMethod]
        public void RepeatedRender_ReusesImmutableMessages_AndOnlyChangedMessageIsSent()
        {
            OnSta(() =>
            {
                using (var view = new TranscriptView())
                {
                    var transcript = Transcript("first", "second");
                    view.Render(transcript, true);
                    var firstSource = Sources(view)[0];
                    Commit(view);
                    long revision = Revision(view);
                    view.Render(transcript, true);
                    Assert.AreEqual(revision, Revision(view));
                    Assert.AreSame(firstSource, Sources(view)[0]);
                    transcript.Messages[1].Parts[0].Text = "changed";
                    view.Render(transcript, true);
                    Assert.AreSame(firstSource, Sources(view)[0]);
                    var delta = Update(view);
                    Assert.AreEqual(1, Messages(delta).Length);
                    var message = (Dictionary<string, object>)Messages(delta)[0];
                    Assert.AreEqual(1, message["index"]);
                    StringAssert.Contains((string)message["html"], "changed");
                    Assert.AreEqual(revision, Convert.ToInt64(delta["baseRevision"]));
                }
            });
        }

        [TestMethod]
        public void AppendShrinkAndReplacement_KeepCompleteOrderedConversation()
        {
            OnSta(() =>
            {
                using (var view = new TranscriptView())
                {
                    view.Render(Transcript("one", "two"), true);
                    Commit(view);
                    view.Render(Transcript("one", "two", "three"), true);
                    Assert.AreEqual(1, Messages(Update(view)).Length);
                    Assert.AreEqual(3, Update(view)["count"]);
                    Commit(view);
                    view.Render(Transcript("one"), true);
                    Assert.AreEqual(0, Messages(Update(view)).Length);
                    Assert.AreEqual(1, Update(view)["count"]);
                    Commit(view);
                    view.Render(Transcript("different"), true);
                    Assert.AreEqual(1, Messages(Update(view)).Length);
                    view.SetEmpty("empty");
                    Assert.AreEqual(0, Update(view)["count"]);
                }
            });
        }

        [TestMethod]
        public void Reset_ChangesGeneration_ReleasesOldBaseline_AndRejectsOldWorkerSnapshot()
        {
            OnSta(() =>
            {
                using (var view = new TranscriptView())
                {
                    view.Render(Transcript("old"), true);
                    Commit(view);
                    object oldState = Get(view, "_latest");
                    long generation = (long)Get(view, "_generation");
                    view.ResetView();
                    Assert.IsNull(Get(view, "_sent"));
                    Invoke(view, "Queue", oldState, false, (long?)generation);
                    Assert.AreEqual(0, Sources(view).Count);
                    view.Render(Transcript("new"), true);
                    var update = Update(view);
                    Assert.IsTrue((bool)update["reset"]);
                    Assert.AreEqual(generation + 1, Convert.ToInt64(update["generation"]));
                    Assert.AreEqual(1, Messages(update).Length);
                }
            });
        }

        [TestMethod]
        public void HiddenView_DoesNotInitializeOrSchedule_AndResumeIncludesAllMissedChanges()
        {
            OnSta(() =>
            {
                using (var form = new Form())
                using (var view = new TranscriptView())
                {
                    form.Controls.Add(view);
                    var handle = view.Handle;
                    Assert.IsFalse((bool)Get(view, "_initializing"));
                    Assert.IsNull(Get(view, "_core"));
                    view.Render(Transcript("one"), true);
                    Commit(view);
                    view.Render(Transcript("one", "two"), true);
                    view.Render(Transcript("one", "two", "three"), true);
                    view.SetActivity("latest", "");
                    Invoke(view, "FlushLatest");
                    Assert.IsFalse((bool)Get(view, "_scheduled"));
                    Assert.AreEqual(1, ((IList)Get(Get(view, "_sent"), "Messages")).Count);
                    Set(view, "_shown", true);
                    Set(view, "_ready", true);
                    Invoke(view, "ScheduleRender");
                    Assert.IsTrue((bool)Get(view, "_scheduled"));
                    var update = Update(view);
                    Assert.AreEqual(3, update["count"]);
                    Assert.AreEqual(2, Messages(update).Length);
                    Assert.AreEqual("latest", update["activity"]);
                }
            });
        }

        [TestMethod]
        public void SuspensionInFlight_DefersPostingUntilResume()
        {
            OnSta(() =>
            {
                using (var view = new TranscriptView())
                {
                    var handle = view.Handle;
                    view.Render(Transcript("one"), true);
                    Set(view, "_shown", true);
                    Set(view, "_ready", true);
                    Set(view, "_suspending", true);
                    Invoke(view, "ScheduleRender");
                    Invoke(view, "FlushLatest");
                    Assert.IsFalse((bool)Get(view, "_scheduled"));
                    Assert.IsNull(Get(view, "_sent"));
                    Set(view, "_suspending", false);
                    Invoke(view, "ScheduleRender");
                    Assert.IsTrue((bool)Get(view, "_scheduled"));
                }
            });
        }

        [TestMethod]
        public void ShowStepsRoleAndLabelChanges_InvalidateOnlyAffectedRendering()
        {
            OnSta(() =>
            {
                using (var view = new TranscriptView())
                {
                    var transcript = Transcript("answer");
                    transcript.Messages[0].Parts.Add(new ChatPart { IsStep = true, Text = "step" });
                    view.Render(transcript, false);
                    Commit(view);
                    view.Render(transcript, true);
                    StringAssert.Contains((string)((Dictionary<string, object>)Messages(Update(view))[0])["html"], "step");
                    Commit(view);
                    transcript.Messages[0].Role = ChatRole.User;
                    view.Render(transcript, true);
                    Assert.AreEqual(true, ((Dictionary<string, object>)Messages(Update(view))[0])["user"]);
                    Commit(view);
                    view.AssistantLabel = "Changed";
                    Assert.AreEqual("Changed", Update(view)["bot"]);
                    Assert.AreEqual(1, Messages(Update(view)).Length);
                }
            });
        }

        [TestMethod]
        public void LostPageBaseline_ResendsAllMessages_WithoutTruncation()
        {
            OnSta(() =>
            {
                using (var view = new TranscriptView())
                {
                    var transcript = new ChatTranscript();
                    for (int i = 0; i < 600; i++) transcript.Messages.Add(Transcript("message " + i).Messages[0]);
                    view.Render(transcript, true);
                    Commit(view);
                    Set(view, "_sent", null);
                    Set(view, "_sentRevision", -1L);
                    view.ScrollTopOnReset = true;
                    var update = Update(view);
                    Assert.AreEqual(600, Messages(update).Length);
                    Assert.AreEqual(600, update["count"]);
                    Assert.IsTrue((bool)update["reset"]);
                    Assert.IsTrue((bool)update["top"]);
                }
            });
        }

        [TestMethod]
        public void Dispose_ReleasesSnapshots_AndIgnoresLateUpdates()
        {
            OnSta(() =>
            {
                var view = new TranscriptView();
                view.Render(Transcript("retained"), true);
                Commit(view);
                view.SetActivity("working", "pending");
                view.Dispose();
                Assert.IsNull(Get(view, "_sent"));
                Assert.AreEqual(0, Sources(view).Count);
                Assert.AreEqual("", Get(view, "_pending"));
                long revision = Revision(view);
                view.Render(Transcript("late"), true);
                view.ResetView();
                view.SetActivity("late", "late");
                Invoke(view, "FlushLatest");
                Assert.AreEqual(revision, Revision(view));
                Assert.AreEqual(0, Sources(view).Count);
            });
        }

        [TestMethod]
        public void BrowserVisibility_RendersAndResumesLatestDeltaInIsolatedProfile()
        {
            OnSta(() =>
            {
                using (var data = new TempDataFolder())
                using (var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-10000, -10000), Size = new System.Drawing.Size(360, 240) })
                using (var view = new TranscriptView { Dock = DockStyle.Fill })
                {
                    string profile = TranscriptView.UserDataFolder;
                    var environment = typeof(TranscriptView).GetField("_sharedEnvironment", BindingFlags.Static | BindingFlags.NonPublic);
                    object previous = environment.GetValue(null);
                    Exception failure = null;
                    TranscriptView.UserDataFolder = data.File("WebView2");
                    environment.SetValue(null, null);
                    try
                    {
                        form.Controls.Add(view);
                        view.Render(Transcript("first", "second"), true);
                        form.Shown += async (s, e) =>
                        {
                            try
                            {
                                await WaitUntilAsync(() => (bool)Get(view, "_ready") && (long)Get(view, "_sentRevision") == Revision(view));
                                var core = (Microsoft.Web.WebView2.Core.CoreWebView2)Get(view, "_core");
                                Assert.AreEqual("2", await core.ExecuteScriptAsync("document.getElementById('transcript').children.length"));
                                view.Visible = false;
                                await WaitUntilAsync(() => !(bool)Get(view, "_suspending"));
                                Assert.IsFalse((bool)Get(view, "_shown"));
                                Assert.IsTrue(core.IsSuspended || core.MemoryUsageTargetLevel == Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low,
                                    "A runtime may decline suspension, but the hidden view must retain its low-memory target.");
                                long sent = (long)Get(view, "_sentRevision");
                                view.Render(Transcript("first", "second", "latest"), true);
                                Assert.AreEqual(sent, (long)Get(view, "_sentRevision"));
                                view.Visible = true;
                                await WaitUntilAsync(() => !core.IsSuspended && (long)Get(view, "_sentRevision") == Revision(view));
                                Assert.AreEqual("3", await core.ExecuteScriptAsync("document.getElementById('transcript').children.length"));
                                StringAssert.Contains(await core.ExecuteScriptAsync("document.getElementById('transcript').lastElementChild.textContent"), "latest");
                            }
                            catch (Exception ex) { failure = ex; }
                            finally { form.Close(); }
                        };
                        Application.Run(form);
                        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                    finally
                    {
                        TranscriptView.UserDataFolder = profile;
                        environment.SetValue(null, previous);
                    }
                }
            });
        }

        private static async System.Threading.Tasks.Task WaitUntilAsync(Func<bool> predicate)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(15)) Assert.Fail("Browser transition timed out.");
                await System.Threading.Tasks.Task.Delay(50);
            }
        }

        private static ChatTranscript Transcript(params string[] texts)
        {
            var transcript = new ChatTranscript();
            foreach (string text in texts)
                transcript.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Parts = new List<ChatPart> { new ChatPart { Text = text } } });
            return transcript;
        }

        private static string Payload(TranscriptView view) => (string)Invoke(view, "BuildUpdate", Get(view, "_latest"), Revision(view), Get(view, "_done"), Get(view, "_activity"), Get(view, "_pending"));

        private static Dictionary<string, object> Update(TranscriptView view)
        {
            object parser = Get(view, "_json");
            return (Dictionary<string, object>)parser.GetType().GetMethod("DeserializeObject").Invoke(parser, new object[] { Payload(view) });
        }

        // Simulate a successful post without launching WebView2 or accessing its real profile.
        private static void Commit(TranscriptView view)
        {
            Set(view, "_sent", Get(view, "_latest"));
            Set(view, "_sentRevision", Revision(view));
            Set(view, "_sentBot", view.AssistantLabel ?? "Copilot");
        }

        private static object[] Messages(Dictionary<string, object> update) => (object[])update["messages"];
        private static IList Sources(TranscriptView view) => (IList)Get(Get(view, "_latest"), "Messages");
        private static long Revision(TranscriptView view) => (long)Get(view, "_revision");
        private static object Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);

        private static void OnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "STA test timed out.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
