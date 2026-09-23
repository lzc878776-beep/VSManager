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
        private static void Main(string[] args)
        {
            // 看门狗模式：不显示界面、不占用单实例互斥体 / Watchdog mode: no UI, does not take the single-instance mutex
            int? watch = ProcessWatchdog.IntArg(args, ProcessWatchdog.WatchdogArg);
            if (watch.HasValue)
            {
                Application.EnableVisualStyles();
                Environment.ExitCode = ProcessWatchdog.Run(watch.Value);
                return;
            }
            // 解析重启参数，必要时等待被替换的旧实例退出 / Parse restart arguments and wait for a replaced instance if needed
            ProcessWatchdog.InitMain(args);
            using (var mutex = new Mutex(true, "VSManager_SingleInstance_{7D4C2E1A}", out bool created))
            {
                if (!created)
                {
                    // 允许已运行的实例抢前台，再通知它显示主窗口 / Let the running instance take the foreground, then ask it to show
                    Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                    if (ShowMainMessage == 0 || !Native.PostMessage(Native.HWND_BROADCAST, ShowMainMessage, IntPtr.Zero, IntPtr.Zero))
                        MessageBox.Show("多 VS 管理工具已在运行（请查看系统托盘，或按 Ctrl+Alt+0 显示主窗口）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    // 已有实例在运行属正常退出，看门狗无需重启 / Another instance is running: a clean exit, no watchdog restart
                    ProcessWatchdog.MarkCleanExit();
                    return;
                }
                AppLog.HookUnhandled();
                MessageFilter.Register();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                ProcessWatchdog.MarkCleanExit();
            }
        }
    }
}
