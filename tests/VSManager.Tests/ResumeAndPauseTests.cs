using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AIRole = Microsoft.Extensions.AI.ChatRole;

namespace VSManager.Tests
{
    /// <summary>重开接续 AI 对话、任务队列暂停 / 中断与任务记录恢复。/ Resuming the AI conversation, pausing / interrupting the queue and restoring task records.</summary>
    [TestClass]
    public class ResumeAndPauseTests
    {
        private TempDataFolder _data;
        private string _oldChatPath;

        [TestInitialize]
        public void Init()
        {
            _data = new TempDataFolder();
            _oldChatPath = AgentChatLog.FilePath;
            AgentChatLog.FilePath = _data.File(AgentChatLog.FileName);
        }

        [TestCleanup]
        public void Cleanup()
        {
            AgentChatLog.FilePath = _oldChatPath;
            _data.Dispose();
        }

        private static AgentChatRecord Rec(string role, string text, string detail = null, bool local = false, bool reset = false, params string[] steps) =>
            new AgentChatRecord { Time = DateTime.Now, Role = role, Text = text, Detail = detail, Local = local, Reset = reset, Steps = steps.ToList() };

        private static AgentService NewAgent() => new AgentService(null, () => new AppSettings());

        [TestMethod]
        public void ChatLog_RoundTripsLocalAndResetFlags()
        {
            AgentChatLog.Append(AgentChatLog.RoleNotice, "local", "d", local: true);
            AgentChatLog.Append(AgentChatLog.RoleNotice, AgentService.NewConversationMarker, reset: true);
            AgentChatLog.Append(AgentChatLog.RoleUser, "plain");
            var all = AgentChatLog.ReadAll();
            Assert.AreEqual(3, all.Count);
            Assert.IsTrue(all[0].Local && !all[0].Reset);
            Assert.IsTrue(all[1].Reset && !all[1].Local);
            Assert.IsFalse(all[2].Local || all[2].Reset);
        }

        [TestMethod]
        public void Restore_ResumesOnlyAfterLatestNewConversation()
        {
            var records = new List<AgentChatRecord>
            {
                Rec("user", "old question"), Rec("assistant", "old answer"),
                Rec("notice", AgentService.NewConversationMarker, reset: true),
                Rec("user", "question"),
                Rec("notice", "📋 任务 #3 完成", "[任务通知] full body"),
                Rec("notice", "local only", "local body", local: true),
                Rec("notice", "⟳ AI 助手已重启 / AI assistant restarted：x"),
                Rec("assistant", "answer", null, false, false, "⚙ 查看任务清单", "↳ 完成")
            };
            using (var agent = NewAgent())
            {
                Assert.AreEqual(5, agent.RestoreConversation(records));
                var msgs = agent.Transcript.Messages;
                Assert.AreEqual(6, msgs.Count, "5 条记录 + 接续提示 / 5 records + resumed notice");
                Assert.AreEqual(ChatRole.User, msgs[0].Role);
                Assert.AreEqual("question", msgs[0].Parts[0].Text);
                Assert.AreEqual(2, msgs[4].Parts.Count(p => p.IsStep));
                Assert.AreEqual(AgentService.ResumedNotice, msgs[5].Parts[0].Text);
                Assert.IsFalse(msgs.Any(m => m.Parts.Any(p => p.Text.Contains("old"))));

                var history = Restored(agent);
                Assert.AreEqual(3, history.Count, "本机通知与重启说明不进上下文 / local and restart notices stay out");
                Assert.AreEqual(AIRole.User, history[0].Role);
                Assert.AreEqual("question", history[0].Text);
                Assert.AreEqual("[任务通知] full body", history[1].Text);
                Assert.AreEqual(AIRole.Assistant, history[2].Role);
                StringAssert.StartsWith(history[2].Text, "answer");
                Assert.IsFalse(history[2].Text.Contains("⚙"), "工具步骤不以文字进入上下文 / tool steps never enter the context as text");
            }
        }

        /// <summary>去掉开头的恢复说明后的上下文。/ Context without the leading restore note.</summary>
        private static List<Microsoft.Extensions.AI.ChatMessage> Restored(AgentService agent)
        {
            var h = agent.HistoryForTests;
            Assert.AreEqual(AgentService.RestoredContextNote, h[0].Text, "恢复的上下文先说明来源 / the restored context starts with the note");
            return h.Skip(1).ToList();
        }

