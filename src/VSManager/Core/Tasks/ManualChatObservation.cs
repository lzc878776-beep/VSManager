using System;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>只保存活动类别，不保存草稿或输入内容。/ Stores activity categories, never drafts or input content.</summary>
    public enum ManualChatObservation { Idle, Generating, Draft, Unknown, PaneMissing }

    public static class ManualChatProtection
    {
        public const int DefaultTimeoutSeconds = 300;
        public const string WaitPrefix = "等待手动对话 / Waiting for manual chat: ";
        public const string UncertainPrefix = "发送结果待核实，禁止自动重发 / Verify delivery before retry: ";
        /// <summary>空白及零宽格式字符不算草稿；保留标点、组合符、代理项和非空白控制字符。null 不代表安全空输入。/ Ignores whitespace and zero-width format characters; retains punctuation, combining marks, surrogates and non-whitespace controls. Null is not safely empty.</summary>
        public static bool IsEmptyInput(string text) => text != null && NormalizeInput(text).Length == 0;
        public static bool HasDraft(string text) => text != null && !IsEmptyInput(text);
        public static string NormalizeInput(string text) => PasteVerifier.Normalize(text, preserveControls: true);
        public static string DeliveryResult(bool touched, string result) =>
            touched && !SendRetryPolicy.IsDelivered(result) && result?.StartsWith(UncertainPrefix, StringComparison.Ordinal) != true
                ? UncertainPrefix + result : result;
        public static int ClampTimeout(int seconds) => seconds <= 0 ? DefaultTimeoutSeconds : Math.Max(10, Math.Min(86400, seconds));
        public static bool Blocks(ManualChatObservation value) => value != ManualChatObservation.Idle && value != ManualChatObservation.PaneMissing;
        public static ManualChatObservation Classify(bool generating, bool readable, bool nonempty, bool focused) =>
            generating ? ManualChatObservation.Generating : !readable ? ManualChatObservation.Unknown : nonempty ? ManualChatObservation.Draft : ManualChatObservation.Idle;
        public static string Reason(ManualChatObservation value) => value == ManualChatObservation.Generating
            ? "目标仍在忙，正在生成回复 / Target is busy generating a reply"
            : value == ManualChatObservation.Draft ? "目标输入框有未发送草稿或附件 / Target has an unsent draft or attachment"
            : "无法确认目标输入安全，继续等待 / Cannot confirm safe target input; continuing to wait";
        public static bool IsWait(string result) => result?.StartsWith(WaitPrefix, StringComparison.Ordinal) == true;
    }

    /// <summary>队列专用安全探测及发送；未实现时保守等待。/ Queue-only observation and guarded send; missing capability waits conservatively.</summary>
    public interface IManualChatDispatchHost
    {
        Task<ManualChatObservation> ObserveManualChatAsync(VsInstance target);
        Task<string> SendQueuedAsync(VsInstance target, QueuedTask task, Func<bool> stillValid);
    }

    /// <summary>用户重新检查时仅失效该目标的缓存，不绕过探测。/ Invalidates only the requested target's cache without bypassing observation.</summary>
    public interface IManualChatRefreshHost
    {
        void RefreshManualChat(VsInstance target);
    }
}
