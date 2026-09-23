using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 进程看门狗（可选，默认关闭）：开启后主程序启动一个独立的看门狗进程（同一个 exe，参数 --watchdog &lt;pid&gt;）。
    /// 主程序正常退出前发出「正常退出」信号；看门狗发现主程序在没有该信号的情况下退出（崩溃、被结束）时，按防风暴限制重新启动它。
    /// 任务清单与配置在每次变更时已写盘，崩溃时还会尽力再保存一次；重启后按磁盘状态恢复。
    /// Process watchdog (optional, off by default): when enabled, the app starts a separate watchdog process (same exe,
    /// argument --watchdog &lt;pid&gt;). The app signals "clean exit" before it quits normally; when the watchdog sees the app
    /// exit without that signal (crash, killed) it starts it again, subject to the restart-storm limit.
    /// The task list and settings are written on every change and saved once more on a crash; a restart recovers from disk.
    /// </summary>
    public static class ProcessWatchdog
    {
        /// <summary>以看门狗模式运行。/ Run as the watchdog.</summary>
        public const string WatchdogArg = "--watchdog";
        /// <summary>启动前等待指定进程退出（用于「重启 VSManager」）。/ Wait for a process to exit before starting (used by "Restart VSManager").</summary>
        public const string WaitPidArg = "--wait-pid";
        /// <summary>由看门狗重新启动，后跟上次的退出代码。/ Restarted by the watchdog, followed by the previous exit code.</summary>
        public const string RestartedArg = "--restarted";
        /// <summary>已被指定 PID 的看门狗看护。/ Already supervised by the watchdog with this PID.</summary>
        public const string WatchedByArg = "--watched-by";

        /// <summary>看门狗日志文件。/ Watchdog log file.</summary>
        public const string LogFile = "watchdog.log";

        private static EventWaitHandle _cleanExit, _stop;
        private static int _watchdogPid;

        /// <summary>本次是否由看门狗在异常退出后重新启动。/ Whether this instance was restarted by the watchdog after an abnormal exit.</summary>
        public static bool RestartedAfterCrash { get; private set; }

        /// <summary>上一个实例的退出代码（仅在 <see cref="RestartedAfterCrash"/> 时有效）。/ Exit code of the previous instance (valid when <see cref="RestartedAfterCrash"/>).</summary>
        public static int PreviousExitCode { get; private set; }

        private static string CleanExitName(int pid) => @"Local\VSManager_CleanExit_" + pid;
        private static string StopName(int pid) => @"Local\VSManager_WatchdogStop_" + pid;

        private static void Log(string text) => AppLog.Write(LogFile, text);

        /// <summary>读取命令行中的整数参数。/ Reads an integer argument from the command line.</summary>
        internal static int? IntArg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    return v;
            return null;
        }

        /// <summary>显示用的退出代码（十六进制与十进制）。/ Exit code for display (hex and decimal).</summary>
        public static string FormatExitCode(int code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture) + " (" + code + ")";

        #region 主程序侧 / App side

        /// <summary>
        /// 主程序启动时调用：解析参数、等待被替换的旧实例退出、创建退出信号。
        /// Called at app start: parses arguments, waits for the replaced instance to exit and creates the exit signals.
        /// </summary>
        public static void InitMain(string[] args)
        {
            int? wait = IntArg(args, WaitPidArg);
            if (wait.HasValue && wait.Value != Process.GetCurrentProcess().Id)
            {
                try { using (var p = Process.GetProcessById(wait.Value)) p.WaitForExit(30000); } catch { }
            }
            int? restarted = IntArg(args, RestartedArg);
            RestartedAfterCrash = restarted.HasValue;
            PreviousExitCode = restarted ?? 0;
            _watchdogPid = IntArg(args, WatchedByArg) ?? 0;

            int me = Process.GetCurrentProcess().Id;
            try
            {
                _cleanExit = new EventWaitHandle(false, EventResetMode.ManualReset, CleanExitName(me));
                _stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName(me));
            }
            catch (Exception ex) { AppLog.Error(LogFile, "创建看门狗信号失败 / Failed to create watchdog signals", ex); }
        }

        /// <summary>看门狗进程是否在运行。/ Whether the watchdog process is running.</summary>
        public static bool WatchdogRunning => IsAlive(_watchdogPid);

        private static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch { return false; }
        }

        /// <summary>
        /// 按设置启动或停止看门狗（启动时与修改设置后调用）。返回错误信息，成功时返回 null。
        /// Starts or stops the watchdog according to the settings (at start and after a settings change). Returns an error or null.
        /// </summary>
        public static string Apply(AppSettings s)
        {
            if (_stop == null) return null;
            try
            {
                if (!s.ProcessWatchdogEnabled)
                {
                    if (WatchdogRunning)
                    {
                        _stop.Set();
                        Log("主程序关闭看门狗 / Watchdog disabled by the app");
                    }
                    return null;
                }
                _stop.Reset();
                if (WatchdogRunning) return null;
                int me = Process.GetCurrentProcess().Id;
                var psi = new ProcessStartInfo(Application.ExecutablePath, WatchdogArg + " " + me.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = false };
                using (var p = Process.Start(psi)) _watchdogPid = p?.Id ?? 0;
                Log("启动看门狗 / Watchdog started（PID " + _watchdogPid + "，看护 / watching " + me + "）");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Error(LogFile, "启动看门狗失败 / Failed to start the watchdog", ex);
                return ex.Message;
            }
        }

        /// <summary>正常退出前调用：通知看门狗无需重启。/ Call before a normal exit: tells the watchdog not to restart.</summary>
        public static void MarkCleanExit()
        {
            try { _cleanExit?.Set(); } catch { }
        }

        /// <summary>
        /// 启动一个新实例替换当前实例（新实例会等待当前进程退出后再启动）。返回错误信息，成功时返回 null。
        /// Starts a new instance to replace this one (it waits for this process to exit first). Returns an error or null.
        /// </summary>
        public static string LaunchReplacement()
        {
            try
            {
                int me = Process.GetCurrentProcess().Id;
                var psi = new ProcessStartInfo(Application.ExecutablePath, WaitPidArg + " " + me.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = false };
                using (Process.Start(psi)) { }
                Log("手动重启 VSManager / Manual restart requested（PID " + me + "）");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Error(LogFile, "手动重启失败 / Manual restart failed", ex);
                return ex.Message;
            }
        }

        #endregion

        #region 看门狗侧 / Watchdog side

        /// <summary>
        /// 看门狗主循环：等待被看护的进程退出；异常退出且未超过重启上限时重新启动它并继续看护。返回进程退出代码。
        /// Watchdog main loop: waits for the supervised process to exit; after an abnormal exit within the restart limit it
        /// starts the app again and keeps watching. Returns the process exit code.
        /// </summary>
        public static int Run(int pid)
        {
            int me = Process.GetCurrentProcess().Id;
            var limiter = new RestartLimiter(AppSettings.DefaultAutoRestartMaxCount, TimeSpan.FromMinutes(AppSettings.DefaultAutoRestartWindowMinutes));
            Log("看门狗运行 / Watchdog running（PID " + me + "，看护 / watching " + pid + "）");
            while (true)
            {
                Process target;
                // 立即打开句柄，进程退出后仍可读取退出代码 / Open the handle now so the exit code stays readable after exit
                try { target = Process.GetProcessById(pid); _ = target.Handle; }
                catch { Log("被看护的进程已不存在，看门狗退出 / Supervised process not found; watchdog exits"); return 0; }

                int code;
                using (target)
                using (var clean = new EventWaitHandle(false, EventResetMode.ManualReset, CleanExitName(pid)))
                using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName(pid)))
                {
                    while (!target.WaitForExit(1000))
                    {
                        if (stop.WaitOne(0)) { Log("收到停止信号，看门狗退出 / Stop signal received; watchdog exits"); return 0; }
                    }
                    if (clean.WaitOne(0)) { Log("VSManager 正常退出，看门狗退出 / VSManager exited normally; watchdog exits"); return 0; }
                    if (stop.WaitOne(0)) { Log("收到停止信号，看门狗退出 / Stop signal received; watchdog exits"); return 0; }
                    try { code = target.ExitCode; } catch { code = -1; }
                }

                Log("VSManager 异常退出 / VSManager exited abnormally（退出代码 / exit code " + FormatExitCode(code) + "）");
                AppSettings s;
                try { s = AppSettings.Load(); } catch { s = new AppSettings(); }
                if (!s.ProcessWatchdogEnabled) { Log("看门狗已在设置中关闭，不再重启 / Watchdog disabled in the settings; no restart"); return 0; }
                limiter.MaxCount = s.AutoRestartMaxCount;
                limiter.Window = TimeSpan.FromMinutes(s.AutoRestartWindowMinutes);
                if (!limiter.TryAcquire(DateTime.Now))
                {
                    string msg = "VSManager 在 " + s.AutoRestartWindowMinutes + " 分钟内已异常退出并重启 " + s.AutoRestartMaxCount + " 次，看门狗已停止自动重启。\n" +
                                 "请查看日志：%APPDATA%\\VSManager\\logs\\" + LogFile + " 与 crash.log\n\n" +
                                 "VSManager exited abnormally and was restarted " + s.AutoRestartMaxCount + " times within " + s.AutoRestartWindowMinutes +
                                 " minutes; the watchdog stopped restarting it. See watchdog.log and crash.log under %APPDATA%\\VSManager\\logs\\.";
                    Log("重启次数已达上限，停止自动重启 / Restart limit reached; automatic restarts stopped");
                    try { MessageBox.Show(msg, "VSManager 看门狗 / Watchdog", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
                    return 1;
                }
                Thread.Sleep(2000);
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath,
                        RestartedArg + " " + code.ToString(CultureInfo.InvariantCulture) + " " + WatchedByArg + " " + me.ToString(CultureInfo.InvariantCulture) +
                        " " + WaitPidArg + " " + pid.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = false };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) { Log("重新启动失败 / Restart failed"); return 1; }
                        pid = p.Id;
                    }
                    Log("已重新启动 VSManager / VSManager restarted（新 PID / new PID " + pid + "，窗口内第 " + limiter.Count(DateTime.Now) + " 次 / restart #" + limiter.Count(DateTime.Now) + " in window）");
                }
                catch (Exception ex)
                {
                    AppLog.Error(LogFile, "重新启动失败 / Restart failed", ex);
                    return 1;
                }
            }
        }

        #endregion
    }
}
