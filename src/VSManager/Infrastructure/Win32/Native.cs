using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace VSManager
{
    public static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool FlashWindowEx(ref FLASHWINFO fi);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("ole32.dll")] public static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
        [DllImport("ole32.dll")] public static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        public const int SW_RESTORE = 9, SW_MAXIMIZE = 3, SW_SHOW = 5;
        public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2), HWND_BROADCAST = new IntPtr(0xFFFF);
        public const int ASFW_ANY = -1;
        public const uint GW_OWNER = 4;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;

        public static string GetText(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClass(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static Rectangle GetRect(IntPtr h)
        {
            GetWindowRect(h, out var r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        public static List<IntPtr> GetProcessWindows(int pid, bool visibleOnly = true)
        {
            var list = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint p);
                if (p == pid && (!visibleOnly || IsWindowVisible(h))) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static void Activate(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            var fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != me && AttachThreadInput(me, fgThread, true);
            BringWindowToTop(h);
            bool ok = SetForegroundWindow(h);
            if (attached) AttachThreadInput(me, fgThread, false);
            if (!ok || GetForegroundWindow() != h)
            {
                // Alt 键技巧：解除系统前台锁定
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SetForegroundWindow(h);
                BringWindowToTop(h);
            }
        }

        /// <summary>
        /// 把本程序的窗口可靠地切到前台：恢复最小化 → 附着前台线程输入后 SetForegroundWindow →
        /// 仍被前台锁拒绝时短暂置顶再复位（至少保证可见），并闪烁任务栏提示。不模拟按键。
        /// Reliably bring our own window to the foreground: restore → attach to the foreground thread and SetForegroundWindow →
        /// if the foreground lock still refuses, briefly toggle topmost (so it is at least visible) and flash the taskbar. No synthetic keys.
        /// </summary>
        /// <param name="keepTopMost">窗口本身是否应保持置顶（用户设置）。/ Whether the window should stay topmost (user setting).</param>
        /// <returns>是否已成为前台窗口。/ True if the window is now in the foreground.</returns>
        public static bool ForceForeground(IntPtr h, bool keepTopMost)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) return false;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            else if (!IsWindowVisible(h)) ShowWindow(h, SW_SHOW);
            if (GetForegroundWindow() == h) return true;

            var fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            if (GetForegroundWindow() == h) return true;

            // 前台锁仍拒绝：短暂置顶再复位，把窗口抬到最上层 / Still refused: raise it via a brief topmost toggle
            const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW;
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, flags);
            if (!keepTopMost) SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            SetForegroundWindow(h);
            if (GetForegroundWindow() == h) return true;
            Flash(h);
            return false;
        }

        public static void MoveAndMaximize(IntPtr h, Rectangle workArea)
        {
            if (IsIconic(h) || IsZoomed(h)) ShowWindow(h, SW_RESTORE);
            int w = Math.Min(workArea.Width, 1600), hh = Math.Min(workArea.Height, 1000);
            SetWindowPos(h, IntPtr.Zero, workArea.X + 20, workArea.Y + 20, w - 40, hh - 40, SWP_NOZORDER | SWP_NOACTIVATE);
            ShowWindow(h, SW_MAXIMIZE);
        }

        public static void MoveWindow(IntPtr h, Rectangle r)
        {
            if (IsIconic(h) || IsZoomed(h)) ShowWindow(h, SW_RESTORE);
            SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public static void Flash(IntPtr h)
        {
            var fi = new FLASHWINFO { hwnd = h, dwFlags = 3 | 0x0C /* ALL | TIMERNOFG */, uCount = 5, dwTimeout = 0 };
            fi.cbSize = (uint)Marshal.SizeOf(fi);
            FlashWindowEx(ref fi);
        }
    }

    /// <summary>VS 忙时 COM 调用会被拒绝，注册消息过滤器以自动重试。</summary>
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    internal class MessageFilter : IOleMessageFilter
    {
        [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        public static void Register() => CoRegisterMessageFilter(new MessageFilter(), out _);

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;
        // 被拒绝时 100ms 后重试，最多重试约 2 秒
        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType) =>
            dwRejectType == 2 && dwTickCount < 2000 ? 100 : -1;
        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2;
    }
}
