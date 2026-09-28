using System;
using System.Threading.Tasks;

namespace VSManager
{
    public partial class MainForm : IManualChatDispatchHost, IManualChatRefreshHost
    {
        private ManualChatProbeCache _manualProbes;
        void IManualChatRefreshHost.RefreshManualChat(VsInstance target) => _manualProbes?.Invalidate(target);

        Task<ManualChatObservation> IManualChatDispatchHost.ObserveManualChatAsync(VsInstance target)
        {
            if (!_settings.WaitForManualChat) { _manualProbes?.Clear(); return Task.FromResult(ManualChatObservation.Idle); }
            if (_manualProbes == null)
                _manualProbes = new ManualChatProbeCache(v => DteWorker.RunSta(() => new CopilotChat(() => _settings).ObserveManualChat(v)));
            return Task.FromResult(_manualProbes.Read(target, _instances));
        }

        Task<string> IManualChatDispatchHost.SendQueuedAsync(VsInstance target, QueuedTask task, Func<bool> stillValid)
        {
            if (task.HasExplicitTarget) return SendMentionedQueuedAsync(target, task, stillValid);
            Func<bool> guard = () => CheckQueueSendValid(stillValid);
            return task.HasAttachments ? SendTaskCore(target, task, guard)
                : SendChatCore(target, TaskStateMachine.DispatchText(task), queueGuard: guard);
        }

        private async Task<string> SendMentionedQueuedAsync(VsInstance target, QueuedTask task, Func<bool> stillValid)
        {
            bool live = await DteWorker.RunSta(() => task.MatchesExplicitTarget(target)
                && MentionTargetStillLive(target, task.ExplicitSolutionPath, true));
            if (!live) return VsMentionSession.MissingError;
            bool targetChanged = false;
            Func<bool> guard = () =>
            {
                if (!task.MatchesExplicitTarget(target) || !MentionTargetStillLive(target, task.ExplicitSolutionPath, true))
                {
                    targetChanged = true;
                    return false;
                }
                return CheckQueueSendValid(stillValid);
            };
            string result = await (task.HasAttachments ? SendTaskCore(target, task, guard)
                : SendChatCore(target, TaskStateMachine.DispatchText(task), queueGuard: guard));
            // 仅在发送器确认尚未提交时替换原因；不掩盖送达不确定性。/ Replace the reason only when the sender confirms no submission; preserve delivery uncertainty.
            return targetChanged && ManualChatProtection.IsWait(result) ? VsMentionSession.MissingError : result;
        }

        internal bool CheckQueueSendValid(Func<bool> stillValid)
        {
            // 界面线程直接检查，不能等待排给自身的回调。/ Check directly on the UI thread; never wait for a callback queued to itself.
            if (!InvokeRequired) return !IsDisposed && IsHandleCreated && stillValid();
            return OnUi(() => !IsDisposed && stillValid()).GetAwaiter().GetResult();
        }
    }
}
