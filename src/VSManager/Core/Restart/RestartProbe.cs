using System;
using System.IO;
using System.Linq;
using System.Text;

namespace VSManager
{
    /// <summary>
    /// 读取重启交接文件（restart-ui.json 与 restart-handoff.json）的内容与消费状态，供 AI 总控助手验证重启后的恢复。
    /// 两个文件都由新进程读取后立即删除，所以「文件不存在」通常表示已被消费。
    /// Reads the restart handoff files (restart-ui.json and restart-handoff.json) with their consumption state so the AI assistant can
    /// verify what a restart restored. The new process deletes both right after reading, so "not present" usually means consumed.
    /// </summary>
    public static class RestartProbe
    {
        public const int MaxContentChars = 4000;

        public static string DescribeFiles(DateTime nowUtc) =>
            Describe("restart-ui.json", RestartUi.FilePath, RestartUi.MaxAge, nowUtc) + Environment.NewLine +
            Describe("restart-handoff.json", SelfRestart.FilePath, SelfRestart.MaxAge, nowUtc) + Environment.NewLine +
            DescribePlans(AgentPlans.FilePath);

        /// <summary>
        /// 执行计划文件（常驻，不随重启消费）：进行中的计划数与各自进度。
        /// The execution plan file (resident, not consumed by a restart): number of active plans and their progress.
        /// </summary>
        internal static string DescribePlans(string path)
        {
            var sb = new StringBuilder("【agent-plans.json】（常驻，重启后保留，complete_plan 才释放 / resident, kept across restarts until complete_plan）").AppendLine();
            if (!File.Exists(path)) return sb.Append("文件不存在：从未创建过执行计划 / Not present: no plan was ever created").ToString();
            var plans = AgentPlans.Active(out string error, path);
            if (error != null) return sb.Append(error).ToString();
            return sb.Append(plans.Count == 0 ? "没有进行中的计划（均已释放）/ No active plan (all released)"
                : "进行中 / Active: " + string.Join("；", plans.Select(p => "#" + p.Id + " " + p.Title + "（" + p.Finished + "/" + p.Steps.Count + "）"))).ToString();
        }

        internal static string Describe(string label, string path, TimeSpan maxAge, DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.Append("【").Append(label).Append("】（数据目录 / data folder）").AppendLine();
            if (File.Exists(path + ".tmp")) sb.AppendLine("存在未完成的临时文件 .tmp（写入可能中断）/ A leftover .tmp file exists (a write may have been interrupted).");
            if (!File.Exists(path))
            {
                sb.Append("文件不存在：已被新进程读取并删除（已消费），或本次没有写入 / Not present: consumed (read and deleted) by the new process, or never written.");
                return sb.ToString();
            }
            try
            {
                var info = new FileInfo(path);
                var age = nowUtc - info.LastWriteTimeUtc;
                sb.Append("未消费：文件仍在 / Not consumed: the file is still present | 大小 / size ").Append(info.Length).Append(" B | 写入于 / written ")
                  .Append(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")).Append("（").Append(Math.Max(0, (int)age.TotalSeconds)).Append(" 秒前 / s ago）| ")
                  .Append(age <= maxAge ? "仍有效 / still valid" : "已过期，新进程会忽略 / expired, a new process will ignore it")
                  .Append("（有效期 / valid for ").Append((int)maxAge.TotalMinutes).AppendLine(" min）");
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (text.Length > MaxContentChars) text = text.Substring(0, MaxContentChars) + "…（已截断 / truncated）";
                sb.Append("内容 / Content: ").Append(text);
            }
            catch (Exception ex)
            {
                sb.Append("读取失败 / Read failed: ").Append(ex.Message);
            }
            return sb.ToString();
        }
    }
}
