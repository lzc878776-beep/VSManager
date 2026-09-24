using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    public sealed partial class TaskDispatcher
    {
        private readonly Func<AppSettings> _startSettings;
        private readonly Dictionary<QueuedTask, bool> _automatic = new Dictionary<QueuedTask, bool>();
        private readonly Dictionary<QueuedTask, bool> _pendingMaintenance = new Dictionary<QueuedTask, bool>();
        private bool _allEnabled;

        public const string AiAutomatic = "AI 自动启动 / AI automatic start";
        public const string AllAutomatic = "全部任务自动启动 / All-tasks automatic start";

        /// <summary>启动资格只保存在会话内，不改变任务来源、状态或顺序。/ Session-only eligibility; source, state and order are unchanged.</summary>
        public bool CanRun(QueuedTask task) => task != null && _tasks.Find(task.Id) == task
            && (IsStarted || _automatic.ContainsKey(task));

        public bool IsAutomatic(QueuedTask task) => task != null && _automatic.ContainsKey(task);

        public bool HasDispatchActivity => _tasks.Items.Any(t => QueueStatus.Active(t.Status) && CanRun(t))
            || _pendingMaintenance.Count > 0;

        public string StartModeText => IsStarted
            ? "手动流程已启动；新任务按编号调度 / Manual workflow started; new tasks dispatch in ID order"
            : _startSettings?.Invoke()?.AutoStartAllTasks == true
            ? "全部自动（含恢复任务）/ All automatic (including restored tasks)"
            : _startSettings?.Invoke()?.AutoStartAiTasks == true
                ? "新发布 AI 任务自动；手动及恢复任务等待 Start / Newly submitted AI tasks automatic; manual and restored tasks await Start"
                : "新入队任务等待 Start；既有授权继续 / New admissions await Start; existing grants continue";

        public string StartStateText(QueuedTask task)
        {
            if (task?.Status == QueueStatus.Done)
                return "已结束，无需启动 / Finished; no start needed" + AutomaticCompletionText(task);
            if (!CanRun(task)) return WaitingForStart;
            string mode = _automatic.TryGetValue(task, out bool all) ? (all ? AllAutomatic : AiAutomatic)
                : "手动流程已启动 / Manual workflow started";
            var blocker = DispatchBlocker(task);
            return mode + (blocker == null
                ? "；按编号等待目标就绪，无需另点 Start / Awaiting target readiness in ID order; no additional Start needed"
                : $"；等待前序 @{blocker.Id}，不会插队 / Waiting for predecessor @{blocker.Id}; no queue jumping");
        }

        /// <summary>仅真实入队入口在保存成功后调用；重复 AI 发布可授权旧 AI 条目，但不能授权手动条目或旧合并。/ Call only after a real, persisted admission; duplicates may authorize AI entries, never manual entries or old integrations.</summary>
        public string AcceptQueued(QueuedTask task, string submittedSource)
        {
            if (task == null || _tasks.Find(task.Id) != task || !QueueStatus.Active(task.Status))
                return "任务已不在活动队列，请查询清单 / Task is no longer active; check the list";
            if (_tasks.SaveError != null)
                return "任务保存失败，未授权自动启动；保存恢复后请重新发布或点击 Start / Task save failed; automatic start not authorized; resubmit or click Start after saving recovers: " + _tasks.SaveError;
            var settings = _startSettings?.Invoke();
            if (!IsStarted && !task.IsWorktreeMerge && (settings?.AutoStartAllTasks == true
                || (settings?.AutoStartAiTasks == true && submittedSource == "AI" && task.FromAgent)))
                Grant(task, settings.AutoStartAllTasks);
            _host.QueueActivityChanged(HasDispatchActivity);
            return StartStateText(task);
        }

        /// <summary>全部模式显式启用时包括已恢复任务；关闭只影响后续入队，既有授权与在途完成继续。/ Enabling all mode explicitly includes restored tasks; disabling affects later admissions, not existing grants or in-flight completion.</summary>
        public void ApplyAutomaticStart()
        {
            bool all = _startSettings?.Invoke()?.AutoStartAllTasks == true;
            if (all && !_allEnabled && _tasks.SaveError == null)
                foreach (var task in _tasks.Items.Where(t => QueueStatus.Active(t.Status)).ToList())
                    if (!IsStarted) Grant(task, true);
            if (all && !_allEnabled && _tasks.SaveError != null)
                _host.SetStatus("任务保存失败，全部自动未授权；恢复保存后请重新启用 / Task save failed; all-mode grants withheld; enable again after saving recovers");
            _allEnabled = all;
            _host.QueueActivityChanged(HasDispatchActivity);
        }

        // 暂存前序未获启动资格时仍按实际目标阻塞；只映射副本，不推进其状态。/ Parked predecessors still block by resolved target; map copies without advancing their state.
        private QueuedTask DispatchBlocker(QueuedTask task)
        {
            var blocker = _tasks.BlockingTask(task);
            if (blocker != null || !_tasks.Items.Any(t => t.Status == QueueStatus.WaitingVs)) return blocker;
            var mapped = _tasks.Items.Select(t =>
            {
                var copy = t.Clone();
                copy.VsKey = _host.FindTargetVs(t)?.Key ?? t.VsKey;
                return copy;
            }).ToList();
            var candidate = mapped.FirstOrDefault(t => t.Id == task.Id);
            if (candidate == null) return null;
            blocker = TaskStateMachine.BlockingTask(mapped, candidate, _tasks.SkipFailedPredecessors);
            return blocker == null ? null : _tasks.Find(blocker.Id);
        }

        private void Grant(QueuedTask task, bool all)
        {
            if (_automatic.ContainsKey(task)) return;
            _automatic.Add(task, all);
            _host.AnnounceTask(task,
                $"任务 {task.Id} 已启用{(all ? "全部任务" : "AI")}自动启动，按编号等待目标及前序，无需另点开始",
                $"Task {task.Id}: {(all ? "all-tasks" : "AI")} automatic start enabled; waiting for target and predecessors in ID order; no additional Start needed");
        }

        private string AutomaticCompletionText(QueuedTask task) => _automatic.TryGetValue(task, out bool all)
            ? "\n启动方式 / Start mode: " + (all ? AllAutomatic : AiAutomatic) : "";

        private void CommitCompletion(QueuedTask task)
        {
            var existing = new HashSet<QueuedTask>(_tasks.Items.Where(t => t.IsWorktreeMerge));
            _tasks.Commit();
            if (_automatic.TryGetValue(task, out bool all))
            {
                // 仅继承本次完成新产生的合并屏障，绝不授权恢复的旧屏障。/ Inherit only barriers newly generated by this completion, never restored barriers.
                if (task.Worktree != null && !task.IsWorktreeMerge)
                    foreach (var merge in _tasks.Items.Where(t => t.IsWorktreeMerge && !existing.Contains(t)
                        && SolutionMatcher.SamePath(t.Worktree?.Root, task.Worktree.Root)))
                        _pendingMaintenance[merge] = all;
                GrantSavedMaintenance();
                _host.AnnounceTask(task, $"任务 {task.Id} 已完成，启动方式：{(all ? "全部任务自动" : "AI 自动")}" + (_yielded.Contains(task) ? "，已礼让手动对话" : ""),
                    $"Task {task.Id} completed; start mode: {(all ? "all-tasks automatic" : "AI automatic")}" + (_yielded.Contains(task) ? "; yielded to manual chat" : ""));
            }
            else if (_yielded.Contains(task))
                _host.AnnounceTask(task, $"任务 {task.Id} 已完成，已礼让手动对话", $"Task {task.Id} completed after yielding to manual chat");
        }

        private void ForgetRemovedGrants()
        {
            var retained = new HashSet<QueuedTask>(_tasks.Items);
            foreach (var task in _automatic.Keys.Where(t => !retained.Contains(t)).ToArray()) _automatic.Remove(task);
        }

        private void GrantSavedMaintenance()
        {
            if (_tasks.SaveError != null) return;
            foreach (var entry in _pendingMaintenance.ToArray())
            {
                if (_tasks.Find(entry.Key.Id) == entry.Key && QueueStatus.Active(entry.Key.Status)) Grant(entry.Key, entry.Value);
                _pendingMaintenance.Remove(entry.Key);
            }
        }
    }
}
