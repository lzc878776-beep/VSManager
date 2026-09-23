using System;
using System.Collections.Generic;
using System.Linq;

namespace VSManager
{
    /// <summary>执行中任务的检查结论。/ Verdict of checking a running task.</summary>
    public enum RunningVerdict
    {
        /// <summary>继续等待。/ Keep waiting.</summary>
        None,
        /// <summary>观察到 Copilot 忙碌（记录下来，等它回到空闲时即完成）。/ Copilot was seen busy (remember it; finished when idle again).</summary>
        SawBusy,
        /// <summary>目标 VS 已关闭超过 15 秒：任务失败。/ Target VS has been closed for more than 15 s: the task fails.</summary>
        VsClosed,
        /// <summary>始终没观察到 Copilot 开始运行（任务极快或状态不可读）超过 30 秒：按已完成处理。
        /// Copilot was never seen running (very fast task or unreadable state) for more than 30 s: treat as done.</summary>
        AssumeDone
    }

    /// <summary>
    /// 任务状态机：集中定义任务状态的全部流转，界面与调度器只调用这里的方法修改状态。
    /// 排队(waiting) → 发送中(sending) → 执行中(running) → 已完成(done)；
    /// 发送失败 → 排队（等待重试）或 失败(failed)；排队 / 执行中 → 已取消(cancelled)；失败 / 已取消 → 重新排队。
    /// Task state machine: the single place that defines every status transition; the UI and the dispatcher only change
    /// status through these methods.
    /// waiting → sending → running → done; send failure → waiting (retry) or failed; waiting / running → cancelled;
    /// failed / cancelled → waiting again (retry).
    /// </summary>
    public static class TaskStateMachine
    {
        /// <summary>VS 关闭多久后判定任务失败。/ How long a closed VS is tolerated before the task fails.</summary>
        public static readonly TimeSpan VsClosedTimeout = TimeSpan.FromSeconds(15);

        /// <summary>始终未观察到忙碌时，多久后按已完成处理。/ After how long a never-busy task is treated as done.</summary>
        public static readonly TimeSpan NeverBusyTimeout = TimeSpan.FromSeconds(30);

        /// <summary>VS 已关闭时记录的错误。/ Error recorded when the VS was closed.</summary>
        public const string VsClosedError = "目标 VS 已关闭，无法确认任务结果";

        /// <summary>排队 → 发送中，并累计尝试次数。/ waiting → sending, counting the attempt.</summary>
        public static bool BeginSend(QueuedTask t, string vsName)
        {
            if (t == null || t.Status != QueueStatus.Waiting) return false;
            t.Status = QueueStatus.Sending;
            t.VsName = vsName;
            t.Attempts++;
            return true;
        }

        /// <summary>
        /// 根据发送结果流转：送达 → 执行中；未送达 → 排队等待重试，或达到上限时失败。
        /// Applies a send result: delivered → running; otherwise → waiting for a retry, or failed once out of attempts.
        /// </summary>
        public static SendDecision ApplySendResult(QueuedTask t, string result, DateTime now)
        {
            var d = SendRetryPolicy.Decide(t.Attempts, result);
            switch (d)
            {
                case SendDecision.Delivered:
                    t.Status = QueueStatus.Running;
                    t.Started = now;
                    t.SawBusy = false;
                    t.Error = null;
                    break;
                case SendDecision.Fail:
                    Fail(t, result, now);
                    break;
                default:
                    t.Status = QueueStatus.Waiting;
                    t.Error = result;
                    t.NextTry = now + SendRetryPolicy.RetryDelay;
                    break;
            }
            return d;
        }

        /// <summary>→ 失败。/ → failed.</summary>
        public static void Fail(QueuedTask t, string error, DateTime now)
        {
            t.Status = QueueStatus.Failed;
            t.Error = error;
            t.Finished = now;
        }

        /// <summary>执行中 → 已完成。/ running → done.</summary>
        public static bool Complete(QueuedTask t, DateTime now)
        {
            if (t == null || t.Status != QueueStatus.Running) return false;
            t.Status = QueueStatus.Done;
            t.Finished = now;
            return true;
        }