        [TestMethod]
        public void Restore_ModelContext_KeepsOnlyRecentTail()
        {
            var records = new List<AgentChatRecord>();
            for (int i = 0; i < 60; i++) { records.Add(Rec("user", "q" + i)); records.Add(Rec("assistant", "a" + i)); }
            using (var agent = NewAgent())
            {
                Assert.AreEqual(120, agent.RestoreConversation(records));
                Assert.AreEqual(121, agent.Transcript.Messages.Count, "界面显示全部 / the transcript shows everything");
                var h = Restored(agent);
                Assert.AreEqual(AgentService.RestoredContextRecords, h.Count);
                Assert.AreEqual(AIRole.User, h[0].Role);
                Assert.AreEqual("a59", h.Last().Text);
            }
        }

        [TestMethod]
        public void ChatLog_RoundTripsToolCalls()
        {
            AgentChatLog.Append(AgentChatLog.RoleAssistant, "ok", steps: new[] { "⚙ x" },
                calls: new List<AgentToolCall> { new AgentToolCall { Name = "send_task", Args = "{\"vs\":\"1\"}", Result = "已加入任务清单：@7" } });
            var r = AgentChatLog.ReadAll().Single();
            Assert.AreEqual(1, r.Calls.Count);
            Assert.AreEqual("send_task", r.Calls[0].Name);
            Assert.AreEqual("{\"vs\":\"1\"}", r.Calls[0].Args);
            Assert.AreEqual("已加入任务清单：@7", r.Calls[0].Result);
        }

        [TestMethod]
        public void Restore_RecordedCalls_BecomeStructuredToolMessages()
        {
            var answer = Rec("assistant", "已发布 @7", null, false, false, "⚙ 发布任务", "↳ 已加入任务清单：@7");
            answer.Calls.Add(new AgentToolCall { Name = "send_task", Args = "{\"vs\":\"1\",\"text\":\"fix\"}", Result = "已加入任务清单：@7" });
            using (var agent = NewAgent())
            {
                agent.RestoreConversation(new List<AgentChatRecord> { Rec("user", "发布"), answer });
                var h = Restored(agent);
                Assert.AreEqual(4, h.Count);
                var call = h[1].Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>().Single();
                Assert.AreEqual("send_task", call.Name);
                Assert.AreEqual("1", call.Arguments["vs"].ToString());
                Assert.AreEqual(AIRole.Tool, h[2].Role);
                var result = h[2].Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().Single();
                Assert.AreEqual(call.CallId, result.CallId);
                Assert.AreEqual("已发布 @7", h[3].Text);
            }
        }

        [TestMethod]
        public void Restore_FabricatedReply_IsReplacedByDiscardNote()
        {
            var fake = Rec("assistant", "已发布 @149\n〔工具记录：↳ 已加入任务清单：@149〕", null, false, false, "⚠ 核查未通过：@149 不在任务清单中");
            using (var agent = NewAgent())
            {
                agent.RestoreConversation(new List<AgentChatRecord> { Rec("user", "发布"), fake });
                var h = Restored(agent);
                Assert.AreEqual(2, h.Count);
                Assert.AreEqual(ToolClaimCheck.DiscardedReply, h[1].Text);
                Assert.IsFalse(h.Any(m => m.Text.Contains("@149")));
            }
        }

        [TestMethod]
        public async Task LiveRound_RecordsRealToolCalls()
        {
            var settings = new AppSettings { AgentEndpoint = "http://localhost:11434/v1", AgentModel = "test", AgentConfirm = false };
            var host = new AgentDesktopTests.DesktopHost();
            host.Queue = new TaskQueue(settings, new MemoryTaskStore(), new RecordingArchive(), () => DateTime.Now);
            using (var agent = new AgentService(host, () => settings, new ToolCallingClient()))
            {
                await agent.RunAsync("看看清单");
            }
            var r = AgentChatLog.ReadAll().Last(x => x.Role == AgentChatLog.RoleAssistant);
            Assert.AreEqual(1, r.Calls.Count, string.Join("|", r.Steps));
            Assert.AreEqual("list_tasks", r.Calls[0].Name);
            Assert.IsNotNull(r.Calls[0].Result);
        }

