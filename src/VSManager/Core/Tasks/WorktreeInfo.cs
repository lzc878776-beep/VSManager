using System;
using System.Runtime.Serialization;

namespace VSManager
{
    [DataContract]
    public sealed class WorktreeInfo
    {
        [DataMember] public string MainRoot;
        [DataMember] public string Root;
        [DataMember] public string SolutionPath;
        [DataMember] public string MainBranch;
        [DataMember] public string Branch;
        public WorktreeInfo Clone() => (WorktreeInfo)MemberwiseClone();
        public static bool Same(WorktreeInfo a, WorktreeInfo b) => a != null && b != null
            && string.Equals(a.Root, b.Root, StringComparison.OrdinalIgnoreCase);

        public const string DevelopmentInstructions = "【Worktree】仅在此工作树的任务分支开发；开始时必须干净，仅提交本任务修改，禁止提交无关用户修改。完成前验证并提交，保持工作树干净，不切换分支、不推送、不操作主项目。 / Develop only on this worktree's task branch; start clean, commit only this task's changes after validation, leave it clean. Never switch branches, push, or modify the main checkout.";
        public const string MergeInstructions = "【Worktree 本地推送/合并任务】主项目尚未更新。仅在当前 worktree 中解决现有 Git 合并冲突，保留双方意图，验证后仅提交冲突解决结果。不要中止合并、切换分支、操作主项目或远程推送。无法确定时报告失败。VSManager 会验证干净状态及祖先关系并在主项目快进；仅回复成功不代表主项目已更新。 / Resolve the existing merge in this worktree only, preserve both sides, validate and commit the resolution. Never abort, switch branches, touch the main checkout or push remotely. VSManager verifies and fast-forwards the main checkout itself.";
    }
}
