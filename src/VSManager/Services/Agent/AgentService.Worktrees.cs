using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    public sealed partial class AgentService
    {
        [Description("仅在用户明确要求 worktree 工作线时调用：从已登记的主项目当前分支创建独立 task/名字 分支和 项目目录.worktree.名字，登记并打开该工作树的 VS。源仓库和共同父目录必须已在文件授权范围。每5个成功且已提交的开发任务自动插入本地合并任务；失败/取消阻塞后续任务。冲突仅在工作树解决；从不远程 push。随后用返回的精确别名 send_task。 / Explicit opt-in only: create, register and open an isolated worktree. Requires grants for source and parent; every five successful committed tasks inserts a blocking local integration task, never a remote push. Use the returned exact alias with send_task.")]
        internal async Task<string> CreateWorktree(
            [Description("已登记主项目的精确别名 / Exact registered main-project alias")] string project,
            [Description("工作线名字，1–40位字母数字、下划线或横线 / Lane name, 1–40 ASCII letters, digits, underscore or hyphen")] string name)
        {
            var source = _host.Solutions.Items.FirstOrDefault(e => string.Equals(e.Alias, project, StringComparison.OrdinalIgnoreCase));
            if (source == null || source.Worktree != null) return "请选择已登记的主项目精确别名 / Select an exact registered main-project alias";
            string alias = source.Alias + ".worktree." + name;
            if (_host.Solutions.Items.Any(e => string.Equals(e.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                return "工作线已登记；请使用 list_worktrees 查询后 send_task / Already registered; use list_worktrees then send_task";
            if (_settings().AgentConfirm && !await ConfirmAsync("创建 Worktree / Create worktree", alias + "\n每5个成功任务合并回本地主项目，不推送远程 / Integrate locally every five successful tasks; no remote push"))
                return "用户拒绝了该操作 / Denied";
            try
            {
                var service = new WorktreeService(FileRoots);
                var info = await Task.Run(() => service.Create(source.Path, name));
                string error = _host.Solutions.Upsert(new SolutionEntry
                {
                    Alias = alias, Path = info.SolutionPath, Worktree = info,
                    Description = "Worktree: " + info.Branch + " → " + info.MainBranch + " (5 tasks / 本地合并 local integration)"
                });
                if (error != null) return "工作树已创建，但登记保存失败；请先恢复登记再派发任务 / Worktree created but registry persistence failed: " + error;
                error = await _host.LaunchSolution(info.SolutionPath);
                return "工作线已创建并持久化 / Worktree registered: " + alias + "\n" + info.Root + "\n" + info.Branch + " → " + info.MainBranch
                    + "\n使用 send_task 的 vs 精确填写 / Exact send_task target: " + alias
                    + (error == null ? "\n正在打开 VS；任务可暂存，打开后自动发布 / Opening VS; tasks may be parked until ready" : "\nVS 打开失败，可用 open_solution 重试 / Open failed; retry open_solution: " + error);
            }
            catch (Exception ex) { return "创建工作树失败；不会覆盖或删除已有目录 / Worktree creation failed; existing directories retained: " + ex.Message; }
        }

        [Description("查询已登记 worktree 工作线、任务分支、主分支和 send_task 精确别名。任务计数、合并屏障和错误另用 list_tasks 查询。 / List registered worktree lanes, branches and exact send_task aliases; use list_tasks for batch tasks, barriers and failures.")]
        internal string ListWorktrees()
        {
            var entries = _host.Solutions.Items.Where(e => e.Worktree != null).ToList();
            return entries.Count == 0 ? "尚无 worktree 工作线 / No worktree lanes" : Truncate(string.Join("\n", entries.Select(e =>
                e.Alias + " | " + e.Path + " | " + e.Worktree.Branch + " → " + e.Worktree.MainBranch + " | 每5个成功任务本地合并 / integrate every 5 successes")), MaxToolText);
        }
    }
}