        /// <summary>第一次请求调用 list_tasks，之后回复文字。/ Calls list_tasks on the first request, then replies with text.</summary>
        private sealed class ToolCallingClient : IAiClientFactory, Microsoft.Extensions.AI.IChatClient
        {
            private int _requests;
            public Microsoft.Extensions.AI.IChatClient Create(Uri endpoint, string model, string apiKey) => this;
            public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions options = null, System.Threading.CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                if (_requests++ == 0)
                    yield return new Microsoft.Extensions.AI.ChatResponseUpdate(AIRole.Assistant, new List<Microsoft.Extensions.AI.AIContent> { new Microsoft.Extensions.AI.FunctionCallContent("c1", "list_tasks", new Dictionary<string, object>()) });
                else
                    yield return new Microsoft.Extensions.AI.ChatResponseUpdate(AIRole.Assistant, "done");
            }
            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }

        [TestMethod]
        public void Restore_UnansweredLastRound_EndsWithInterruptionNote()
        {
            using (var agent = NewAgent())
            {
                agent.RestoreConversation(new List<AgentChatRecord> { Rec("user", "q1"), Rec("assistant", "a1"), Rec("user", "q2") });
                var last = agent.HistoryForTests.Last();
                Assert.AreEqual(AIRole.Assistant, last.Role);
                StringAssert.Contains(last.Text, "VSManager");
            }
        }

        [TestMethod]
        public void Restore_NothingAfterMarker_KeepsEmptyConversation()
        {
            using (var agent = NewAgent())
            {
                Assert.AreEqual(0, agent.RestoreConversation(new List<AgentChatRecord> { Rec("user", "q"), Rec("notice", AgentService.NewConversationMarker, reset: true) }));
                Assert.AreEqual(0, agent.Transcript.Messages.Count);
                Assert.AreEqual(0, agent.HistoryForTests.Count);
            }
        }

        [TestMethod]
        public void NewConversation_WritesMarker_SoNextReopenStartsFresh()
        {
            AgentChatLog.Append(AgentChatLog.RoleUser, "before");
            AgentChatLog.Append(AgentChatLog.RoleAssistant, "reply");
            using (var agent = NewAgent())
            {
                Assert.AreEqual(2, agent.RestoreConversation());
                agent.Clear();
            }
            Assert.IsTrue(AgentChatLog.ReadAll().Last().Reset);
            using (var agent = NewAgent())
                Assert.AreEqual(0, agent.RestoreConversation());
            using (var agent = NewAgent())
            {
                agent.Clear();
                Assert.AreEqual(1, AgentChatLog.ReadAll().Count(r => r.Reset), "空对话清空不重复写标记 / clearing an empty conversation writes no extra marker");
            }
        }

        private (TaskQueue Queue, FakeDispatchHost Host, TaskDispatcher Dispatcher) NewDispatcher()
        {
            var clock = new FakeClock();
            var queue = new TaskQueue(new AppSettings(), new MemoryTaskStore(), new RecordingArchive(), clock.Func);
            var host = new FakeDispatchHost();
            var dispatcher = new TaskDispatcher(queue, host, clock.Func);
            dispatcher.Start();
            return (queue, host, dispatcher);
        }

        [TestMethod]
        public async Task Pause_StopsPublishing_KeepsQueue_ResumePublishes()
        {
            var (queue, host, dispatcher) = NewDispatcher();
            host.AddVs("A");
            var t = queue.Add("A", "A", "task", "AI");
            dispatcher.SetPaused(true);
            await dispatcher.PumpAsync();
            Assert.AreEqual(0, host.Sent.Count);
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
            StringAssert.Contains(dispatcher.StartModeText, TaskDispatcher.PausedText);
            StringAssert.Contains(dispatcher.StartStateText(t), "Queue paused");

            dispatcher.SetPaused(false);
            await dispatcher.PumpAsync();
            Assert.AreEqual(1, host.Sent.Count);
            Assert.AreEqual(QueueStatus.Running, t.Status);
        }

