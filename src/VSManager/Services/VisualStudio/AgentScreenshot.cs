using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// Captures visible screen pixels only; overlays may be included. Mandatory approval preview must precede upload.
    /// 仅截取窗口所在屏幕区域，可能包含覆盖层；上传前必须预览并批准。
    /// </summary>
    internal static class AgentScreenshot
    {
        internal static byte[] Capture(VsInstance vs)
        {
            if (vs == null) throw new ArgumentNullException(nameof(vs));
            int pid = vs.Pid;
            IntPtr main = vs.MainHwnd;
            IntPtr window = Native.GetForegroundWindow();
            Rectangle bounds = Validate(vs, pid, main, window);
            uint thread = Native.GetWindowThreadProcessId(window, out _);

            try
            {
                using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb))
                {
                    EnsureUnchanged(vs, pid, main, window, thread, bounds);
                    using (var graphics = Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                    EnsureUnchanged(vs, pid, main, window, thread, bounds);
                    if (IsUniform(bitmap))
                        throw Error("截图为空或只有单一颜色，请确认 VS 可见 / Capture is blank or uniform; ensure VS is visible.");

                    using (var output = new MemoryStream())
                    {
                        int side = Math.Max(bitmap.Width, bitmap.Height);
                        if (side > 1600)
                        {
                            int width = Math.Max(1, (int)((long)bitmap.Width * 1600 / side));
                            int height = Math.Max(1, (int)((long)bitmap.Height * 1600 / side));
                            using (var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                            {
                                using (var graphics = Graphics.FromImage(scaled))
                                {
                                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                    graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height));
                                }
                                scaled.Save(output, ImageFormat.Png);
                            }
                        }
                        else bitmap.Save(output, ImageFormat.Png);
                        EnsureUnchanged(vs, pid, main, window, thread, bounds);
                        return output.ToArray();
                    }
                }
            }
            catch (Win32Exception ex)
            {
                throw Error("无法读取屏幕像素 / Unable to capture screen pixels.", ex);
            }
            catch (ExternalException ex)
            {
                throw Error("无法生成 PNG 截图 / Unable to create the PNG screenshot.", ex);
            }
        }

        private static Rectangle Validate(VsInstance vs, int pid, IntPtr main, IntPtr window)
        {
            if (pid <= 0 || vs.Pid != pid || vs.MainHwnd != main || main == IntPtr.Zero ||
                !Native.IsWindow(main) || Native.GetWindowThreadProcessId(main, out uint mainPid) == 0 || mainPid != (uint)pid)
                throw Error("VS 进程或主窗口已改变 / VS process or main window changed.");
            if (window == IntPtr.Zero || !Native.IsWindow(window) || !Native.IsWindowVisible(window) ||
                Native.IsIconic(window) || Native.GetForegroundWindow() != window ||
                Native.GetWindowThreadProcessId(window, out uint windowPid) == 0 || windowPid != (uint)pid)
                throw Error("目标 VS 窗口必须在前台且未最小化 / Target VS window must be visible, foreground and not minimized.");

            IntPtr current = window;
            for (int depth = 0; current != main && current != IntPtr.Zero && depth < 32; depth++)
                current = Native.GetWindow(current, Native.GW_OWNER);
            if (current != main)
                throw Error("前台窗口不属于目标 VS 主窗口 / Foreground window is not the target VS main window or its owned popup.");

            if (!Native.GetWindowRect(window, out Native.RECT rect))
                throw Error("无法读取窗口范围 / Unable to read window bounds.");
            // DWM excludes invisible resize borders, which extend outside the screen when maximized.
            // DWM 排除最大化时延伸到屏幕外的不可见调整边框。
            if (DwmGetWindowAttribute(window, 9, out Native.RECT visible, Marshal.SizeOf(typeof(Native.RECT))) == 0)
                rect = visible;
            long width = (long)rect.Right - rect.Left;
            long height = (long)rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || width * height > 32000000)
                throw Error("窗口尺寸无效或过大 / Invalid or excessive window dimensions.");
            var bounds = new Rectangle(rect.Left, rect.Top, (int)width, (int)height);
            if (Native.IsZoomed(window))
                bounds = Rectangle.Intersect(bounds, Screen.FromHandle(window).WorkingArea);
            if (bounds.Width <= 0 || bounds.Height <= 0 || !SystemInformation.VirtualScreen.Contains(bounds))
                throw Error("窗口必须完全位于屏幕内 / The entire window must be on screen.");
            return bounds;
        }

        private static void EnsureUnchanged(VsInstance vs, int pid, IntPtr main, IntPtr window, uint thread, Rectangle bounds)
        {
            if (Validate(vs, pid, main, window) != bounds ||
                Native.GetWindowThreadProcessId(window, out _) != thread)
                throw Error("截图期间窗口已移动或改变，请重试 / Window moved or changed during capture; retry.");
        }

        private static bool IsUniform(Bitmap bitmap)
        {
            Color first = bitmap.GetPixel(0, 0);
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[bitmap.Width * 3];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x += 3)
                        if (row[x] != first.B || row[x + 1] != first.G || row[x + 2] != first.R) return false;
                }
                return true;
            }
            finally { bitmap.UnlockBits(data); }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Native.RECT value, int size);

        private static InvalidOperationException Error(string message, Exception inner = null) =>
            new InvalidOperationException("截图失败 / Screenshot failed: " + message, inner);
    }
}
