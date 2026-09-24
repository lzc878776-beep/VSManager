using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ManualChatQueueGuardTests
    {
        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        [Timeout(15000)]
        public void UiThread_CheckReturnsWithoutPumping(bool valid)
        {
            RunSta(form =>
            {
                var handle = form.Handle;
                int calls = 0;
                bool posted = false;
                form.BeginInvoke(new Action(() => posted = true));
                Assert.AreEqual(valid, form.CheckQueueSendValid(() =>
                {
                    calls++;
                    Assert.IsFalse(form.InvokeRequired);
                    return valid;
                }));
                Assert.AreEqual(1, calls);
                Assert.IsFalse(posted, "检查不得泵送消息 / The check must not pump messages");
            });
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        [Timeout(15000)]
        public void WorkerThread_CheckMarshalsPredicateToUi(bool valid)
        {
            RunSta(form =>
            {
                var handle = form.Handle;
                int uiThread = Thread.CurrentThread.ManagedThreadId;
                int calls = 0;
                RunWorkerWithPump(form, () =>
                {
                    Assert.IsTrue(form.InvokeRequired);
                    Assert.AreEqual(valid, form.CheckQueueSendValid(() =>
                    {
                        Assert.AreEqual(uiThread, Thread.CurrentThread.ManagedThreadId);
                        calls++;
                        return valid;
                    }));
                });
                Assert.AreEqual(1, calls);
            });
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        [Timeout(15000)]
        public void MissingOrDisposedHandle_NeverRunsPredicate(bool disposed, bool worker)
        {
            RunSta(form =>
            {
                if (disposed) { var handle = form.Handle; form.Dispose(); }
                int calls = 0;
                Action check = () =>
                {
                    Assert.IsFalse(form.IsHandleCreated);
                    Assert.IsFalse(form.CheckQueueSendValid(() => { Interlocked.Increment(ref calls); return true; }));
                    Assert.IsFalse(form.IsHandleCreated);
                };
                if (worker) RunWorkerWithoutPump(check);
                else check();
                Assert.AreEqual(0, calls, "失效窗口不得检查或发送 / An invalid window must not check or send");
            });
        }

        [TestMethod]
        [Timeout(15000)]
        public void WorkerThread_PredicateExceptionPropagatesWithoutHanging()
        {
            RunSta(form =>
            {
                var handle = form.Handle;
                var expected = new InvalidOperationException("隔离检查失败 / Isolated check failure");
                RunWorkerWithPump(form, () =>
                    Assert.AreSame(expected, Assert.ThrowsException<InvalidOperationException>(
                        () => form.CheckQueueSendValid(() => throw expected))));
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        [Timeout(15000)]
        public void SendQueuedAsync_InvalidGuardReachesUiPreSendCheckAndNeverSends(bool attachments)
        {
            RunSta(form =>
            {
                var settings = new AppSettings { WaitForManualChat = false, CloseVsDocumentsBeforeSend = false };
                var target = new VsInstance { Pid = int.MaxValue, Key = "isolated-queue-target", Title = "隔离目标 / Isolated target" };
                var chatService = new CopilotChat(() => settings);
                using (var chat = new ChatPanel())
                using (var list = new VsListBox())
                using (var status = new System.Windows.Forms.Label())
                using ((AutoResetEvent)typeof(CopilotChat).GetField("_wake", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(chatService))
                {
                    SetField(form, "_settings", settings);
                    SetField(form, "_instances", new List<VsInstance> { target });
                    SetField(form, "_chat", chat);
                    SetField(form, "_list", list);
                    SetField(form, "_status", status);
                    SetField(form, "_chatSvc", chatService);
                    var handle = form.Handle;
                    var task = new QueuedTask { Id = 1, VsKey = target.Key, Text = "隔离任务 / Isolated task" };
                    if (attachments)
                        task.Attachments = new[] { new AttachmentRef { Name = "isolated.bin", RelPath = "isolated.bin" } };
                    int calls = 0;
                    bool posted = false;
                    form.BeginInvoke(new Action(() => posted = true));
                    // 真实接口经过 SendTaskCore/SendChatCore，在发送前的 UI 检查处退出，绝不调用真实 VS。
                    // The real interface reaches SendTaskCore/SendChatCore and exits at the UI pre-send check, never touching VS.
                    var send = ((IManualChatDispatchHost)form).SendQueuedAsync(target, task, () =>
                    {
                        Assert.IsFalse(form.InvokeRequired);
                        Assert.IsTrue(((ITaskDispatchHost)form).IsSending);
                        Assert.IsTrue(chatService.Paused);
                        calls++;
                        return false;
                    });
                    Assert.IsTrue(send.IsCompleted, "发送前检查不得等待 UI 自身 / The pre-send check must not wait on its own UI");
                    Assert.IsTrue(ManualChatProtection.IsWait(send.GetAwaiter().GetResult()));
                    Assert.AreEqual(1, calls);
                    Assert.IsFalse(posted);
                    Assert.IsFalse(((ITaskDispatchHost)form).IsSending);
                    Assert.IsFalse(chatService.Paused);
                }
            });
        }

        // 只运行 Form 基类构造器，跳过 MainForm 的设置加载、服务启动、热键及退出副作用。
        // Run only the Form base constructor, skipping MainForm settings, services, hotkeys and shutdown side effects.
        private sealed class IsolatedMainForm : MainForm
        {
            protected override void OnHandleCreated(EventArgs e) { }
            protected override void OnShown(EventArgs e) { }
            protected override void OnFormClosing(FormClosingEventArgs e) { }
        }

        private static MainForm CreateForm()
        {
            var form = (MainForm)FormatterServices.GetUninitializedObject(typeof(IsolatedMainForm));
            var initialize = new DynamicMethod("InitializeIsolatedForm", typeof(void), new[] { typeof(Form) }, typeof(ManualChatQueueGuardTests), true);
            var il = initialize.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(Form).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Ret);
            ((Action<Form>)initialize.CreateDelegate(typeof(Action<Form>)))(form);
            return form;
        }

        private static void SetField(MainForm form, string name, object value) =>
            typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value);

        private static void RunSta(Action<MainForm> action)
        {
            Exception failure = null;
            using (var data = new TempDataFolder())
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        using (var form = CreateForm()) action(form);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { Application.ExitThread(); }
                }) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                JoinOwnedThread(thread);
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void RunWorkerWithoutPump(Action action)
        {
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            worker.Start();
            JoinOwnedThread(worker);
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void RunWorkerWithPump(MainForm form, Action action)
        {
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { form.BeginInvoke(new Action(Application.ExitThread)); }
                    catch (InvalidOperationException) { }
                }
            }) { IsBackground = true };
            form.BeginInvoke(new Action(worker.Start));
            try { Application.Run(); }
            finally { JoinOwnedThread(worker); }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void JoinOwnedThread(Thread thread)
        {
            if (thread.Join(TimeSpan.FromSeconds(5))) return;
            // 旧回归会阻塞托管等待；中断本测试线程使 finally 释放窗口，再以超时失败。
            // The old regression blocks a managed wait; interrupt only our thread so finally releases its window, then fail.
            thread.Interrupt();
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                thread.Abort();
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(2)), "测试线程未退出 / Test thread did not exit");
            }
            Assert.Fail("队列检查死锁或超时 / Queue check deadlocked or timed out");
        }
    }
}
