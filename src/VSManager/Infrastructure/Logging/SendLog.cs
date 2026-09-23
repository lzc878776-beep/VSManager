using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace VSManager
{
    /// <summary>
    /// 发送日志：记录每次向 VS Copilot 发送消息的完整步骤，便于排查“显示已发送但 VS 没收到”等问题。
    /// 启用归档时写入 ArchiveRoot\logs\send-yyyyMMdd.log（按天滚动、过大分卷），否则写入 %APPDATA%\VSManager\logs。
    /// 默认永久保留，清理按设置项 ArchiveRetentionDays 执行（见 Archive）。
    /// </summary>
    public static class SendLog
    {
        private static readonly object Lock = new object();

        public static string Folder => Archive.Root != null ? Path.Combine(Archive.Root, "logs") : Archive.AppLogFolder;

        public static string TodayFile => Archive.CurrentFile("logs", "send", ".log") ?? Path.Combine(Archive.AppLogFolder, "send-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

        public static void Write(string text)
        {
            text = text.EndsWith("\n") ? text : text + "\r\n";
            if (Archive.Enabled) { Archive.Append("logs", "send", ".log", text); return; }
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Archive.AppLogFolder);
                    File.AppendAllText(TodayFile, text, Encoding.UTF8);
                }
            }
            catch { }
        }

        /// <summary>单行事件（如发送被拒绝）。</summary>
        public static void Event(string vsName, string text) =>
            Write(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + vsName + "] " + text);

        /// <summary>单次发送的步骤记录，结束时一次性写入。</summary>
        public sealed class Trace
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly Stopwatch _sw = Stopwatch.StartNew();

            public Trace(VsInstance vs, string text, int images, bool background)
            {
                string one = (text ?? "").Replace("\r", " ").Replace("\n", " ⏎ ");
                if (one.Length > 80) one = one.Substring(0, 80) + "…";
                _sb.Append("===== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                   .Append("  pid=").Append(vs?.Pid).Append("  ").Append(vs?.Title)
                   .Append("\r\n  消息(").Append((text ?? "").Length).Append(" 字");
                if (images > 0) _sb.Append("，").Append(images).Append(" 张图片");
                _sb.Append("，").Append(background ? "后台优先" : "前台").Append(")：").Append(one).Append("\r\n");
            }

            public void Step(string s) => _sb.Append("  +").Append(_sw.ElapsedMilliseconds.ToString().PadLeft(5)).Append("ms  ").Append(s).Append("\r\n");

            public void Done(string result)
            {
                _sb.Append("  => ").Append(result).Append("  (").Append(_sw.ElapsedMilliseconds).Append("ms)\r\n");
                Write(_sb.ToString());
            }
        }
    }
}
