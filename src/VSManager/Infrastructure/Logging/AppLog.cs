using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 统一的程序日志入口：写入 %APPDATA%\VSManager\logs\ 下的指定文件，每行「时间 内容」。
    /// 日志写入失败时静默忽略，绝不影响主流程。
    /// Unified application log entry point: appends "time text" lines to a file under %APPDATA%\VSManager\logs\.
    /// Logging failures are ignored silently and never affect the main flow.
    /// </summary>
    public static class AppLog
    {
        /// <summary>任务清单与归档日志。/ Task list and archive log.</summary>
        public const string TasksFile = "tasks.log";
        /// <summary>未处理异常日志。/ Unhandled exception log.</summary>
        public const string CrashFile = "crash.log";

        private static readonly object Lock = new object();
        private static bool _hooked;

        public static string PathOf(string fileName) => Path.Combine(AppPaths.LogFolder, fileName);

        /// <summary>追加一行日志。/ Appends one log line.</summary>
        public static void Write(string fileName, string text)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(AppPaths.LogFolder);
                    File.AppendAllText(PathOf(fileName), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        /// <summary>记录异常（类型、消息与调用栈）。/ Logs an exception (type, message and stack trace).</summary>
        public static void Error(string fileName, string context, Exception ex) =>
            Write(fileName, context + "：" + (ex == null ? "(null)" : ex.GetType().Name + "：" + ex.Message + "\r\n" + ex.StackTrace));

        /// <summary>
        /// 只记录未处理异常与未观察的任务异常，不改变程序原有的异常处理行为。
        /// Only records unhandled and unobserved task exceptions; it does not change the existing exception behaviour.
        /// </summary>
        public static void HookUnhandled()
        {
            if (_hooked) return;
            _hooked = true;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Error(CrashFile, "未处理异常 / Unhandled exception", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) => Error(CrashFile, "未观察的任务异常 / Unobserved task exception", e.Exception);
        }
    }
}
