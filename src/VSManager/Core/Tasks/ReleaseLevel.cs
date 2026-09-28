using System;

namespace VSManager
{
    /// <summary>
    /// 任务队列的接续等级（四档）：前序任务以什么结果结束时，同一目标的后续任务可以自动发布；被阻塞时等待用户处理，
    /// 避免需要用户处理的内容被后续任务覆盖对话上下文。排队中 / 发送中 / 执行中的前序始终阻塞后续。
    /// Continuation level of the task queue (four stops): which predecessor outcomes let successors on the same target
    /// dispatch automatically; a blocked queue waits for the user so that content needing the user is not buried by later
    /// tasks. Waiting / sending / running predecessors always block successors.
    /// </summary>
    public enum ReleaseLevel
    {
        /// <summary>已完成：只有成功完成才放行；待确认与失败都会阻塞后续。/ Completed: only success releases; awaiting confirmation and failures block successors.</summary>
        Completed = 0,
        /// <summary>待确认：待确认（需要用户测试或确认）阻塞后续；成功与失败放行。/ Awaiting confirmation: it blocks successors; success and failure release.</summary>
        NeedsUser = 1,
        /// <summary>失败：失败阻塞后续；成功与待确认放行。/ Failed: a failure blocks successors; success and awaiting confirmation release.</summary>
        Failed = 2,
        /// <summary>不限：任何结束结果都放行（默认，与旧版「跳过失败前序」一致）。/ Unlimited: any finished outcome releases (default, same as the legacy "skip failed predecessors").</summary>
        Unlimited = 3
    }

    /// <summary>接续等级的存储键、解析与显示文字。/ Storage keys, parsing and display text of continuation levels.</summary>
    public static class ReleaseLevels
    {
        public const string CompletedKey = "completed", NeedsUserKey = "needs_user", FailedKey = "failed", UnlimitedKey = "unlimited";

        /// <summary>默认等级：沿用旧版「跳过失败前序」的默认行为（不阻塞）。/ Default level: keeps the old "skip failed predecessors" default (never blocks).</summary>
        public const ReleaseLevel Default = ReleaseLevel.Unlimited;

        public static readonly ReleaseLevel[] All = { ReleaseLevel.Completed, ReleaseLevel.NeedsUser, ReleaseLevel.Failed, ReleaseLevel.Unlimited };

        public static string Key(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed: return CompletedKey;
                case ReleaseLevel.NeedsUser: return NeedsUserKey;
                case ReleaseLevel.Failed: return FailedKey;
                default: return UnlimitedKey;
            }
        }

        /// <summary>
        /// 解析存储键、英文名、中文名或 0–3 的数字；无法识别时返回 null。
        /// Parses a storage key, English name, Chinese name or a number 0-3; null when unrecognized.
        /// </summary>
        public static ReleaseLevel? TryParse(string text)
        {
            string s = (text ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            switch (s)
            {
                case "0": case CompletedKey: case "done": case "success": case "已完成": case "完成": case "成功":
                    return ReleaseLevel.Completed;
                case "1": case NeedsUserKey: case "needsuser": case "confirm": case "verify": case "awaiting_confirmation": case "awaiting_verification":
                case "待确认": case "待验证": case "待用户验证": case "确认": case "验证":
                    return ReleaseLevel.NeedsUser;
                case "2": case FailedKey: case "fail": case "failure": case "失败":
                    return ReleaseLevel.Failed;
                case "3": case UnlimitedKey: case "any": case "none": case "always": case "no_limit": case "nolimit": case "不限": case "无限制": case "全部放行":
                    return ReleaseLevel.Unlimited;
                default: return null;
            }
        }

        /// <summary>
        /// 解析三档时代的旧存储键（needs_user = 失败阻塞，failed = 全部放行），按原行为映射到四档。
        /// Parses storage keys from the three-stop era (needs_user = failures block, failed = release all), mapped by behavior.
        /// </summary>
        public static ReleaseLevel? TryParseLegacy(string key)
        {
            switch ((key ?? "").Trim().ToLowerInvariant())
            {
                case CompletedKey: return ReleaseLevel.Completed;
                case NeedsUserKey: return ReleaseLevel.Failed;
                case FailedKey: return ReleaseLevel.Unlimited;
                default: return null;
            }
        }

        /// <summary>界面刻度上的短名（中文）。/ Short Chinese name shown on the slider ticks.</summary>
        public static string ShortName(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed: return "已完成";
                case ReleaseLevel.NeedsUser: return "待确认";
                case ReleaseLevel.Failed: return "失败";
                default: return "不限";
            }
        }

