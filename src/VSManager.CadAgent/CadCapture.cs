using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VSManager.CadAgent
{
    /// <summary>
    /// 截取当前 CAD 主窗口为 PNG（只在内存中返回，不写磁盘）。/ Captures the current CAD main window as PNG (returned in memory, never written to disk).
    /// </summary>
    public static class CadCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>最大 PNG 字节数；超出时缩小。/ Maximum PNG bytes; larger images are scaled down.</summary>
        public const int MaxBytes = 8 * 1024 * 1024;

        public static CadArtifact CaptureMainWindow(int maxWidth = 1920)
        {
            IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("找不到 CAD 主窗口 / CAD main window not found");
            if (IsIconic(hwnd)) throw new InvalidOperationException("CAD 窗口已最小化，无法截图 / The CAD window is minimized");
            if (!GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) throw new InvalidOperationException("CAD 窗口尺寸无效 / Invalid CAD window size");
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                if (!PrintWithTimeout(hwnd, bmp, 5000))
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
                return ToArtifact(bmp, Math.Max(320, maxWidth));
            }
        }

        // PrintWindow 要求窗口线程处理 WM_PRINT；主线程忙时限时退回屏幕复制。/ PrintWindow needs the window thread; fall back to screen copy when it is busy.
        private static bool PrintWithTimeout(IntPtr hwnd, Bitmap bmp, int timeoutMs)
        {
            using (var buffer = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb))
            {
                bool ok = false;
                var t = new Thread(() =>
                {
                    try
                    {
                        using (var g = Graphics.FromImage(buffer))
                        {
                            IntPtr hdc = g.GetHdc();
                            try { ok = PrintWindow(hwnd, hdc, 2); } finally { g.ReleaseHdc(hdc); }
                        }
                    }
                    catch { ok = false; }
                }) { IsBackground = true };
                t.Start();
                if (!t.Join(timeoutMs) || !ok) return false;
                using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(buffer, 0, 0);
                return true;
            }
        }

        internal static CadArtifact ToArtifact(Bitmap bmp, int maxWidth)
        {
            int width = Math.Min(bmp.Width, maxWidth);
            while (true)
            {
                int height = Math.Max(1, (int)((long)bmp.Height * width / bmp.Width));
                byte[] png;
                using (var scaled = width == bmp.Width ? (Bitmap)bmp.Clone() : new Bitmap(width, height))
                {
                    if (width != bmp.Width)
                        using (var g = Graphics.FromImage(scaled))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(bmp, 0, 0, width, height);
                        }
                    using (var ms = new MemoryStream()) { scaled.Save(ms, ImageFormat.Png); png = ms.ToArray(); }
                }
                if (png.Length <= MaxBytes || width <= 320)
                    return new CadArtifact { Kind = CadArtifact.Image, Name = "cad-" + DateTime.Now.ToString("HHmmss") + ".png", Mime = "image/png", Data = Convert.ToBase64String(png), Width = width, Height = height };
                width = Math.Max(320, width * 3 / 4);
            }
        }
    }

    /// <summary>
    /// 读取适配包声明的日志末尾（共享读取，不锁定日志文件）。/ Reads the tail of adapter-declared logs (shared read; never locks the log file).
    /// </summary>
    public static class CadLogReader
    {
        public const int MaxBytes = 64 * 1024;

        public static CadArtifact ReadTail(string path, int lines, DateTime? sinceLocal = null)
        {
            lines = Math.Max(1, Math.Min(2000, lines));
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - MaxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                string text = Decode(buf, read);
                var all = text.Replace("\r\n", "\n").Split('\n').ToList();
                if (start > 0 && all.Count > 0) all.RemoveAt(0);
                if (all.Count > 0 && all[all.Count - 1].Length == 0) all.RemoveAt(all.Count - 1);
                if (sinceLocal.HasValue) all = FilterSince(all, sinceLocal.Value);
                var tail = all.Skip(Math.Max(0, all.Count - lines));
                return new CadArtifact { Kind = CadArtifact.Log, Name = Path.GetFileName(path), Mime = "text/plain", Content = string.Join("\n", tail) };
            }
        }

        private static string Decode(byte[] buf, int count)
        {
            try { return new UTF8Encoding(false, true).GetString(buf, 0, count); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(buf, 0, count); }
        }

        // 行首为时间戳时只保留 since 之后的行；无法识别时间的行跟随上一行。/ Keeps lines after since when they start with a timestamp; unparsable lines follow the previous line.
        private static List<string> FilterSince(List<string> all, DateTime since)
        {
            var kept = new List<string>();
            bool keep = false, sawStamp = false;
            foreach (var line in all)
            {
                if (line.Length >= 19 && DateTime.TryParse(line.Substring(0, 19).Replace('T', ' '), out var t)) { keep = t >= since; sawStamp = true; }
                if (keep) kept.Add(line);
            }
            return sawStamp ? kept : all;
        }
    }
}
