using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 任务队列暂停 / 继续：顶栏按钮与 AI 工具共用的宿主实现；暂停状态写入 settings.json，重开后保持。
    /// Task queue pause / resume: host implementation shared by the header button and the AI tool; the paused state is saved
    /// in settings.json and survives reopening.
    /// </summary>
    public partial class MainForm : IAgentQueuePauseHost
    {
        bool IAgentQueuePauseHost.QueuePaused => _dispatcher.IsPaused;

        Task<string> IAgentQueuePauseHost.SetQueuePaused(bool paused, bool interruptRunning) => OnUiAsync(async () =>
        {
            if (!paused) return SetQueuePausedCore(false);
            string text = SetQueuePausedCore(true);
            if (interruptRunning) text += "\n" + await InterruptRunningAsync();
            return text;
        });

        /// <summary>顶栏「暂停」：有执行中的任务时询问让它做完还是中断。/ Header "Pause": asks whether running tasks finish or are interrupted.</summary>
        private async void PauseQueueFromUi()
        {
            var running = _tasks.Items.Where(t => t.Status == QueueStatus.Running).ToList();
            bool interrupt = false;
            if (running.Count > 0)
            {
                string ids = string.Join("、", running.Select(t => "#" + t.Id + " " + t.VsName));
                var answer = MessageBox.Show(this,
                    "有 " + running.Count + " 个任务正在执行：" + ids + "\n\n" +
                    "是：中断这些任务（停止 Copilot，任务重新排队，点「继续」后重新发送并接着做）\n" +
                    "否：让它们执行完，只是不再发布新任务\n" +
                    "取消：不暂停\n\n" +
                    running.Count + " task(s) are running.\n" +
                    "Yes: interrupt them (stop Copilot, requeue, re-send and continue after Resume)\n" +
                    "No: let them finish; just stop publishing new tasks\n" +
                    "Cancel: do not pause",
                    "暂停任务队列 / Pause task queue", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Cancel) return;
                interrupt = answer == DialogResult.Yes;
            }
            SetQueuePausedCore(true);
            if (interrupt) SetStatus(await InterruptRunningAsync());
        }

        /// <summary>切换暂停状态、保存设置并同步界面，返回结果文字。/ Switches the paused state, saves the setting and syncs the UI; returns result text.</summary>
        private string SetQueuePausedCore(bool paused)
        {
            bool changed = _dispatcher.IsPaused != paused;
            _settings.TaskQueuePaused = paused;
            _settings.Save();
            _dispatcher.SetPaused(paused);
            _taskPanel.SetQueuePaused(paused);
            _taskPanel.RefreshItems();
            UpdateTaskTimer();
            if (changed) AppLog.Write(AppLog.TasksFile, paused ? "任务队列已暂停 / Task queue paused" : "任务队列已继续 / Task queue resumed");
            int waiting = _tasks.Items.Count(t => QueueStatus.Active(t.Status) && t.Status != QueueStatus.Running && t.Status != QueueStatus.Sending);
            int running = _tasks.Items.Count(t => t.Status == QueueStatus.Running || t.Status == QueueStatus.Sending);
            return paused
                ? (changed ? "" : "（原本已暂停 / already paused）") + TaskDispatcher.PausedText + $"；排队 {waiting} 条、执行中 {running} 条 / {waiting} waiting, {running} running"
                : (changed ? "" : "（原本未暂停 / was not paused）") + $"任务队列已继续，{waiting} 条排队任务按编号发布 / Task queue resumed; {waiting} waiting task(s) dispatch in ID order";
        }

        /// <summary>中断所有执行中的任务：先重新排队再停止对应 Copilot，返回结果文字。/ Interrupts every running task: requeue first, then stop its Copilot; returns result text.</summary>
        private async Task<string> InterruptRunningAsync()
        {
            var sb = new StringBuilder();
            var stopped = new HashSet<VsInstance>();
            foreach (var t in _tasks.Items.Where(x => x.Status == QueueStatus.Running).ToList())
            {
                var v = FindTaskVs(t);
                if (!_dispatcher.Interrupt(t)) { sb.Append($"#{t.Id} 正在收尾，未中断 / is finishing, not interrupted；"); continue; }
                string stop = "";
                if (v != null && v.Copilot == CopilotState.Busy && stopped.Add(v))
                    stop = await ((IAgentHost)this).InvokeChatButton(v, "CancelButton", "停止 Copilot");
                sb.Append($"#{t.Id} 已中断并重新排队 / interrupted and requeued").Append(stop.Length > 0 ? "（" + stop + "）" : "").Append("；");
            }
            _taskPanel.RefreshItems();
            return sb.Length == 0 ? "没有执行中的任务 / No running tasks" : sb.ToString().TrimEnd('；');
        }
    }
}
