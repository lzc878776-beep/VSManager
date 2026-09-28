using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace VSManager
{
    /// <summary>
    /// 自迭代状态：用户授权 AI 总控助手围绕一个目标循环「发布 VSManager 任务 → 完成后重启测试 → 按失败项发修复任务」。
    /// 保存在 %APPDATA%\VSManager\self-iteration.json，跨自测重启保留；轮次上限由工具强制执行。
    /// Self-iteration state: the user authorizes the AI assistant to loop around one goal ("publish a VSManager task → restart and
    /// test after it completes → publish a fix for failed items"). Stored in %APPDATA%\VSManager\self-iteration.json so it survives
    /// self-test restarts; the round limit is enforced by the tools.
    /// </summary>
    [DataContract]
    public sealed class SelfIterationState
    {
        [DataMember] public string Goal;
        [DataMember] public int MaxRounds;
        /// <summary>已安排的测试重启次数（每次重启测试算一轮）。/ Test restarts scheduled so far (each restart-and-test is one round).</summary>
        [DataMember] public int Round;
        [DataMember] public DateTime StartedUtc;
        [DataMember] public string VsName;
        [DataMember] public string Scope;
        /// <summary>
        /// 补 skill 闭环：为验证 TargetTaskId 第 TargetItem 项而新增的 skill；为空表示用户发起的普通自迭代。
        /// Skill-gap loop: the skill being added to verify item TargetItem of task TargetTaskId; empty for a user-started self-iteration.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public string Skill;
        [DataMember(EmitDefaultValue = false)] public int TargetTaskId;
        [DataMember(EmitDefaultValue = false)] public int TargetItem;

        public bool IsSkillGap => !string.IsNullOrWhiteSpace(Skill);
    }

    /// <summary>自迭代状态的读写与说明文字（纯逻辑，便于测试）。/ Persistence and text for the self-iteration state (pure logic, easy to test).</summary>
    public static class SelfIteration
    {
        public const int DefaultRounds = 3;
        public const int MaxRoundsLimit = 10;
        public const int MaxGoalChars = 2000;
        /// <summary>补 skill 闭环默认轮次（实现 + 至多两次修复）。/ Default rounds of a skill-gap loop (implement plus up to two fixes).</summary>
        public const int SkillGapRounds = 3;

        /// <summary>超过该时长未结束的自迭代视为过期。/ A self-iteration older than this is treated as expired.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        public static string FilePath => Path.Combine(AppPaths.DataFolder, "self-iteration.json");

        public static int ClampRounds(int rounds) => rounds <= 0 ? DefaultRounds : Math.Min(rounds, MaxRoundsLimit);

        /// <summary>读取进行中的自迭代；没有或已过期（过期时删除）返回 null。/ Loads the active self-iteration; null when none or expired (expired files are deleted).</summary>
        public static SelfIterationState Load(DateTime nowUtc)
        {
            string path = FilePath;
            if (!File.Exists(path)) return null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                {
                    var trimmed = new byte[data.Length - 3];
                    Array.Copy(data, 3, trimmed, 0, trimmed.Length);
                    data = trimmed;
                }
                SelfIterationState state;
                using (var r = JsonReaderWriterFactory.CreateJsonReader(data, XmlDictionaryReaderQuotas.Max))
                    state = (SelfIterationState)new DataContractJsonSerializer(typeof(SelfIterationState)).ReadObject(r);
                if (state == null || string.IsNullOrWhiteSpace(state.Goal) || nowUtc - state.StartedUtc > MaxAge) { Clear(); return null; }
                return state;
            }
            catch (Exception) { Clear(); return null; }
        }

        /// <summary>保存状态，失败时返回错误说明。/ Saves the state; returns an error on failure.</summary>
        public static string Save(SelfIterationState state)
        {
            if (state == null) return "状态为空 / Empty state";
            if (state.StartedUtc.Kind != DateTimeKind.Utc)
                state.StartedUtc = state.StartedUtc.Kind == DateTimeKind.Local ? state.StartedUtc.ToUniversalTime() : DateTime.SpecifyKind(state.StartedUtc, DateTimeKind.Utc);
            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                var r = AtomicFile.Write(FilePath, stream =>
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                        new DataContractJsonSerializer(typeof(SelfIterationState)).WriteObject(w, state);
                }, backupBeforeOverwrite: false, skipFallbackOnSerializationError: true);
                return r.Ok ? null : r.Error.GetType().Name + "：" + r.Error.Message;
            }
            catch (Exception ex) { return ex.GetType().Name + "：" + ex.Message; }
        }

        public static void Clear()
        {
            try { File.Delete(FilePath); } catch { }
            try { File.Delete(FilePath + ".tmp"); } catch { }
        }

        /// <summary>
        /// 重启完成通知中的自迭代说明：本轮序号、目标与测试后的下一步。
        /// Self-iteration block of the restart-completed notice: round, goal and what to do after testing.
        /// </summary>
        public static string NoticeBlock(SelfIterationState s)
        {
            if (s == null) return "";
            bool last = s.Round >= s.MaxRounds;
            var sb = new StringBuilder();
            sb.AppendLine($"[自迭代 / Self-iteration] 第 {s.Round}/{s.MaxRounds} 轮 / round {s.Round}/{s.MaxRounds}；目标 / Goal: {s.Goal.Trim()}");
            if (s.IsSkillGap)
                sb.AppendLine($"[补 skill 闭环 / Skill-gap loop] 新 skill「{s.Skill.Trim()}」应已随新程序加载：先确认工具列表里有它（没有即视为未通过），再用它验证任务 #{s.TargetTaskId} 第 {s.TargetItem} 项；" +
                    $"通过就 mark_test_item（evidence 写明新 skill 与返回），再按下面的规则结束或继续。/ The new skill \"{s.Skill.Trim()}\" should be loaded now: confirm it is in your tool list (missing counts as a failure), " +
                    $"then use it to verify item {s.TargetItem} of task #{s.TargetTaskId}; if it passes, mark_test_item with the skill and its output as evidence, then finish or continue per the rules below.");
            if (s.IsSkillGap)
                sb.AppendLine("若新 skill 工作正常、但被测项本身确实不符：这是原任务的问题，不再改 skill——调用 stop_self_iteration，再按原任务的正常流程带证据处理（retry_task_with_info 或转告用户）。只有 skill 缺失、报错或读不到所需状态时才向 VSManager 发修复任务。" +
                    "/ If the skill works but the item itself really fails, that is the original task's problem: stop changing the skill, call stop_self_iteration and handle the original task with the evidence (retry_task_with_info or tell the user). Send VSManager a fix only when the skill is missing, errors or cannot read the needed state.");
            sb.AppendLine("测试完成后 / After testing:" +
                "\n- 目标达成且测试项全部通过：调用 stop_self_iteration 汇报结果，结束循环。/ Goal met and every item passed: call stop_self_iteration with the summary." +
                (last
                    ? "\n- 已达轮次上限：不要再发布修复任务，调用 stop_self_iteration，把未通过项、已做调整和建议交给用户。/ Round limit reached: publish no more fixes; call stop_self_iteration and hand failures, adjustments and suggestions to the user."
                    : $"\n- 有未通过项：用 send_task 向「{s.VsName}」发布修复任务，写明未通过的项、工具返回的证据和要改哪里（不要原样重发）；收到该任务的「[任务完成通知]」后再调用 restart_vsmanager_for_testing 进入下一轮。/ Failures: send_task a fix to that VS with the failed items, tool evidence and what to change (never resend unchanged); after its completion notice call restart_vsmanager_for_testing for the next round.") +
                "\n- 需要用户决定、界面观察或信息不足：调用 stop_self_iteration 并交给用户。/ Needs a user decision, visual check or missing information: call stop_self_iteration and hand over.");
            return sb.ToString();
        }
    }
}
