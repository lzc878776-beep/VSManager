using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>
    /// 任务自动接续：任务返回待验证或部分完成时，AI 助手判断剩余步骤并向同一 VS 发布接续任务，而不是停下来等用户。
    /// 这里负责判断是否可接续、生成通知中的接续说明与接续任务正文，以及接续链的次数上限。
    /// Task auto-continue: when a task returns awaiting verification or partially done, the AI assistant works out the remaining
    /// steps and publishes a continuation task to the same VS instead of waiting for the user. This class decides whether a task
    /// can be continued, builds the notice block and the continuation text, and enforces the chain limit.
    /// </summary>
    public static class TaskContinuation
    {
        /// <summary>同一原任务最多连续接续的次数，防止无限循环。/ Maximum continuations in one chain, preventing endless loops.</summary>
        public const int MaxDepth = 3;
        /// <summary>上限的文字形式（用于特性说明）。/ The limit as text (for attribute descriptions).</summary>
        public const string MaxDepthText = "3";

        /// <summary>接续任务正文的开头标记。/ Prefix marking a continuation task's text.</summary>
        public const string Marker = "【接续任务】";

        // 已完成的回复里提到还有剩余工作：视为部分完成 / A completed reply that mentions remaining work counts as partially done
        private static readonly Regex PartialHint = new Regex(
            @"部分完成|尚未完成|还未完成|没有完成|未完成的|剩余(?:的)?(?:步骤|工作|部分|任务|事项)|后续(?:步骤|工作|还需|需要)|还需要(?:继续|再|补)|下一步(?:需要|要|可以|建议)|待(?:后续|下一轮)|TODO|" +
            @"partially (?:done|complete|implemented)|not (?:yet )?(?:done|finished|implemented|completed)|remaining (?:steps|work|parts?|items)|next steps?|still (?:needs?|to do)|follow-?up (?:work|steps)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 只写「继续」之类没有具体内容的剩余步骤会被拒绝 / Remaining steps that only say "continue" are refused
        private static readonly Regex GenericRemaining = new Regex(
            @"^\s*(?:请)?(?:继续|接着做|接着|继续完成|完成剩余(?:部分|步骤)?|剩余步骤|please )?(?:continue|go on|finish(?: the rest)?)?\s*[。.!！]*\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>已完成的回复是否提到还有剩余工作（部分完成）。/ Whether a completed reply mentions remaining work (partially done).</summary>
        public static bool LooksPartial(string result) => !string.IsNullOrWhiteSpace(result) && PartialHint.IsMatch(result);

        /// <summary>
        /// 是否属于接续候选：待验证，或已完成但回复提到还有剩余工作。
        /// Whether the task is a continuation candidate: awaiting verification, or completed with a reply mentioning remaining work.
        /// </summary>
        public static bool Candidate(QueuedTask t) =>
            t != null && (t.Status == QueueStatus.Unverified || (t.Status == QueueStatus.Done && LooksPartial(t.FullResult ?? t.Result)));

        /// <summary>
        /// 完成通知中附加的「[自动接续]」说明（中英）；不是候选时返回 null。
        /// The "[自动接续 / Auto-continue]" block appended to completion notices (zh + en); null when the task is not a candidate.
        /// </summary>
        public static string Notice(QueuedTask t)
        {
            if (!Candidate(t)) return null;
            string state = t.Status == QueueStatus.Unverified ? "待验证" : "已完成但回复提到还有剩余步骤";
            string stateEn = t.Status == QueueStatus.Unverified ? "awaiting verification" : "completed, but the reply mentions remaining steps";
            int depth = Math.Max(0, t.ContinuationDepth);
            if (depth >= MaxDepth)
                return $"[自动接续 / Auto-continue] 任务 #{t.Id} {state}，接续已达上限 {MaxDepth} 次：不要再调用 continue_task，把剩余步骤与未验证项如实交给用户。/ "
                    + $"Task #{t.Id} is {stateEn} and its chain reached the limit of {MaxDepth} continuations: do not call continue_task again; hand the remaining steps and unverified items to the user.";
            return $"[自动接续 / Auto-continue] 任务 #{t.Id} {state}（已接续 {depth}/{MaxDepth} 次）。不要停下来等用户，先按上文验证 [AI] 项，再判断剩余步骤：" +
                $"① 目标 VS 自己能完成的剩余工作（回复或「待处理」中列出的未完成部分、后续步骤、还需补的代码 / 编译 / 单元测试 / 文档）→ 调用 continue_task（id={t.Id}，remaining 写清具体剩余步骤）发布接续任务；" +
                "② [AI] 项验证不通过、属于本次改动本身的问题 → 用 retry_task_with_info 带证据重试原任务，不用 continue_task；" +
                "③ 只剩 [人工] 项、只有用户才有的信息或需要用户授权的操作 → 不发布接续，告诉用户需要做什么；" +
                "④ 已全部完成 → 不发布接续，简要汇报。/ " +
                $"Task #{t.Id} is {stateEn} (continued {depth}/{MaxDepth}). Do not stop to wait for the user: verify the [AI] items as described above, then judge what remains: " +
                $"(1) remaining work the target VS can do itself (unfinished parts, follow-up steps, missing code / build / unit tests / docs listed in the reply or the pending note) → call continue_task (id={t.Id}, remaining = the concrete steps) to publish a continuation; " +
                "(2) an [AI] item fails because of this change itself → retry the original task with retry_task_with_info and the evidence, not continue_task; " +
                "(3) only [人工] items, information only the user has, or actions needing the user's authorization remain → publish nothing and tell the user what to do; " +
                "(4) everything is done → publish nothing and report briefly.";
        }

        /// <summary>
        /// 检查能否为 <paramref name="parent"/> 发布接续任务，不能时返回中英原因，可以时返回 null。
        /// Checks whether a continuation can be published for <paramref name="parent"/>; returns a bilingual reason when not, otherwise null.
        /// </summary>
        public static string Check(IEnumerable<QueuedTask> all, QueuedTask parent, int id, string remaining)
        {
            if (parent == null) return $"没有任务 #{id} / No task #{id}";
            if (parent.Status != QueueStatus.Unverified && parent.Status != QueueStatus.Done)
                return $"任务 #{id} 不是待验证或已完成状态，不能接续；失败的任务请用 retry_task_with_info / Task #{id} is neither awaiting verification nor done and cannot be continued; retry failed tasks with retry_task_with_info";
            string r = (remaining ?? "").Trim();
            if (r.Length < 6 || GenericRemaining.IsMatch(r))
                return "remaining 必须写清具体的剩余步骤（做什么、改哪里、如何验收），只写「继续」会被拒绝 / remaining must state the concrete remaining steps (what, where, how to accept); a bare \"continue\" is refused";
            if (parent.ContinuationDepth >= MaxDepth)
                return $"任务 #{id} 所在接续链已达上限 {MaxDepth} 次，请把剩余步骤交给用户 / The chain of task #{id} reached the limit of {MaxDepth} continuations; hand the remaining steps to the user";
            var child = (all ?? Enumerable.Empty<QueuedTask>()).FirstOrDefault(x => x != null && x.ContinuedFrom == parent.Id && x.Status != QueueStatus.Cancelled);
            if (child != null)
                return $"任务 #{id} 已有接续任务 #{child.Id}，不要重复接续；需要调整时对 #{child.Id} 处理 / Task #{id} already has continuation #{child.Id}; do not continue it twice, work on #{child.Id} instead";
            return null;
        }

        /// <summary>
        /// 接续任务正文（单行）：原任务摘要、上一轮结果摘要与本轮剩余步骤。
        /// Continuation text (one line): the original task summary, the previous result summary and the remaining steps.
        /// </summary>
        public static string Compose(QueuedTask parent, string remaining)
        {
            int depth = Math.Max(0, parent.ContinuationDepth) + 1;
            string title = string.IsNullOrEmpty(parent.Title) ? "" : "「" + parent.Title + "」";
            string state = parent.Status == QueueStatus.Unverified ? "待验证" : "已完成（部分）";
            return Marker + $"接续任务 #{parent.Id}{title}（第 {depth} 次接续，上限 {MaxDepth} 次）。" +
                "原任务：" + OneLine(StripSuffix(parent.Text), 300) + "。" +
                $"上一轮结果（{state}）：" + OneLine(parent.Result, 500) + "。" +
                "本轮只完成以下剩余步骤：" + OneLine(remaining, 1500) + "。" +
                "已完成的部分不要重做；需要用户操作或观察的测试项不要自行模拟，写进测试清单。";
        }

        /// <summary>去掉原任务末尾自动附加的开源约束（接续任务会重新附加）。/ Drops the auto-appended open-source constraint (the continuation appends its own).</summary>
        private static string StripSuffix(string text)
        {
            string s = text ?? "";
            foreach (string mark in new[] { "【开源约束】", "[Open-source constraint]" })
            {
                int i = s.IndexOf(mark, StringComparison.Ordinal);
                if (i >= 0) s = s.Substring(0, i);
            }
            return s.Trim();
        }

        private static string OneLine(string text, int max)
        {
            string s = Regex.Replace((text ?? "").Trim(), @"\s*[\r\n]+\s*", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
