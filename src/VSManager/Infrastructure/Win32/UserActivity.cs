using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VSManager
{
    /// <summary>
    /// 用户键盘 / 鼠标活动检测：队列发送在切换前台前等待用户短暂空闲，避免与用户抢焦点。
    /// User keyboard / mouse activity: queued sends wait for a short idle moment before switching the foreground, so they do not fight the user for focus.
    /// </summary>
    public static class UserActivity
    {
        /// <summary>切换前台前要求的最短空闲时间。/ Minimum idle time required before switching the foreground.</summary>
        public const int RequiredIdleMs = 1500;

        /// <summary>单次发送内最多等待用户空闲的时间；超时后任务稍后重试。/ Longest in-send wait for idle; after that the task retries later.</summary>
        public const int MaxWaitMs = 4000;

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        /// <summary>本程序模拟输入后，这段时间内的输入视为本程序产生。/ Input within this window after our own synthetic input is attributed to us.</summary>
        internal const int SyntheticWindowMs = 500;

        private static int _lastSynthetic;
        private static int _hasSynthetic;

        /// <summary>
        /// 记录本程序即将模拟键盘输入（Alt 解锁前台、Ctrl+V、回车等），避免被误判为用户正在操作。
        /// Records that we are about to inject keyboard input (Alt foreground unlock, Ctrl+V, Enter, ...) so it is not mistaken for user activity.
        /// </summary>
        public static void MarkSynthetic()
        {
            Interlocked.Exchange(ref _lastSynthetic, Environment.TickCount);
            Interlocked.Exchange(ref _hasSynthetic, 1);
        }

        /// <summary>距用户最近一次输入的毫秒数；无法读取时视为空闲。/ Milliseconds since the user's last input; treated as idle when unavailable.</summary>
        public static int IdleMs()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) return int.MaxValue;
            int now = Environment.TickCount;
            int last = (int)info.dwTime;
            if (IsOwnInput(last, Volatile.Read(ref _hasSynthetic) != 0, Volatile.Read(ref _lastSynthetic))) return int.MaxValue;
            return Math.Max(0, unchecked(now - last));
        }

        /// <summary>
        /// 最近一次输入是否紧随本程序的模拟输入（即由本程序产生，而非用户操作）。
        /// Whether the latest input closely follows our own synthetic input (i.e. produced by us, not by the user).
        /// </summary>
        internal static bool IsOwnInput(int lastInputTick, bool hasSynthetic, int syntheticTick)
        {
            if (!hasSynthetic) return false;
            int delta = unchecked(lastInputTick - syntheticTick);
            return delta >= 0 && delta <= SyntheticWindowMs;
        }

        /// <summary>
        /// 等待用户空闲至少 <paramref name="requiredMs"/>；最多等待 <paramref name="maxWaitMs"/>，返回是否已空闲。
        /// Waits until the user has been idle for at least <paramref name="requiredMs"/>, up to <paramref name="maxWaitMs"/>; returns whether idle.
        /// </summary>
        public static bool WaitIdle(Func<int> idleMs, int requiredMs, int maxWaitMs, Action<int> sleep = null)
        {
            idleMs = idleMs ?? IdleMs;
            sleep = sleep ?? Thread.Sleep;
            int waited = 0;
            while (true)
            {
                int idle = idleMs();
                if (idle >= requiredMs) return true;
                if (waited >= maxWaitMs) return false;
                int step = Math.Max(50, Math.Min(250, requiredMs - idle));
                sleep(step);
                waited += step;
            }
        }
    }
}
