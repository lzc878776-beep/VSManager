using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    public partial class MainForm
    {
        private VsAutoMemoryTrimmer _autoTrim;

        /// <summary>创建并启动定期自动清理（后台定时器）。/ Creates and starts the periodic auto cleanup (background timer).</summary>
        private void StartAutoTrim()
        {
            if (_autoTrim != null) return;
            _autoTrim = new VsAutoMemoryTrimmer(() => _settings, CollectAutoTrimTargets);
            _autoTrim.Completed += report =>
            {
                try { BeginInvoke(new Action(() => ReportAutoTrim(report))); }
                catch (InvalidOperationException) { }
            };
            _autoTrim.Start();
        }

        /// <summary>
        /// 在界面线程上采集 VS 列表与每个实例是否有发送中 / 执行中的任务（只读，开销极小）。
        /// Captures the VS list and whether each instance has a task being sent / running, on the UI thread (read-only, cheap).
        /// </summary>
        private Task<IList<AutoTrimTarget>> CollectAutoTrimTargets() => OnUi<IList<AutoTrimTarget>>(() =>
            BuildVsRefs().Select(r => new AutoTrimTarget
            {
                Ref = r,
                TaskActive = _tasks.Items.Any(t => t.VsKey == r.Vs.Key && (t.Status == QueueStatus.Sending || t.Status == QueueStatus.Running))
            }).ToList());

        /// <summary>
        /// 状态栏始终更新；只有实际释放了内存才通知与播报，无效果或全部跳过时只记日志。
        /// Always updates the status bar; notifies and announces only when memory was actually freed, otherwise it is only logged.
        /// </summary>
        private void ReportAutoTrim(AutoTrimReport report)
        {
            if (report == null || IsDisposed) return;
            SetStatus("🧠 " + report.Summary);
            if (report.Worth) NotifyWithVoice("内存 / Memory", report.SummaryZh, report.SummaryEn);
        }
    }
}