        /// <summary>排队 / 执行中 → 已取消。/ waiting / running → cancelled.</summary>
        public static bool Cancel(QueuedTask t, DateTime now)
        {
            if (t == null || (t.Status != QueueStatus.Waiting && t.Status != QueueStatus.Running)) return false;
            t.Status = QueueStatus.Cancelled;
            t.Finished = now;
            return true;
        }

        /// <summary>重新排队：清空尝试次数、结果与时间，立即可发布。/ Requeue: clears attempts, result and times; publishable at once.</summary>
        public static void Requeue(QueuedTask t)
        {
            t.Status = QueueStatus.Waiting;
            t.Attempts = 0;
            t.Error = null;
            t.Result = null;
            t.Started = t.Finished = null;
            t.NextTry = DateTime.MinValue;
        }

        /// <summary>上次退出时正在发送的任务重新排队。/ A task that was being sent when the app exited goes back to the queue.</summary>
        public static void RecoverAfterRestart(QueuedTask t)
        {
            if (t.Status == QueueStatus.Sending) t.Status = QueueStatus.Waiting;
        }

        /// <summary>发送中的任务不能移除。/ A task that is being sent cannot be removed.</summary>
        public static bool CanRemove(QueuedTask t) => t != null && t.Status != QueueStatus.Sending;

        /// <summary>
        /// 检查执行中的任务。<paramref name="canDispatch"/> 只在需要时才调用。
        /// Checks a running task. <paramref name="canDispatch"/> is only evaluated when needed.
        /// </summary>
        public static RunningVerdict CheckRunning(QueuedTask t, bool vsOpen, CopilotState copilot, Func<bool> canDispatch, DateTime now)
        {
            var elapsed = now - (t.Started ?? now);
            if (!vsOpen) return elapsed > VsClosedTimeout ? RunningVerdict.VsClosed : RunningVerdict.None;
            if (copilot == CopilotState.Busy) return RunningVerdict.SawBusy;
            if (!t.SawBusy && copilot == CopilotState.Idle && canDispatch() && elapsed > NeverBusyTimeout) return RunningVerdict.AssumeDone;
            return RunningVerdict.None;
        }

        /// <summary>
        /// 本轮可发布的任务：每个没有发送中 / 执行中任务的 VS，取编号最小的排队任务（且已到重试时间），按编号排序。
        /// Tasks to publish in this round: for every VS without a sending / running task, its lowest-numbered waiting task
        /// (whose retry time has come), ordered by id.
        /// </summary>
        public static List<QueuedTask> NextToDispatch(IEnumerable<QueuedTask> items, DateTime now) =>
            items.Where(x => QueueStatus.Active(x.Status)).GroupBy(x => x.VsKey)
                .Where(g => !g.Any(x => x.Status != QueueStatus.Waiting))
                .Select(g => g.OrderBy(x => x.Id).First())
                .Where(x => x.NextTry <= now)
                .OrderBy(x => x.Id).ToList();

        /// <summary>同一 VS 中内容相同的未完成任务（避免重复添加）。/ An unfinished task with the same text for the same VS (avoids duplicates).</summary>
        public static QueuedTask FindActiveDuplicate(IEnumerable<QueuedTask> items, string vsKey, string text) =>
            items.FirstOrDefault(t => t.VsKey == vsKey && QueueStatus.Active(t.Status) && t.Text == text);

        /// <summary>任务状态的简短文字（界面、AI 工具返回共用）。/ Short status text (shared by the UI and AI tool results).</summary>
        public static string StatusText(QueuedTask t, DateTime now)
        {
            switch (t.Status)
            {
                case QueueStatus.Waiting: return t.Attempts > 0 ? "等待重试" : "排队中";
                case QueueStatus.Sending: return "发送中";
                case QueueStatus.Running: return "执行中（" + TextUtil.FormatDuration(now - (t.Started ?? now)) + "）";
                case QueueStatus.Done: return "已完成";
                case QueueStatus.Failed: return "失败";
                default: return "已取消";
            }
        }
    }
}
