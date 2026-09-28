using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace VSManager
{
    /// <summary>执行计划中的一步。/ One step of an execution plan.</summary>
    [DataContract]
    public sealed class AgentPlanStep
    {
        [DataMember] public string Title;
        /// <summary>pending / in_progress / done / failed / skipped。</summary>
        [DataMember] public string Status = AgentPlans.Pending;
        [DataMember] public string Note;
        [DataMember] public DateTime UpdatedUtc;
    }

    /// <summary>
    /// AI 总控助手的执行计划：复杂流程（多步骤、跨重启、步骤间有依赖）的步骤与状态，常驻磁盘直到流程完成才释放。
    /// An execution plan of the AI assistant: the steps and states of a complex flow (multi-step, across restarts, dependent steps),
    /// kept on disk until the flow completes.
    /// </summary>
    [DataContract]
    public sealed class AgentPlan
    {
        [DataMember] public int Id;
        [DataMember] public string Title;
        [DataMember] public string Goal;
        /// <summary>所属项目（会话隔离），空为全局。/ Owning project (session isolation); empty = global.</summary>
        [DataMember] public string Scope;
        [DataMember] public DateTime CreatedUtc;
        [DataMember] public DateTime UpdatedUtc;
        [DataMember] public List<AgentPlanStep> Steps = new List<AgentPlanStep>();

        /// <summary>已结束（完成或跳过）的步数。/ Number of finished (done or skipped) steps.</summary>
        public int Finished => Steps.Count(s => AgentPlans.IsFinished(s.Status));
        /// <summary>所有步骤是否都已完成或跳过。/ Whether every step is done or skipped.</summary>
        public bool AllFinished => Steps.Count > 0 && Steps.All(s => AgentPlans.IsFinished(s.Status));
    }

    [DataContract]
    internal sealed class AgentPlanFile
    {
        [DataMember] public int NextId = 1;
        [DataMember] public List<AgentPlan> Plans = new List<AgentPlan>();
    }

    /// <summary>
    /// 执行计划的持久化与操作：计划保存在 %APPDATA%\VSManager\agent-plans.json，每次修改立即原子写入；
    /// 与一次性消费的重启交接文件不同，它在重启后保留，新进程读回并提示 AI 接着执行，直到 complete_plan 释放。
    /// Persistence and operations for execution plans: plans live in %APPDATA%\VSManager\agent-plans.json and every change is written
    /// atomically at once. Unlike the consume-once restart handoff files it survives restarts: the new process reads it back and prompts
    /// the AI to continue until complete_plan releases it.
    /// </summary>
    public static class AgentPlans
    {
        public const string Pending = "pending", InProgress = "in_progress", Done = "done", Failed = "failed", Skipped = "skipped";
        public const int MaxActivePlans = 5, MaxSteps = 30, MaxText = 400;
        /// <summary>超过该时间未更新的计划不再在启动时自动续跑（仍可读取）。/ Plans not updated for longer are not auto-resumed at startup (still readable).</summary>
        public static readonly TimeSpan ResumeWindow = TimeSpan.FromDays(3);
        /// <summary>计划操作日志（可用 read_vsmanager_log 读取）。/ Plan operation log (readable with read_vsmanager_log).</summary>
        public const string LogFile = "plan.log";

        /// <summary>
        /// plan.log 中启动恢复记录的固定前缀（写入与 read_agent_chat 解析共用，勿随意改动）。
        /// Fixed prefixes of the startup-resume records in plan.log (shared by the writer and the read_agent_chat parser; do not change casually).
        /// </summary>
        public const string StartupMarker = "启动时读回执行计划 / Plans read back at startup: ",
            RestartNoticeMarker = "重启完成通知附带执行计划 / Restart notice carries plans: ",
            NoticeMarker = "发出执行计划恢复通知 / Plan resume notice sent: ";

        /// <summary>计划列表的日志写法「#1 标题、#2 标题」。/ Log form of a plan list: "#1 title、#2 title".</summary>
        public static string LogList(IEnumerable<AgentPlan> plans) =>
            string.Join("、", plans.Select(p => "#" + p.Id + " " + OneLineTitle(p.Title)));

        /// <summary>启动时读回记录：读回数、续跑数、续跑的计划与进程号。/ Startup read-back record: count read, count resumed, the resumed plans and the process id.</summary>
        public static string StartupLogLine(int readBack, IList<AgentPlan> resuming, int pid) =>
            StartupMarker + readBack + "，续跑 / resuming " + resuming.Count + (resuming.Count == 0 ? "" : "（" + LogList(resuming) + "）") + " [PID " + pid + "]";

        /// <summary>「执行计划恢复」通知记录（viaRestart=并入重启完成通知）。/ "Plan resumed" notice record (viaRestart = merged into the restart notice).</summary>
        public static string NoticeLogLine(IEnumerable<AgentPlan> plans, bool viaRestart, int pid) =>
            (viaRestart ? RestartNoticeMarker : NoticeMarker) + LogList(plans) + " [PID " + pid + "]";

        private static string OneLineTitle(string title) => (title ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace("、#", "、 #").Trim();

        private static readonly object Gate = new object();

        public static string FilePath => Path.Combine(AppPaths.DataFolder, "agent-plans.json");

        /// <summary>规范化状态（接受中文与常见别名），无法识别返回 null。/ Normalizes a status (Chinese and common aliases accepted); null when unknown.</summary>
        public static string NormalizeStatus(string status)
        {
            switch ((status ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_'))
            {
                case "pending": case "todo": case "待办": case "未开始": return Pending;
                case "in_progress": case "running": case "doing": case "active": case "进行中": case "执行中": return InProgress;
                case "done": case "completed": case "complete": case "ok": case "passed": case "完成": case "已完成": return Done;
                case "failed": case "fail": case "error": case "blocked": case "失败": case "受阻": return Failed;
                case "skipped": case "skip": case "跳过": case "已跳过": return Skipped;
                default: return null;
            }
        }

        public static bool IsFinished(string status) => status == Done || status == Skipped;

        private static string Icon(string status)
        {
            switch (status)
            {
                case Done: return "✅";
                case InProgress: return "▶";
                case Failed: return "❌";
                case Skipped: return "⏭";
                default: return "⬜";
            }
        }

        private static string Clip(string s, int max = MaxText)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        #region 读写 / Load and save

        internal static AgentPlanFile Load(string path, out string error)
        {
            error = null;
            if (!File.Exists(path)) return new AgentPlanFile();
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Skip(3).ToArray();
                AgentPlanFile file;
                using (var r = JsonReaderWriterFactory.CreateJsonReader(data, XmlDictionaryReaderQuotas.Max))
                    file = (AgentPlanFile)new DataContractJsonSerializer(typeof(AgentPlanFile)).ReadObject(r);
                file = file ?? new AgentPlanFile();
                file.Plans = (file.Plans ?? new List<AgentPlan>()).Where(p => p != null).ToList();
                foreach (var p in file.Plans) p.Steps = (p.Steps ?? new List<AgentPlanStep>()).Where(s => s != null).ToList();
                int maxId = file.Plans.Select(p => p.Id).DefaultIfEmpty(0).Max();
                if (file.NextId <= maxId) file.NextId = maxId + 1;
                return file;
            }
            catch (Exception ex)
            {
                error = "读取执行计划失败 / Failed to read the execution plans: " + ex.GetType().Name + "：" + ex.Message;
                return null;
            }
        }

        internal static string Save(AgentPlanFile file, string path)
        {
            // 计划全部释放后仍保留文件（空列表）以延续编号 / Keep the file (empty list) after all plans are released so ids keep increasing
            try
            {
                var r = AtomicFile.Write(path, stream =>
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                        new DataContractJsonSerializer(typeof(AgentPlanFile)).WriteObject(w, file);
                }, backupBeforeOverwrite: false, skipFallbackOnSerializationError: true);
                return r.Ok ? null : r.Error.GetType().Name + "：" + r.Error.Message;
            }
            catch (Exception ex) { return ex.GetType().Name + "：" + ex.Message; }
        }

        /// <summary>读取所有进行中的计划；文件损坏时返回空列表并给出错误。/ Reads all active plans; empty with an error when the file is damaged.</summary>
        public static List<AgentPlan> Active(out string error, string path = null)
        {
            lock (Gate) return Load(path ?? FilePath, out error)?.Plans ?? new List<AgentPlan>();
        }

        private static string Mutate(string path, Func<AgentPlanFile, string> change)
        {
            lock (Gate)
            {
                path = path ?? FilePath;
                var file = Load(path, out string error);
                if (file == null) return "⚠ " + error + "（文件未改动 / file left unchanged）";
                string result = change(file);
                if (result == null || result.StartsWith("⚠", StringComparison.Ordinal)) return result;
                string saveError = Save(file, path);
                return saveError == null ? result : "⚠ 执行计划保存失败 / Failed to save the execution plan: " + saveError;
            }
        }

        #endregion

        #region 操作 / Operations

        /// <summary>创建计划。/ Creates a plan.</summary>
        public static string Create(string title, string goal, IEnumerable<string> steps, string scope, DateTime nowUtc, string path = null)
        {
            title = Clip(title, 120);
            var list = (steps ?? Enumerable.Empty<string>())
                .SelectMany(s => (s ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(s => System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"^(?:\d+[.)、]|[-*•])\s*", ""))
                .Where(s => s.Length > 0).Select(s => Clip(s)).ToList();
            if (title.Length == 0) return "⚠ 请填写计划标题 / Please give the plan a title";
            if (list.Count < 2) return "⚠ 计划至少需要 2 步；简单请求不要建计划 / A plan needs at least 2 steps; do not plan simple requests";
            if (list.Count > MaxSteps) return "⚠ 步骤过多（最多 " + MaxSteps + " 步），请合并 / Too many steps (max " + MaxSteps + "); merge some";
            AgentPlan created = null;
            string r = Mutate(path, file =>
            {
                var same = file.Plans.FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                    return "⚠ 已有同名进行中的计划 #" + same.Id + "，请用 read_plan / update_plan_step 继续，或先 complete_plan / An active plan with this title exists (#" + same.Id + "); continue it or complete it first\n" + Describe(same);
                if (file.Plans.Count >= MaxActivePlans)
                    return "⚠ 进行中的计划已达上限 " + MaxActivePlans + " 个，请先完成或释放 / Active plan limit " + MaxActivePlans + " reached; complete or release one first";
                created = new AgentPlan
                {
                    Id = file.NextId++, Title = title, Goal = Clip(goal, 800), Scope = scope ?? "",
                    CreatedUtc = nowUtc, UpdatedUtc = nowUtc,
                    Steps = list.Select(s => new AgentPlanStep { Title = s, Status = Pending, UpdatedUtc = nowUtc }).ToList()
                };
                file.Plans.Add(created);
                return "已创建执行计划 #" + created.Id + "，已持久化，重启后自动读回 / Created and persisted plan #" + created.Id + "; it survives restarts\n" + Describe(created);
            });
            if (created != null && r != null && !r.StartsWith("⚠", StringComparison.Ordinal))
                AppLog.Write(LogFile, "创建执行计划 / Plan created #" + created.Id + " 「" + created.Title + "」 " + created.Steps.Count + " 步 / steps");
            return r;
        }

        /// <summary>更新某一步的状态与说明。/ Updates a step's status and note.</summary>
        public static string UpdateStep(int planId, int step, string status, string note, DateTime nowUtc, string path = null)
        {
            string normalized = NormalizeStatus(status);
            if (normalized == null) return "⚠ 无法识别的状态 / Unknown status: " + status + "（pending / in_progress / done / failed / skipped）";
            string log = null;
            string r = Mutate(path, file =>
            {
                var plan = Pick(file, planId, out string err);
                if (plan == null) return err;
                if (step < 1 || step > plan.Steps.Count) return "⚠ 计划 #" + plan.Id + " 没有第 " + step + " 步（共 " + plan.Steps.Count + " 步）/ Plan #" + plan.Id + " has no step " + step;
                var s = plan.Steps[step - 1];
                s.Status = normalized;
                if (!string.IsNullOrWhiteSpace(note)) s.Note = Clip(note);
                s.UpdatedUtc = plan.UpdatedUtc = nowUtc;
                log = "更新执行计划 / Plan step updated #" + plan.Id + " 第 " + step + " 步 / step → " + normalized + (string.IsNullOrWhiteSpace(note) ? "" : "：" + Clip(note, 120));
                string next = plan.AllFinished
                    ? "所有步骤都已完成，请确认结果后调用 complete_plan 释放 / Every step is finished; confirm the result and call complete_plan"
                    : NextHint(plan);
                return "已更新 / Updated\n" + Describe(plan) + "\n" + next;
            });
            if (log != null && r != null && !r.StartsWith("⚠", StringComparison.Ordinal)) AppLog.Write(LogFile, log);
            return r;
        }

        /// <summary>完成并释放计划；有未完成步骤时需要 force。/ Completes and releases a plan; unfinished steps need force.</summary>
        public static string Complete(int planId, string summary, bool force, string path = null)
        {
            string log = null;
            string r = Mutate(path, file =>
            {
                var plan = Pick(file, planId, out string err);
                if (plan == null) return err;
                var open = plan.Steps.Select((s, i) => new { s, i }).Where(x => !IsFinished(x.s.Status)).ToList();
                if (open.Count > 0 && !force)
                    return "⚠ 计划 #" + plan.Id + " 还有 " + open.Count + " 步未完成（第 " + string.Join("、", open.Select(x => x.i + 1)) + " 步），流程完成前不释放；先完成或标为 skipped，确需放弃（如用户取消）时用 force=true 并在 summary 写明原因 / "
                         + open.Count + " step(s) are unfinished; finish or skip them first, or use force=true with the reason in summary when the flow is abandoned";
                file.Plans.Remove(plan);
                log = "释放执行计划 / Plan released #" + plan.Id + " 「" + plan.Title + "」" + (open.Count > 0 ? "（强制，未完成 " + open.Count + " 步 / forced, " + open.Count + " unfinished）" : "") + "：" + Clip(summary, 200);
                return "已完成并释放计划 #" + plan.Id + " / Plan #" + plan.Id + " completed and released" + (open.Count > 0 ? "（强制 / forced）" : "");
            });
            if (log != null && r != null && !r.StartsWith("⚠", StringComparison.Ordinal)) AppLog.Write(LogFile, log);
            return r;
        }

        /// <summary>planId 为 0 时：只有一个进行中的计划就选它。/ When planId is 0, picks the only active plan.</summary>
        private static AgentPlan Pick(AgentPlanFile file, int planId, out string error)
        {
            error = null;
            if (file.Plans.Count == 0) { error = "⚠ 没有进行中的执行计划 / No active execution plan"; return null; }
            if (planId <= 0)
            {
                if (file.Plans.Count == 1) return file.Plans[0];
                error = "⚠ 有多个进行中的计划，请指定 planId / Several active plans; give planId: " + string.Join("、", file.Plans.Select(p => "#" + p.Id + " " + p.Title));
                return null;
            }
            var plan = file.Plans.FirstOrDefault(p => p.Id == planId);
            if (plan == null) error = "⚠ 没有进行中的计划 #" + planId + "（可能已释放）/ No active plan #" + planId + " (maybe released)";
            return plan;
        }

        #endregion

        #region 描述 / Descriptions

        private static string NextHint(AgentPlan plan)
        {
            int i = plan.Steps.FindIndex(s => !IsFinished(s.Status));
            return i < 0 ? "" : "下一步 / Next: 第 " + (i + 1) + " 步 / step " + (i + 1) + "「" + plan.Steps[i].Title + "」";
        }

        /// <summary>完整描述一个计划。/ Full description of a plan.</summary>
        public static string Describe(AgentPlan plan)
        {
            var sb = new StringBuilder();
            sb.Append("【执行计划 / Plan #").Append(plan.Id).Append("】").Append(plan.Title)
              .Append("（").Append(plan.Finished).Append("/").Append(plan.Steps.Count).Append(" 完成 / done")
              .Append(string.IsNullOrEmpty(plan.Scope) ? "" : "，项目 / project " + plan.Scope).Append("）").AppendLine();
            if (!string.IsNullOrWhiteSpace(plan.Goal)) sb.Append("目标 / Goal: ").AppendLine(plan.Goal);
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var s = plan.Steps[i];
                sb.Append(Icon(s.Status)).Append(' ').Append(i + 1).Append(". ").Append(s.Title).Append(" [").Append(s.Status).Append(']');
                if (!string.IsNullOrWhiteSpace(s.Note)) sb.Append(" — ").Append(s.Note);
                sb.AppendLine();
            }
            sb.Append("更新于 / Updated ").Append(plan.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            return sb.ToString();
        }

        /// <summary>多个计划的描述；没有时说明。/ Description of several plans, or a note when there are none.</summary>
        public static string DescribeAll(IEnumerable<AgentPlan> plans)
        {
            var list = (plans ?? Enumerable.Empty<AgentPlan>()).ToList();
            return list.Count == 0 ? "没有进行中的执行计划 / No active execution plan" : string.Join("\n\n", list.Select(Describe));
        }

        /// <summary>
        /// 系统提示词中的常驻摘要（每个计划一行 + 下一步），没有计划时为 null。
        /// Resident summary for the system prompt (one line per plan plus the next step); null when there is none.
        /// </summary>
        public static string PromptSummary(IEnumerable<AgentPlan> plans)
        {
            var list = (plans ?? Enumerable.Empty<AgentPlan>()).ToList();
            if (list.Count == 0) return null;
            return string.Join("\n", list.Select(p => "- #" + p.Id + " " + p.Title + "（" + p.Finished + "/" + p.Steps.Count + "）" +
                (string.IsNullOrEmpty(p.Scope) ? "" : " [" + p.Scope + "]") + " " + NextHint(p)));
        }

        /// <summary>
        /// 重启后续跑的通知正文。/ Notice body for resuming after a restart.
        /// </summary>
        public static string ResumeNotice(IEnumerable<AgentPlan> plans) =>
            "[执行计划恢复 / Plan resumed] VSManager 已重启，以下执行计划从磁盘读回，流程尚未完成：请先用 read_plan 核对进度（必要时用 list_tasks 等工具确认实际状态），然后从下一步接着执行，每完成一步用 update_plan_step 更新，全部完成后 complete_plan 释放；不要重新创建计划。"
            + " / VSManager restarted and the plans below were read back from disk; the flow is not finished. Check progress with read_plan (confirm the real state with list_tasks etc. if needed), continue from the next step, update each finished step with update_plan_step and release with complete_plan when all are done; do not create the plan again.\n"
            + DescribeAll(plans);

        #endregion
    }
}
