using System;
using System.Collections.Generic;

namespace VSManager
{
    /// <summary>
    /// 防重启风暴：滑动时间窗口内最多允许 N 次自动重启；超过后进入「已停止」状态，直到窗口内的记录过期或手动重置。
    /// Restart-storm guard: at most N automatic restarts within a sliding window; beyond that it stays "stopped" until the
    /// recorded restarts expire or it is reset manually.
    /// </summary>
    public sealed class RestartLimiter
    {
        private readonly Queue<DateTime> _history = new Queue<DateTime>();

        /// <summary>窗口内最多重启次数。/ Max restarts within the window.</summary>
        public int MaxCount { get; set; }

        /// <summary>时间窗口。/ The sliding window.</summary>
        public TimeSpan Window { get; set; }

        /// <summary>是否因超过上限而停止了自动重启（需手动重置或等待窗口过期）。/ Whether automatic restarts were stopped after hitting the limit.</summary>
        public bool Tripped { get; private set; }

        public RestartLimiter(int maxCount, TimeSpan window)
        {
            MaxCount = maxCount;
            Window = window;
        }

        /// <summary>窗口内已发生的重启次数。/ Restarts recorded within the window.</summary>
        public int Count(DateTime now)
        {
            Expire(now);
            return _history.Count;
        }

        /// <summary>
        /// 申请一次自动重启：未超过上限时记录并返回 true；否则返回 false 并标记为已停止。
        /// Requests one automatic restart: records it and returns true below the limit; otherwise returns false and trips.
        /// </summary>
        public bool TryAcquire(DateTime now)
        {
            Expire(now);
            if (Tripped && _history.Count == 0) Tripped = false;
            if (Tripped || _history.Count >= Math.Max(1, MaxCount))
            {
                Tripped = true;
                return false;
            }
            _history.Enqueue(now);
            return true;
        }

        /// <summary>手动重启后清空记录，恢复自动重启。/ Clears the history after a manual restart and re-enables automatic restarts.</summary>
        public void Reset()
        {
            _history.Clear();
            Tripped = false;
        }

        private void Expire(DateTime now)
        {
            while (_history.Count > 0 && now - _history.Peek() >= Window) _history.Dequeue();
        }
    }
}
