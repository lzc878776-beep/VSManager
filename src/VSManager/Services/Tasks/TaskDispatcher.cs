using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 任务调度器依赖的宿主能力（由主窗口实现）：查找 VS、判断能否发布、发送消息、读取回复、状态提示与通知 AI 助手。
    /// 单元测试可用模拟实现替换。
    /// Host capabilities the task dispatcher depends on (implemented by the main window): find a VS, check whether it can
    /// take a task, send a message, read the reply, show status and notify the AI assistant. Unit tests use a fake.
    /// </summary>
    public interface ITaskDispatchHost
    {
        VsInstance FindVs(string vsKey);
        /// <summary>该 VS 当前能否接收新任务。/ Whether the VS can take a new task now.</summary>
        bool CanDispatch(VsInstance v);
        string NameOf(VsInstance v);
        /// <summary>是否有消息正在发送（同一时刻只发送一条）。/ Whether a message is being sent (one at a time).</summary>
        bool IsSending { get; }
        /// <summary>启动后多久才开始跟踪执行中的任务（等待 Copilot 状态稳定）。/ When tracking of running tasks starts after launch.</summary>
        DateTime TrackingReadyAt { get; }
        /// <summary>发送到 Copilot，返回结果文字（以「已发送」开头表示送达）。/ Sends to Copilot; a result starting with "已发送" means delivered.</summary>
        Task<string> SendAsync(VsInstance v, string text);
        /// <summary>读取 Copilot 最新回复文本，读取失败返回 null。/ Reads the latest Copilot answer; null when it cannot be read.</summary>
        Task<string> ReadAnswerAsync(VsInstance v, QueuedTask expectedTask);
        void SetStatus(string text);
        void LogEvent(string vsName, string text);
        void NotifyAgent(string title, string body);
        /// <summary>每轮调度结束后调用：是否仍有未完成任务（用于开关调度计时器）。/ Called after each round: whether unfinished tasks remain.</summary>
        void QueueActivityChanged(bool anyActive);
        /// <summary>
        /// 查找暂存任务的目标 VS（按解决方案路径 / 登记表别名），尚未打开时返回 null。
        /// Finds the target VS of a parked task (by solution path / registry alias); null while it is not open.
        /// </summary>
        VsInstance FindTargetVs(QueuedTask t);
        /// <summary>目标 VS 打开后延迟多久再推送暂存任务。/ Delay after the target VS opens before parked tasks are pushed.</summary>
        TimeSpan TargetSettleDelay { get; }
        /// <summary>暂存 / 推送等新状态的弹窗与语音播报（中文与英文文案）。/ Notification and voice announcement of new states such as parked / pushed (Chinese and English text).</summary>
        void AnnounceTask(QueuedTask t, string zh, string en);
    }

    /// <summary>
    /// 任务调度器：跟踪执行中的任务，并把每个空闲 VS 最早排队的任务发布出去；失败、完成、取消、重试也在这里处理。
    /// 状态流转统一交给 <see cref="TaskStateMachine"/>，只在界面线程调用。
    /// Task dispatcher: tracks running tasks and publishes the oldest waiting task of every idle VS; also handles failure,
    /// completion, cancellation and retry. Status changes go through <see cref="TaskStateMachine"/>. UI thread only.
    /// </summary>
    public sealed class TaskDispatcher
    {
        private readonly TaskQueue _tasks;
        private readonly ITaskDispatchHost _host;
        private readonly Func<DateTime> _clock;
        private bool _pumping;
        private readonly HashSet<QueuedTask> _finishing = new HashSet<QueuedTask>();

        public TaskDispatcher(TaskQueue tasks, ITaskDispatchHost host, Func<DateTime> clock = null)
        {
            _tasks = tasks;
            _host = host;
            _clock = clock ?? (() => DateTime.Now);
        }

        /// <summary>触发一轮调度（不等待）；异常只显示在状态栏。/ Starts a round without awaiting; errors only go to the status bar.</summary>
        public async void Pump()
        {
            try { await PumpAsync(); }
            catch (Exception ex) { _host.SetStatus("任务清单调度出错：" + ex.Message); }
        }

        /// <summary>执行一轮调度。/ Runs one dispatch round.</summary>
        public async Task PumpAsync()
        {
            if (_pumping) return;
            _pumping = true;
            try
            {
                var now = _clock();
                PushParked(now);
                foreach (var t in _tasks.Items.Where(x => x.Status == QueueStatus.Running).ToList())
                {
                    if (now < _host.TrackingReadyAt) break;
                    if (_finishing.Contains(t)) continue;
                    var v = _host.FindVs(t.VsKey);
                    switch (TaskStateMachine.CheckRunning(t, v != null, v?.Copilot ?? CopilotState.Unknown, () => _host.CanDispatch(v), now))
                    {
                        case RunningVerdict.VsClosed: Fail(t, TaskStateMachine.VsClosedError); break;
                        case RunningVerdict.SawBusy: t.SawBusy = true; break;
                        case RunningVerdict.Unconfirmed: await FinishAsync(t, v, null); break;
                    }
                }

                foreach (var t in TaskStateMachine.NextToDispatch(_tasks.Items, now))
                {
                    if (_host.IsSending) break;
                    var v = _host.FindVs(t.VsKey);
                    if (v == null || !_host.CanDispatch(v) || t.Status != QueueStatus.Waiting
                        || _tasks.Find(t.Id) != t || TaskStateMachine.BlockingTask(_tasks.Items, t) != null) continue;
                    TaskStateMachine.BeginSend(t, _host.NameOf(v));
                    _tasks.Commit();
                    _host.LogEvent(t.VsName, $"任务清单：发布任务 #{t.Id}（第 {t.Attempts} 次）");
                    string r;
                    try { r = await _host.SendAsync(v, TaskStateMachine.DispatchText(t)); }
                    catch (Exception ex)
                    {
                        Fail(t, "发送异常，送达状态未知，请检查后重试：" + ex.Message);
                        continue;
                    }
                    switch (TaskStateMachine.ApplySendResult(t, r, _clock()))
                    {
                        case SendDecision.Delivered:
                            _tasks.Commit();
                            _host.SetStatus($"任务清单：#{t.Id} 已发布到「{t.VsName}」");
                            break;
                        case SendDecision.Fail:
                            AfterFail(t, r);
                            break;
                        default:
                            _tasks.Commit();
                            _host.SetStatus($"任务清单：#{t.Id} 发送失败（{r}），30 秒后重试");
                            break;
                    }
                }
            }
            finally { _pumping = false; }
            _host.QueueActivityChanged(_tasks.Items.Any(x => QueueStatus.Active(x.Status)));
        }

        /// <summary>
        /// 暂存任务的目标 VS 已打开：转为排队（延迟 <see cref="ITaskDispatchHost.TargetSettleDelay"/> 后发布），记录日志并通知。
        /// The target VS of a parked task has opened: back to waiting (published after <see cref="ITaskDispatchHost.TargetSettleDelay"/>),
        /// logged and announced.
        /// </summary>
        private void PushParked(DateTime now)
        {
            foreach (var t in _tasks.Items.Where(x => x.Status == QueueStatus.WaitingVs).ToList())
            {
                var v = _host.FindTargetVs(t);
                if (v == null) continue;
                string target = t.Target ?? t.VsName;
                var delay = _host.TargetSettleDelay;
                if (!TaskStateMachine.TargetOpened(t, v.Key, _host.NameOf(v), now + delay)) continue;
                _tasks.Commit();
                int secs = (int)Math.Max(0, delay.TotalSeconds);
                _host.LogEvent(t.VsName, $"任务清单：目标「{target}」已打开，暂存任务 #{t.Id} 转为排队，{secs} 秒后自动推送 / target opened, parked task #{t.Id} queued, pushed in {secs} s");
                _host.SetStatus($"任务清单：「{target}」已打开，暂存任务 #{t.Id} 将在 {secs} 秒后自动推送 / \"{target}\" opened, parked task #{t.Id} will be pushed in {secs} s");
                _host.AnnounceTask(t, $"{target}已打开，暂存任务即将自动推送", $"{target} is open, the parked task will be pushed shortly");
                if (t.FromAgent)
                    _host.NotifyAgent($"📋 任务 #{t.Id} 目标已打开 · {t.VsName}",
                        $"[任务通知] 暂存任务 #{t.Id} 的目标「{target}」已打开（VS：{t.VsName}），任务将在 {secs} 秒后自动推送，完成后会再通知你。仅供知悉，无需重复发布。");
            }
        }

        /// <summary>任务失败：记录错误并通知 AI 助手（仅 AI 发布的任务）。/ Fails a task and notifies the AI assistant (AI tasks only).</summary>
        public void Fail(QueuedTask t, string error)
        {
            TaskStateMachine.Fail(t, error, _clock());
            AfterFail(t, error);
        }

        private void AfterFail(QueuedTask t, string error)
        {
            _tasks.Commit();
            _host.SetStatus($"任务清单：#{t.Id}「{t.VsName}」失败：{error}");
            if (t.FromAgent)
                _host.NotifyAgent($"📋 任务 #{t.Id} 失败 · {t.VsName}",
                    $"[任务失败通知] 任务 #{t.Id} 在「{t.VsName}」发布或执行失败：{error}。任务内容：{TextUtil.Clip(t.Text, 300)}。该目标后续任务已暂停；请处理错误，重发时以「重发 #{t.Id}：」开头，原失败条目会被移除，重发任务成功返回前不得继续后续步骤。");
        }

        /// <summary>任务完成：读取 Copilot 最新回复作为结果，并通知 AI 助手。/ Completes a task: stores the latest Copilot answer and notifies the AI assistant.</summary>
        public async void Finish(QueuedTask t, VsInstance v, TimeSpan? dur)
        {
            try { await FinishAsync(t, v, dur); }
            catch (Exception ex) { _host.SetStatus("任务结果处理出错：" + ex.Message); }
        }

        public async Task FinishAsync(QueuedTask t, VsInstance v, TimeSpan? dur)
        {
            if (t == null || t.Status != QueueStatus.Running || _tasks.Find(t.Id) != t || !_finishing.Add(t)) return;
            try
            {
                string answer;
                try { answer = await _host.ReadAnswerAsync(v, t); }
                catch (Exception ex)
                {
                    if (t.Status == QueueStatus.Running && _tasks.Find(t.Id) == t)
                        Fail(t, "读取任务结果失败，后续任务已暂停：" + ex.Message);
                    return;
                }
                if (t.Status != QueueStatus.Running || _tasks.Find(t.Id) != t) return;
                if (!TaskStateMachine.TryReadSuccess(t, answer, out string result))
                {
                    t.Result = TextUtil.Clip(answer, 1500);
                    Fail(t, "未收到本次任务的成功回执，后续任务已暂停。" + TextUtil.Clip(answer, 300));
                    return;
                }
                t.Result = TextUtil.Clip(result, 1500);
                TaskStateMachine.Complete(t, _clock());
                _tasks.Commit();
                if (t.FromAgent)
                {
                    string took = TextUtil.FormatDuration(dur ?? (t.Finished.Value - (t.Started ?? t.Finished.Value)));
                    int left = _tasks.Items.Count(x => QueueStatus.Active(x.Status));
                    _host.NotifyAgent($"📋 任务 #{t.Id} 已完成 · {t.VsName}（{took}）",
                        $"[任务完成通知] 任务 #{t.Id} 已在「{t.VsName}」返回结果（用时 {took}）。任务：{TextUtil.Clip(t.Text, 300)}\n" +
                        "Copilot 回复：" + t.Result +
                        $"\n任务清单中还有 {left} 个未完成任务。请向用户简要汇报，不要重复发布清单中已有的任务。");
                }
            }
            finally { _finishing.Remove(t); }
            Pump();
        }

        /// <summary>取消排队中或执行中的任务。/ Cancels a waiting or running task.</summary>
        public bool Cancel(QueuedTask t)
        {
            if (!TaskStateMachine.Cancel(t, _clock())) return false;
            _tasks.Commit();
            return true;
        }

        /// <summary>重新排队并立即尝试发布。/ Requeues a task and tries to publish it right away.</summary>
        public void Retry(QueuedTask t)
        {
            if (t == null || _tasks.Find(t.Id) != t || _finishing.Contains(t)
                || (t.Status != QueueStatus.Failed && t.Status != QueueStatus.Cancelled))
            {
                _host.SetStatus("任务已被替换、正在处理或不可重试，请刷新任务清单");
                return;
            }
            TaskStateMachine.Requeue(t);
            _tasks.Commit();
            Pump();
        }

        /// <summary>立即发布（跳过重试等待）；不能立即发布时说明原因。/ Publishes now (skips the retry delay); explains why when it cannot.</summary>
        public void DispatchNow(QueuedTask t)
        {
            t.NextTry = DateTime.MinValue;
            if (t.Status == QueueStatus.WaitingVs)
            {
                if (_host.FindTargetVs(t) == null)
                    _host.SetStatus($"「{t.Target ?? t.VsName}」尚未打开，任务 #{t.Id} 已暂存，打开后自动推送 / not open yet; task #{t.Id} is parked and pushed once it opens");
                Pump();
                return;
            }
            var target = _host.FindVs(t.VsKey);
            if (target == null) _host.SetStatus($"「{t.VsName}」当前未打开，任务会在它打开并空闲后发布");
            else if (!_host.CanDispatch(target) || _tasks.Items.Any(x => x.VsKey == t.VsKey && (x.Status == QueueStatus.Running || x.Status == QueueStatus.Sending)))
                _host.SetStatus($"「{t.VsName}」仍在忙，任务 #{t.Id} 会在空闲后自动发布");
            else if (TaskStateMachine.BlockingTask(_tasks.Items, t) is QueuedTask blocker)
                _host.SetStatus($"任务 #{t.Id} 等待前序 #{blocker.Id} 成功返回（{TaskStateMachine.StatusText(blocker, _clock())}）");
            Pump();
        }
    }
}
