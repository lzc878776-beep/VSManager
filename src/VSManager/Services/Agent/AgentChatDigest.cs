using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>AI 对话中的一轮：一条用户消息或通知，加上随后 AI 的回复。/ One AI chat round: a user message or notice plus the AI reply that follows.</summary>
    public sealed class AgentChatRound
    {
        /// <summary>在对话记录文件中的轮次序号（从 1 开始）。/ Round number within the chat file (1-based).</summary>
        public int Index;
        public DateTime Time;
        /// <summary>user / notice；没有前导消息的回复为 assistant。/ user / notice; assistant for a reply without a leading message.</summary>
        public string Kind;
        public string Text;
        /// <summary>本轮实际执行的工具名（按调用顺序）。/ Tool names actually executed in the round, in call order.</summary>
        public List<string> Tools = new List<string>();
        public bool HasReply;
        /// <summary>没有工具明细时的步骤条数（旧记录或出错的轮次）。/ Step count when there are no call details (older records or failed rounds).</summary>
        public int LegacySteps;
        public string Error;
    }

    /// <summary>一次启动时读回执行计划的记录。/ One startup's plan read-back record.</summary>
    public sealed class PlanStartupRecord
    {
        public DateTime Time;
        public int? Pid;
        public int ReadBack;
        public int Resuming;
        public List<string> Plans = new List<string>();
        /// <summary>该次启动发出的「执行计划恢复」通知（每项「#1 标题」及方式）。/ "Plan resumed" notices sent by that startup ("#1 title" plus how).</summary>
        public List<string> Notices = new List<string>();
    }

    /// <summary>
    /// read_agent_chat 的实现：把 agent-chat.jsonl 整理成「轮次 + 实际调用的工具」，把 plan.log 整理成历次启动的计划读回记录。只读。
    /// Implementation of read_agent_chat: turns agent-chat.jsonl into "rounds + tools actually called" and plan.log into per-startup plan
    /// read-back records. Read-only.
    /// </summary>
    public static class AgentChatDigest
    {
        public const int DefaultRounds = 20, MaxRounds = 200, MaxStartups = 20, SummaryChars = 160;
        public const string SinceFormat = "yyyy-MM-dd HH:mm";

        private static readonly Regex LogLine = new Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (.*)$", RegexOptions.Compiled);
        private static readonly Regex PidSuffix = new Regex(@"\s*\[PID (\d+)\]\s*$", RegexOptions.Compiled);
        private static readonly Regex StartupBody = new Regex(@"^(\d+)，续跑 / resuming (\d+)(?:（(.*)）)?$", RegexOptions.Compiled);
        private static readonly Regex PlanItem = new Regex(@"(?:^|、)#(\d+)(?= |、|$)", RegexOptions.Compiled);

        /// <summary>
        /// 把对话记录分成轮次：每条非本机、非分隔标记的用户消息 / 通知开启一轮，随后的 AI 回复归入该轮。
        /// Splits the records into rounds: each user message / notice that is not local-only and not a reset marker starts a round,
        /// and the AI replies that follow belong to it.
        /// </summary>
        public static List<AgentChatRound> Rounds(IEnumerable<AgentChatRecord> records)
        {
            var rounds = new List<AgentChatRound>();
            AgentChatRound current = null;
            foreach (var r in records ?? Enumerable.Empty<AgentChatRecord>())
            {
                if (r == null || r.Reset || r.Local) continue;
                if (r.Role == AgentChatLog.RoleUser || r.Role == AgentChatLog.RoleNotice)
                {
                    current = new AgentChatRound { Index = rounds.Count + 1, Time = r.Time, Kind = r.Role, Text = r.Text ?? "" };
                    rounds.Add(current);
                    continue;
                }
                if (current == null || current.HasReply)
                {
                    // 没有前导消息的回复（如启动时的接续轮）单独成轮 / A reply without a leading message (e.g. a resumed round) is its own round
                    current = new AgentChatRound { Index = rounds.Count + 1, Time = r.Time, Kind = AgentChatLog.RoleAssistant, Text = "" };
                    rounds.Add(current);
                }
                current.HasReply = true;
                if (r.Calls != null && r.Calls.Count > 0) current.Tools.AddRange(r.Calls.Select(c => c.Name));
                else if (r.Steps != null) current.LegacySteps += r.Steps.Count;
                if (!string.IsNullOrEmpty(r.Error)) current.Error = r.Error;
            }
            return rounds;
        }

        /// <summary>
        /// 解析 plan.log 中的启动恢复记录；通知记录归入它之前最近的一次启动（同 PID 优先）。
        /// Parses the startup-resume records in plan.log; a notice record joins the latest startup before it (same PID preferred).
        /// </summary>
        public static List<PlanStartupRecord> Startups(IEnumerable<string> lines)
        {
            var list = new List<PlanStartupRecord>();
            foreach (var raw in lines ?? Enumerable.Empty<string>())
            {
                var m = LogLine.Match(raw ?? "");
                if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)) continue;
                string body = m.Groups[2].Value;
                int? pid = null;
                var pm = PidSuffix.Match(body);
                if (pm.Success) { pid = int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture); body = body.Substring(0, pm.Index); }
                if (body.StartsWith(AgentPlans.StartupMarker, StringComparison.Ordinal))
                {
                    var sm = StartupBody.Match(body.Substring(AgentPlans.StartupMarker.Length).Trim());
                    if (!sm.Success) continue;
                    var rec = new PlanStartupRecord { Time = time, Pid = pid, ReadBack = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture), Resuming = int.Parse(sm.Groups[2].Value, CultureInfo.InvariantCulture) };
                    rec.Plans.AddRange(SplitPlans(sm.Groups[3].Value));
                    list.Add(rec);
                    continue;
                }
                bool viaRestart = body.StartsWith(AgentPlans.RestartNoticeMarker, StringComparison.Ordinal);
                if (!viaRestart && !body.StartsWith(AgentPlans.NoticeMarker, StringComparison.Ordinal)) continue;
                var owner = list.LastOrDefault(s => pid != null && s.Pid == pid) ?? list.LastOrDefault();
                if (owner == null) continue;
                string how = viaRestart ? "（并入重启完成通知 / in the restart notice）" : "（单独通知 / separate notice）";
                string plans = body.Substring((viaRestart ? AgentPlans.RestartNoticeMarker : AgentPlans.NoticeMarker).Length).Trim();
                owner.Notices.Add(string.Join("、", SplitPlans(plans)) + how);
            }
            return list;
        }

        /// <summary>把「#1 标题、#2 标题」拆成各项（兼容只有编号的旧记录）。/ Splits "#1 title、#2 title" into items (older id-only records too).</summary>
        internal static List<string> SplitPlans(string text)
        {
            var items = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return items;
            var starts = PlanItem.Matches(text).Cast<Match>().ToList();
            for (int i = 0; i < starts.Count; i++)
            {
                int from = starts[i].Index + (text[starts[i].Index] == '、' ? 1 : 0);
                int to = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
                items.Add(text.Substring(from, to - from).Trim());
            }
            return items;
        }

        /// <summary>解析 since（yyyy-MM-dd HH:mm，也接受 yyyy-MM-dd）；空为 null。/ Parses since (yyyy-MM-dd HH:mm, also yyyy-MM-dd); null when empty.</summary>
        public static bool TryParseSince(string since, out DateTime? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(since)) return true;
            if (DateTime.TryParseExact(since.Trim(), new[] { SinceFormat, "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t))
            {
                value = t;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 读取并格式化：末尾 N 轮对话（可按 since 过滤）与历次启动的计划读回记录。
        /// Reads and formats the last N rounds (optionally filtered by since) and the per-startup plan read-back records.
        /// </summary>
        public static string Read(int rounds, string since, string chatPath, IEnumerable<string> planLogPaths, Func<string, string> redact,
            int currentPid, DateTime? processStart, int maxChars)
        {
            if (!TryParseSince(since, out DateTime? from))
                return "⚠ since 格式应为 " + SinceFormat + "（如 2025-01-01 08:30）/ since must be " + SinceFormat + " (e.g. 2025-01-01 08:30)";
            rounds = rounds <= 0 ? DefaultRounds : Math.Min(rounds, MaxRounds);
            redact = redact ?? (s => s);
            var sb = new StringBuilder();

            var all = Rounds(ReadRecords(chatPath, out string chatError));
            var picked = all.Where(r => from == null || r.Time >= from.Value).ToList();
            picked = picked.Skip(Math.Max(0, picked.Count - rounds)).ToList();
            sb.Append("【一、AI 对话轮次 / Part 1: AI chat rounds】").AppendLine();
            sb.Append("文件 / File: ").Append(VsManagerLogReader.FriendlyPath(chatPath)).Append("，共 / total ").Append(all.Count).Append(" 轮 / rounds，显示 / showing ").Append(picked.Count)
                .Append(from == null ? "" : "（" + from.Value.ToString(SinceFormat, CultureInfo.InvariantCulture) + " 之后 / after）").AppendLine();
            if (chatError != null) sb.Append("⚠ ").Append(redact(chatError)).AppendLine();
            else if (picked.Count == 0) sb.Append("（没有符合条件的轮次 / no matching rounds）").AppendLine();
            foreach (var r in picked)
            {
                string kind = r.Kind == AgentChatLog.RoleNotice ? "通知 / notice" : r.Kind == AgentChatLog.RoleUser ? "用户 / user" : "接续 / resumed";
                sb.Append('#').Append(r.Index).Append(' ').Append(r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" [").Append(kind).Append("] ")
                    .Append(Summary(redact(r.Text))).AppendLine();
                sb.Append("   工具 / Tools: ").Append(ToolList(r)).AppendLine();
                if (r.Error != null) sb.Append("   ⚠ ").Append(Summary(redact(r.Error))).AppendLine();
            }

            sb.AppendLine();
            sb.Append("【二、启动时读回的执行计划 / Part 2: Plans read back at startup】").AppendLine();
            string planLog = (planLogPaths ?? Enumerable.Empty<string>()).FirstOrDefault(File.Exists);
            if (planLog == null)
            {
                sb.Append("未找到 plan.log：尚无启动恢复记录 / plan.log not found: no startup records yet");
                return Cap(sb.ToString(), maxChars);
            }
            var startups = Startups(ReadLines(planLog, out string planError));
            var shown = startups.Where(s => from == null || s.Time >= from.Value).ToList();
            shown = shown.Skip(Math.Max(0, shown.Count - MaxStartups)).ToList();
            sb.Append("文件 / File: ").Append(VsManagerLogReader.FriendlyPath(planLog)).Append("，共 / total ").Append(startups.Count).Append(" 次启动记录 / startup records，显示 / showing ").Append(shown.Count).AppendLine();
            if (planError != null) sb.Append("⚠ ").Append(redact(planError)).AppendLine();
            var current = startups.LastOrDefault(s => s.Pid == currentPid)
                ?? (processStart == null ? null : startups.LastOrDefault(s => s.Pid == null && s.Time >= processStart.Value.AddSeconds(-5)));
            if (current == null) sb.Append("本进程（PID ").Append(currentPid).Append("）没有启动读回记录 / No startup record for this process (PID ").Append(currentPid).Append(")").AppendLine();
            foreach (var s in shown)
            {
                sb.Append(s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(s == current ? " ★本进程 / this process" : "")
                    .Append(s.Pid == null ? "" : " PID " + s.Pid).Append("：读回 / read back ").Append(s.ReadBack).Append("，续跑 / resuming ").Append(s.Resuming);
                if (s.Plans.Count > 0) sb.Append("：").Append(redact(string.Join("、", s.Plans)));
                sb.AppendLine();
                sb.Append("   执行计划恢复通知 / Plan resumed notice: ")
                    .Append(s.Notices.Count == 0 ? (s.Resuming == 0 ? "无需发出 / not needed" : "未记录发出 / none recorded") : "已发出 / sent " + redact(string.Join("；", s.Notices)))
                    .AppendLine();
            }
            return Cap(sb.ToString().TrimEnd(), maxChars);
        }

        private static string ToolList(AgentChatRound r)
        {
            if (r.Tools.Count > 0)
                return string.Join(", ", r.Tools.GroupBy(t => t).Select(g => g.Count() > 1 ? g.Key + "×" + g.Count() : g.Key));
            if (!r.HasReply) return "（尚无回复或未运行模型 / no reply yet or no model round）";
            if (r.LegacySteps > 0) return "（无工具调用明细（旧记录或出错），步骤 " + r.LegacySteps + " 条 / no call details (older record or error), " + r.LegacySteps + " steps）";
            return "（无 / none）";
        }

        private static string Summary(string text)
        {
            string one = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (one.Length == 0) return "（无文字 / no text）";
            return one.Length <= SummaryChars ? one : one.Substring(0, SummaryChars) + "…";
        }

        private static string Cap(string text, int maxChars)
        {
            if (maxChars <= 0 || text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) + "…（已截断，可减少 lines 或指定 since / truncated; lower lines or set since）";
        }

        private static List<AgentChatRecord> ReadRecords(string path, out string error)
        {
            error = null;
            var list = new List<AgentChatRecord>();
            foreach (var line in ReadLines(path, out error))
            {
                var r = AgentChatLog.Parse(line);
                if (r != null) list.Add(r);
            }
            return list.Select((r, i) => new { r, i }).OrderBy(x => x.r.Time).ThenBy(x => x.i).Select(x => x.r).ToList();
        }

        private static List<string> ReadLines(string path, out string error)
        {
            error = null;
            var lines = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return lines;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                error = "读取失败 / Read failed: " + ex.Message;
            }
            return lines;
        }
    }
}
