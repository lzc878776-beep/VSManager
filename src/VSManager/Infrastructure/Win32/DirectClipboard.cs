using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 直接通过 Win32 API 写入剪贴板（数据立即就位，不走 OLE 延迟渲染）。
    /// WinForms 的 Clipboard.SetDataObject / SetImage 走 OleSetClipboard + OleFlushClipboard：剪贴板同步 / 云桌面剪贴板代理等
    /// 监听程序收到变更通知后会立即打开剪贴板，并向本进程请求延迟渲染的数据，常常占住剪贴板数秒，导致紧接着的第二次写入
    /// （例如先写文字再写图片）失败并报「所请求的剪贴板操作失败」。直接写入不需要回调本进程，监听程序只是读取，占用时间极短。
    /// Writes the clipboard directly through Win32 (data is placed immediately, no OLE delayed rendering).
    /// WinForms Clipboard.SetDataObject / SetImage go through OleSetClipboard + OleFlushClipboard: clipboard sync tools or
    /// cloud-desktop clipboard agents open the clipboard as soon as they are notified and request delay-rendered data from
    /// this process, often holding it for seconds, so an immediate second write (e.g. text, then an image) fails with
    /// "Requested Clipboard operation did not succeed". Direct writes need no callback, so listeners only read briefly.
    /// </summary>
    public static class DirectClipboard
    {
        private const uint CF_UNICODETEXT = 13, CF_DIB = 8, GMEM_MOVEABLE = 0x0002;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
        [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

        /// <summary>等待剪贴板可用的默认时长。/ Default time to wait for the clipboard to become available.</summary>
        public const int DefaultTimeoutMs = 5000;

        /// <summary>写入 Unicode 文本。/ Writes Unicode text.</summary>
        public static void SetText(string text, int timeoutMs = DefaultTimeoutMs)
        {
            byte[] data = Encoding.Unicode.GetBytes((text ?? "") + "\0");
            Write(timeoutMs, new[] { Tuple.Create(CF_UNICODETEXT, data) });
        }

        /// <summary>写入图片（CF_DIB，系统自动合成 CF_BITMAP；另附 PNG 格式）。/ Writes an image (CF_DIB, from which Windows synthesizes CF_BITMAP; plus a PNG format).</summary>
        public static void SetImage(Image image, int timeoutMs = DefaultTimeoutMs)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            byte[] dib, png;
            // 先铺白底再转 24 位，避免透明区域在部分程序中显示为黑色 / Flatten onto white at 24 bpp so transparent areas never show as black
            using (var flat = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.White);
                    g.DrawImage(image, 0, 0, image.Width, image.Height);
                }
                using (var ms = new MemoryStream())
                {
                    flat.Save(ms, ImageFormat.Bmp);
                    const int fileHeader = 14;
                    dib = new byte[ms.Length - fileHeader];
                    Array.Copy(ms.GetBuffer(), fileHeader, dib, 0, dib.Length);
                }
            }
            using (var ms = new MemoryStream())
            {
                image.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }
            uint pngFormat = RegisterClipboardFormat("PNG");
            var items = pngFormat != 0
                ? new[] { Tuple.Create(CF_DIB, dib), Tuple.Create(pngFormat, png) }
                : new[] { Tuple.Create(CF_DIB, dib) };
            Write(timeoutMs, items);
        }

        /// <summary>清空剪贴板。/ Clears the clipboard.</summary>
        public static void Clear(int timeoutMs = DefaultTimeoutMs) => Write(timeoutMs, new Tuple<uint, byte[]>[0]);

        /// <summary>
        /// 写入后先等 <paramref name="settleMs"/>（让监听程序开始读取），再等剪贴板连续两次无人打开，最长 <paramref name="timeoutMs"/>。
        /// 返回是否确认已释放（超时也继续，由调用方的附件确认兜底）。
        /// After a write, waits <paramref name="settleMs"/> (so listeners start reading), then until nobody has the clipboard
        /// open for two consecutive polls, at most <paramref name="timeoutMs"/>. Returns whether release was confirmed (callers
        /// continue on timeout; their attachment confirmation is the backstop).
        /// </summary>
        public static bool WaitReleased(int settleMs, int timeoutMs)
        {
            if (settleMs > 0) Thread.Sleep(settleMs);
            var sw = Stopwatch.StartNew();
            int free = 0;
            while (sw.ElapsedMilliseconds < Math.Max(0, timeoutMs))
            {
                free = GetOpenClipboardWindow() == IntPtr.Zero ? free + 1 : 0;
                if (free >= 2) return true;
                Thread.Sleep(30);
            }
            return false;
        }

        /// <summary>当前占用剪贴板的进程名（无法确定时为 null），用于诊断。/ Name of the process holding the clipboard open (null if unknown), for diagnostics.</summary>
        public static string Holder()
        {
            try
            {
                var hwnd = GetOpenClipboardWindow();
                if (hwnd == IntPtr.Zero) return null;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return null;
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return null; }
        }

        private static void Write(int timeoutMs, Tuple<uint, byte[]>[] items)
        {
            // 以隐藏的消息窗口作为所有者：以 NULL 打开时 EmptyClipboard 会把所有者置空，文档说明此时 SetClipboardData 可能失败
            // Own the clipboard with a hidden message-only window: with NULL, EmptyClipboard clears the owner and SetClipboardData may fail
            var owner = new NativeWindow();
            owner.CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
            try
            {
                var sw = Stopwatch.StartNew();
                while (!OpenClipboard(owner.Handle))
                {
                    if (sw.ElapsedMilliseconds >= Math.Max(0, timeoutMs))
                    {
                        string holder = Holder();
                        throw new ExternalException("剪贴板被其他程序占用" + (holder == null ? "" : "（" + holder + "）") + "，等待 " + timeoutMs + " ms 后仍无法写入 / "
                            + "The clipboard is held by another program" + (holder == null ? "" : " (" + holder + ")") + " and could not be written within " + timeoutMs + " ms");
                    }
                    Thread.Sleep(20);
                }
                try
                {
                    if (!EmptyClipboard()) throw new ExternalException("清空剪贴板失败 / EmptyClipboard failed", Marshal.GetLastWin32Error());
                    foreach (var item in items)
                    {
                        IntPtr mem = Alloc(item.Item2);
                        if (SetClipboardData(item.Item1, mem) == IntPtr.Zero)
                        {
                            int err = Marshal.GetLastWin32Error();
                            GlobalFree(mem);
                            throw new ExternalException("写入剪贴板数据失败 / SetClipboardData failed", err);
                        }
                    }
                }
                finally { CloseClipboard(); }
            }
            finally { owner.DestroyHandle(); }
        }

        private static IntPtr Alloc(byte[] data)
        {
            IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, data.Length));
            if (mem == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed");
            IntPtr p = GlobalLock(mem);
            if (p == IntPtr.Zero) { GlobalFree(mem); throw new OutOfMemoryException("GlobalLock failed"); }
            try { Marshal.Copy(data, 0, p, data.Length); }
            finally { GlobalUnlock(mem); }
            return mem;
        }
    }
}
