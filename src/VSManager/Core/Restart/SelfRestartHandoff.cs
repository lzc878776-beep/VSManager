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
    /// <summary>
    /// 自测重启中一条自动启动授权（任务编号 + 是否「全部自动」）。
    /// One automatic-start grant in a self-test restart (task id + whether it came from all-tasks mode).
    /// </summary>
    [DataContract]
    public sealed class SelfRestartGrant
    {
        [DataMember] public int Id;
        [DataMember] public bool All;
    }

    /// <summary>
    /// 自测重启交接单：AI 总控助手要求 VSManager 所在 VS 重新启动调试前写入 %APPDATA%\VSManager\restart-handoff.json，
    /// 新进程启动后读取一次即删除，用于恢复本会话的启动授权，并把测试计划交还给助手继续执行。
    /// Self-test restart handoff: written to %APPDATA%\VSManager\restart-handoff.json before the AI assistant has the VS
    /// that debugs VSManager restart the debug session; the new process reads it once and deletes it, restores this
    /// session's start grants and hands the test plan back to the assistant.
    /// </summary>
    [DataContract]
    public sealed class SelfRestartHandoff
    {
        [DataMember] public string Id;
        [DataMember] public DateTime CreatedUtc;
        [DataMember] public int OldPid;
        [DataMember] public int VsPid;
        [DataMember] public DateTime OldExeWriteUtc;
        [DataMember] public string BuildSummary;
        [DataMember] public string TestPlan;
        [DataMember] public int TaskId;
        [DataMember] public string Scope;
        [DataMember] public bool WorkflowStarted;
        [DataMember] public List<SelfRestartGrant> AutoGrants = new List<SelfRestartGrant>();
        [DataMember] public List<int> ManualGrants = new List<int>();
        [DataMember] public int RunningTasks;
        [DataMember] public int WaitingTasks;
        /// <summary>重启前由 prepare_restart_scenario 造出的场景说明 / Scenario set up by prepare_restart_scenario before the restart.</summary>
        [DataMember] public string Scenario;
    }

    /// <summary>
    /// 新进程启动后观察到的结果。/ What the new process observed after starting.
    /// </summary>
    public sealed class SelfRestartOutcome
    {
        public int NewPid;
        public DateTime NewExeWriteUtc;
        public bool DebuggerAttached;
        public int RestoredGrants;
        public int RunningTasks;
        public int WaitingTasks;
    }

    /// <summary>
    /// 自测重启的交接文件读写、校验与通知文字（纯逻辑，便于测试）。
    /// Persistence, validation and notice text for the self-test restart (pure logic, easy to test).
    /// </summary>
    public static class SelfRestart
    {
        /// <summary>测试计划最大长度。/ Maximum test plan length.</summary>
        public const int MaxPlanChars = 4000;

        /// <summary>交接单有效期：超过即视为过期，不再恢复授权或续跑测试。/ Handoff lifetime: older handoffs are ignored.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

        /// <summary>等待助手本轮结束、发送完成的最长时间。/ Longest wait for the assistant round and in-flight sends to finish.</summary>
        public static readonly TimeSpan MaxWaitForIdle = TimeSpan.FromMinutes(5);

        public static string FilePath => Path.Combine(AppPaths.DataFolder, "restart-handoff.json");

        /// <summary>测试计划为空或过长时返回错误说明，否则返回 null。/ Returns an error when the plan is empty or too long, else null.</summary>
        public static string ValidatePlan(string plan)
        {
            if (string.IsNullOrWhiteSpace(plan)) return "测试计划不能为空：请逐项写出重启后要执行的测试 / The test plan is empty: list the tests to run after the restart.";
            if (plan.Length > MaxPlanChars) return $"测试计划过长（{plan.Length} 字，上限 {MaxPlanChars}）/ Test plan too long ({plan.Length}, max {MaxPlanChars}).";
            return null;
        }

        /// <summary>保存交接单，失败时返回错误说明。/ Saves the handoff; returns an error on failure.</summary>
        public static string Save(SelfRestartHandoff handoff)
        {
            if (handoff == null) return "交接单为空 / Empty handoff";
            // 统一为 UTC，避免未指定种类的最小值在序列化时越界 / Normalize to UTC so an unspecified MinValue cannot overflow during serialization
            handoff.CreatedUtc = AsUtc(handoff.CreatedUtc);
            handoff.OldExeWriteUtc = AsUtc(handoff.OldExeWriteUtc);
            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                var r = AtomicFile.Write(FilePath, stream =>
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                        new DataContractJsonSerializer(typeof(SelfRestartHandoff)).WriteObject(w, handoff);
                }, backupBeforeOverwrite: false, skipFallbackOnSerializationError: true);
                return r.Ok ? null : r.Error.GetType().Name + "：" + r.Error.Message;
            }
            catch (Exception ex) { return ex.GetType().Name + "：" + ex.Message; }
        }

        private static DateTime AsUtc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        /// <summary>读取并删除交接单（只消费一次）；没有文件时返回 null。/ Reads and deletes the handoff (consumed once); null when absent.</summary>
        public static SelfRestartHandoff Take(out string error)
        {
            error = null;
            string path = FilePath;
            if (!File.Exists(path)) return null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Skip(3).ToArray();
                using (var r = JsonReaderWriterFactory.CreateJsonReader(data, XmlDictionaryReaderQuotas.Max))
                    return (SelfRestartHandoff)new DataContractJsonSerializer(typeof(SelfRestartHandoff)).ReadObject(r);
            }
            catch (Exception ex)
            {
                error = "读取重启交接单失败 / Failed to read the restart handoff: " + ex.Message;
                return null;
            }
            finally { Discard(); }
        }

        /// <summary>删除交接单。/ Deletes the handoff.</summary>
        public static void Discard()
        {
            try { File.Delete(FilePath); } catch { }
            try { File.Delete(FilePath + ".tmp"); } catch { }
        }

        public static bool IsFresh(SelfRestartHandoff h, DateTime nowUtc) =>
            h != null && h.CreatedUtc <= nowUtc.AddMinutes(1) && nowUtc - h.CreatedUtc <= MaxAge;

        /// <summary>新进程的程序文件比旧进程新，说明已加载新生成的程序。/ The new exe is newer than the old one: the new build is loaded.</summary>
        public static bool LoadedNewBuild(SelfRestartHandoff h, SelfRestartOutcome o) =>
            h != null && o != null && o.NewExeWriteUtc > h.OldExeWriteUtc;

        /// <summary>
        /// 从正在运行的 exe 所在目录向上查找 VSManager.csproj（最多 6 层），找不到返回 null。
        /// Walks up from the running exe folder to find VSManager.csproj (at most 6 levels); null when not found.
        /// </summary>
        public static string FindProjectFile(string exePath, string projectName = "VSManager.csproj")
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(exePath)));
                for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, projectName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 由 devenv.exe 路径推出同一安装中的 MSBuild.exe（Common7\IDE → MSBuild\Current\Bin）。
        /// Derives MSBuild.exe of the same installation from devenv.exe (Common7\IDE → MSBuild\Current\Bin).
        /// </summary>
        public static string MsBuildFromDevenv(string devenvPath)
        {
            if (string.IsNullOrWhiteSpace(devenvPath)) return null;
            try
            {
                var ide = new DirectoryInfo(Path.GetDirectoryName(devenvPath));
                var root = ide.Parent?.Parent;
                if (root == null || !ide.Name.Equals("IDE", StringComparison.OrdinalIgnoreCase)) return null;
                return Path.Combine(root.FullName, "MSBuild", "Current", "Bin", "MSBuild.exe");
            }
            catch { return null; }
        }

        /// <summary>按输出路径猜测生成配置（路径中含 Release 段即 Release，否则 Debug）。/ Guesses the configuration from the output path.</summary>
        public static string GuessConfiguration(string exePath)
        {
            var parts = (exePath ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(p => p.Equals("Release", StringComparison.OrdinalIgnoreCase)) ? "Release" : "Debug";
        }

        /// <summary>重启完成后界面显示的标题。/ Title shown in the UI after the restart.</summary>
        public static string NoticeDisplay(SelfRestartHandoff h, SelfRestartOutcome o) =>
            LoadedNewBuild(h, o)
                ? "🔁 VSManager 已重启并加载新程序，继续自测 / VSManager restarted with the new build; continuing self-test"
                : "⚠ VSManager 已重启，但未检测到新程序 / VSManager restarted, but no new build was detected";

        /// <summary>
        /// 交给助手的续跑通知：说明重启结果、任务恢复情况，并要求按测试计划只用真实工具结果判定。
        /// Continuation notice for the assistant: restart result, task recovery, and the plan to run judged only by real tool results.
        /// </summary>
        public static string NoticeContent(SelfRestartHandoff h, SelfRestartOutcome o, SelfIterationState iteration = null)
        {
            bool fresh = LoadedNewBuild(h, o);
            var sb = new StringBuilder();
            sb.AppendLine("[重启完成通知 / Restart completed] 你之前调用 restart_vsmanager_for_testing 安排的重启已完成，本条是系统自动发出的续跑通知。");
            sb.AppendLine($"- 进程 / Process: PID {h.OldPid} → {o.NewPid}；调试器附加 / Debugger attached: {(o.DebuggerAttached ? "是 / yes" : "否 / no")}");
            sb.AppendLine($"- 程序文件时间 / Exe time (UTC): {h.OldExeWriteUtc:yyyy-MM-dd HH:mm:ss} → {o.NewExeWriteUtc:yyyy-MM-dd HH:mm:ss}；" +
                (fresh ? "已加载新生成的程序 / new build loaded" : "未检测到新程序（可能生成失败或选择了运行上次成功的生成），测试结果不能代表本次改动 / no new build detected; results do not reflect the new changes"));
            if (!string.IsNullOrWhiteSpace(h.BuildSummary)) sb.AppendLine("- 重启前预编译 / Pre-build: " + h.BuildSummary.Trim());
            sb.AppendLine($"- 对话已接续；任务清单已从磁盘恢复：执行中 {o.RunningTasks} 条继续跟踪、排队 {o.WaitingTasks} 条，恢复本会话启动授权 {o.RestoredGrants} 项" +
                (h.WorkflowStarted ? "（任务流程保持已启动）" : "") +
                $" / Conversation resumed; tasks restored: {o.RunningTasks} running still tracked, {o.WaitingTasks} waiting, {o.RestoredGrants} session start grant(s) restored" +
                (h.WorkflowStarted ? " (workflow kept started)" : ""));
            if (h.TaskId > 0) sb.AppendLine($"- 待验证任务 / Task under test: #{h.TaskId}");
            if (!string.IsNullOrWhiteSpace(h.Scenario))
                sb.AppendLine("- 重启前场景 / Pre-restart scenario: " + h.Scenario.Trim() + "；请用 get_window_state / get_foreground_window 核对恢复结果是否与该场景一致 / check with get_window_state / get_foreground_window that the restore matches it");
            sb.AppendLine("测试计划 / Test plan:");
            sb.AppendLine((h.TestPlan ?? "").Trim());
            sb.AppendLine();
            sb.AppendLine("请现在逐项执行测试计划中你能用工具完成的部分（例如 list_vs、list_tasks、get_displays、read_vs_chat、get_errors、read_vs_screenshot 等；窗口位置 / 页面 / 草稿恢复、是否抢焦点、托盘残影与交接文件消费分别用 get_window_state、get_foreground_window（先调用）、list_tray_icons、read_restart_handoff），每项只按本轮工具的真实返回判断是否通过，不得臆测或声称做过未调用的检查。" +
                (h.TaskId > 0 ? $"对任务 #{h.TaskId} 测试清单中确实已验证通过的项，调用 mark_test_item 勾选并写明依据；未通过的项保持未勾选并说明现象。" : "") +
                "需要用户在界面上操作或观察、工具无法验证的项，列为「需用户测试」。最后汇总：已通过 / 未通过 / 需用户测试。" +
                (fresh ? "" : "由于未加载新程序，请先如实告知用户，并建议检查生成错误后再重试，不要把结果当作新改动的验证。"));
            sb.Append("Now run the parts of the plan you can do with tools (window bounds / page / draft restore, focus stealing, ghost tray icons and handoff consumption via get_window_state, get_foreground_window (call it first), list_tray_icons and read_restart_handoff), judging each item only by real tool results in this round; never guess or claim checks you did not call. " +
                (h.TaskId > 0 ? $"For checklist items of task #{h.TaskId} that truly passed, call mark_test_item with the evidence; leave failures unchecked and describe them. " : "") +
                "Items needing the user's hands or eyes are listed as \"needs user testing\". Finish with a summary: passed / failed / needs user testing." +
                (fresh ? "" : " No new build was loaded: tell the user first and suggest fixing build errors before retrying; do not treat the results as verification of the new changes."));
            if (iteration != null) sb.AppendLine().AppendLine().Append(SelfIteration.NoticeBlock(iteration).TrimEnd());
            return sb.ToString();
        }
    }
}
