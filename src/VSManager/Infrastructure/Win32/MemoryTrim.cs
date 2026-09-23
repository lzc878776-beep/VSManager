using System;
using System.Diagnostics;
using System.Runtime;

namespace VSManager
{
    /// <summary>
    /// 定期回收 UI Automation / COM 包装对象占用的非托管内存。
    /// 这些对象的托管部分很小，GC 很少触发，但每个对象背后挂着 UIA 节点句柄等非托管内存，只有终结后才释放，
    /// 长时间轮询 VS 时私有字节会持续上涨。这里在内存明显增长或间隔足够久时做一次完整回收并运行终结器。
    /// Periodically reclaims unmanaged memory held by UI Automation / COM wrappers. Their managed part is tiny, so the GC
    /// rarely runs, yet each wrapper pins native UIA node handles that are only released on finalization; long polling of VS
    /// makes private bytes creep up. A full collection with finalizers runs when memory grew noticeably or enough time passed.
    /// </summary>
    public static class MemoryTrim
    {
        private const long GrowthBytes = 24L * 1024 * 1024;
        private static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(5);
        private static DateTime _last = DateTime.Now;
        private static long _baseline;
        private static int _busy;

        /// <summary>界面线程定时调用（开销极小）。/ Call periodically from the UI timer (cheap).</summary>
        public static void Tick(bool force = false)
        {
            try
            {
                long priv;
                using (var p = Process.GetCurrentProcess()) priv = p.PrivateMemorySize64;
                if (_baseline == 0) _baseline = priv;
                if (!force && priv - _baseline < GrowthBytes && DateTime.Now - _last < MaxInterval) return;
                if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
                _last = DateTime.Now;
                // 在后台线程等待终结器，避免 STA 上的 COM 终结阻塞界面 / Wait for finalizers off the UI thread
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Collect();
                        using (var p = Process.GetCurrentProcess()) _baseline = p.PrivateMemorySize64;
                    }
                    catch { }
                    finally { System.Threading.Volatile.Write(ref _busy, 0); }
                });
            }
            catch { }
        }

        /// <summary>
        /// 立即做一次完整回收（同步执行，请在后台线程调用），用于内存面板的「清理 VSManager」。
        /// Runs a full collection right away (synchronous; call from a background thread). Used by "Clean VSManager".
        /// </summary>
        public static void CollectNow()
        {
            _last = DateTime.Now;
            Collect();
            try { using (var p = Process.GetCurrentProcess()) _baseline = p.PrivateMemorySize64; } catch { }
        }

        private static void Collect()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
