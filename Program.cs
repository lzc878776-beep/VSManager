using System;
using System.Threading;
using System.Windows.Forms;

namespace VSManager
{
    internal static class Program
    {
        /// <summary>
        /// 第二个实例启动时广播此消息，已运行的实例收到后显示主窗口。
        /// Broadcast by a second instance; the running instance shows its main window.
        /// </summary>
        public static readonly int ShowMainMessage = Native.RegisterWindowMessage("VSManager_ShowMain_{7D4C2E1A}");

        [STAThread]
        private static void Main()
        {
            using (var mutex = new Mutex(true, "VSManager_SingleInstance_{7D4C2E1A}", out bool created))
            {
                if (!created)
                {
                    // 允许已运行的实例抢前台，再通知它显示主窗口 / Let the running instance take the foreground, then ask it to show
                    Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                    if (ShowMainMessage == 0 || !Native.PostMessage(Native.HWND_BROADCAST, ShowMainMessage, IntPtr.Zero, IntPtr.Zero))
                        MessageBox.Show("多 VS 管理工具已在运行（请查看系统托盘，或按 Ctrl+Alt+0 显示主窗口）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                MessageFilter.Register();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
