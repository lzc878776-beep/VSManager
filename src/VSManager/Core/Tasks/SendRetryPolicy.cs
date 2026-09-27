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

        public static bool IsBlocked(string result) =>
            ManualChatProtection.IsWait(result) || (result != null && result.StartsWith(BlockedPrefix, StringComparison.Ordinal));

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
