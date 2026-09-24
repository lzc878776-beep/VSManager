using System;

namespace VSManager
{
    /// <summary>发送结果的处理方式。/ How a send result is handled.</summary>
    public enum SendDecision
    {
        /// <summary>已送达，任务进入执行中。/ Delivered; the task becomes running.</summary>
        Delivered,
        /// <summary>未送达，稍后重试。/ Not delivered; retry later.</summary>
        Retry,
        /// <summary>未送达且已达重试上限，任务失败。/ Not delivered and out of attempts; the task fails.</summary>
        Fail
    }

    /// <summary>
    /// 消息发送的重试判定：发送结果以「已发送」开头视为送达；否则在达到最多尝试次数前每 30 秒重试一次。
    /// Retry rules for sending: a result that starts with "已发送" (sent) counts as delivered; otherwise the task is retried
    /// every 30 seconds until the maximum number of attempts is reached.
    /// </summary>
    public static class SendRetryPolicy
    {
        /// <summary>送达结果的前缀（由 Copilot 发送逻辑返回）。/ Prefix of a delivered result (returned by the Copilot send logic).</summary>
        public const string DeliveredPrefix = "已发送";

        // Only returned before submission; never use this for uncertain delivery.
        public const string BlockedPrefix = "等待处理 VS 弹窗：";
        public static readonly TimeSpan BlockedRetryDelay = TimeSpan.FromSeconds(3);

        public static bool IsBlocked(string result) =>
            result != null && result.StartsWith(BlockedPrefix, StringComparison.Ordinal);

        /// <summary>最多尝试次数。/ Maximum number of attempts.</summary>
        public const int MaxAttempts = 3;

        /// <summary>两次尝试之间的间隔。/ Delay between attempts.</summary>
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

        public static bool IsDelivered(string result) =>
            result != null && result.StartsWith(DeliveredPrefix, StringComparison.Ordinal);

        /// <param name="attempts">含本次在内已尝试的次数。/ Attempts made so far, including this one.</param>
        /// <param name="result">本次发送结果。/ Result of this attempt.</param>
        public static SendDecision Decide(int attempts, string result) =>
            IsDelivered(result) ? SendDecision.Delivered : IsBlocked(result) ? SendDecision.Retry :
            attempts >= MaxAttempts ? SendDecision.Fail : SendDecision.Retry;
    }
}
