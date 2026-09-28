using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    public sealed partial class TaskDispatcher
    {
        private sealed class ManualWait
        {
            internal VsInstance Target;
            internal string Path;
            internal string Identity;
            internal DateTime Since;
            internal bool TimedOut;
        }
        private readonly Dictionary<QueuedTask, ManualWait> _manualWaits = new Dictionary<QueuedTask, ManualWait>();
        private readonly HashSet<QueuedTask> _yielded = new HashSet<QueuedTask>();
        public bool HasYieldedToManualChat(QueuedTask task) => task != null && _yielded.Contains(task);
        private bool WaitForManualChat => _startSettings?.Invoke()?.WaitForManualChat ?? true;

        private bool SameTarget(QueuedTask task, VsInstance target, string path, string identity = null)
        {
            var current = ResolveTarget(task, true);
            return ReferenceEquals(current, target) && (identity == null || target.InstanceKey == identity)
                && string.Equals(target.SolutionPath, path, StringComparison.OrdinalIgnoreCase);
        }

        private void ClearManualWait(QueuedTask task)
        {
            _manualWaits.Remove(task);
            if (task.ManualChatWaitReason == null) return;
            task.ManualChatWaitReason = null;
            _host.QueueActivityChanged(HasDispatchActivity);
        }

        private void PruneManualWaits()
        {
            foreach (var entry in _manualWaits.ToArray())
                if (!WaitForManualChat || entry.Key.Status != QueueStatus.Waiting || !CanRun(entry.Key) || DispatchBlocker(entry.Key) != null
                    || !SameTarget(entry.Key, entry.Value.Target, entry.Value.Path, entry.Value.Identity)) ClearManualWait(entry.Key);
            _yielded.RemoveWhere(t => _tasks.Find(t.Id) != t || t.Status == QueueStatus.Cancelled || t.Status == QueueStatus.Failed);
        }

        private void RecordManualWait(QueuedTask task, VsInstance target, ManualChatObservation observation, bool probeFailed = false)
        {
            if (!_manualWaits.TryGetValue(task, out var wait))
            {
                wait = new ManualWait { Target = target, Path = target.SolutionPath, Identity = target.InstanceKey, Since = _clock() };
                _manualWaits[task] = wait;
                AppLog.Write(AppLog.TasksFile, $"任务 #{task.Id} 开始等待，尚未发送 / Waiting entered; not sent: {observation}");
                _host.AnnounceTask(task, $"任务 {task.Id} 等待目标手动对话结束，尚未发送", $"Task {task.Id} is waiting for the target's manual chat; not sent");
            }
            string reason = ManualChatProtection.WaitPrefix + (!probeFailed && observation == ManualChatObservation.Unknown && _clock() - wait.Since < TimeSpan.FromSeconds(15)
                ? "正在确认目标输入框状态 / Checking the target input"
                : ManualChatProtection.Reason(observation));
            int seconds = ManualChatProtection.ClampTimeout(_startSettings?.Invoke()?.ManualChatWaitTimeoutSeconds ?? 0);
            if (_clock() - wait.Since >= TimeSpan.FromSeconds(seconds))
            {
                reason += "；已超时，仍等待，不强制发送 / Timed out; still waiting, no forced send";
                if (!wait.TimedOut)
                {
                    wait.TimedOut = true;
                    AppLog.Write(AppLog.TasksFile, $"任务 #{task.Id} 等待超时，未强制发送；可手动重新检查 / Wait timed out; no forced send; manual recheck available: {observation}");
                    _host.AnnounceTask(task, $"任务 {task.Id} 等待手动对话已超时，仍继续等待；请检查目标草稿或探测状态，不会强制发送",
                        $"Task {task.Id} manual-chat wait timed out; still waiting. Check the target draft or observation; no forced send");
                }
            }
            if (task.ManualChatWaitReason == reason) return;
            task.ManualChatWaitReason = reason;
            AppLog.Write(AppLog.TasksFile, $"任务 #{task.Id} 等待原因 / Wait reason: {observation}；已等待 / waited {(int)(_clock() - wait.Since).TotalSeconds}s；目标 / target {task.VsName}；{reason}");
            _host.SetStatus(reason);
            _host.QueueActivityChanged(HasDispatchActivity);
        }

        private async Task<bool> WaitForManualAsync(QueuedTask task, VsInstance target)
        {
            if (!WaitForManualChat) { ClearManualWait(task); return false; }
            string path = target.SolutionPath, identity = target.InstanceKey;
            ManualChatObservation observation;
            bool probeFailed = false;
            try
            {
                observation = target.Copilot == CopilotState.Busy ? ManualChatObservation.Generating
                    : _host is IManualChatDispatchHost guard ? await guard.ObserveManualChatAsync(target) : ManualChatObservation.Unknown;
            }
            catch (Exception ex)
            {
                if (!_manualWaits.ContainsKey(task))
                    AppLog.Write(AppLog.TasksFile, $"任务 #{task.Id} 探测异常类别 / Probe error type: {ex.GetType().Name}");
                observation = ManualChatObservation.Unknown;
                probeFailed = true;
            }
            if (task.Status != QueueStatus.Waiting || !CanRun(task) || !SameTarget(task, target, path, identity) || DispatchBlocker(task) != null)
            { ClearManualWait(task); return true; }
            if (!WaitForManualChat) { ClearManualWait(task); return false; }
            if (ManualChatProtection.Blocks(observation)) { RecordManualWait(task, target, observation, probeFailed); return true; }
            // 等待结束只更新显示，真正送达才播报继续发送。/ Readiness updates display; only confirmed delivery announces resumption.
            if (_manualWaits.TryGetValue(task, out var ended))
            {
                if (task.ManualChatWaitReason != null && ManualChatProtection.IsWait(task.ManualChatWaitReason))
                    AppLog.Write(AppLog.TasksFile, $"任务 #{task.Id} 目标已空闲，准备发布 / Target idle; dispatching；已等待 / waited {(int)(_clock() - ended.Since).TotalSeconds}s");
                task.ManualChatWaitReason = "手动对话已让行，等待目标及发送确认 / Manual chat yielded; awaiting target readiness and delivery";
                _host.QueueActivityChanged(HasDispatchActivity);
            }
            return false;
        }

        private void ManualDelivery(QueuedTask task)
        {
            if (!_manualWaits.ContainsKey(task)) return;
            ClearManualWait(task);
            _yielded.Add(task);
            _host.AnnounceTask(task, $"手动对话已结束，任务 {task.Id} 已自动继续发送", $"Manual chat ended; task {task.Id} automatically resumed and was delivered");
        }

        private string ManualCompletionText(QueuedTask task) => _yielded.Contains(task)
            ? "\n本任务已礼让手动对话后完成 / This task completed after yielding to manual chat" : "";
    }
}