        /// <summary>英文短名。/ Short English name.</summary>
        public static string ShortNameEn(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed: return "Completed";
                case ReleaseLevel.NeedsUser: return "Awaiting confirmation";
                case ReleaseLevel.Failed: return "Failed";
                default: return "Unlimited";
            }
        }

        /// <summary>等级含义（中英）。/ Meaning of the level (zh + en).</summary>
        public static string Describe(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed:
                    return "接续等级「已完成」：只有成功完成才自动执行下一项，待确认（含未验证）或失败都会阻塞同一 VS 的后续任务，等待用户处理 / "
                        + "Continuation level \"Completed\": only a successful task releases the next one; awaiting confirmation (unverified included) or failure blocks successors on the same VS until the user handles it";
                case ReleaseLevel.NeedsUser:
                    return "接续等级「待确认」：需要用户测试或确认的结果（待验证、未验证）会阻塞同一 VS 的后续任务，等待用户处理；成功与失败自动执行下一项 / "
                        + "Continuation level \"Awaiting confirmation\": a result that needs user testing or confirmation (awaiting verification or unverified) blocks successors on the same VS until the user handles it; success and failure release the next one";
                case ReleaseLevel.Failed:
                    return "接续等级「失败」：失败会阻塞同一 VS 的后续任务，等待用户处理；已完成与待确认自动执行下一项 / "
                        + "Continuation level \"Failed\": a failure blocks successors on the same VS until the user handles it; completed and awaiting-confirmation tasks release the next one";
                default:
                    return "接续等级「不限」：无论成功、待确认还是失败都自动执行下一项，失败记录保留 / "
                        + "Continuation level \"Unlimited\": the next task runs whether the previous one succeeded, awaits confirmation or failed; failure records are kept";
            }
        }

        /// <summary>入队提示中的接续说明（中英）。/ Continuation note for the enqueue message (zh + en).</summary>
        public static string QueuedNote(ReleaseLevel level)
        {
            switch (level)
            {
                case ReleaseLevel.Completed: return "失败或待确认的前序会阻塞后续 / Failed or awaiting-confirmation predecessors block successors";
                case ReleaseLevel.NeedsUser: return "待确认的前序会阻塞后续 / Awaiting-confirmation predecessors block successors";
                case ReleaseLevel.Failed: return "失败的前序会阻塞后续 / Failed predecessors block successors";
                default: return "前序结束后自动推送，失败跳过 / Dispatch after predecessors finish, skipping failures";
            }
        }

        /// <summary>该等级下，已结束的前序是否仍阻塞后续
        public static bool Blocks(ReleaseLevel level, QueuedTask finished)
        {
            if (finished == null) return false;
            if (finished.Status == QueueStatus.Failed) return BlocksFailures(level);
            // 未验证与待用户验证同属「待确认」/ Unverified counts as awaiting confirmation, like awaiting user verification
            if ((finished.Status == QueueStatus.Done && finished.NeedsUser) || finished.Status == QueueStatus.Unverified) return BlocksNeedsUser(level);
            return false;
        }

        /// <summary>该等级下失败是否阻塞后续。/ Whether failures block successors at this level.</summary>
        public static bool BlocksFailures(ReleaseLevel level) => level == ReleaseLevel.Completed || level == ReleaseLevel.Failed;

        /// <summary>该等级下待确认是否阻塞后续。/ Whether awaiting confirmation blocks successors at this level.</summary>
        public static bool BlocksNeedsUser(ReleaseLevel level) => level == ReleaseLevel.Completed || level == ReleaseLevel.NeedsUser;

        /// <summary>旧版布尔开关对应的等级：开启 = 不限，关闭 = 失败。/ Level matching the legacy boolean switch: on = Unlimited, off = Failed.</summary>
        public static ReleaseLevel FromSkipFailed(bool skipFailedPredecessors) =>
            skipFailedPredecessors ? ReleaseLevel.Unlimited : ReleaseLevel.Failed;

        public static ReleaseLevel Clamp(int value) =>
            value <= 0 ? ReleaseLevel.Completed : value >= 3 ? ReleaseLevel.Unlimited : (ReleaseLevel)value;

        internal static bool IsDefined(ReleaseLevel level) => Enum.IsDefined(typeof(ReleaseLevel), level);
    }
}
