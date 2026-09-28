using System.ComponentModel;
using System.Threading.Tasks;

namespace VSManager
{
    public sealed partial class AgentService
    {
        /// <summary>日志搜索目录（测试可替换）。/ Log folders to search (replaceable in tests).</summary>
        internal System.Func<System.Collections.Generic.IReadOnlyList<string>> LogFolders = VsManagerLogReader.DefaultFolders;

        [Description("读取 VSManager 自身日志的末尾若干行（只读），返回日志文件路径、总行数与末尾 N 行原文（已按现有规则脱敏）。日志在 %APPDATA%\\VSManager\\archive\\logs（send-日期.log 等归档日志）与 %APPDATA%\\VSManager\\logs（tasks.log、watchdog.log、cad.log、mcp.log、memory.log、crash.log 等）。" +
            "用于核对某条记录是否存在，例如「已按测试项文字重判 N 个测试项的可验证性」「【执行方式】请按自动推荐的提示词执行」「对话窗格停留在聊天历史列表」。agent.log 与 file-audit.log 不开放。" +
            " / Reads the last lines of VSManager's own logs (read-only) and returns the log file path, total line count and the last N lines verbatim (redacted with the existing rules). " +
            "Logs live in %APPDATA%\\VSManager\\archive\\logs (archived send-<date>.log…) and %APPDATA%\\VSManager\\logs (tasks.log, watchdog.log, cad.log, mcp.log, memory.log, crash.log…). " +
            "Use it to check whether a record exists. agent.log and file-audit.log are not available.")]
        internal Task<string> ReadVsManagerLog(
            [Description("日志名，如 tasks、send、watchdog、cad、mcp、memory、crash、send-diagnostics，或直接给文件名（如 tasks.log、send-20250101.log）；不接受路径 / Log name such as tasks, send, watchdog, cad, mcp, memory, crash, send-diagnostics, or a file name (e.g. tasks.log, send-20250101.log); paths are not accepted")] string name,
            [Description("读取末尾行数 1–2000，默认 200 / Number of tail lines 1–2000, default 200")] int lines = VsManagerLogReader.DefaultLines,
            [Description("可选日期 yyyy-MM-dd，默认当天：按天分文件的日志（如 send）取该日文件；不分天的日志（如 tasks）给出日期时只取该日记录，不给时读整个文件末尾 / Optional date yyyy-MM-dd, today by default: per-day logs (e.g. send) use that day's file; undated logs (e.g. tasks) keep only that day's records when a date is given, otherwise the tail of the whole file")] string date = null)
        {
            var folders = LogFolders();
            var settings = _settings();
            int max = MaxToolText;
            return Task.Run(() => VsManagerLogReader.Read(name, lines, date, folders, System.DateTime.Now,
                text => AgentFileService.RedactText(text, settings), max));
        }
    }
}
