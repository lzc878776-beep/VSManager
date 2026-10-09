using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VSManager
{
    /// <summary>
    /// Agent 自循环反馈：读取外部 Agent 自动评测脚本的运行目录（每次运行一个子目录，含 result.json、cases.json、transcript.md、logs\），
    /// 汇总每次运行与每一轮的通过 / 失败、失败检查项、工具调用与异常，供侧边栏与反馈窗口可视化。只读，不修改任何评测文件。
    /// Agent self-loop feedback: reads the run directory of an external agent evaluation script (one sub-directory per run with
    /// result.json, cases.json, transcript.md, logs\) and summarizes each run and turn (pass / fail, failed checks, tool calls, errors)
    /// for the sidebar and the feedback window. Read-only; evaluation files are never modified.
    /// </summary>
    public enum AgentEvalState { Passed, Failed, Fatal, Running, NoResult }

    [DataContract]
    public sealed class AgentEvalCheck
    {
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "value")] public string Value;
        [DataMember(Name = "passed")] public bool Passed;
    }

    [DataContract]
    public sealed class AgentEvalToolCall
    {
        [DataMember(Name = "toolName")] public string ToolName;
        [DataMember(Name = "displayName")] public string DisplayName;
        [DataMember(Name = "message")] public string Message;
        [DataMember(Name = "succeeded")] public bool Succeeded;
        [DataMember(Name = "durationSeconds")] public double DurationSeconds;
    }

    [DataContract]
    public sealed class AgentEvalTurn
    {
        [DataMember(Name = "index")] public int Index;
        [DataMember(Name = "prompt")] public string Prompt;
        [DataMember(Name = "answer")] public string Answer;
        [DataMember(Name = "passed")] public bool Passed;
        [DataMember(Name = "durationSeconds")] public double DurationSeconds;
        [DataMember(Name = "error")] public string Error;
        [DataMember(Name = "toolCalls")] public List<AgentEvalToolCall> ToolCalls;
        [DataMember(Name = "checks")] public List<AgentEvalCheck> Checks;

        public IEnumerable<AgentEvalCheck> FailedChecks => (Checks ?? new List<AgentEvalCheck>()).Where(c => c != null && !c.Passed);
        public string Tools => string.Join(", ", (ToolCalls ?? new List<AgentEvalToolCall>()).Where(t => t != null).Select(t => t.ToolName));
    }

    [DataContract]
    public sealed class AgentEvalConversation
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "turns")] public List<AgentEvalTurn> Turns;
    }

    [DataContract]
    public sealed class AgentEvalResult
    {
        [DataMember(Name = "runId")] public string RunId;
        [DataMember(Name = "document")] public string Document;
        [DataMember(Name = "model")] public string Model;
        [DataMember(Name = "startedAt")] public string StartedAt;
        [DataMember(Name = "durationSeconds")] public double DurationSeconds;
        [DataMember(Name = "passedTurns")] public int PassedTurns;
        [DataMember(Name = "failedTurns")] public int FailedTurns;
        [DataMember(Name = "fatalError")] public string FatalError;
        [DataMember(Name = "conversations")] public List<AgentEvalConversation> Conversations;
    }

    /// <summary>一次评测运行（一个运行子目录）。/ One evaluation run (one run sub-directory).</summary>
    public sealed class AgentEvalRun
    {
        public string Name;
        public string Folder;
        public DateTime Time;
        public AgentEvalState State;
        public AgentEvalResult Result;
        /// <summary>result.json 读取失败时的原因。/ Why result.json could not be read.</summary>
        public string ReadError;
        /// <summary>本次用例的会话名（来自 cases.json，没有结果时也可显示）。/ Conversation names from cases.json (shown even without a result).</summary>
        public List<string> CaseNames = new List<string>();

        public int Passed => Result?.PassedTurns ?? 0;
        public int Failed => Result?.FailedTurns ?? 0;
        public int Total => Passed + Failed;
        public string ResultPath => Path.Combine(Folder, "result.json");
        public string TranscriptPath => Path.Combine(Folder, "transcript.md");
        public string LogsFolder => Path.Combine(Folder, "logs");

        /// <summary>全部失败轮次（会话名, 轮次）。/ Every failed turn (conversation name, turn).</summary>
        public IEnumerable<(string Conversation, AgentEvalTurn Turn)> FailedTurns()
        {
            foreach (var c in Result?.Conversations ?? new List<AgentEvalConversation>())
                foreach (var t in c?.Turns ?? new List<AgentEvalTurn>())
                    if (t != null && !t.Passed) yield return (c.Name, t);
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case AgentEvalState.Passed: return $"通过 {Passed}/{Total} / passed";
                    case AgentEvalState.Failed: return $"失败 {Failed}/{Total} / failed";
                    case AgentEvalState.Fatal: return "整体失败 / fatal";
                    case AgentEvalState.Running: return "运行中 / running";
                    default: return "无结果 / no result";
                }
            }
        }
    }

    /// <summary>跨运行归并的一个失败用例。/ One failing case merged across runs.</summary>
    public sealed class AgentEvalIssue
    {
        public string Conversation;
        public int Index;
        public string Prompt;
        public int Failures;
        public AgentEvalRun LastFailure;
        public AgentEvalRun LastSeen;
        /// <summary>该用例最近一次出现时是否通过（即问题已恢复）。/ Whether the case passed the last time it ran (i.e. resolved).</summary>
        public bool LastPassed;
        /// <summary>仍失败，但之后的运行没有再跑这个用例（可能已失效或换了用例集）。/ Still failing, but later runs did not include this case (possibly stale or a different case set).</summary>
        public bool NotRerun;
        public string State => LastPassed ? "已恢复 / resolved" : NotRerun ? "未再运行 / not rerun" : "仍失败 / failing";
        public string LastReason;
    }

    /// <summary>某个问题在一次运行中的一次测试记录。/ One test attempt of a question in one run.</summary>
    public sealed class AgentEvalAttempt
    {
        public AgentEvalRun Run;
        public AgentEvalTurn Turn;
    }

    /// <summary>
    /// 问题汇总表中的一个问题（会话名 + 轮次），附带历次测试记录。最近一次测试通过即视为通过（先失败、后来通过也算通过）。
    /// One question of the question table (conversation + turn) with every test attempt. It counts as passed when its latest attempt passed
    /// (a question that failed first and passed later is passed).
    /// </summary>
    public sealed class AgentEvalQuestion
    {
        /// <summary>问题编号，按用例顺序从 1 开始。/ Question number in case order, starting at 1.</summary>
        public int Number;
        public string Conversation;
        public int Index;
        /// <summary>测试记录，旧→新。/ Test attempts, oldest first.</summary>
        public List<AgentEvalAttempt> Attempts = new List<AgentEvalAttempt>();

        public string Key => Conversation + "#" + Index;
        public AgentEvalAttempt Latest => Attempts.Count > 0 ? Attempts[Attempts.Count - 1] : null;
        /// <summary>最新提问（提问可能被改写）。/ The latest prompt (prompts may be reworded).</summary>
        public string Prompt => Latest?.Turn.Prompt ?? "";
        public bool Passed => Latest?.Turn.Passed ?? false;
        public int PassCount => Attempts.Count(a => a.Turn.Passed);
        public int FailCount => Attempts.Count - PassCount;
        /// <summary>曾经通过过。/ Passed at least once.</summary>
        public bool EverPassed => PassCount > 0;

        public string ResultText
        {
            get
            {
                if (Passed) return FailCount > 0 ? "✓ 通过（曾失败）/ passed after failing" : "✓ 通过 / passed";
                return EverPassed ? "✗ 失败（曾通过）/ failing, passed before" : "✗ 失败 / failing";
            }
        }

        /// <summary>
        /// 表格用的简短结果（英文见列标题与详情）：通过 / 失败，曾有相反结果时附「· 曾失败 / · 曾通过」。
        /// Short result for the table (English is in the column header and the details): passed / failed, with "· failed before" /
        /// "· passed before" when an earlier test had the opposite result.
        /// </summary>
        public string ShortResult => Passed ? (FailCount > 0 ? "✓ 通过 · 曾失败" : "✓ 通过") : (EverPassed ? "✗ 失败 · 曾通过" : "✗ 失败");

        /// <summary>
        /// 历次测试结果（旧→新，✓ / ✗，最多显示最近 <paramref name="max"/> 次，更早的用「…」表示），一眼看出何时由失败转为通过。
        /// Test history oldest → newest as ✓ / ✗ (at most the latest <paramref name="max"/>, earlier ones shown as "…"), showing at a glance
        /// when a question turned from failing to passing.
        /// </summary>
        public string History(int max = 12)
        {
            var marks = Attempts.Skip(Math.Max(0, Attempts.Count - max)).Select(a => a.Turn.Passed ? "✓" : "✗");
            return (Attempts.Count > max ? "…" : "") + string.Concat(marks);
        }

        /// <summary>测试列文字：历次结果 + 通过次数 / 总次数，例如「✗✗✗✓  1/4」。/ Tests column: history plus passes / total, e.g. "✗✗✗✓  1/4".</summary>
        public string TestsText => $"{History()}  {PassCount}/{Attempts.Count}";
    }

    /// <summary>一个批次（一次评测运行）中的一个问题。/ One question tested in one batch (evaluation run).</summary>
    public sealed class AgentEvalBatchItem
    {
        public AgentEvalQuestion Question;
        /// <summary>本批次的测试结果。/ The test result in this batch.</summary>
        public AgentEvalTurn Turn;
    }

    /// <summary>一个批次：一次评测运行及其测试过的全部问题。/ One batch: an evaluation run and every question it tested.</summary>
    public sealed class AgentEvalBatch
    {
        public AgentEvalRun Run;
        public List<AgentEvalBatchItem> Items = new List<AgentEvalBatchItem>();
    }

    public static class AgentEvalStore
    {
        /// <summary>没有 result.json 的运行在最后修改后多久内仍视为运行中。/ How long after its last write a run without result.json still counts as running.</summary>
        public static readonly TimeSpan RunningWindow = TimeSpan.FromMinutes(30);

        /// <summary>
        /// 解析配置的目录：可以直接是运行目录（子目录含 result.json / cases.json），也可以是其上一级（含 runs\）；支持环境变量。
        /// Resolves the configured directory: either the run directory itself (sub-directories hold result.json / cases.json)
        /// or its parent containing runs\; environment variables are expanded.
        /// </summary>
        public static string ResolveRunsDir(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured)) return null;
            string dir = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (!Directory.Exists(dir)) return null;
            string runs = Path.Combine(dir, "runs");
            return Directory.Exists(runs) ? runs : dir;
        }

        /// <summary>按时间倒序读取运行；max ≤ 0 时读取全部，问题汇总因此覆盖所有运行。/ Reads runs newest first; max ≤ 0 reads all, so the issue summary covers every run.</summary>
        public static List<AgentEvalRun> Load(string configured, int max = 0, DateTime? now = null)
        {
            var list = new List<AgentEvalRun>();
            string dir = ResolveRunsDir(configured);
            if (dir == null) return list;
            DirectoryInfo[] subs;
            try { subs = new DirectoryInfo(dir).GetDirectories(); }
            catch { return list; }
            foreach (var d in subs.OrderByDescending(d => d.Name, StringComparer.Ordinal).Take(max > 0 ? max : int.MaxValue))
            {
                bool looksLikeRun = File.Exists(Path.Combine(d.FullName, "result.json")) || File.Exists(Path.Combine(d.FullName, "cases.json"));
                if (looksLikeRun) list.Add(LoadRun(d.FullName, now ?? DateTime.Now));
            }
            return list;
        }

        public static AgentEvalRun LoadRun(string folder, DateTime now)
        {
            var run = new AgentEvalRun { Folder = folder, Name = Path.GetFileName(folder) };
            run.Time = ParseRunTime(run.Name) ?? SafeLastWrite(folder);
            run.CaseNames = ReadCaseNames(Path.Combine(folder, "cases.json"));
            string result = Path.Combine(folder, "result.json");
            if (File.Exists(result))
            {
                try
                {
                    run.Result = Parse(ReadShared(result));
                    if (DateTimeOffset.TryParse(run.Result.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
                        run.Time = started.LocalDateTime;
                }
                catch (Exception ex) { run.ReadError = ex.Message; }
            }
            run.State = Classify(run, SafeLastWrite(folder), now);
            return run;
        }

        public static AgentEvalResult Parse(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
                return (AgentEvalResult)new DataContractJsonSerializer(typeof(AgentEvalResult)).ReadObject(ms);
        }

        public static AgentEvalState Classify(AgentEvalRun run, DateTime lastWrite, DateTime now)
        {
            if (run.Result == null)
                return run.ReadError == null && now - lastWrite < RunningWindow ? AgentEvalState.Running : AgentEvalState.NoResult;
            if (!string.IsNullOrEmpty(run.Result.FatalError)) return AgentEvalState.Fatal;
            if (run.Result.FailedTurns > 0) return AgentEvalState.Failed;
            return run.Result.PassedTurns > 0 ? AgentEvalState.Passed : AgentEvalState.NoResult;
        }

        /// <summary>运行目录名形如 yyyyMMdd-HHmmss。/ Run folder names look like yyyyMMdd-HHmmss.</summary>
        public static DateTime? ParseRunTime(string name) =>
            DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : (DateTime?)null;

        /// <summary>
        /// 从 cases.json（允许 // 注释与尾逗号）中提取会话名，只做轻量文本扫描。
        /// Extracts conversation names from cases.json (comments and trailing commas allowed) with a light text scan.
        /// </summary>
        public static List<string> ReadCaseNames(string path)
        {
            var names = new List<string>();
            try
            {
                if (!File.Exists(path)) return names;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ReadShared(path), "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                    names.Add(m.Groups[1].Value);
            }
            catch { }
            return names;
        }

        /// <summary>
        /// 侧边栏摘要：最近一次运行的状态，以及最近若干次的通过数 / 总数。
        /// Sidebar summary: the latest run state plus pass counts of the recent runs.
        /// </summary>
        public static string Summary(IList<AgentEvalRun> runs)
        {
            if (runs == null || runs.Count == 0) return "暂无评测运行 / No evaluation runs yet";
            var last = runs[0];
            var done = runs.Where(r => r.Result != null).Take(10).ToList();
            int ok = done.Count(r => r.State == AgentEvalState.Passed);
            return $"最近：{last.Time:MM-dd HH:mm} {last.StateText}" + (done.Count > 0 ? $" · 近 {done.Count} 次通过 {ok} 次 / {ok} of last {done.Count} passed" : "");
        }

        /// <summary>
        /// 问题汇总：按「会话名 + 轮次」归并各次运行中的失败（提问改写后仍视为同一用例，显示最新提问），记录失败次数、最近一次失败及其后是否已通过。
        /// Issue summary: groups failures across runs by conversation and turn (a reworded prompt is still the same case; the latest prompt is shown), recording the failure count,
        /// the latest failure and whether the case passed afterwards.
        /// </summary>
        public static List<AgentEvalIssue> Issues(IList<AgentEvalRun> runs)
        {
            var map = new Dictionary<string, AgentEvalIssue>(StringComparer.Ordinal);
            // runs 为新→旧；按旧→新遍历，最后一次出现即为最新结果 / runs are newest first; walk oldest first so the last sighting is the latest result
            foreach (var run in (runs ?? new List<AgentEvalRun>()).Where(r => r.Result != null).Reverse())
                foreach (var c in run.Result.Conversations ?? new List<AgentEvalConversation>())
                    foreach (var t in c?.Turns ?? new List<AgentEvalTurn>())
                    {
                        if (t == null) continue;
                        string key = c.Name + "#" + t.Index;
                        map.TryGetValue(key, out var issue);
                        if (issue == null && t.Passed) continue;
                        if (issue == null) map[key] = issue = new AgentEvalIssue { Conversation = c.Name, Index = t.Index, Prompt = t.Prompt };
                        issue.LastSeen = run;
                        issue.Prompt = t.Prompt;
                        issue.LastPassed = t.Passed;
                        if (t.Passed) continue;
                        issue.Failures++;
                        issue.LastFailure = run;
                        issue.LastReason = Reason(t);
                    }
            var newest = (runs ?? new List<AgentEvalRun>()).Where(r => r.Result != null).Select(r => r.Time).DefaultIfEmpty(DateTime.MinValue).Max();
            foreach (var i in map.Values) i.NotRerun = !i.LastPassed && i.LastFailure.Time < newest;
            return map.Values.OrderBy(i => i.LastPassed).ThenBy(i => i.NotRerun).ThenByDescending(i => i.LastFailure.Time).ToList();
        }

        /// <summary>
        /// 问题汇总表：按「会话名 + 轮次」归并所有运行中的提问（无论通过与否），按用例顺序编号排列（不按时间），每个问题附带全部测试记录。
        /// Question table: groups every prompt across runs by conversation and turn (passed or not), numbered and ordered by case order
        /// (not by time), each with all of its test attempts.
        /// </summary>
        public static List<AgentEvalQuestion> Questions(IList<AgentEvalRun> runs)
        {
            var map = new Dictionary<string, AgentEvalQuestion>(StringComparer.Ordinal);
            var conversationOrder = new List<string>();
            // runs 为新→旧；按旧→新遍历，测试记录按时间先后排列 / runs are newest first; walk oldest first so attempts are chronological
            foreach (var run in (runs ?? new List<AgentEvalRun>()).Where(r => r.Result != null).Reverse())
                foreach (var c in run.Result.Conversations ?? new List<AgentEvalConversation>())
                {
                    string name = c?.Name ?? "";
                    // 用例顺序以最新运行为准，旧运行中独有的用例排在后面 / The newest run decides case order; cases only in older runs go last
                    conversationOrder.Remove(name);
                    conversationOrder.Add(name);
                    foreach (var t in c?.Turns ?? new List<AgentEvalTurn>())
                    {
                        if (t == null) continue;
                        string key = name + "#" + t.Index;
                        if (!map.TryGetValue(key, out var q)) map[key] = q = new AgentEvalQuestion { Conversation = name, Index = t.Index };
                        q.Attempts.Add(new AgentEvalAttempt { Run = run, Turn = t });
                    }
                }
            var newest = new List<string>();
            foreach (var run in (runs ?? new List<AgentEvalRun>()).Where(r => r.Result != null))
            {
                newest = (run.Result.Conversations ?? new List<AgentEvalConversation>()).Select(c => c?.Name ?? "").Distinct().ToList();
                break;
            }
            int Rank(string name)
            {
                int i = newest.IndexOf(name);
                return i >= 0 ? i : newest.Count + conversationOrder.IndexOf(name);
            }
            var list = map.Values.OrderBy(q => Rank(q.Conversation)).ThenBy(q => q.Index).ToList();
            for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
            return list;
        }

        /// <summary>
        /// 按批次列出测试过的全部问题：批次按时间新→旧（含无结果的运行），批次内按问题编号排列。
        /// Lists every tested question per batch: batches newest first (runs without results included), questions by number within a batch.
        /// </summary>
        public static List<AgentEvalBatch> Batches(IList<AgentEvalRun> runs)
        {
            runs = runs ?? new List<AgentEvalRun>();
            var byAttempt = new Dictionary<AgentEvalTurn, AgentEvalQuestion>();
            foreach (var q in Questions(runs))
                foreach (var a in q.Attempts) byAttempt[a.Turn] = q;
            var list = new List<AgentEvalBatch>();
            foreach (var run in runs)
            {
                var batch = new AgentEvalBatch { Run = run };
                foreach (var c in run.Result?.Conversations ?? new List<AgentEvalConversation>())
                    foreach (var t in c?.Turns ?? new List<AgentEvalTurn>())
                        if (t != null && byAttempt.TryGetValue(t, out var q)) batch.Items.Add(new AgentEvalBatchItem { Question = q, Turn = t });
                batch.Items = batch.Items.OrderBy(i => i.Question.Number).ToList();
                list.Add(batch);
            }
            return list;
        }

        /// <summary>某个问题的全部测试记录（具体提问与回答）。/ Every test attempt of a question (the exact prompt and answer).</summary>
        public static string DescribeQuestion(AgentEvalQuestion q)
        {
            if (q == null) return "";
            var sb = new StringBuilder();
            sb.AppendLine($"问题 {q.Number} / Question {q.Number}  [{q.Key}]  {q.ResultText}");
            sb.AppendLine($"测试 {q.Attempts.Count} 次：通过 {q.PassCount}，失败 {q.FailCount} / {q.Attempts.Count} tests: {q.PassCount} passed, {q.FailCount} failed");
            int n = 0;
            foreach (var a in q.Attempts)
            {
                var t = a.Turn;
                sb.AppendLine();
                sb.AppendLine($"======== 第 {++n} 次 / Test {n}：{(t.Passed ? "✓ 通过 / passed" : "✗ 失败 / failed")}  {a.Run.Time:yyyy-MM-dd HH:mm}  {t.DurationSeconds:0.0}s"
                    + (a.Run.Result?.Model != null ? "  " + a.Run.Result.Model : "") + $"  ({a.Run.Name})");
                sb.AppendLine("提问 / Prompt：").AppendLine(t.Prompt ?? "");
                sb.AppendLine("回答 / Answer：").AppendLine(string.IsNullOrEmpty(t.Answer) ? "（空 / empty）" : t.Answer);
                if (!t.Passed) sb.AppendLine("未通过原因 / Reason：" + Reason(t));
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>失败原因：异常优先，其次未通过的检查项。/ Failure reason: the error first, then the failed checks.</summary>
        public static string Reason(AgentEvalTurn t)
        {
            if (!string.IsNullOrEmpty(t.Error)) return t.Error;
            var checks = t.FailedChecks.Select(c => (c.Kind + " " + c.Value).Trim()).ToList();
            return checks.Count > 0 ? string.Join("; ", checks) : "未通过 / failed";
        }

        /// <summary>把失败轮次整理成可复制给 AI 的反馈文本。/ Formats failed turns as feedback text that can be pasted to an AI.</summary>
        public static string Feedback(AgentEvalRun run)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"评测运行 / Evaluation run {run.Name}：{run.StateText}" + (run.Result?.Model != null ? $"，模型 / model {run.Result.Model}" : ""));
            if (!string.IsNullOrEmpty(run.Result?.FatalError)) sb.AppendLine("整体失败 / Fatal：" + run.Result.FatalError);
            if (run.ReadError != null) sb.AppendLine("结果读取失败 / Failed to read result：" + run.ReadError);
            foreach (var (conversation, turn) in run.FailedTurns())
            {
                sb.AppendLine($"- [{conversation}#{turn.Index}] {turn.Prompt}");
                if (!string.IsNullOrEmpty(turn.Tools)) sb.AppendLine("  工具 / tools：" + turn.Tools);
                if (!string.IsNullOrEmpty(turn.Error)) sb.AppendLine("  异常 / error：" + turn.Error);
                foreach (var c in turn.FailedChecks.Where(c => c.Kind != "noError"))
                    sb.AppendLine($"  未通过检查 / failed check：{c.Kind} {c.Value}");
            }
            sb.AppendLine("目录 / Folder：" + run.Folder);
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 全部问题的文字清单（不截断）：state 为 failing / resolved / notrerun 时只列该状态，留空列出全部。
        /// Text list of every issue (never truncated); state failing / resolved / notrerun lists only that state, empty lists all.
        /// </summary>
        public static string DescribeIssues(IList<AgentEvalRun> runs, string state = null)
        {
            var all = Issues(runs);
            string f = (state ?? "").Trim().ToLowerInvariant();
            var list = all.Where(i => f.Length == 0 || f == "all"
                || (f == "failing" && !i.LastPassed && !i.NotRerun)
                || (f == "resolved" && i.LastPassed)
                || ((f == "notrerun" || f == "not_rerun") && i.NotRerun)).ToList();
            var sb = new StringBuilder();
            sb.AppendLine(Summary(runs));
            sb.AppendLine($"问题共 {all.Count} 个：仍失败 {all.Count(i => !i.LastPassed && !i.NotRerun)}，未再运行 {all.Count(i => i.NotRerun)}，已恢复 {all.Count(i => i.LastPassed)}"
                + $" / {all.Count} issues: {all.Count(i => !i.LastPassed && !i.NotRerun)} failing, {all.Count(i => i.NotRerun)} not rerun, {all.Count(i => i.LastPassed)} resolved");
            if (list.Count != all.Count) sb.AppendLine($"按状态 {f} 列出 {list.Count} 个 / Listing {list.Count} with state {f}");
            int n = 0;
            foreach (var i in list)
            {
                sb.AppendLine($"{++n}. [{i.State}] {i.Conversation}#{i.Index}：{i.Prompt}");
                sb.AppendLine($"   失败 {i.Failures} 次，最近失败 {i.LastFailure.Time:MM-dd HH:mm}（{i.LastFailure.Name}）/ failed {i.Failures}x, last {i.LastFailure.Name}"
                    + (i.LastPassed ? $"；最近通过 {i.LastSeen.Name} / last passed {i.LastSeen.Name}" : ""));
                sb.AppendLine("   原因 / reason：" + i.LastReason);
            }
            foreach (var r in (runs ?? new List<AgentEvalRun>()).Where(r => r.State == AgentEvalState.Fatal || r.ReadError != null))
                sb.AppendLine($"运行 {r.Name} 整体失败 / run failed：" + (r.Result?.FatalError ?? r.ReadError));
            return sb.ToString().TrimEnd();
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8, true))
                return r.ReadToEnd();
        }

        private static DateTime SafeLastWrite(string folder)
        {
            // 运行中只追加 transcript.md 时目录时间不变，取目录与其中文件的最晚时间
            // Appending to transcript.md does not touch the folder time, so take the latest of the folder and its files
            try { return new DirectoryInfo(folder).GetFiles().Select(f => f.LastWriteTime).DefaultIfEmpty(DateTime.MinValue).Concat(new[] { Directory.GetLastWriteTime(folder) }).Max(); }
            catch { return DateTime.MinValue; }
        }
    }
}
