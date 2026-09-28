using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    /// <summary>
    /// 普通任务（非 @ 提及）的目标实例选择。同一解决方案可能被多个 VS 同时打开，各自是不同的 Copilot 对话，
    /// 因此只按解决方案路径查找会把任务发到先枚举到的那个实例。任务入队时记录所选实例（<see cref="QueuedTask.TargetInstanceKey"/>），
    /// 之后一律优先回到该实例，绝不改投同一解决方案的其他实例。
    /// Target instance selection for ordinary (non-mention) tasks. One solution may be open in several VS instances, each
    /// with its own Copilot conversation, so a lookup by solution path alone sends the task to whichever instance happens to
    /// be enumerated first. The chosen instance is recorded on enqueue (<see cref="QueuedTask.TargetInstanceKey"/>) and the
    /// task always goes back to it, never to another instance of the same solution.
    /// </summary>
    public static class TaskTarget
    {
        /// <summary>
        /// 从打开了任务目标解决方案的实例中选出目标；无法确定时返回 null（任务继续等待，不猜测）。
        /// 已记录实例：只接受该实例；它已关闭时，仅当剩下唯一一个实例且它是在任务入队之后才启动的（即 VS 重启），才改用它。
        /// 未记录实例（旧任务、暂存任务）：唯一实例直接使用；多个实例时优先名称与任务记录一致的那个，否则沿用第一个。
        /// Picks the target among instances that have the task's solution open; null when it cannot be determined (the task keeps
        /// waiting instead of guessing). Recorded instance: only that instance is accepted; once it has closed, a single remaining
        /// instance is used only if it started after the task was queued (i.e. VS was restarted). No recorded instance (older or
        /// parked tasks): a single instance is used directly; with several, the one whose name matches the task record wins,
        /// otherwise the first.
        /// </summary>
        public static VsInstance Pick(IEnumerable<VsInstance> sameSolution, QueuedTask task, Func<VsInstance, string> nameOf = null)
        {
            var list = (sameSolution ?? Enumerable.Empty<VsInstance>()).Where(v => v != null).Distinct().ToList();
            if (task == null || list.Count == 0) return null;
            if (!string.IsNullOrEmpty(task.TargetInstanceKey))
            {
                var exact = list.FirstOrDefault(v => v.InstanceKey == task.TargetInstanceKey);
                if (exact != null) return exact;
                return list.Count == 1 && StartedAfter(list[0], task.Created) ? list[0] : null;
            }
            if (list.Count == 1) return list[0];
            if (nameOf != null && !string.IsNullOrEmpty(task.VsName))
            {
                var named = list.Where(v => string.Equals(nameOf(v), task.VsName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count == 1) return named[0];
            }
            return list[0];
        }

        /// <summary>
        /// 两个任务是否可能指向同一实例（用于去重：发给另一实例的相同文字不是重复）。未记录实例的一方视为可能相同。
        /// Whether two tasks may target the same instance (for de-duplication: the same text for another instance is not a
        /// duplicate). A side without a recorded instance counts as possibly the same.
        /// </summary>
        public static bool MayShareInstance(QueuedTask existing, string instanceKey) =>
            existing != null && (string.IsNullOrEmpty(existing.TargetInstanceKey) || string.IsNullOrEmpty(instanceKey)
                || existing.TargetInstanceKey == instanceKey);

        /// <summary>
        /// 任务提交时记录目标实例；@ 提及任务已有精确目标，不记录。
        /// Records the target instance when the task is submitted; mention tasks already carry an exact target.
        /// </summary>
        public static void Pin(QueuedTask task, VsInstance v)
        {
            if (task == null || v == null || task.HasExplicitTarget) return;
            task.TargetInstanceKey = v.InstanceKey;
        }

        private static bool StartedAfter(VsInstance v, DateTime created)
        {
            if (v.StartTicks <= 0) return false;
            var utc = created.Kind == DateTimeKind.Utc ? created : created.ToUniversalTime();
            return v.StartTicks > utc.Ticks;
        }
    }
}
