using System;

namespace VSManager
{
    /// <summary>AI 助手故障类型。/ Kind of AI assistant fault.</summary>
    public enum AgentFaultKind
    {
        /// <summary>内部未处理异常。/ Internal unhandled exception.</summary>
        Exception,
        /// <summary>模型请求连续失败。/ Repeated model request failures.</summary>
        RequestFailures,
        /// <summary>长时间无响应。/ No progress for too long.</summary>
        Hang
    }

    /// <summary>
    /// 可被看护与重建的 AI 助手（由 <see cref="AgentService"/> 实现，单元测试可替换）。
    /// An AI assistant that can be supervised and rebuilt (implemented by <see cref="AgentService"/>; replaceable in tests).
    /// </summary>
    public interface IRestartableAgent
    {
        bool Running { get; }
        /// <summary>最近一次有进展的时间。/ Time of the last progress.</summary>
        DateTime LastProgress { get; }
        /// <summary>是否正在等待用户确认。/ Whether it is waiting for the user to confirm.</summary>
        bool AwaitingUser { get; }
        event Action<AgentFaultKind, string> Faulted;
        /// <summary>重建助手并恢复可用状态。/ Rebuilds the assistant and restores a usable state.</summary>
        void Restart(string reason);
    }

    /// <summary>
    /// AI 助手看护：收到故障（未处理异常、请求连续失败）或检测到长时间无响应时自动重建助手，
    /// 并按 <see cref="RestartLimiter"/> 防止重启风暴。只在界面线程使用（<see cref="Tick"/> 由界面计时器调用）。
    /// AI assistant supervisor: rebuilds the assistant automatically on a fault (unhandled error, repeated request failures)
    /// or a detected hang, guarded against restart storms by <see cref="RestartLimiter"/>. UI thread only (<see cref="Tick"/>
    /// is called by a UI timer).
    /// </summary>
    public sealed class AgentSupervisor
    {
        private readonly IRestartableAgent _agent;
        private readonly Func<AppSettings> _settings;
        private readonly Func<DateTime> _clock;
        private DateTime _warnedHangAt = DateTime.MinValue;
        private DateTime _hangAttemptAt = DateTime.MinValue;
        private bool _stormNotified;

        /// <summary>需要显示在状态栏的提示。/ Messages to show in the status bar.</summary>
        public event Action<string> Notice;

        /// <summary>自动重启次数限制（窗口与上限每次按设置刷新）。/ Automatic restart limit (window and maximum refreshed from the settings).</summary>
        public RestartLimiter Limiter { get; }

        /// <summary>本次启动以来的自动重启次数。/ Automatic restarts since launch.</summary>
        public int AutoRestarts { get; private set; }

        public AgentSupervisor(IRestartableAgent agent, Func<AppSettings> settings, Func<DateTime> clock = null)
        {
            _agent = agent;
            _settings = settings;
            _clock = clock ?? (() => DateTime.Now);
            Limiter = new RestartLimiter(AppSettings.DefaultAutoRestartMaxCount, TimeSpan.FromMinutes(AppSettings.DefaultAutoRestartWindowMinutes));
            _agent.Faulted += (kind, message) => HandleFault(kind, message);
        }

        private static string KindText(AgentFaultKind kind)
        {
            switch (kind)
            {
                case AgentFaultKind.RequestFailures: return "请求连续失败 / repeated request failures";
                case AgentFaultKind.Hang: return "长时间无响应 / no response";
                default: return "内部异常 / internal error";
            }
        }

