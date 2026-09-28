using System;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 补 skill 闭环的宿主部分：校验并登记要用新 skill 验证的测试项。
    /// Host side of the skill-gap loop: validates and registers the checklist item a new skill will verify.
    /// </summary>
    public partial class MainForm : IAgentSkillGapHost
    {
        Task<Tuple<string, string>> IAgentSkillGapHost.ClaimSkillGap(int taskId, int item, string skill) => OnUi(() =>
        {
            var t = _tasks.Find(taskId);
            if (t == null) return Tuple.Create<string, string>(null, "没有任务 #" + taskId + " / No task #" + taskId);
            if (!TaskTestChecklist.Pending(t)) return Tuple.Create<string, string>(null, $"任务 #{taskId} 不在待验证状态 / Task #{taskId} is not awaiting verification.");
            var items = TaskTestChecklist.Ensure(t);
            if (item < 1 || item > items.Length || items[item - 1] == null)
                return Tuple.Create<string, string>(null, $"任务 #{taskId} 的测试清单共 {items.Length} 项，没有第 {item} 项 / The checklist has {items.Length} items; no item {item}.");
            var it = items[item - 1];
            string reason = TaskTestChecklist.SkillGapRefusal(it);
            if (reason != null) return Tuple.Create<string, string>(null, reason);
            it.Skill = skill;
            _tasks.Save();
            _taskPanel.RefreshItems();
            AppLog.Write(AppLog.TasksFile, $"AI 助手为任务 #{taskId} 测试项 {item} 发起补 skill 闭环 / Assistant started a skill-gap loop for test item {item}: " + TextUtil.Clip(skill, 200));
            return Tuple.Create<string, string>(it.Text, null);
        });

        Task IAgentSkillGapHost.ReleaseSkillGap(int taskId, int item) => OnUi(() =>
        {
            var items = _tasks.Find(taskId)?.TestItems;
            if (items != null && item >= 1 && item <= items.Length && items[item - 1] != null)
            {
                items[item - 1].Skill = null;
                _tasks.Save();
                _taskPanel.RefreshItems();
            }
            return true;
        });
    }
}
