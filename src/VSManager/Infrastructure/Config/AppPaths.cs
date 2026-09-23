using System;
using System.IO;

namespace VSManager
{
    /// <summary>
    /// 本机数据目录的统一入口（%APPDATA%\VSManager）：设置、任务清单、日志、对话记录等都放在这里。
    /// 测试可临时改为其他目录，避免读写真实用户数据。
    /// Single entry point for the local data folder (%APPDATA%\VSManager) that holds settings, the task list, logs,
    /// chat history and so on. Tests can redirect it to another folder so that real user data is never touched.
    /// </summary>
    public static class AppPaths
    {
        private static string _override;

        /// <summary>数据根目录。/ Data root folder.</summary>
        public static string DataFolder => _override ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VSManager");

        /// <summary>程序日志目录（tasks.log、memory.log 等）。/ Application log folder (tasks.log, memory.log, ...).</summary>
        public static string LogFolder => Path.Combine(DataFolder, "logs");

        /// <summary>
        /// 仅供测试：把数据目录临时指向 <paramref name="folder"/>，释放返回值后恢复。
        /// Test only: temporarily points the data folder at <paramref name="folder"/>; disposing the result restores it.
        /// </summary>
        internal static IDisposable OverrideDataFolder(string folder)
        {
            string previous = _override;
            _override = folder;
            return new Restore(() => _override = previous);
        }

        private sealed class Restore : IDisposable
        {
            private Action _undo;
            public Restore(Action undo) { _undo = undo; }
            public void Dispose() { _undo?.Invoke(); _undo = null; }
        }
    }
}
