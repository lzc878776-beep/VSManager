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
        /// <summary>No execution was observed; the result is unconfirmed, not successful.</summary>
        Unconfirmed
    }

    /// <summary>
    /// 任务状态机：集中定义任务状态的全部流转，界面与调度器只调用这里的方法修改状态。
    /// 排队(waiting) → 发送中(sending) → 执行中(running) → 已完成(done)；
    /// 发送失败 → 排队（等待重试）或 失败(failed)；排队 / 执行中 → 已取消(cancelled)；失败 / 已取消 → 重新排队。
    /// 等待目标 VS(waiting_vs) → 目标打开后转为排队(waiting)，或 → 已取消。
    /// Task state machine: the single place that defines every status transition; the UI and the dispatcher only change
    /// status through these methods.
    /// waiting → sending → running → done; send failure → waiting (retry) or failed; waiting / running → cancelled;
    /// failed / cancelled → waiting again (retry); waiting_vs → waiting once the target VS opens, or → cancelled.
    /// </summary>
    public static class TaskStateMachine
    {
        /// <summary>VS 关闭多久后判定任务失败。/ How long a closed VS is tolerated before the task fails.</summary>
        public static readonly TimeSpan VsClosedTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Timeout for an unconfirmed start; never treated as successful completion.</summary>
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
            t.CompletionToken = Guid.NewGuid().ToString("N");
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

        public static string SuccessReceipt(QueuedTask t) => "[VSManager:" + t.CompletionToken + ":SUCCESS]";
        public static string FailureReceipt(QueuedTask t) => "[VSManager:" + t.CompletionToken + ":FAILED]";

        public static string DispatchText(QueuedTask t) => t.Text + " "
            + "任务队列回执（仅用于确认本次结果）：只有本任务全部成功完成，才在最终回复最后单独一行输出 " + SuccessReceipt(t)
            + "；遇到错误、未完成、需要用户处理或无法确认成功时，说明原因并在最后单独一行输出 " + FailureReceipt(t)
            + "。不要在过程消息中输出回执。";

        public static bool TryReadSuccess(QueuedTask t, string answer, out string result)
        {
            result = answer?.Trim();
            if (string.IsNullOrEmpty(t.CompletionToken) || string.IsNullOrEmpty(result)) return false;
            string receipt = SuccessReceipt(t);
            if (!result.EndsWith(receipt, StringComparison.Ordinal) || result.Contains(FailureReceipt(t))) return false;
            int receiptStart = result.Length - receipt.Length;
            if (receiptStart > 0 && result[receiptStart - 1] != '\n') return false;
            result = result.Substring(0, receiptStart).TrimEnd();
            if (result.Length == 0) result = "任务已成功完成";
            return true;
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

        /// <summary>排队 / 等待目标 VS / 执行中 → 已取消。/ waiting / waiting_vs / running → cancelled.</summary>
        public static bool Cancel(QueuedTask t, DateTime now)
        {
            if (t == null || (t.Status != QueueStatus.Waiting && t.Status != QueueStatus.WaitingVs && t.Status != QueueStatus.Running)) return false;
            t.Status = QueueStatus.Cancelled;
            t.Finished = now;
            return true;
        }

        /// <summary>
        /// 等待目标 VS → 排队：改用已打开 VS 的键与名称，并在 <paramref name="notBefore"/> 之后才发布（等待解决方案加载）。
        /// waiting_vs → waiting: switches to the opened VS's key and name, publishable only after <paramref name="notBefore"/>
        /// (lets the solution finish loading).
        /// </summary>
        public static bool TargetOpened(QueuedTask t, string vsKey, string vsName, DateTime notBefore)
        {
            if (t == null || t.Status != QueueStatus.WaitingVs) return false;
            t.Status = QueueStatus.Waiting;
            if (!string.IsNullOrEmpty(vsKey)) t.VsKey = vsKey;
            if (!string.IsNullOrEmpty(vsName)) t.VsName = vsName;
            t.NextTry = notBefore;
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
            if (!t.SawBusy && copilot == CopilotState.Idle && canDispatch() && elapsed > NeverBusyTimeout) return RunningVerdict.Unconfirmed;
            return RunningVerdict.None;
        }

        /// <summary>Earlier unfinished or failed work blocks the same target; explicit cancellation/removal skips it.</summary>
        public static QueuedTask BlockingTask(IEnumerable<QueuedTask> items, QueuedTask task) =>
            items.Where(x => x != task && ResentTaskMatcher.SameTarget(x, task)
                && (x.Status == QueueStatus.Sending || x.Status == QueueStatus.Running
                    || (x.Order < task.Order && (QueueStatus.Active(x.Status) || x.Status == QueueStatus.Failed))))
                .OrderBy(x => x.Order).ThenBy(x => x.Id).FirstOrDefault();

        public static List<QueuedTask> NextToDispatch(IEnumerable<QueuedTask> items, DateTime now)
        {
            var all = items.ToList();
            return all.Where(x => x.Status == QueueStatus.Waiting && x.NextTry <= now && BlockingTask(all, x) == null)
                .OrderBy(x => x.Order).ThenBy(x => x.Id).ToList();
        }

        /// <summary>Ignore resend markers and formatting when detecting an already queued task.</summary>
        public static QueuedTask FindActiveDuplicate(IEnumerable<QueuedTask> items, string vsKey, string text)
        {
            string normalized = ResentTaskMatcher.Normalize(ResentTaskMatcher.StripMarker(text, out _));
            return items.FirstOrDefault(t => string.Equals(t.VsKey, vsKey, StringComparison.OrdinalIgnoreCase)
                && QueueStatus.Active(t.Status)
                && ResentTaskMatcher.Normalize(ResentTaskMatcher.StripMarker(t.Text, out _)) == normalized);
        }

        public static string StatusText(QueuedTask t, DateTime now, IEnumerable<QueuedTask> items)
        {
            var blocker = t.Status == QueueStatus.Waiting ? BlockingTask(items, t) : null;
            return blocker?.Status == QueueStatus.Failed
                ? $"已暂停（前序 #{blocker.Id} 失败）" : StatusText(t, now);
        }

        /// <summary>任务状态的简短文字（界面、AI 工具返回共用）。/ Short status text (shared by the UI and AI tool results).</summary>
        public static string StatusText(QueuedTask t, DateTime now)
        {
            switch (t.Status)
            {
                case QueueStatus.Waiting: return t.Attempts > 0 ? "等待重试" : "排队中";
                case QueueStatus.WaitingVs: return "等待目标 VS（等待打开「" + (t.Target ?? t.VsName) + "」）";
                case QueueStatus.Sending: return "发送中";
                case QueueStatus.Running: return "执行中（" + TextUtil.FormatDuration(now - (t.Started ?? now)) + "）";
                case QueueStatus.Done: return "已完成";
                case QueueStatus.Failed: return "失败";
                default: return "已取消";
            }
        }
    }
}
