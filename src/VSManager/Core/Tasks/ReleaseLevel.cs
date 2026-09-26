using System;

namespace VSManager
{
    /// <summary>
    /// 任务队列的放行等级：前序任务以什么结果结束时，同一目标的后续任务可以自动发布。
    /// 等级越高越宽松；排队中 / 发送中 / 执行中的前序始终阻塞后续。
    /// Release level of the task queue: which predecessor outcomes let successors on the same target dispatch automatically.
    /// Higher levels are more permissive; waiting / sending / running predecessors always block successors.
    /// </summary>
    public enum ReleaseLevel
    {
        /// <summary>已完成：只有成功完成才放行；待验证与失败都会暂停后续。/ Completed: only success releases; awaiting verification and failures pause successors.</summary>
        Completed = 0,
        /// <summary>待验证：已完成与待验证都放行；失败会暂停后续。/ Awaiting verification: success and awaiting verification release; failures pause successors.</summary>
        NeedsUser = 1,
        /// <summary>失败：任何结束结果都放行，失败也继续执行下一项（默认）。/ Failed: any finished outcome releases, even failures (default).</summary>
        Failed = 2
    }

    /// <summary>放行等级的存储键、解析与显示文字。/ Storage keys, parsing and display text of release levels.</summary>
    public static class ReleaseLevels
    {
        public const string CompletedKey = "completed", NeedsUserKey = "needs_user", FailedKey = "failed";

        /// <summary>默认等级：沿用旧版「跳过失败前序」的默认行为。/ Default level: keeps the old "skip failed predecessors" default.</summary>
        public const ReleaseLevel Default = ReleaseLevel.Failed;

        public static readonly ReleaseLevel[] All = { ReleaseLevel.Completed, ReleaseLevel.NeedsUser, ReleaseLevel.Failed };

        public static string Key(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed: return CompletedKey;
                case ReleaseLevel.NeedsUser: return NeedsUserKey;
                default: return FailedKey;
            }
        }

        /// <summary>
        /// 解析存储键、英文名、中文名或 0–2 的数字；无法识别时返回 null。
        /// Parses a storage key, English name, Chinese name or a number 0-2; null when unrecognized.
        /// </summary>
        public static ReleaseLevel? TryParse(string text)
        {
            string s = (text ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            switch (s)
            {
                case "0": case CompletedKey: case "done": case "success": case "已完成": case "完成": case "成功":
                    return ReleaseLevel.Completed;
                case "1": case NeedsUserKey: case "needsuser": case "verify": case "awaiting_verification": case "待验证": case "待用户验证": case "验证":
                    return ReleaseLevel.NeedsUser;
                case "2": case FailedKey: case "fail": case "failure": case "失败":
                    return ReleaseLevel.Failed;
                default: return null;
            }
        }

        /// <summary>界面刻度上的短名（中文）。/ Short Chinese name shown on the slider ticks.</summary>
        public static string ShortName(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed: return "已完成";
                case ReleaseLevel.NeedsUser: return "待验证";
                default: return "失败";
            }
        }

        /// <summary>英文短名。/ Short English name.</summary>
        public static string ShortNameEn(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed: return "Completed";
                case ReleaseLevel.NeedsUser: return "Awaiting verification";
                default: return "Failed";
            }
        }

        /// <summary>等级含义（中英）。/ Meaning of the level (zh + en).</summary>
        public static string Describe(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed:
                    return "放行等级「已完成」：只有成功完成才自动执行下一项，待验证或失败都会暂停同一 VS 的后续任务 / "
                        + "Release level \"Completed\": only a successful task releases the next one; awaiting verification or failure pauses successors on the same VS";
                case ReleaseLevel.NeedsUser:
                    return "放行等级「待验证」：已完成与待验证自动执行下一项，失败会暂停同一 VS 的后续任务 / "
                        + "Release level \"Awaiting verification\": completed and awaiting-verification tasks release the next one; a failure pauses successors on the same VS";
                default:
                    return "放行等级「失败」：无论成功、待验证还是失败都自动执行下一项，失败记录保留 / "
                        + "Release level \"Failed\": the next task runs whether the previous one succeeded, awaits verification or failed; failure records are kept";
            }
        }

        /// <summary>该等级下，已结束的前序是否仍阻塞后续（不含已放行与已被取代的判断）。/ Whether a finished predecessor still blocks successors at this level (release and supersede checks excluded).</summary>
        public static bool Blocks(ReleaseLevel level, QueuedTask finished)
        {
            if (finished == null) return false;
            if (finished.Status == QueueStatus.Failed) return level < ReleaseLevel.Failed;
            if (finished.Status == QueueStatus.Done && finished.NeedsUser) return level < ReleaseLevel.NeedsUser;
            return false;
        }

        /// <summary>旧版布尔开关对应的等级：开启 = 失败，关闭 = 待验证。/ Level matching the legacy boolean switch: on = Failed, off = NeedsUser.</summary>
        public static ReleaseLevel FromSkipFailed(bool skipFailedPredecessors) =>
            skipFailedPredecessors ? ReleaseLevel.Failed : ReleaseLevel.NeedsUser;

        public static ReleaseLevel Clamp(int value) =>
            value <= 0 ? ReleaseLevel.Completed : value == 1 ? ReleaseLevel.NeedsUser : ReleaseLevel.Failed;

        internal static bool IsDefined(ReleaseLevel level) => Enum.IsDefined(typeof(ReleaseLevel), level);
    }
}
