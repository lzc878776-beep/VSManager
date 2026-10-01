using System;
using System.Collections.Generic;
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
                _vs = vs;
                string one = (text ?? "").Replace("\r", " ").Replace("\n", " ⏎ ");
                if (one.Length > 80) one = one.Substring(0, 80) + "…";
                _sb.Append("===== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                   .Append("  pid=").Append(vs?.Pid).Append("  ").Append(vs?.Title)
                   .Append("\r\n  消息(").Append((text ?? "").Length).Append(" 字");
                if (images > 0) _sb.Append("，").Append(images).Append(" 张图片");
                _sb.Append("，").Append(background ? "后台优先" : "前台").Append(")：").Append(one).Append("\r\n");
            }

            private readonly VsInstance _vs;
            private readonly List<string> _steps = new List<string>();
            private SendStage _stage = SendStage.Queue;
            private SendStageState? _focus;
            private string _focusDetail;

            /// <summary>发送进入的最后一步。/ Last step the send entered.</summary>
            public SendStage Stage => _stage;

            public void Step(string s)
            {
                string line = "+" + _sw.ElapsedMilliseconds.ToString().PadLeft(5) + "ms  " + s;
                _steps.Add(line);
                _sb.Append("  ").Append(line).Append("\r\n");
            }

            /// <summary>进入某一步（只前进不后退）。/ Enters a step (only moves forward).</summary>
            public void Enter(SendStage stage)
            {
                if (stage <= _stage || stage == SendStage.Focus) return;
                _stage = stage;
                Step("【步骤 / Step】" + SendDiagnosis.Name(stage));
            }

            /// <summary>记录恢复焦点的结果。/ Records the focus-restore outcome.</summary>
            public void Focus(SendStageState state, string detail)
            {
                _focus = state;
                _focusDetail = detail;
            }

            public SendDiagnosisRecord Done(string result)
            {
                var record = SendDiagnosis.Add(_vs?.Pid ?? 0, _vs?.Title, _stage, result, _focus, _focusDetail, _sw.ElapsedMilliseconds, _steps);
                _sb.Append("  阶段 / Stages：").Append(record.StageLine).Append("\r\n");
                _sb.Append("  => ").Append(result).Append("  (").Append(_sw.ElapsedMilliseconds).Append("ms)\r\n");
                Write(_sb.ToString());
                return record;
            }
        }
    }
}