        [TestMethod]
        public async Task Pause_RunningTaskStillCompletes_SuccessorWaits()
        {
            var (queue, host, dispatcher) = NewDispatcher();
            var v = host.AddVs("A");
            var first = queue.Add("A", "A", "first", "AI");
            var second = queue.Add("A", "A", "second", "AI");
            await dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Running, first.Status);
            dispatcher.SetPaused(true);
            await dispatcher.FinishAsync(first, v, TimeSpan.FromSeconds(5));
            await dispatcher.PumpAsync();
            Assert.AreEqual(QueueStatus.Done, first.Status);
            Assert.AreEqual(QueueStatus.Waiting, second.Status, "暂停后不发布下一项 / the next task waits while paused");
            Assert.AreEqual(1, host.Sent.Count);
        }

        [TestMethod]
        public async Task Interrupt_RequeuesRunningTask_AndResendAsksToContinue()
        {
            var (queue, host, dispatcher) = NewDispatcher();
            host.AddVs("A");
            var t = queue.Add("A", "A", "big task", "AI");
            await dispatcher.PumpAsync();
            string firstToken = t.CompletionToken;
            dispatcher.SetPaused(true);
            Assert.IsTrue(dispatcher.Interrupt(t));
            Assert.AreEqual(QueueStatus.Waiting, t.Status);
            Assert.IsTrue(t.Interrupted);
            Assert.IsFalse(dispatcher.Interrupt(t), "只能中断执行中的任务 / only running tasks can be interrupted");
            await dispatcher.PumpAsync();
            Assert.AreEqual(1, host.Sent.Count);

            dispatcher.SetPaused(false);
            await dispatcher.PumpAsync();
            Assert.AreEqual(2, host.Sent.Count);
            StringAssert.Contains(host.Sent[1], TaskStateMachine.InterruptedNote);
            Assert.AreEqual(QueueStatus.Running, t.Status);
            Assert.IsFalse(t.Interrupted, "送达后清除 / cleared once delivered");
            Assert.AreNotEqual(firstToken, t.CompletionToken, "旧回执不会被当作新结果 / the old receipt cannot complete the resend");
        }

        [TestMethod]
        public void Store_RoundTripsReleasedAndInterrupted()
        {
            string path = _data.File("tasks.json");
            var store = new JsonTaskStore(path);
            var failed = new QueuedTask { Id = 1, VsKey = "A", VsName = "A", Text = "failed", Source = "AI", Status = QueueStatus.Failed, Created = DateTime.Now, Finished = DateTime.Now, Released = true };
            var waiting = new QueuedTask { Id = 2, VsKey = "A", VsName = "A", Text = "interrupted", Source = "AI", Status = QueueStatus.Waiting, Created = DateTime.Now, Interrupted = true };
            Assert.IsNull(store.Save(new List<QueuedTask> { failed, waiting }));
            var back = new JsonTaskStore(path).Load(new List<string>());
            Assert.IsTrue(back[0].Released, "已放行的失败重开后不再阻塞 / a released failure stays released after reopening");
            Assert.IsTrue(back[1].Interrupted);
            Assert.AreEqual(QueueStatus.Waiting, back[1].Status);
        }

        [TestMethod]
        public void Reopen_KeepsUnfinishedAndQueuedTasks()
        {
            string path = _data.File("tasks.json");
            var settings = new AppSettings { ReleaseLevel = ReleaseLevel.Failed };
            var queue = new TaskQueue(settings, new JsonTaskStore(path), new RecordingArchive(), () => DateTime.Now);
            var released = queue.Add("A", "A", "failed but released", "AI");
            var running = queue.Add("A", "A", "running", "AI");
            var sending = queue.Add("B", "B", "sending", "AI");
            var waiting = queue.Add("A", "A", "waiting", "用户");
            var parked = queue.AddParked(@"%USERPROFILE%\src\Other\Other.sln", "Other", "parked", "AI");
            TaskStateMachine.Fail(released, "boom", DateTime.Now);
            released.Released = true;
            running.Status = QueueStatus.Running;
            sending.Status = QueueStatus.Sending;
            queue.Commit();

            var reopened = new TaskQueue(settings, new JsonTaskStore(path), new RecordingArchive(), () => DateTime.Now);
            Assert.AreEqual(5, reopened.Items.Count);
            Assert.AreEqual(QueueStatus.Running, reopened.Find(running.Id).Status);
            Assert.AreEqual(QueueStatus.Waiting, reopened.Find(sending.Id).Status, "发送中恢复为排队 / sending goes back to waiting");
            Assert.AreEqual(QueueStatus.Waiting, reopened.Find(waiting.Id).Status);
            Assert.AreEqual(QueueStatus.WaitingVs, reopened.Find(parked.Id).Status);
            Assert.IsTrue(reopened.Find(released.Id).Released);
            Assert.AreNotSame(reopened.Find(released.Id), reopened.BlockingTask(reopened.Find(waiting.Id)), "已放行的失败不阻塞 / released failure does not block");
        }
    }
}
