using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace VSManager
{
    /// <summary>
    /// 操作 VS（发送消息、关闭文档等）前记录前台窗口，结束后若前台被被操作的 VS 抢走，则切回原窗口（通常是 VSManager）。
    /// 发送前文档清理等 DTE 调用会在发送流程记录前台状态之前就把 VS 带到前台，发送流程内部的切回因此失效，需要在最外层统一核对。
    /// Records the foreground window before operating on VS (sending, closing documents, ...); afterwards, if the operated VS took the
    /// foreground, switches back to the original window (usually VSManager). DTE calls such as the pre-send document cleanup can raise VS
    /// before the send routine records the foreground state, which defeats its own switch-back, so the outermost caller verifies it.
    /// </summary>
    internal sealed class ForegroundKeeper
    {
        public IntPtr Window { get; }
        public uint Pid { get; }

        private ForegroundKeeper(IntPtr window, uint pid) { Window = window; Pid = pid; }

        /// <summary>记录当前前台窗口。/ Records the current foreground window.</summary>
        public static ForegroundKeeper Capture()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint pid = 0;
            if (fg != IntPtr.Zero) Native.GetWindowThreadProcessId(fg, out pid);
            return new ForegroundKeeper(fg, pid);
        }

        /// <summary>
        /// 是否需要切回：开始时前台不属于被操作的 VS，而现在属于。用户本来就在该 VS 中时不切走。
        /// Whether to switch back: the foreground did not belong to an operated VS at the start but does now. Never leaves a VS the user was already in.
        /// </summary>
        internal static bool ShouldRestore(uint startPid, uint nowPid, ICollection<int> vsPids)
        {
            if (vsPids == null || vsPids.Count == 0 || nowPid == 0) return false;
            if (startPid != 0 && vsPids.Contains((int)startPid)) return false;
            return vsPids.Contains((int)nowPid);
        }

        /// <summary>操作结束后继续守护前台的时长、检查间隔（毫秒）与最多切回次数。/ How long (ms) the foreground is still guarded after an operation, the check interval and the maximum number of switch-backs.</summary>
        public const int GuardMs = 6000, GuardTickMs = 200, GuardMaxRestores = 3;

        internal enum GuardStep { Wait, Restore, Stop }

        /// <summary>
        /// 延迟守护的下一步：超时或用户已自行操作（键盘 / 鼠标）时停止；VS 抢到前台时切回；否则继续等待。
        /// VS 常在发送完成后才异步激活自己（或临时窗口关闭后系统把前台交给 VS），只在结束时检查一次会漏掉。
        /// Next step of the delayed guard: stop on timeout or once the user acted (keyboard / mouse); switch back when VS took the
        /// foreground; otherwise keep waiting. VS often activates itself asynchronously after the send finished (or the system hands it
        /// the foreground when a transient window closes), which a single check at the end misses.
        /// </summary>
        internal static GuardStep NextGuardStep(int elapsedMs, bool userActed, bool stolen)
        {
            if (userActed || elapsedMs > GuardMs) return GuardStep.Stop;
            return stolen ? GuardStep.Restore : GuardStep.Wait;
        }

        /// <summary>
        /// 最近一次输入是否晚于 sinceTick（Environment.TickCount 时刻，可回绕）。/ Whether the last input happened after sinceTick (Environment.TickCount, wraps).
        /// </summary>
        internal static bool UserActedSince(int lastInputTick, int sinceTick) => unchecked(lastInputTick - sinceTick) > 0;

        /// <summary>当前前台是否被被操作的 VS 抢走。/ Whether an operated VS currently holds the foreground it did not hold at the start.</summary>
        public bool IsStolen(IEnumerable<int> vsPids)
        {
            var pids = new HashSet<int>((vsPids ?? Enumerable.Empty<int>()).Where(p => p > 0));
            IntPtr now = Native.GetForegroundWindow();
            uint nowPid = 0;
            if (now != IntPtr.Zero) Native.GetWindowThreadProcessId(now, out nowPid);
            return ShouldRestore(Pid, nowPid, pids);
        }

        /// <summary>
        /// 前台被被操作的 VS 抢走时切回原窗口；原窗口已关闭时切回 ownWindow。必须在界面线程调用。返回说明文字，未切换时为 null。
        /// Switches back when an operated VS took the foreground; falls back to ownWindow if the original window is gone. Call on the UI thread.
        /// Returns a description, or null when nothing was switched.
        /// </summary>
        public string RestoreIfStolen(IEnumerable<int> vsPids, IntPtr ownWindow, bool keepTopMost)
        {
            var pids = new HashSet<int>((vsPids ?? Enumerable.Empty<int>()).Where(p => p > 0));
            IntPtr now = Native.GetForegroundWindow();
            uint nowPid = 0;
            if (now != IntPtr.Zero) Native.GetWindowThreadProcessId(now, out nowPid);
            if (!ShouldRestore(Pid, nowPid, pids)) return null;

            IntPtr target = Window != IntPtr.Zero && Native.IsWindow(Window) && Native.IsWindowVisible(Window) && !pids.Contains((int)Pid) ? Window : ownWindow;
            if (target == IntPtr.Zero || !Native.IsWindow(target)) return null;
            // 不把最小化或隐藏到托盘的窗口弹出来 / Never pop up a window that is minimized or hidden to the tray
            if (target == ownWindow && target != Window && (Native.IsIconic(target) || !Native.IsWindowVisible(target))) return null;
            Native.GetWindowThreadProcessId(target, out uint targetPid);
            bool ok;
            if (targetPid == (uint)Process.GetCurrentProcess().Id) ok = Native.ForceForeground(target, keepTopMost);
            else
            {
                Native.Activate(target);
                ok = Native.GetForegroundWindow() == target;
            }
            return (ok ? "VS 抢占了前台，已切回原窗口 / VS took the foreground; switched back to the original window"
                       : "VS 抢占了前台，切回原窗口未成功 / VS took the foreground; switching back failed")
                + (target == ownWindow ? "（VSManager）" : "");
        }
    }
}