        /// <summary>检查是否长时间无响应（界面计时器定期调用）。/ Checks for a hang (called periodically by a UI timer).</summary>
        public void Tick()
        {
            if (!_agent.Running || _agent.AwaitingUser) return;
            var s = _settings();
            int timeout = s.AgentHangTimeoutSeconds > 0 ? s.AgentHangTimeoutSeconds : AppSettings.DefaultAgentHangTimeoutSeconds;
            var last = _agent.LastProgress;
            var idle = _clock() - last;
            if (idle.TotalSeconds < timeout) return;
            // 同一次卡住：自动重启关闭时只提示一次；受限时每分钟最多再尝试一次
            // Same hang: warn once when auto-restart is off; retry at most once a minute while limited
            if (last == _warnedHangAt && (!s.AgentAutoRestart || _clock() - _hangAttemptAt < TimeSpan.FromMinutes(1))) return;
            _warnedHangAt = last;
            _hangAttemptAt = _clock();
            HandleFault(AgentFaultKind.Hang, "已 " + (int)idle.TotalSeconds + " 秒没有进展 / no progress for " + (int)idle.TotalSeconds + " s");
        }

        /// <summary>
        /// 处理一次故障：允许时重建助手并返回 true；自动重启关闭或触发防风暴限制时只提示并返回 false。
        /// Handles a fault: rebuilds the assistant and returns true when allowed; only notifies and returns false when
        /// auto-restart is off or the storm limit was hit.
        /// </summary>
        public bool HandleFault(AgentFaultKind kind, string detail)
        {
            var s = _settings();
            string reason = KindText(kind) + (string.IsNullOrWhiteSpace(detail) ? "" : "（" + detail + "）");
            AppLog.Write(AgentService.LogFile, "检测到 AI 助手故障 / Assistant fault detected：" + reason);
            if (!s.AgentAutoRestart)
            {
                Raise("⚠ AI 助手" + KindText(kind) + "；自动重启已关闭，可在「⟳ 重启」菜单中手动重启 / Auto-restart is off; restart it from the ⟳ menu");
                return false;
            }
            Limiter.MaxCount = s.AutoRestartMaxCount > 0 ? s.AutoRestartMaxCount : AppSettings.DefaultAutoRestartMaxCount;
            Limiter.Window = TimeSpan.FromMinutes(s.AutoRestartWindowMinutes > 0 ? s.AutoRestartWindowMinutes : AppSettings.DefaultAutoRestartWindowMinutes);
            if (!Limiter.TryAcquire(_clock()))
            {
                AppLog.Write(AgentService.LogFile, "自动重启已达上限，停止自动重启 / Restart limit reached; automatic restarts stopped");
                if (!_stormNotified)
                {
                    _stormNotified = true;
                    Raise("⚠ AI 助手在 " + (int)Limiter.Window.TotalMinutes + " 分钟内已自动重启 " + Limiter.MaxCount + " 次，已停止自动重启，请查看日志 logs\\" + AgentService.LogFile +
                          " / Restarted " + Limiter.MaxCount + " times within " + (int)Limiter.Window.TotalMinutes + " min; automatic restarts stopped, see the log");
                }
                return false;
            }
            _stormNotified = false;
            AutoRestarts++;
            try { _agent.Restart("自动 / automatic · " + reason); }
            catch (Exception ex)
            {
                AppLog.Error(AgentService.LogFile, "重建 AI 助手失败 / Rebuild failed", ex);
                Raise("⚠ AI 助手重启失败 / Assistant restart failed：" + ex.Message);
                return false;
            }
            Raise("⟳ AI 助手已自动重启 / AI assistant restarted automatically：" + reason);
            return true;
        }

        /// <summary>手动重启：不受次数限制，并清空防风暴计数。/ Manual restart: not limited, and clears the storm counter.</summary>
        public void RestartNow()
        {
            Limiter.Reset();
            _stormNotified = false;
            AppLog.Write(AgentService.LogFile, "手动重启 AI 助手 / Manual assistant restart");
            try { _agent.Restart("手动 / manual"); }
            catch (Exception ex)
            {
                AppLog.Error(AgentService.LogFile, "重建 AI 助手失败 / Rebuild failed", ex);
                Raise("⚠ AI 助手重启失败 / Assistant restart failed：" + ex.Message);
                return;
            }
            Raise("⟳ AI 助手已重启 / AI assistant restarted");
        }

        private void Raise(string text)
        {
            try { Notice?.Invoke(text); } catch { }
        }
    }
}
