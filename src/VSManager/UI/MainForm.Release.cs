using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 任务队列放行等级：顶栏滑块、任务菜单与 AI 工具共用的宿主实现。
    /// Task queue release level: host implementation shared by the header slider, the task menu and the AI tools.
    /// </summary>
    public partial class MainForm : IAgentReleaseHost, IAgentTaskResultHost, IAgentWorkflowHost, IAgentTaskControlHost, IAgentBlockedTaskHost, IAgentContinuationHost
    {
        bool IAgentWorkflowHost.WorkflowStarted => _dispatcher.IsStarted;

        Task<string> IAgentWorkflowHost.StartWorkflow() => OnUi(() =>
        {
            if (_dispatcher.IsStarted) return "任务流程已启动 / The task workflow is already started.";
            _dispatcher.Start();
            AppLog.Write(AppLog.TasksFile, "AI 启动任务流程 / The AI started the task workflow");
            int waiting = _tasks.Items.Count(x => QueueStatus.Active(x.Status));
            return $"已启动任务流程，按钮已同步为「已启动」；{waiting} 个活动任务将按编号调度 / Task workflow started and the button now shows Started; {waiting} active tasks dispatch in ID order";
        });

        Task<string> IAgentBlockedTaskHost.CheckBlockedTarget(VsInstance v, SolutionEntry parkFor, string text) => OnUi(() =>
        {
            string key = parkFor != null ? parkFor.Path : v?.Key;
            if (string.IsNullOrEmpty(key)) return null;
            var blockers = TaskFailureAnalyzer.HeldBlockers(_tasks.Items, key, text, _settings.ReleaseLevel);
            if (blockers.Count == 0) return null;
            AppLog.Write(AppLog.TasksFile, $"AI 新任务未入队：目标被 #{blockers[0].Id} 阻塞，已提示补充信息 / AI task not queued: target blocked by #{blockers[0].Id}");
            return TaskFailureAnalyzer.BlockedSendText(blockers, parkFor != null ? parkFor.Alias : NameOf(v));
        });

        Task<string> IAgentTaskControlHost.DescribeTask(int id) => OnUi(() =>
        {
            var t = _tasks.Find(id);
            if (t == null) return null;
            string note = t.Status == QueueStatus.Failed && !string.IsNullOrEmpty(t.FailureReason) ? "\r\n失败原因 / Reason：" + TextUtil.Clip(t.FailureReason, 300)
                : TaskHoldNote.IsPending(t) && !string.IsNullOrEmpty(t.PendingNote) ? "\r\n待处理 / Pending：" + TextUtil.Clip(t.PendingNote, 300) : "";
            return $"#{t.Id} · {StatusText(t)} · {t.VsName}\r\n{TextUtil.Clip(t.Text, 400)}{note}";
        });

        Task<string> IAgentTaskControlHost.DeleteTask(int id) => OnUi(() =>
        {
            var t = _tasks.Find(id);
            if (t == null) return "没有任务 #" + id + " / No task #" + id;
            if (t.Worktree != null) return "Worktree 记录用于批次计数和合并屏障，不能删除，请改用 cancel_task / Worktree ledger entries cannot be deleted; use cancel_task";
            if (!_tasks.Remove(t.Id)) return $"任务 #{id} 当前{StatusText(t)}，无法删除 / Task #{id} cannot be deleted in its current state";
            if (TaskHideList.Remove(_settings.HiddenResentTasks, id)) _settings.Save();
            AppLog.Write(AppLog.TasksFile, $"AI 助手经用户确认删除任务 #{id} / Task #{id} deleted by the assistant after user confirmation");
            _taskPanel.RefreshItems();
            return $"已从任务清单删除任务 #{id}（归档保留）/ Task #{id} deleted from the list (archive kept)";
        });
        /// <summary>切换放行等级：保存设置、同步滑块、刷新任务清单并重新调度。/ Switches the level: saves, syncs the slider, refreshes the list and re-pumps.</summary>
        private string ApplyReleaseLevel(ReleaseLevel level, string by)
        {
            var old = _settings.ReleaseLevel;
            _settings.ReleaseLevel = level;
            _settings.Save();
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

        Task<string> IAgentReleaseHost.RetryTaskWithInfo(int id, string info, bool fromUser, bool replace, bool freshContext) => OnUi(() =>
        {
            var t = _tasks.Find(id);
            if (t == null) return "没有任务 #" + id + " / No task #" + id;
            if (TaskStateMachine.IsQueued(t))
            {
                // 排队中：合并到原任务，尚未消耗 Copilot 执行，无需重试限制 / Queued: merged into the task; no Copilot run used yet, so no retry limits apply
                if (!RetryWithInfo(t, info, out string mergeError, false, replace, freshContext)) return $"任务 #{id} 当前{StatusText(t)}：{mergeError}";
                AppLog.Write(AppLog.TasksFile, $"AI 助手把补充要求合并到排队中的任务 #{id} / Assistant merged info into queued task #{id}");
                return $"任务 #{id} 仍在排队，补充要求已合并到原任务，发送时一并带上，没有新建任务 / "
                    + $"Task #{id} is still queued; the info was merged into it and will be sent along, no new task created"
                    + (replace ? "；已替换此前的补充 / replaced earlier supplements" : "");
            }
            // AI 自主重试：限制次数并要求有新信息；用户提供的补充（已经用户确认）不受限 / AI self-retries: capped and must carry new information; user-provided info (user-confirmed) is not
            // 送达不确定时 AI 不得重发（原样或补充后都可能重复执行）/ With uncertain delivery the AI may not resend (either way it could run twice)
            if (!fromUser && SendDiagnosis.IsUncertainFailure(t))
            {
                string uncertain = SendDiagnosis.UncertainRetryRefusal(id);
                AppLog.Write(AppLog.TasksFile, $"拒绝 AI 补充重试 #{id} / Refused AI retry: " + TextUtil.Clip(uncertain, 200));
                return uncertain;
            }
            if (!fromUser && TaskFailureAnalyzer.CheckAiRetry(_tasks.Items, t, info, replace, freshContext) is string refused)
            {
                AppLog.Write(AppLog.TasksFile, $"拒绝 AI 补充重试 #{id} / Refused AI retry: " + TextUtil.Clip(refused, 200));
                return refused;
            }
            if (!RetryWithInfo(t, info, out string error, !fromUser, replace, freshContext)) return $"任务 #{id} 当前{StatusText(t)}：{error}";
            if (fromUser) AppLog.Write(AppLog.TasksFile, $"AI 助手转交用户补充信息重试 #{id} / Assistant relayed user info to retry #{id}");
            string how = (replace ? "；已替换此前的补充与反馈 / replaced earlier supplements and feedback" : "")
                + (freshContext ? "；已标记对话重置，Copilot 会先重新阅读相关内容 / marked as a fresh conversation" : "");
            return $"已为任务 #{id} 插入补充信息并在原条目重新排队（第 {t.SupplementCount} 次补充{(fromUser ? "，来自用户" : $"，AI 自主上限 {TaskStateMachine.MaxSupplements}")}），阻塞随之解除，完成后会再通知你 / "
                + $"Task #{id} requeued in place with info (supplement {t.SupplementCount}{(fromUser ? ", from the user" : $", AI limit {TaskStateMachine.MaxSupplements}")}); the block is lifted and you will be notified" + how;
        });

        /// <summary>
        /// 任务自动接续：向原任务的 VS 发布只做剩余步骤的接续任务；原任务阻塞同一 VS 时放行（测试清单保持待验证），以免接续任务排在它后面卡住。
        /// Task auto-continue: publishes a continuation covering only the remaining steps to the original task's VS; a blocking original is released
        /// (its checklist stays pending) so the continuation is not stuck behind it.
        /// </summary>
        Task<string> IAgentContinuationHost.ContinueTask(int id, string remaining, string title) => OnUiAsync(async () =>
        {
            var t = _tasks.Find(id);
            if (TaskContinuation.Check(_tasks.Items, t, id, remaining) is string refused)
            {
                AppLog.Write(AppLog.TasksFile, $"拒绝接续任务 #{id} / Refused continuation: " + TextUtil.Clip(refused, 200));
                return NotPushed(refused);
            }
            var v = _instances.FirstOrDefault(i => i.Key == t.VsKey);
            if (v == null)
                return NotPushed($"任务 #{id} 的目标 VS「{t.VsName}」未打开，请先打开或把剩余步骤告诉用户 / The target VS of task #{id} is not open; open it first or tell the user the remaining steps");
            string text = TaskContinuation.Compose(t, remaining) + AgentService.OpenSourceSuffixFor(v, _settings.IsEnglishVoice);
            string item = t.Title;
            int dot = item?.IndexOf(" · ", System.StringComparison.Ordinal) ?? -1;
            if (dot >= 0) item = item.Substring(dot + 3);
            string taskTitle = TaskTitle.Normalize(string.IsNullOrWhiteSpace(title) ? (string.IsNullOrEmpty(item) ? "接续 #" + id : item + " 接续") : title);
            string queued = EnqueueTextTask(v, text, "AI", out var admitted, null, taskTitle);
            if (admitted == null) return queued;
            admitted.ContinuedFrom = t.Id;
            admitted.ContinuationDepth = System.Math.Max(0, t.ContinuationDepth) + 1;
            _tasks.Commit();
            string released = "";
            if (TaskStateMachine.IsHoldOutcome(t) && !t.Released && _dispatcher.Release(t, out _))
                released = $"\n已放行原任务 #{id}（测试清单保持待验证），接续任务不会被它阻塞 / Released original #{id} (checklist stays pending) so the continuation is not blocked";
            AppLog.Write(AppLog.TasksFile, $"AI 助手为任务 #{id} 发布接续任务 #{admitted.Id}（第 {admitted.ContinuationDepth}/{TaskContinuation.MaxDepth} 次）/ Continuation #{admitted.Id} of task #{id}");
            _taskPanel.RefreshItems();
            return await ConfirmPushAsync(admitted, $"接续任务 #{admitted.Id} ← #{id}（第 {admitted.ContinuationDepth}/{TaskContinuation.MaxDepth} 次）/ Continuation #{admitted.Id} of #{id}\n" + queued + released);
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

        Task<string> IAgentReleaseHost.RetryTask(int id, string note, bool freshContext) => OnUi(() =>
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
            _dispatcher.Retry(t, note, freshContext);
            _taskPanel.RefreshItems();
            if (t.Status == QueueStatus.Failed)
            {
                t.RecoveryRetries--;
                return $"任务 #{id} 暂时不能重新排队（任务清单未开始或正在处理），请稍后再试或交给用户 / Task #{id} cannot be requeued right now (task list not started or busy); try later or hand it to the user.";
            }
            return $"已原样重新排队任务 #{id}（直接重试第 {t.RecoveryRetries}/{TaskFailureAnalyzer.MaxRecoveryRetries} 次，不计入执行次数），完成后会再通知你 / "
                + $"Task #{id} requeued unchanged (direct retry {t.RecoveryRetries}/{TaskFailureAnalyzer.MaxRecoveryRetries}, not counted as a run); you will be notified";
        });

        Task<string> IAgentTaskReplyHost.ReadTaskReply(int id, int page) => OnUi(() => TaskReply.Page(_tasks.Find(id), page));

        private bool RetryWithInfo(QueuedTask t, string info, out string error, bool enforceLimit, bool replace = false, bool freshContext = false)
        {
            // 重试复用原条目，不再隐藏 / Retrying reuses the entry, which is no longer hidden
            if (TaskHideList.Remove(_settings.HiddenResentTasks, t.Id)) _settings.Save();
            bool ok = _dispatcher.RetryWithInfo(t, info, out error, enforceLimit, replace, freshContext);
            _taskPanel.RefreshItems();
            return ok;
        }

        /// <summary>
        /// 送达不确定的任务在用户重发前强提醒（默认「否」），并附上出错步骤；其他任务直接放行。
        /// Before the user resends a task with uncertain delivery, shows a strong warning (default "No") with the failing step; other tasks pass.
        /// </summary>
        private bool ConfirmUncertainResend(QueuedTask t)
        {
            if (!SendDiagnosis.IsUncertainFailure(t)) return true;
            var record = SendDiagnosis.LatestForTask(t.Id);
            string detail = record != null
                ? "\n\n" + SendDiagnosis.Verdict(record) + "\n" + record.StageLine
                : "\n\n" + TextUtil.Clip(t.Error, 200);
            string message = $"任务 #{t.Id} 的送达结果不确定：消息可能已经提交给 Copilot。请先在目标 VS 的 Copilot 对话中确认没有收到这条消息，并清理输入框中的草稿，否则重发会让同一任务执行两次。"
                + $"\nDelivery of task #{t.Id} is uncertain: the message may already have been submitted to Copilot. First confirm in the target VS Copilot chat that it was not received and clear any draft in the input box; otherwise resending runs the same task twice."
                + detail + "\n\n已确认未送达，仍要重新发送？/ Confirmed not delivered and resend anyway?";
            return MessageBox.Show(this, message, "送达不确定 / Delivery uncertain", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        /// <summary>任务菜单「补充信息」：弹出输入框；排队中的任务合并到原任务，失败 / 待验证的任务补充后重试。/ Task menu "Add info": shows an input dialog; queued tasks get it merged in, failed / awaiting-verification tasks are retried with it.</summary>
        private void PromptSupplement(QueuedTask t)
        {
            if (!TaskStateMachine.CanSupplement(t)) { SetStatus("只有排队中、失败或待验证的任务可以补充信息 / Only queued, failed or awaiting-verification tasks can take supplementary info"); return; }
            bool queued = TaskStateMachine.IsQueued(t);
            if (!queued && !ConfirmUncertainResend(t)) return;
            using (var f = new SupplementForm(t))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                // 对话框打开期间任务可能已开始发送 / The task may have started sending while the dialog was open
                if (queued && !TaskStateMachine.CanSupplement(t))
                {
                    // 保留用户刚写的内容，便于完成后再补充 / Keep what the user just wrote so it can be added once the task finishes
                    try { Clipboard.SetText(f.Info); }
                    catch (System.Runtime.InteropServices.ExternalException) { }
                    SetStatus($"任务 #{t.Id} 已开始发送（{StatusText(t)}），补充未合并，内容已复制到剪贴板；完成后可再补充重试 / Task #{t.Id} already started sending; info not merged and copied to the clipboard");
                    return;
                }
                bool merge = TaskStateMachine.IsQueued(t);
                // 用户亲自补充不受 AI 自主次数限制 / The user's own supplements are not capped
                SetStatus(RetryWithInfo(t, f.Info, out string error, false, f.ReplacePrevious, f.FreshContext)
                    ? (merge
                        ? $"补充要求已合并到排队中的任务 #{t.Id}，发送时一并带上 / Info merged into queued task #{t.Id}"
                        : $"任务 #{t.Id} 已插入补充信息并重新排队 / Task #{t.Id} requeued with info")
                    : error);
            }
        }
    }
}
