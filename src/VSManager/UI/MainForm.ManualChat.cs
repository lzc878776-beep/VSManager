using System;
using System.Threading.Tasks;

namespace VSManager
{
    public partial class MainForm : IManualChatDispatchHost
    {
        private ManualChatProbeCache _manualProbes;

        Task<ManualChatObservation> IManualChatDispatchHost.ObserveManualChatAsync(VsInstance target)
        {
            if (!_settings.WaitForManualChat) { _manualProbes?.Clear(); return Task.FromResult(ManualChatObservation.Idle); }
            if (_manualProbes == null)
                _manualProbes = new ManualChatProbeCache(v => DteWorker.RunSta(() => new CopilotChat(() => _settings).ObserveManualChat(v)));
            return Task.FromResult(_manualProbes.Read(target, _instances));
        }

        Task<string> IManualChatDispatchHost.SendQueuedAsync(VsInstance target, QueuedTask task, Func<bool> stillValid)
        {
            Func<bool> guard = () => CheckQueueSendValid(stillValid);
            return task.HasAttachments ? SendTaskCore(target, task, guard)
                : SendChatCore(target, TaskStateMachine.DispatchText(task), queueGuard: guard);
        }

        internal bool CheckQueueSendValid(Func<bool> stillValid)
        {
            // 界面线程直接检查，不能等待排给自身的回调。/ Check directly on the UI thread; never wait for a callback queued to itself.
            if (!InvokeRequired) return !IsDisposed && IsHandleCreated && stillValid();
            return OnUi(() => !IsDisposed && stillValid()).GetAwaiter().GetResult();
        }
    }
}
