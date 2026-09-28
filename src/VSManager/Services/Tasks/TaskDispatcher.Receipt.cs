using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    public sealed partial class TaskDispatcher
    {
        /// <summary>
        /// 回执宽限期：读到的回复没有回执时，先在这段时间内继续观察，Copilot 重新开始运行或回执出现都不判失败；为零时立即判定（默认，便于测试）。
        /// Receipt grace: when the reply has no receipt, keep watching for this long; if Copilot resumes or the receipt appears it is not a
        /// failure. Zero judges at once (the default, convenient for tests).
        /// </summary>
        public TimeSpan ReceiptGrace { get; set; } = TimeSpan.Zero;

        /// <summary>宽限期内的检查间隔。/ Poll interval during the grace period.</summary>
        public TimeSpan ReceiptPollInterval { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>宽限期内的等待函数，默认 Task.Delay。/ Delay used during the grace period; defaults to Task.Delay.</summary>
        public Func<TimeSpan, Task> ReceiptDelay { get; set; } = Task.Delay;

        /// <summary>迟到回执能纠正失败的最长时间。/ How long after a failure a late receipt may still correct it.</summary>
        public static readonly TimeSpan LateReceiptWindow = TimeSpan.FromHours(6);

        internal const string LateReceiptNote = "注意：此前曾因「缺少回执」判定该任务失败，那是 Copilot 中途停顿造成的误判；Copilot 之后输出了回执，已按回执更正为本结果，请以此为准，不要再按失败处理。"
            + " / Note: this task was earlier judged failed for a missing receipt; that was a misjudgment caused by Copilot pausing mid-run. Copilot later output the receipt and the result was corrected accordingly; go by this result and do not handle it as a failure.";

        /// <summary>按迟到回执纠正、完成通知需附加说明的任务。/ Tasks corrected by a late receipt whose completion notice carries a note.</summary>
        private readonly HashSet<QueuedTask> _lateRecovered = new HashSet<QueuedTask>();

        /// <summary>
        /// 宽限期内反复检查：回执出现则返回新回复；Copilot 重新运行、任务状态变化时返回 null（任务保持执行中，等下次完成再判）；到期仍无回执时返回最后读到的回复。
        /// Polls during the grace period: returns the new reply once the receipt appears; returns null when Copilot runs again or the task
        /// changed (it stays running and is judged at the next completion); returns the last reply read when the grace expires.
        /// </summary>
        private async Task<string> AwaitReceiptAsync(QueuedTask t, VsInstance v, string answer)
        {
            var interval = ReceiptPollInterval > TimeSpan.Zero ? ReceiptPollInterval : TimeSpan.FromSeconds(1);
            int polls = Math.Max(1, (int)Math.Ceiling(ReceiptGrace.TotalMilliseconds / interval.TotalMilliseconds));
            for (int i = 0; i < polls; i++)
            {
                await ReceiptDelay(interval);
                if (t.Status != QueueStatus.Running || _tasks.Find(t.Id) != t) return null;
                if (v != null && v.Copilot == CopilotState.Busy)
                {
                    t.SawBusy = true;
                    _host.LogEvent(t.VsName, $"任务 #{t.Id}：Copilot 停顿后继续运行，暂不判定结果 / Copilot resumed after a pause; result not judged yet");
                    return null;
                }
                string now;
                try { now = await _host.ReadAnswerAsync(v, t); }
                catch { continue; }
                if (t.Status != QueueStatus.Running || _tasks.Find(t.Id) != t) return null;
                if (string.IsNullOrEmpty(now)) continue;
                answer = now;
                if (TaskStateMachine.ReadReceipt(t, answer, out _) != TaskReceipt.None) return answer;
            }
            return answer;
        }

        /// <summary>
        /// 迟到回执：任务曾因「缺少回执」或「未执行完」判定失败，之后同一轮对话结尾出现了本次尝试的回执（Copilot 停顿后继续运行），
        /// 按回执重新判定结果。只处理尚未被重试、取代的失败。返回是否已纠正。
        /// Late receipt: a task judged failed for a missing receipt or an unfinished run, whose conversation turn later ends with this
        /// attempt's receipt (Copilot resumed after a pause), is re-judged by that receipt. Only failures not yet retried or superseded
        /// are handled. Returns whether it was corrected.
        /// </summary>
        public async Task<bool> RecoverLateReceiptAsync(VsInstance v, string question, string answer)
        {
            if (v == null || string.IsNullOrEmpty(question) || string.IsNullOrEmpty(answer)) return false;
            var now = _clock();
            var t = _tasks.Items.LastOrDefault(x => x.Status == QueueStatus.Failed
                && (x.FailureKind == FailureKind.NoReceipt || x.FailureKind == FailureKind.Interrupted)
                && !string.IsNullOrEmpty(x.CompletionToken)
                && question.IndexOf("[VSManager:" + x.CompletionToken + ":", StringComparison.Ordinal) >= 0
                && x.Finished.HasValue && now - x.Finished.Value <= LateReceiptWindow);
            if (t == null || _finishing.Contains(t)) return false;
            if (_tasks.Items.Any(x => x.Replaces != null && x.Replaces.Contains(t.Id))) return false;
            if (ResolveTarget(t) != v) return false;
            if (TaskStateMachine.ReadReceipt(t, answer, out _) == TaskReceipt.None) return false;
            // 恢复为执行中，交给正常的完成流程读取并判定 / Back to running; the normal completion path reads and judges it
            string oldKind = t.FailureKind;
            if (FailureKind.IsContent(oldKind) && t.ContentRuns > 0) t.ContentRuns--;
            t.Status = QueueStatus.Running;
            t.Error = null;
            t.FailureKind = null;
            t.FailureReason = null;
            t.Finished = null;
            t.RunIssue = null;
            t.SawBusy = true;
            _tasks.Commit();
            string log = $"任务 #{t.Id} 收到迟到的回执，撤销「{FailureKind.Label(oldKind)}」失败并按回执重新判定 / Late receipt received; failure reverted and re-judged by the receipt";
            AppLog.Write(AppLog.TasksFile, log);
            _host.LogEvent(t.VsName, log);
            if (t.FromAgent) _lateRecovered.Add(t);
            await FinishAsync(t, v, null, answer);
            _lateRecovered.Remove(t);
            return t.Status != QueueStatus.Running;
        }
    }
}
