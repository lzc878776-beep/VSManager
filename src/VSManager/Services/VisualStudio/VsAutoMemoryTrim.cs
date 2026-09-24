using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 一个待定期清理的 VS 实例：在界面线程上采集的引用与「是否有发送中 / 执行中的任务」。
    /// A VS instance for periodic cleanup: the reference captured on the UI thread and whether a task is being sent or running.
    /// </summary>
    public sealed class AutoTrimTarget
    {
        public VsRef Ref;
        public bool TaskActive;
    }

    /// <summary>一次定期（或手动触发）清理的结果。/ Result of one periodic (or manually triggered) cleanup run.</summary>
    public sealed class AutoTrimReport
    {
        public DateTime At;
        public bool Manual;
        public int Cleaned, Skipped, Failed;
        /// <summary>各实例工作集下降量之和（只计下降）。/ Sum of working-set decreases (decreases only).</summary>
        public long FreedBytes;
        public string Error;
        public readonly List<string> Lines = new List<string>();

        /// <summary>是否有实际效果、值得通知与播报。/ Whether it had a real effect worth notifying and announcing.</summary>
        public bool Worth => Error == null && Cleaned > 0 && FreedBytes >= VsAutoMemoryTrimmer.NotifyMinBytes;

        public string SummaryZh => Error != null ? "自动清理失败：" + Error
            : $"已自动清理 {Cleaned} 个 VS 实例内存，释放 {VsMemory.Mb(FreedBytes)}" + (Skipped > 0 ? $"，跳过 {Skipped} 个" : "");

        public string SummaryEn => Error != null ? "Auto cleanup failed: " + Error
            : $"Auto-cleaned memory of {Cleaned} VS instance(s), freed {VsMemory.Mb(FreedBytes)}" + (Skipped > 0 ? $", skipped {Skipped}" : "");

        public string Summary => SummaryZh + " / " + SummaryEn;
    }

    /// <summary>
    /// 定期自动温和清理 VS 内存：后台线程定时检查（不占用界面线程），到期后复用 <see cref="VsMemory.CleanAsync"/>，
    /// 只修剪「可安全清理」进程的工作集，从不结束进程；调试 / 生成 / Copilot 运行 / 任务执行中或状态未知的实例一律跳过。
    /// Periodic gentle VS memory cleanup: a background timer checks the schedule (never on the UI thread) and, when due, reuses
    /// <see cref="VsMemory.CleanAsync"/>, trimming only "safe" processes and never terminating anything. Instances that are
    /// debugging, building, running Copilot or a task, or whose state is unknown, are skipped.
    /// </summary>
    public sealed class VsAutoMemoryTrimmer : IDisposable
    {
        public const int DefaultIntervalMinutes = 30, MinIntervalMinutes = 5, MaxIntervalMinutes = 1440;
        /// <summary>低于此释放量视为无实际效果，不通知不播报。/ Below this, a run is considered ineffective and is not announced.</summary>
        public const long NotifyMinBytes = 1048576;
        private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

        private readonly Func<AppSettings> _settings;
        private readonly Func<Task<IList<AutoTrimTarget>>> _targets;
        private readonly Func<IList<VsRef>, MemSnapshot> _take;
        private readonly Func<string, IList<VsRef>, Task<MemCleanResult>> _clean;
        private readonly Func<DateTime> _now;
        private readonly object _gate = new object();
        private Timer _timer;
        private DateTime? _anchor;
        private int _busy;

        /// <summary>每次运行结束后触发（在后台线程上）。/ Raised after every run (on a background thread).</summary>
        public event Action<AutoTrimReport> Completed;

        public VsAutoMemoryTrimmer(Func<AppSettings> settings, Func<Task<IList<AutoTrimTarget>>> targets)
            : this(settings, targets, VsMemory.Take, VsMemory.CleanAsync, () => DateTime.Now) { }

        internal VsAutoMemoryTrimmer(Func<AppSettings> settings, Func<Task<IList<AutoTrimTarget>>> targets,
            Func<IList<VsRef>, MemSnapshot> take, Func<string, IList<VsRef>, Task<MemCleanResult>> clean, Func<DateTime> now)
        {
            _settings = settings;
            _targets = targets;
            _take = take;
            _clean = clean;
            _now = now;
        }

        public static int ClampInterval(int minutes) =>
            minutes <= 0 ? DefaultIntervalMinutes : Math.Min(Math.Max(MinIntervalMinutes, minutes), MaxIntervalMinutes);

        public static int ClampThreshold(int mb) => mb <= 0 ? 0 : Math.Min(mb, VsMemory.MaxThresholdMB);

        /// <summary>
        /// 自动清理的跳过原因；可以清理时返回 null。调试 / 生成状态无法确认（DTE 不可用）时同样跳过。
        /// Why an instance must be skipped; null when it may be cleaned. Also skipped when the debug / build state cannot be confirmed (no DTE).
        /// </summary>
        public static string SkipReason(VsInstance vs, bool taskActive)
        {
            if (vs == null) return "不在 VS 列表中 / not in the VS list";
            if (vs.Dte == null) return "自动化接口不可用，无法确认调试 / 生成状态 / DTE unavailable, debug / build state unknown";
            if (vs.DebugMode == 2 || vs.DebugMode == 3) return "正在调试 / debugging";
            if (vs.Building) return "正在生成 / building";
            if (vs.Copilot == CopilotState.Busy) return "Copilot 正在运行 / Copilot is running";
            if (taskActive) return "有发送中或执行中的任务 / a task is being sent or running";
            return null;
        }

        public DateTime? LastRun { get; private set; }
        public AutoTrimReport LastReport { get; private set; }
        public bool Running => Volatile.Read(ref _busy) == 1;

        /// <summary>下次自动清理的预计时间；未开启时为 null。/ Expected time of the next automatic run; null when off.</summary>
        public DateTime? NextRun
        {
            get
            {
                var s = _settings();
                lock (_gate)
                    return s == null || !s.AutoTrimVsMemory ? (DateTime?)null
                        : (_anchor ?? _now()).AddMinutes(ClampInterval(s.AutoTrimIntervalMinutes));
            }
        }

        /// <summary>启动后台检查定时器（System.Threading.Timer，线程池回调）。/ Starts the background check timer (thread-pool callbacks).</summary>
        public void Start()
        {
            lock (_gate)
                if (_timer == null) _timer = new Timer(state => { _ = TickAsync(); }, null, CheckEvery, CheckEvery);
        }

        /// <summary>配置变化后从现在重新计时。/ Restarts the schedule from now after a settings change.</summary>
        public void Reschedule()
        {
            var s = _settings();
            lock (_gate) _anchor = s != null && s.AutoTrimVsMemory ? _now() : (DateTime?)null;
        }

        /// <summary>检查是否到期，到期则运行一次（到期判断在线程池上执行）。/ Checks the schedule and runs when due.</summary>
        internal async Task TickAsync()
        {
            try
            {
                var s = _settings();
                bool due;
                lock (_gate)
                {
                    if (s == null || !s.AutoTrimVsMemory) { _anchor = null; return; }
                    if (_anchor == null) { _anchor = _now(); return; }
                    due = _now() >= _anchor.Value.AddMinutes(ClampInterval(s.AutoTrimIntervalMinutes));
                }
                if (due) await RunAsync(false).ConfigureAwait(false);
            }
            catch (Exception ex) { VsMemory.Log("定期清理检查失败 / periodic cleanup check failed: " + ex.Message); }
        }

        /// <summary>立即执行一次（遵守相同的安全规则与阈值）。已有运行时返回 null。/ Runs once now (same safety rules and threshold); null if already running.</summary>
        public Task<AutoTrimReport> RunNowAsync() => RunAsync(true);

        private async Task<AutoTrimReport> RunAsync(bool manual)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
            var report = new AutoTrimReport { Manual = manual, At = _now() };
            try
            {
                var settings = _settings();
                long limit = (long)ClampThreshold(settings?.AutoTrimThresholdMB ?? 0) * 1048576;
                var targets = (await _targets().ConfigureAwait(false)) ?? new List<AutoTrimTarget>();
                var refs = targets.Where(t => t?.Ref != null).Select(t => t.Ref).ToList();
                var snap = await Task.Run(() => _take(refs)).ConfigureAwait(false);
                foreach (var g in snap.Groups.Where(x => x.Kind == MemGroupKind.Vs).ToList())
                {
                    var target = targets.FirstOrDefault(t => t?.Ref?.Vs != null && t.Ref.Vs.Pid == g.RootPid);
                    string reason = !g.CanClean ? "没有可安全清理的进程 / no safe process to trim"
                        : limit > 0 && g.WorkingSet <= limit ? $"工作集 {VsMemory.Mb(g.WorkingSet)} 未超过阈值 {VsMemory.Mb(limit)} / below threshold"
                        : SkipReason(target?.Ref.Vs, target?.TaskActive == true);
                    if (reason != null)
                    {
                        report.Skipped++;
                        report.Lines.Add("跳过 / Skipped " + g.Owner + "：" + reason);
                        continue;
                    }
                    var r = await _clean(g.Key, refs).ConfigureAwait(false);
                    report.Cleaned++;
                    report.Failed += r.Failed;
                    if (r.WsAfter > 0) report.FreedBytes += Math.Max(0, r.WsBefore - r.WsAfter);
                    report.Lines.Add("🧹 " + r.Summary());
                }
            }
            catch (Exception ex) { report.Error = ex.Message; }
            finally
            {
                report.At = _now();
                lock (_gate)
                {
                    LastRun = report.At;
                    LastReport = report;
                    if (_settings()?.AutoTrimVsMemory == true) _anchor = report.At;
                }
                string head = manual ? "定期清理（手动触发）/ Periodic cleanup (manual): " : "定期自动清理 / Periodic auto cleanup: ";
                foreach (var line in report.Lines.Where(l => !l.StartsWith("🧹", StringComparison.Ordinal))) VsMemory.Log(head + line);
                VsMemory.Log(head + report.Summary + (report.Failed > 0 ? $"；修剪失败 {report.Failed} / {report.Failed} trim failure(s)" : ""));
                Volatile.Write(ref _busy, 0);
            }
            try { Completed?.Invoke(report); }
            catch (Exception ex) { VsMemory.Log("定期清理通知失败 / periodic cleanup notify failed: " + ex.Message); }
            return report;
        }

        public void Dispose()
        {
            lock (_gate) { _timer?.Dispose(); _timer = null; }
        }
    }
}
