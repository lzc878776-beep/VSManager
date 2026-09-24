using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager.Tests
{
    /// <summary>
    /// 临时数据目录：测试期间把用户数据重定向到测试输出下的独立目录，结束后删除，绝不触碰真实用户数据。
    /// Temporary data folder: redirects user data to an isolated directory under the test output and deletes it afterwards.
    /// </summary>
    internal sealed class TempDataFolder : IDisposable
    {
        private readonly IDisposable _override;
        public string Path { get; }

        public TempDataFolder()
        {
            Path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            _override = AppPaths.OverrideDataFolder(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            _override.Dispose();
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    /// <summary>内存中的任务存储，可模拟保存失败。/ In-memory task store that can simulate save failures.</summary>
    internal sealed class MemoryTaskStore : ITaskStore
    {
        public List<QueuedTask> Initial = new List<QueuedTask>();
        public List<string> InitialProblems = new List<string>();
        public List<QueuedTask> Saved;
        public int SaveCount;
        public string FailWith;

        public List<QueuedTask> Load(List<string> problems)
        {
            problems.AddRange(InitialProblems);
            return Initial.Select(t => t.Clone()).ToList();
        }

        public string Save(IList<QueuedTask> items)
        {
            SaveCount++;
            if (FailWith != null) return FailWith;
            Saved = items.Select(t => t.Clone()).ToList();
            return null;
        }
    }

    /// <summary>记录归档事件。/ Records archive events.</summary>
    internal sealed class RecordingArchive : ITaskArchiveSink
    {
        public readonly List<string> Events = new List<string>();
        public void TaskEvent(QueuedTask task, string evt) => Events.Add("#" + task.Id + ":" + evt);
    }

    /// <summary>可控时钟。/ Controllable clock.</summary>
    internal sealed class FakeClock
    {
        public DateTime Now = new DateTime(2026, 1, 1, 9, 0, 0);
        public void Advance(TimeSpan d) => Now += d;
        public Func<DateTime> Func => () => Now;
    }

    /// <summary>任务调度器的模拟宿主。/ Fake host for the task dispatcher.</summary>
    internal sealed class FakeDispatchHost : ITaskDispatchHost, IManualChatDispatchHost
    {
        public Func<VsInstance, Task<ManualChatObservation>> ManualReader;
        public Func<VsInstance, QueuedTask, Func<bool>, Task<string>> GuardedSender;
        public int ManualReads;
        public Task<ManualChatObservation> ObserveManualChatAsync(VsInstance target)
        {
            ManualReads++;
            return ManualReader?.Invoke(target) ?? Task.FromResult(ManualChatObservation.Idle);
        }
        public Task<string> SendQueuedAsync(VsInstance target, QueuedTask task, Func<bool> valid) => GuardedSender != null
            ? GuardedSender(target, task, valid) : !valid() ? Task.FromResult(ManualChatProtection.WaitPrefix)
            : SendAsync(target, TaskStateMachine.DispatchText(task));
        public readonly Dictionary<string, VsInstance> Vs = new Dictionary<string, VsInstance>();
        public readonly HashSet<string> Busy = new HashSet<string>();
        public readonly Queue<string> SendResults = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public readonly List<string> Status = new List<string>();
        public readonly List<string> Notices = new List<string>();
        public readonly List<string> NoticeBodies = new List<string>();
        public readonly List<string> Events = new List<string>();
        public string Answer = "完成了";
        public bool IncludeSuccessReceipt = true;
        public Func<QueuedTask, Task<string>> AnswerReader;
        public Exception SendException;
        public bool Sending;
        public DateTime ReadyAt = DateTime.MinValue;
        public bool? LastActivity;

        public VsInstance AddVs(string key, CopilotState state = CopilotState.Idle)
        {
            var v = new VsInstance { Pid = Vs.Count + 100, Key = key, Title = key, Copilot = state };
            Vs[key] = v;
            return v;
        }

        public VsInstance FindVs(string vsKey) => Vs.TryGetValue(vsKey, out var v) ? v : null;
        public bool CanDispatch(VsInstance v) => v.Copilot != CopilotState.Busy && !Busy.Contains(v.Key);
        public string NameOf(VsInstance v) => v.Key;
        public bool IsSending => Sending;
        public DateTime TrackingReadyAt => ReadyAt;

        public Task<string> SendAsync(VsInstance v, string text)
        {
            Sent.Add(v.Key + ":" + text);
            if (SendException != null) throw SendException;
            return Task.FromResult(SendResults.Count > 0 ? SendResults.Dequeue() : "已发送");
        }

        public Task<string> ReadAnswerAsync(VsInstance v, QueuedTask expectedTask) => AnswerReader != null
            ? AnswerReader(expectedTask)
            : Task.FromResult(IncludeSuccessReceipt && !string.IsNullOrWhiteSpace(Answer)
                ? Answer + "\r\n" + TaskStateMachine.SuccessReceipt(expectedTask) : Answer);
        public void SetStatus(string text) => Status.Add(text);
        public void LogEvent(string vsName, string text) => Events.Add(vsName + ":" + text);
        public void NotifyAgent(string title, string body)
        {
            Notices.Add(title);
            NoticeBodies.Add(body);
        }
        public void QueueActivityChanged(bool anyActive) => LastActivity = anyActive;
        public TimeSpan Settle = TimeSpan.Zero;
        public readonly List<string> Announced = new List<string>();
        public VsInstance FindTargetVs(QueuedTask t) => FindVs(t.VsKey);
        public TimeSpan TargetSettleDelay => Settle;
        public void AnnounceTask(QueuedTask t, string zh, string en) => Announced.Add(zh + " / " + en);
    }
}
