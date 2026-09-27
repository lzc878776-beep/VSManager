using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>目标 VS 最终回复的结论。/ Outcome of the target VS's final reply.</summary>
    public enum TaskReplyOutcome
    {
        /// <summary>失败或无法确认。/ Failed or unconfirmed.</summary>
        Failed,
        /// <summary>全部成功完成。/ Fully succeeded.</summary>
        Succeeded,
        /// <summary>已实现，仅未在运行中的程序里实际验证。/ Implemented; only not verified in the running app.</summary>
        Unverified
    }

    /// <summary>
    /// 阅读目标 VS 的最终反馈文字：若说明功能已实现、只是尚未在运行中的程序里实际验证，则按「未验证」处理而非失败。
    /// Reads the target VS's final feedback: when it says the work is implemented and only runtime verification is missing,
    /// the task is treated as unverified rather than failed.
    /// </summary>
    public static class TaskReplyVerdict
    {
        private static readonly Regex NotVerified = new Regex(
            @"未验证|尚未.{0,16}验证|没有?.{0,12}(实际|真实|手动|运行).{0,6}验证|未实测|没.{0,4}实测|还没.{0,8}实用|unverified|not (yet )?(been )?(manually )?verified|not (yet )?tested in the running",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Implemented = new Regex(
            @"已实现|实现完毕|已经实现|都已完成|已全部完成|改动已完成|测试.{0,12}通过|全部通过|构建成功|编译通过|生成成功|implemented|tests? (all )?pass|tests passed|build succeeded",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex HardFailure = new Regex(
            @"构建失败|编译失败|编译错误|生成失败|测试未通过|测试失败|未能实现|无法实现|没有实现|尚未实现|未实现|需要你.{0,6}(决定|处理|选择|提供|授权)|请你?.{0,4}(决定|选择|提供)|冲突未解决|权限不足|build failed|compil(e|ation) (error|failed)|tests? failed|failing tests?|not implemented|could not (build|compile|implement|complete)|unable to (build|compile|implement|complete)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 反馈是否表示「已实现，只差运行中验证」：须同时提到未验证与已实现 / 测试通过，且没有构建失败、未实现或需要用户决定等硬性失败。
        /// Whether the feedback means "implemented, only runtime verification missing": it must mention both missing verification
        /// and implementation / passing tests, without hard failures such as build errors, missing implementation or user decisions.
        /// </summary>
        public static bool OnlyRuntimeUnverified(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return false;
            return NotVerified.IsMatch(reply) && Implemented.IsMatch(reply) && !HardFailure.IsMatch(reply);
        }
    }
}