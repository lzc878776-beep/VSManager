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
            Func<bool> guard = () => OnUi(() => !IsDisposed && stillValid()).GetAwaiter().GetResult();
            return task.HasAttachments ? SendTaskCore(target, task, guard)
                : SendChatCore(target, TaskStateMachine.DispatchText(task), queueGuard: guard);
        }
    }
}
