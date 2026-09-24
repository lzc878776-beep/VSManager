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
}
