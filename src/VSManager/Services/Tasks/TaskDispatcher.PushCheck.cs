using System;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>推送核实结论。/ Outcome of verifying a task push.</summary>
    public enum PushOutcome
    {
        /// <summary>未进入任务清单或未能保存。/ Not in the task list, or not saved.</summary>
        NotAdmitted,
        /// <summary>仍在发送或即将发送（核实期间继续等待）。/ Sending or about to send (keep waiting while verifying).</summary>
        Pending,
        /// <summary>已入队，但当前不会发送（原因见说明）。/ Queued but not being sent now (see the reason).</summary>
        Held,
        /// <summary>已送达目标 Copilot。/ Delivered to the target Copilot.</summary>
        Delivered,
        /// <summary>推送失败或已取消。/ Push failed or was cancelled.</summary>
        Failed,
    }

    /// <summary>推送核实结果：结论与原因说明。/ Push verification result: outcome and reason.</summary>
    public sealed class PushCheck
    {
        public PushOutcome Outcome;
        public string Reason;

        public PushCheck(PushOutcome outcome, string reason) { Outcome = outcome; Reason = reason ?? ""; }

        /// <summary>
        /// 面向用户 / 主控 AI 的首行结论；只有真正送达时才以「✅ 推送成功」开头。
        /// Headline for the user / main AI; only a real delivery starts with "✅ 推送成功".
        /// </summary>
        public string Headline(QueuedTask t)
        {
            string id = t == null ? "" : "@" + t.Id;
            string name = t == null ? "" : "「" + t.VsName + "」";
            switch (Outcome)
            {
                case PushOutcome.Delivered:
                    return $"✅ 推送成功：任务 {id} 已送达{name}的 Copilot / Pushed: task {id} was delivered to Copilot";
                case PushOutcome.Failed:
                    return $"❌ 推送失败：任务 {id} 未能送达{name}；原因 / Push failed, reason: {Reason}";
                case PushOutcome.NotAdmitted:
                    return $"❌ 未推送：未能确认任务已加入任务清单；原因 / Not pushed, task not confirmed in the list: {Reason}";
                default:
                    return $"⏳ 已加入任务清单 {id}，尚未推送到 Copilot；原因 / Queued, not yet pushed: {Reason}；送达或失败后会另行通知 / you will be notified on delivery or failure";
            }
        }

        /// <summary>结果文字是否表示推送失败或未入队。/ Whether a result text means failed or not admitted.</summary>
        public static bool IsRejected(string text) => text != null && text.StartsWith("❌", StringComparison.Ordinal);
    }

    public sealed partial class TaskDispatcher
    {
        /// <summary>核实轮询间隔（测试可替换等待函数）。/ Verification poll interval (tests may replace the delay).</summary>
        public static readonly TimeSpan PushPollInterval = TimeSpan.FromMilliseconds(400);

        /// <summary>核实期间的等待函数，默认 Task.Delay。/ Delay used while verifying; defaults to Task.Delay.</summary>
        public Func<TimeSpan, Task> PushDelay { get; set; } = Task.Delay;

        /// <summary>
        /// 立即判断一次任务的推送状态：是否确实在清单中且已保存、是否已送达 / 失败，或为何暂未发送。
        /// Checks a task's push state once: whether it is really listed and saved, delivered / failed, or why it is not being sent.
        /// </summary>
        public PushCheck CheckPush(QueuedTask t)
        {
            if (t == null) return new PushCheck(PushOutcome.NotAdmitted, "任务未创建 / Task was not created");
            if (_tasks.Find(t.Id) != t)
                return new PushCheck(PushOutcome.NotAdmitted, $"任务 @{t.Id} 不在任务清单中（可能已被删除或替换）/ Task @{t.Id} is not in the task list (removed or replaced)");
            if (_tasks.SaveError != null && t.Status != QueueStatus.Running && !QueueStatus.Delivered(t.Status))
                return new PushCheck(PushOutcome.NotAdmitted, "任务清单保存失败，任务未持久化 / Task list save failed; task not persisted: " + _tasks.SaveError);
            if (t.Status == QueueStatus.Running || QueueStatus.Delivered(t.Status))
                return new PushCheck(PushOutcome.Delivered, "");
            if (t.Status == QueueStatus.Failed)
                return new PushCheck(PushOutcome.Failed, FirstText(t.FailureReason, t.Error, "未知原因 / Unknown reason"));
            if (t.Status == QueueStatus.Cancelled)
                return new PushCheck(PushOutcome.Failed, "任务已被取消 / Task was cancelled");
            if (t.Status == QueueStatus.Sending) return new PushCheck(PushOutcome.Pending, "正在发送 / Sending");
            if (t.Status == QueueStatus.WaitingVs)
                return new PushCheck(PushOutcome.Held, "目标解决方案尚未打开，任务已暂存 / Target solution not open; task parked");
            if (IsPaused) return new PushCheck(PushOutcome.Held, PausedText);
            if (!CanRun(t)) return new PushCheck(PushOutcome.Held, WaitingForStart);
            if (!IsStarted && _tasks.SaveError != null)
                return new PushCheck(PushOutcome.Held, "任务清单保存失败，暂停发送 / Task list save failed; sending held");
            var blocker = DispatchBlocker(t);
            if (blocker != null)
                return new PushCheck(PushOutcome.Held, $"等待前序任务 @{blocker.Id}（{_tasks.StatusText(blocker, _clock())}）/ Waiting for predecessor @{blocker.Id}");
            if (_manualWaits.ContainsKey(t))
                return new PushCheck(PushOutcome.Held, FirstText(t.Error, "目标 VS 有手动对话进行中，礼让后再发送 / Manual chat in progress on the target; waiting to send"));
            var v = ResolveTarget(t, true);
            if (v == null) return new PushCheck(PushOutcome.Held, "目标 VS 未找到或未就绪 / Target VS not found or not ready");
            if (!_host.CanDispatch(v))
                return new PushCheck(PushOutcome.Held, "目标 VS 正忙（Copilot 运行中、调试或生成中），空闲后按编号发送 / Target VS busy (Copilot running, debugging or building); sent in ID order when idle");
            if (!string.IsNullOrEmpty(t.Error) && t.NextTry > _clock())
                return new PushCheck(PushOutcome.Held, t.Error);
            return new PushCheck(PushOutcome.Pending, "等待发送 / Waiting to send");
        }

        /// <summary>
        /// 推送后核实：在限定时间内跟踪任务，直到确认送达、失败或确定暂不发送；超时仍未送达时如实返回「尚未推送」。
        /// Verifies a push: tracks the task for a bounded time until it is delivered, fails or is known to be held; if still
        /// undelivered at the deadline it honestly reports "not yet pushed".
        /// </summary>
        public async Task<PushCheck> ConfirmPushAsync(QueuedTask t, TimeSpan timeout)
        {
            int polls = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds / PushPollInterval.TotalMilliseconds));
            for (int i = 0; ; i++)
            {
                var check = CheckPush(t);
                if (check.Outcome != PushOutcome.Pending) return check;
                if (i >= polls)
                    return new PushCheck(PushOutcome.Held, $"{(int)timeout.TotalSeconds} 秒内未确认送达，当前状态 / Delivery not confirmed within {(int)timeout.TotalSeconds}s, current state: {_tasks.StatusText(t, _clock())}");
                if (t.Status == QueueStatus.Waiting) Pump();
                await PushDelay(PushPollInterval);
            }
        }

        private static string FirstText(params string[] texts) => texts.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    }
}