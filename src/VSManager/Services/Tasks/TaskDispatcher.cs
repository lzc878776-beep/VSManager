using System;
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
        Task<string> ReadAnswerAsync(VsInstance v);
        void SetStatus(string text);
        void LogEvent(string vsName, string text);
        void NotifyAgent(string title, string body);
        /// <summary>每轮调度结束后调用：是否仍有未完成任务（用于开关调度计时器）。/ Called after each round: whether unfinished tasks remain.</summary>
        void QueueActivityChanged(bool anyActive);
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
                foreach (var t in _tasks.Items.Where(x => x.Status == QueueStatus.Running).ToList())
                {
                    if (now < _host.TrackingReadyAt) break;
                    var v = _host.FindVs(t.VsKey);
                    switch (TaskStateMachine.CheckRunning(t, v != null, v?.Copilot ?? CopilotState.Unknown, () => _host.CanDispatch(v), now))
                    {
                        case RunningVerdict.VsClosed: Fail(t, TaskStateMachine.VsClosedError); break;
                        case RunningVerdict.SawBusy: t.SawBusy = true; break;
                        case RunningVerdict.AssumeDone: Finish(t, v, null); break;
                    }
                }

                foreach (var t in TaskStateMachine.NextToDispatch(_tasks.Items, now))
                {
                    if (_host.IsSending) break;
                    var v = _host.FindVs(t.VsKey);
                    if (v == null || !_host.CanDispatch(v) || t.Status != QueueStatus.Waiting) continue;
                    TaskStateMachine.BeginSend(t, _host.NameOf(v));
                    _tasks.Commit();
                    _host.LogEvent(t.VsName, $"任务清单：发布任务 #{t.Id}（第 {t.Attempts} 次）");
                    string r = await _host.SendAsync(v, t.Text);
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
                    $"[任务完成通知] 任务 #{t.Id} 在「{t.VsName}」发布或执行失败：{error}。任务内容：{TextUtil.Clip(t.Text, 300)}。请判断是否改派、重试或告知用户。");
        }

        /// <summary>任务完成：读取 Copilot 最新回复作为结果，并通知 AI 助手。/ Completes a task: stores the latest Copilot answer and notifies the AI assistant.</summary>
        public async void Finish(QueuedTask t, VsInstance v, TimeSpan? dur)
        {
            if (!TaskStateMachine.Complete(t, _clock())) return;
            _tasks.Commit();
            string answer = null;
            try { answer = await _host.ReadAnswerAsync(v); } catch { }
            t.Result = string.IsNullOrWhiteSpace(answer) ? null : TextUtil.Clip(answer, 1500);
            _tasks.Commit();
            if (t.FromAgent)
            {
                string took = TextUtil.FormatDuration(dur ?? (t.Finished.Value - (t.Started ?? t.Finished.Value)));
                int left = _tasks.Items.Count(x => QueueStatus.Active(x.Status));
                _host.NotifyAgent($"📋 任务 #{t.Id} 已完成 · {t.VsName}（{took}）",
                    $"[任务完成通知] 任务 #{t.Id} 已在「{t.VsName}」完成（用时 {took}）。任务：{TextUtil.Clip(t.Text, 300)}\n" +
                    "Copilot 回复：" + (t.Result ?? "（未能读取回复内容）") +
                    $"\n任务清单中还有 {left} 个未完成任务。请向用户简要汇报；若有依赖此结果的后续步骤就继续发布。");
            }
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
            TaskStateMachine.Requeue(t);
            _tasks.Commit();
            Pump();
        }

        /// <summary>立即发布（跳过重试等待）；不能立即发布时说明原因。/ Publishes now (skips the retry delay); explains why when it cannot.</summary>
        public void DispatchNow(QueuedTask t)
        {
            t.NextTry = DateTime.MinValue;
            var target = _host.FindVs(t.VsKey);
            if (target == null) _host.SetStatus($"「{t.VsName}」当前未打开，任务会在它打开并空闲后发布");
            else if (!_host.CanDispatch(target) || _tasks.Items.Any(x => x.VsKey == t.VsKey && (x.Status == QueueStatus.Running || x.Status == QueueStatus.Sending)))
                _host.SetStatus($"「{t.VsName}」仍在忙，任务 #{t.Id} 会在空闲后自动发布");
            else if (_tasks.Items.Any(x => x.VsKey == t.VsKey && x.Status == QueueStatus.Waiting && x.Id < t.Id))
                _host.SetStatus($"任务 #{t.Id} 前面还有排队任务，按顺序发布");
            Pump();
        }
    }
}
