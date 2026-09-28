using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 任务队列放行等级：顶栏滑块、任务菜单与 AI 工具共用的宿主实现。
    /// Task queue release level: host implementation shared by the header slider, the task menu and the AI tools.
    /// </summary>
    public partial class MainForm : IAgentReleaseHost
    {
        /// <summary>切换放行等级：保存设置、同步滑块、刷新任务清单并重新调度。/ Switches the level: saves, syncs the slider, refreshes the list and re-pumps.</summary>
        private string ApplyReleaseLevel(ReleaseLevel level, string by)
        {
            var old = _settings.ReleaseLevel;
            _settings.ReleaseLevel = level;
            _settings.Save();
            _agentPanel.SetReleaseLevel(level);
            _taskPanel.SetReleaseLevel(level);
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
            // AI 自主重试：限制次数并要求有新信息 / AI self-retries: capped and must carry new information
            if (TaskFailureAnalyzer.CheckAiRetry(_tasks.Items, t, info) is string refused)
            {
                AppLog.Write(AppLog.TasksFile, $"拒绝 AI 补充重试 #{id} / Refused AI retry: " + TextUtil.Clip(refused, 200));
                return refused;
            }
            if (!RetryWithInfo(t, info, out string error)) return $"任务 #{id} 当前{StatusText(t)}：{error}";
            return $"已为任务 #{id} 插入补充信息并重新排队（第 {t.SupplementCount}/{TaskStateMachine.MaxSupplements} 次），完成后会再通知你 / "
                + $"Task #{id} requeued with info ({t.SupplementCount}/{TaskStateMachine.MaxSupplements}); you will be notified";
        });

        Task<string> IAgentReleaseHost.RetryTask(int id) => OnUi(() =>
        {
            var t = _tasks.Find(id);
            if (t == null) return "没有任务 #" + id + " / No task #" + id;
            // 只允许非内容类失败，且每个任务有直接重试上限 / Non-content failures only, with a per-task direct-retry cap
            if (TaskFailureAnalyzer.CheckRecoveryRetry(t) is string refused)
            {
                AppLog.Write(AppLog.TasksFile, $"拒绝 AI 直接重试 #{id} / Refused AI retry: " + TextUtil.Clip(refused, 200));
                return refused;
            }
            if (TaskHideList.Remove(_settings.HiddenResentTasks, t.Id)) _settings.Save();
            t.RecoveryRetries++;
            _dispatcher.Retry(t);
            _taskPanel.RefreshItems();
            if (t.Status == QueueStatus.Failed)
            {
                t.RecoveryRetries--;
                return $"任务 #{id} 暂时不能重新排队（任务清单未开始或正在处理），请稍后再试或交给用户 / Task #{id} cannot be requeued right now (task list not started or busy); try later or hand it to the user.";
            }
            return $"已原样重新排队任务 #{id}（直接重试第 {t.RecoveryRetries}/{TaskFailureAnalyzer.MaxRecoveryRetries} 次，不计入执行次数），完成后会再通知你 / "
                + $"Task #{id} requeued unchanged (direct retry {t.RecoveryRetries}/{TaskFailureAnalyzer.MaxRecoveryRetries}, not counted as a run); you will be notified";
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
