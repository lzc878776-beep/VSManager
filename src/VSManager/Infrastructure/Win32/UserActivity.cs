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

        /// <summary>距用户最近一次输入的毫秒数；无法读取时视为空闲。/ Milliseconds since the user's last input; treated as idle when unavailable.</summary>
        public static int IdleMs()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) return int.MaxValue;
            return Math.Max(0, unchecked(Environment.TickCount - (int)info.dwTime));
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
