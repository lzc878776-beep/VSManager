using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 任务队列放行等级：顶栏滑块、任务菜单与 AI 工具共用的宿主实现。
    /// Task queue release level: host implementation shared by the header slider, the task menu and the AI tools.
    /// </summary>
    public partial class MainForm : IAgentReleaseHost, IAgentTaskResultHost
    {
        /// <summary>切换放行等级：保存设置、同步滑块、刷新任务清单并重新调度。/ Switches the level: saves, syncs the slider, refreshes the list and re-pumps.</summary>
        private string ApplyReleaseLevel(ReleaseLevel level, string by)
        {
            var old = _settings.ReleaseLevel;
            _settings.ReleaseLevel = level;
            _settings.Save();
            _agentPanel.SetReleaseLevel(level);
            _taskPanel.RefreshItems();
            string text = ReleaseLevels.Describe(level);
            if (old != level)
            {
                AppLog.Write(AppLog.TasksFile, $"{by}修改放行等级 / release level changed: {ReleaseLevels.Key(old)} → {ReleaseLevels.Key(level)}");
                SetStatus(text);
            }
            PumpTasks();
            return text;
        }

        ReleaseLevel IAgentReleaseHost.ReleaseLevel => _settings.ReleaseLevel;

        Task<string> IAgentReleaseHost.SetReleaseLevel(ReleaseLevel level) =>
            OnUi(() => "已设置 / Set. " + ApplyReleaseLevel(level, "AI "));

        Task<string> IAgentReleaseHost.ReleaseTask(int id) => OnUi(() =>
        {
            var t = _tasks.Find(id);
            if (t == null) return "没有任务 #" + id + " / No task #" + id;
            if (!_dispatcher.Release(t, out string error)) return $"任务 #{id} 当前{StatusText(t)}：{error}";
            _taskPanel.RefreshItems();
            int next = _tasks.Items.Count(x => x.Id > id && QueueStatus.Active(x.Status) && ResentTaskMatcher.SameTarget(x, t));
            return $"已放行任务 #{id}，结果保持不变；同一 VS 还有 {next} 个后续任务将继续 / Task #{id} released; {next} successors continue";
        });

        Task<string> IAgentReleaseHost.RetryTaskWithInfo(int id, string info) => OnUi(() =>
        {
            var t = _tasks.Find(id);
            if (t == null) return "没有任务 #" + id + " / No task #" + id;
            if (!RetryWithInfo(t, info, out string error)) return $"任务 #{id} 当前{StatusText(t)}：{error}";
            return $"已为任务 #{id} 插入补充信息并重新排队（第 {t.SupplementCount}/{TaskStateMachine.MaxSupplements} 次），完成后会再通知你 / "
                + $"Task #{id} requeued with info ({t.SupplementCount}/{TaskStateMachine.MaxSupplements}); you will be notified";
        });

        Task<string> IAgentTaskResultHost.EditTaskResult(int id, string text) => OnUi(() =>
        {
            if (!_tasks.EditResult(id, text, out string error)) return error;
            _taskPanel.RefreshItems();
            var t = _tasks.Find(id);
            SetStatus($"AI 助手已修改任务 #{id} 的结果文字 / The assistant edited the result of task #{id}");
            string checklist = t?.TestItems != null && TaskTestChecklist.Pending(t)
                ? $"；测试清单 {t.TestItems.Length} 项，剩 {TaskTestChecklist.Remaining(t)} 项未勾选 / checklist {t.TestItems.Length} items, {TaskTestChecklist.Remaining(t)} unchecked" : "";
            return $"已修改任务 #{id} 的结果文字并保存，状态与排队不变{checklist} / Result of task #{id} edited and saved; status and queue unchanged";
        });

        private bool RetryWithInfo(QueuedTask t, string info, out string error)
        {
            // 重试复用原条目，不再隐藏 / Retrying reuses the entry, which is no longer hidden
            if (TaskHideList.Remove(_settings.HiddenResentTasks, t.Id)) _settings.Save();
            bool ok = _dispatcher.RetryWithInfo(t, info, out error);
            _taskPanel.RefreshItems();
            return ok;
        }

        /// <summary>任务菜单「补充信息后重试」：弹出输入框。/ Task menu "Retry with info": shows an input dialog.</summary>
        private void PromptSupplement(QueuedTask t)
        {
            if (!TaskStateMachine.IsHoldOutcome(t)) { SetStatus("只有失败或待验证的任务可以补充信息重试 / Only failed or awaiting-verification tasks can be retried with info"); return; }
            using (var f = new SupplementForm(t))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                SetStatus(RetryWithInfo(t, f.Info, out string error)
                    ? $"任务 #{t.Id} 已插入补充信息并重新排队 / Task #{t.Id} requeued with info"
                    : error);
            }
        }
    }
}
