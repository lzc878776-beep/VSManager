using System;

namespace VSManager
{
    /// <summary>发送结果的处理方式。/ How a send result is handled.</summary>
    public enum SendDecision
    {
        /// <summary>已送达，任务进入执行中。/ Delivered; the task becomes running.</summary>
        Delivered,
        /// <summary>明确尚未提交，等待保护条件解除（保留旧枚举名）。/ Definitely not submitted; wait for protection to clear (legacy enum name).</summary>
        Retry,
        /// <summary>发送失败或送达不确定，立即终止。/ Failed or uncertain delivery; stop immediately.</summary>
        Fail
    }

    /// <summary>
    /// 送达进入执行；仅明确未提交的保护状态继续等待，其他结果第一次即失败。
    /// Delivered sends run; only explicit pre-submission protection waits. Every other result fails on the first attempt.
    /// </summary>
    public static class SendRetryPolicy
    {
        /// <summary>送达结果的前缀（由 Copilot 发送逻辑返回）。/ Prefix of a delivered result (returned by the Copilot send logic).</summary>
        public const string DeliveredPrefix = "已发送";

        // 仅在提交前返回，不能用于送达不确定。/ Only returned before submission, never for uncertain delivery.
        public const string BlockedPrefix = "等待处理 VS 弹窗：";
        public static readonly TimeSpan BlockedRetryDelay = TimeSpan.FromSeconds(3);

        /// <summary>
        /// 用户正在操作电脑（或前台被系统锁定），切换 VS 前台前暂缓；仅在提交前返回，稍后自动重试。
        /// The user is working on the computer (or the foreground is locked), so switching to VS is deferred; only returned before submission and retried later.
        /// </summary>
        public const string UserBusyPrefix = "等待用户操作空闲：";

        public static bool IsUserBusy(string result) => result != null && result.StartsWith(UserBusyPrefix, StringComparison.Ordinal);

        /// <summary>
        /// 剪贴板被其他程序（如剪贴板同步 / 云桌面代理）占用，尚未向 VS 粘贴任何内容；仅在提交前返回，稍后自动重试。
        /// The clipboard is held by another program (e.g. a clipboard sync / cloud-desktop agent) and nothing has been pasted into VS yet; only returned before submission and retried later.
        /// </summary>
        public const string ClipboardBusyPrefix = "等待剪贴板空闲：";

        public static bool IsClipboardBusy(string result) => result != null && result.StartsWith(ClipboardBusyPrefix, StringComparison.Ordinal);

        /// <summary>
        /// 对话窗格未就绪：发送前已诊断原因并尝试修复（打开窗格、点「返回」等）仍失败，尚未提交任何内容。
        /// 按「诊断 → 修复 → 间隔重试」处理，最多 <see cref="MaxPaneRepairs"/> 轮，之后停止自动重发并提示原因，避免盲目重复发送。
        /// Chat pane not ready: the cause was diagnosed and a repair tried (open pane, press Back, ...) before submission, and nothing was submitted.
        /// Handled as "diagnose → repair → retry after a pause" for at most <see cref="MaxPaneRepairs"/> rounds, then automatic resending stops
        /// with the reason shown, instead of retrying blindly.
        /// </summary>
        public const string PaneNotReadyPrefix = "等待对话窗格就绪 / Chat pane not ready：";

        /// <summary>对话窗格修复的最多轮数（含首次）。/ Maximum pane repair rounds (including the first).</summary>
        public const int MaxPaneRepairs = 3;

        public static bool IsPaneNotReady(string result) => result != null && result.StartsWith(PaneNotReadyPrefix, StringComparison.Ordinal);

        /// <summary>第 n 轮修复失败后的等待间隔：越往后越长，给 VS 加载扩展留时间。/ Pause after the n-th failed repair; grows so VS has time to load the extension.</summary>
        public static TimeSpan PaneRepairDelay(int round) => TimeSpan.FromSeconds(round <= 1 ? 10 : 30);

        /// <summary>停止自动重发时附加的说明。/ Note appended when automatic resending stops.</summary>
        public static string PaneRepairExhausted(int rounds) =>
            $"；已诊断并自动修复 {rounds} 轮仍未就绪，停止自动重发（未提交任何内容）。请按上述原因处理后重新发布，不要原样重复发送"
            + $" / Diagnosed and auto-repaired {rounds} rounds without success; automatic resending stopped (nothing was submitted). Fix the cause above, then publish again instead of resending as is";

        public static bool IsBlocked(string result) =>
            ManualChatProtection.IsWait(result) || IsUserBusy(result) || IsClipboardBusy(result) || IsPaneNotReady(result)
            || (result != null && result.StartsWith(BlockedPrefix, StringComparison.Ordinal));

        /// <summary>最多尝试次数。/ Maximum number of attempts.</summary>
        public const int MaxAttempts = 1;

        /// <summary>兼容旧调用方；发送失败不再使用此间隔。/ Compatibility only; failed sends no longer use this delay.</summary>
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

        public static bool IsDelivered(string result) =>
            result != null && result.StartsWith(DeliveredPrefix, StringComparison.Ordinal);

        /// <param name="attempts">含本次在内已尝试的次数。/ Attempts made so far, including this one.</param>
        /// <param name="result">本次发送结果。/ Result of this attempt.</param>
        public static SendDecision Decide(int attempts, string result) =>
            IsDelivered(result) ? SendDecision.Delivered : IsBlocked(result) ? SendDecision.Retry : SendDecision.Fail;
    }
}
