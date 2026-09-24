using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VSManager
{
    [DataContract]
    public class AliasEntry
    {
        [DataMember] public string Key;
        [DataMember] public string Alias;
    }

    [DataContract]
    public class AppSettings
    {
        [DataMember] public string MainScreen;
        [DataMember] public string ToolScreen;
        [DataMember] public string Layout;
        [DataMember] public bool ActivateMoveMain;
        [DataMember] public bool ActivateMoveTools;
        [DataMember] public bool MonitorCopilot;
        [DataMember] public bool Sound;
        [DataMember] public bool Popup;
        [DataMember] public bool MinimizeToTray;
        [DataMember] public bool Hotkeys;
        [DataMember] public int PollMs;
        [DataMember] public string CopilotPaneKeyword;
        [DataMember] public string BusyButtonIds;
        [DataMember] public bool ShowChatSteps;
        [DataMember] public bool ClickToActivate;
        [DataMember] public bool BackgroundSend;
        [DataMember] public bool AutoOpenChat;
        [DataMember] public bool BackgroundSync;
        /// <summary>手机网页遥控（局域网 HTTP）。</summary>
        [DataMember] public bool WebEnabled;
        [DataMember] public int WebPort;
        /// <summary>访问密钥，包含在网页链接中。</summary>
        [DataMember] public string WebToken;

        public static string NewToken()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
            var bytes = new byte[20];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new System.Text.StringBuilder();
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        [DataMember] public bool TopMost;
        [DataMember] public int SidebarWidth;
        [DataMember] public List<AliasEntry> Aliases;
        /// <summary>各 VS 的职责描述（AI 总控助手据此自动分派任务），Alias 字段存描述文本。</summary>
        [DataMember] public List<AliasEntry> VsNotes;

        /// <summary>任务完成后用豆包语音播报概述（默认开启，填写 API Key 后生效）。旧版默认关闭的 VoiceEnabled 不再读取。</summary>
        [DataMember(Name = "VoiceAnnounce")] public bool VoiceEnabled;
        /// <summary>播报概述优先用 AI 总控助手的模型总结（已配置时）。</summary>
        [DataMember] public bool VoiceAiSummary;
        /// <summary>非中文回答先用豆包机器翻译成中文再播报。</summary>
        [DataMember] public bool VoiceTranslate;
        /// <summary>按住空格语音输入（豆包流式语音识别）。</summary>
        [DataMember] public bool AsrEnabled;
        [DataMember] public string AsrResource;
        [DataMember] public bool VoiceIncludeName;
        [DataMember] public string VoiceResource;
        [DataMember] public string VoiceSpeaker;
        /// <summary>
        /// 语音语言：zh（中文，默认）/ en（English）；同时决定语音播报提示词与 AI 总控助手的回复语言。
        /// Voice language: zh (Chinese, default) / en (English); also selects the voice-summary prompt and the AI assistant reply language.
        /// </summary>
        [DataMember] public string VoiceLanguage;
        /// <summary>
        /// 英文播报使用的音色 ID（seed-tts-*）或声音描述（seed-audio-*）；为空时使用内置英文默认值。
        /// Voice ID (seed-tts-*) or voice description (seed-audio-*) for English announcements; empty = built-in English default.
        /// </summary>
        [DataMember] public string VoiceSpeakerEn;

        /// <summary>当前是否使用英文。/ Whether English is selected.</summary>
        public bool IsEnglishVoice => VoiceLanguages.IsEnglish(VoiceLanguage);

        /// <summary>当前语言对应的音色设置（英文为空时回退到内置默认）。/ Voice setting for the current language (English falls back to the built-in default when empty).</summary>
        public string SpeakerFor(bool english) =>
            english ? (string.IsNullOrWhiteSpace(VoiceSpeakerEn) ? DoubaoVoice.DefaultVoiceFor(VoiceResource, true) : VoiceSpeakerEn.Trim()) : VoiceSpeaker;
        /// <summary>DPAPI（当前用户）加密后的 API Key。</summary>
        [DataMember] public string VoiceKeyProtected;

        public string VoiceApiKey
        {
            get
            {
                if (string.IsNullOrEmpty(VoiceKeyProtected)) return "";
                try
                {
                    var b = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(VoiceKeyProtected), null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    return System.Text.Encoding.UTF8.GetString(b);
                }
                catch { return ""; }
            }
            set
            {
                VoiceKeyProtected = string.IsNullOrWhiteSpace(value) ? null : Convert.ToBase64String(
                    System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value.Trim()), null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
        }

        public AppSettings() => SetDefaults();

        /// <summary>环境变量：设置中未填写 Key 时使用（便于不把 Key 写入任何文件）。</summary>
        public const string AgentKeyEnvVar = "VSMANAGER_AGENT_API_KEY", VoiceKeyEnvVar = "VSMANAGER_DOUBAO_API_KEY";

        private static string Env(string name)
        {
            try { return (Environment.GetEnvironmentVariable(name) ?? "").Trim(); }
            catch { return ""; }
        }

        /// <summary>实际使用的 AI 助手 Key：设置优先，否则读取环境变量 VSMANAGER_AGENT_API_KEY。</summary>
        public string EffectiveAgentApiKey { get { string k = AgentApiKey; return string.IsNullOrWhiteSpace(k) ? Env(AgentKeyEnvVar) : k; } }

        /// <summary>实际使用的豆包语音 Key：设置优先，否则读取环境变量 VSMANAGER_DOUBAO_API_KEY。</summary>
        public string EffectiveVoiceApiKey { get { string k = VoiceApiKey; return string.IsNullOrWhiteSpace(k) ? Env(VoiceKeyEnvVar) : k; } }

        public bool HasVoiceKey => !string.IsNullOrWhiteSpace(VoiceKeyProtected) || Env(VoiceKeyEnvVar).Length > 0;

        /// <summary>侧边栏 AI 总控助手（OpenAI 兼容接口）。</summary>
        [DataMember] public bool AgentEnabled;
        /// <summary>Copilot 对话窗格被切到后台（如切换到其他文档标签）时自动切回。</summary>
        [DataMember] public bool RestoreCopilotPane;
        /// <summary>对话窗格未真正打开（缺失、不可见或停留在历史记录）时，发送前与手动打开时自动打开到当前会话，默认开启。/ Auto-open the current conversation before sending and on manual open when the pane is missing, hidden or on the history list (default on).</summary>
        [DataMember] public bool AutoOpenCopilotPane;
        [DataMember] public string AgentEndpoint;
        [DataMember] public string AgentModel;
        [DataMember] public string AgentKeyProtected;
        /// <summary>附加到系统提示词的自定义要求。</summary>
        [DataMember] public string AgentInstructions;
        /// <summary>审批模式：发布任务 / 调试等操作执行前弹窗确认。</summary>
        [DataMember] public bool AgentConfirm;
        /// <summary>任务清单中的任务完成后，自动让 AI 助手跟进（汇报结果 / 发布后续任务）。</summary>
        [DataMember] public bool AgentAutoFollowUp;
        /// <summary>允许经预览批准的截图分析；需要视觉模型。/ Allow approved screenshot analysis; requires a vision model.</summary>
        [DataMember] public bool AgentScreenshotEnabled;
        /// <summary>保留旧配置字段；严格文件白名单模式下不再允许 AI 执行任意脚本。/ Legacy field retained; strict file allowlisting no longer permits arbitrary AI scripts.</summary>
        [DataMember] public bool AgentPowerShellEnabled;
        /// <summary>将已登记解决方案目录加入文件授权范围，默认开启。/ Include registered solution directories in file authorization; enabled by default.</summary>
        [DataMember] public bool AgentIncludeSolutionRoots;
        /// <summary>用户显式授权的额外文件根目录，支持环境变量，默认空。/ Additional file roots explicitly authorized by the user, supporting environment variables; empty by default.</summary>
        [DataMember] public List<string> AgentFileRoots;

        // ---- AI 额度（单次上限）/ AI per-call quotas ----
        /// <summary>单次工具返回字符上限；文件工具另受 16000 字符硬上限约束，取较小值。/ Per-tool character quota; file tools also enforce a 16000-character hard cap, using the lower limit.</summary>
        [DataMember] public int AgentMaxToolText;
        /// <summary>读取 VS 对话时单条消息的文本上限（字符）。</summary>
        [DataMember] public int AgentMaxMessageText;
        /// <summary>单次任务文本上限（字符）：发布任务、改进需求说明；能力名为 1/2、职责描述为 1/3。</summary>
        [DataMember] public int AgentMaxTaskText;
        /// <summary>单次回复长度上限（token）；0 表示使用模型默认值。</summary>
        [DataMember] public int AgentMaxOutputTokens;
        /// <summary>上下文保留的历史消息条数。</summary>
        [DataMember] public int AgentMaxHistory;
        /// <summary>单次对话内最多连续调用工具的轮数。</summary>
        [DataMember] public int AgentMaxIterations;
        /// <summary>读取文件时单次最多行数。</summary>
        [DataMember] public int AgentMaxFileLines;
        /// <summary>单次最多读取的对话消息条数；错误列表条数上限为其 5 倍。</summary>
        [DataMember] public int AgentMaxReadCount;

        public const int DefaultAgentMaxToolText = 120000, DefaultAgentMaxMessageText = 30000, DefaultAgentMaxTaskText = 12000,
            DefaultAgentMaxOutputTokens = 0, DefaultAgentMaxHistory = 800, DefaultAgentMaxIterations = 320,
            DefaultAgentMaxFileLines = 4000, DefaultAgentMaxReadCount = 400;

        /// <summary>AI 额度的有效范围：名称 → (最小, 最大, 默认)。</summary>
        public static readonly Dictionary<string, int[]> AgentQuotaRanges = new Dictionary<string, int[]>
        {
            [nameof(AgentMaxToolText)] = new[] { 1000, 2000000, DefaultAgentMaxToolText },
            [nameof(AgentMaxMessageText)] = new[] { 200, 1000000, DefaultAgentMaxMessageText },
            [nameof(AgentMaxTaskText)] = new[] { 300, 1000000, DefaultAgentMaxTaskText },
            [nameof(AgentMaxOutputTokens)] = new[] { 0, 1000000, DefaultAgentMaxOutputTokens },
            [nameof(AgentMaxHistory)] = new[] { 10, 10000, DefaultAgentMaxHistory },
            [nameof(AgentMaxIterations)] = new[] { 1, 1000, DefaultAgentMaxIterations },
            [nameof(AgentMaxFileLines)] = new[] { 10, 100000, DefaultAgentMaxFileLines },
            [nameof(AgentMaxReadCount)] = new[] { 1, 10000, DefaultAgentMaxReadCount },
        };

        /// <summary>把超出范围的额度拉回有效区间（0 / 负数等无效值恢复默认）。</summary>
        public static int ClampQuota(string name, int value)
        {
            var r = AgentQuotaRanges[name];
            if (value < r[0]) return value <= 0 ? r[2] : r[0];
            return Math.Min(value, r[1]);
        }

        public void ClampAgentQuota()
        {
            AgentMaxToolText = ClampQuota(nameof(AgentMaxToolText), AgentMaxToolText);
            AgentMaxMessageText = ClampQuota(nameof(AgentMaxMessageText), AgentMaxMessageText);
            AgentMaxTaskText = ClampQuota(nameof(AgentMaxTaskText), AgentMaxTaskText);
            AgentMaxOutputTokens = ClampQuota(nameof(AgentMaxOutputTokens), AgentMaxOutputTokens);
            AgentMaxHistory = ClampQuota(nameof(AgentMaxHistory), AgentMaxHistory);
            AgentMaxIterations = ClampQuota(nameof(AgentMaxIterations), AgentMaxIterations);
            AgentMaxFileLines = ClampQuota(nameof(AgentMaxFileLines), AgentMaxFileLines);
            AgentMaxReadCount = ClampQuota(nameof(AgentMaxReadCount), AgentMaxReadCount);
            AgentFileRoots = (AgentFileRoots ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            AgentPowerShellEnabled = false;
        }
        [DataMember] public bool TaskPanelCollapsed;
        /// <summary>任务清单按目标 VS 分组显示（false = 平铺列表），默认开启。/ Group the task list by target VS (false = flat list); on by default.</summary>
        [DataMember] public bool TaskListGroupByVs;
        /// <summary>分组排序："activity"（默认，执行中优先，再按最近活动倒序）或 "number"（按 VS 编号）。/ Group order: "activity" (default: running first, then latest activity) or "number" (by VS number).</summary>
        [DataMember] public string TaskListGroupSort;
        /// <summary>已折叠的任务分组（分组键，只保存在本机 settings.json）。/ Collapsed task groups (group keys, kept only in the local settings.json).</summary>
        [DataMember] public List<string> TaskListCollapsedGroups;
        /// <summary>监听各 VS 中手动进行的 Copilot 对话并显示在任务清单中。</summary>
        [DataMember] public bool WatchConversations;
        /// <summary>普通已结束任务保留上限；失败及维护重发关系所需记录除外，0 或负数保留全部。/ Completed history limit, excluding failures and required resend links; zero or negative keeps all.</summary>
        [DataMember] public int TaskHistoryLimit;
        /// <summary>默认跳过失败前序继续排队任务；关闭时失败阻塞后续。/ Skip failed predecessors by default; disabling pauses successors on failure.</summary>
        [DataMember] public bool SkipFailedPredecessors;
        /// <summary>下一个任务编号，保证清除历史后编号也不重复。</summary>
        [DataMember] public int TaskNextId;
        /// <summary>
        /// 任务清单「清除已完成」的时间点：此前完成的任务 / 对话只在界面中隐藏，tasks.json 与归档保持不变；null 表示未清除。
        /// Time of the last "Clear completed": items completed before it are only hidden in the UI; tasks.json and the archive are untouched. null = never cleared.
        /// </summary>
        [DataMember] public DateTime? TaskListClearedAt;
        /// <summary>
        /// 同一任务被重新发布（重新排队）时，自动把原失败条目从任务清单界面隐藏（不删除 tasks.json 记录），默认开启。
        /// When the same task is published again (requeued), hide the original failed entry in the task list UI (tasks.json is untouched); on by default.
        /// </summary>
        [DataMember] public bool AutoHideResentFailedTasks;
        /// <summary>隐藏原失败条目时弹出通知并语音播报（语音还需开启语音播报），默认开启。/ Show a notification and announce by voice when an original failed entry is hidden (voice also needs voice announcements on); on by default.</summary>
        [DataMember] public bool AutoHideResentFailedNotify;
        /// <summary>
        /// 因「已重新排队」而在界面隐藏的失败任务（编号、取代它的新任务编号、隐藏时间）；「撤销清除」会清空。
        /// Failed tasks hidden in the UI as "requeued" (id, id of the superseding task, time); cleared by "Undo clear".
        /// </summary>
        [DataMember] public List<HiddenTaskMark> HiddenResentTasks;
        [DataMember] public int AgentHeight;
        /// <summary>历史记录统一归档（任务流水、AI 助手对话、各 VS 对话、发送日志）。</summary>
        [DataMember] public bool ArchiveEnabled;
        /// <summary>归档根目录（支持 %环境变量%）；默认取环境变量 VSMANAGER_ARCHIVE_ROOT，否则 %APPDATA%\VSManager\archive；不可用时回退到后者。</summary>
        [DataMember] public string ArchiveRoot;
        /// <summary>归档与发送日志保留天数；0 表示永久保留（默认）。</summary>
        [DataMember] public int ArchiveRetentionDays;
        /// <summary>启动时从归档恢复的手动对话条数上限（也是内存中保留的已结束条数）。</summary>
        [DataMember] public int ExternalRestoreLimit;
        /// <summary>只恢复最近多少小时内开始的手动对话。</summary>
        [DataMember] public int ExternalRestoreHours;
        /// <summary>
        /// AI 助手对话记录（agent-chat.jsonl）保留天数；0 表示不按天数裁剪。
        /// Days to keep in the AI assistant chat history (agent-chat.jsonl); 0 = no day limit.
        /// </summary>
        [DataMember] public int AgentChatKeepDays;
        /// <summary>
        /// AI 助手对话记录最多保留条数；0 表示不按条数裁剪。超出后删除最旧的记录（只影响该文件）。
        /// Max records kept in the AI assistant chat history; 0 = no count limit. The oldest are removed (this file only).
        /// </summary>
        [DataMember] public int AgentChatMaxRecords;

        // ---- VS 内存监控 / VS memory monitor ----
        /// <summary>
        /// 是否在某个 VS 实例（含子进程）工作集超过阈值时自动处理；默认关闭。
        /// Whether to act automatically when a VS instance (with its child processes) exceeds the working-set threshold; off by default.
        /// </summary>
        [DataMember] public bool VsMemoryAutoEnabled;
        /// <summary>
        /// 自动处理阈值（MB，按 VS 实例及其子进程的工作集合计）。
        /// Threshold in MB (working set of a VS instance plus its child processes).
        /// </summary>
        [DataMember] public int VsMemoryThresholdMB;
        /// <summary>
        /// 超过阈值时：false = 只提示；true = 自动温和清理（VS 调试 / 生成 / Copilot 运行中时跳过，只提示）。
        /// When exceeded: false = notify only; true = gentle clean automatically (skipped, notify only, while debugging / building / Copilot is running).
        /// </summary>
        [DataMember] public bool VsMemoryAutoClean;
        /// <summary>
        /// 定期自动温和清理 VS 内存，默认关闭；调试 / 生成 / Copilot 运行 / 任务执行中的实例会被跳过。
        /// Periodically gently clean VS memory; off by default. Instances that are debugging, building, running Copilot or a task are skipped.
        /// </summary>
        [DataMember] public bool AutoTrimVsMemory;
        /// <summary>定期清理间隔（分钟），默认 30，范围 5–1440。/ Periodic cleanup interval in minutes; default 30, range 5–1440.</summary>
        [DataMember] public int AutoTrimIntervalMinutes;
        /// <summary>
        /// 定期清理阈值（MB，VS 实例及其子进程的工作集合计）；0 表示不限制，大于 0 时只清理超过该值的实例。
        /// Periodic cleanup threshold in MB (working set of a VS instance plus children); 0 = no limit, otherwise only larger instances are cleaned.
        /// </summary>
        [DataMember] public int AutoTrimThresholdMB;

        // ---- AI 助手附件 / AI assistant attachments ----
        /// <summary>单个附件大小上限（MB），默认 10，范围 1–100。/ Maximum size of one attachment in MB; default 10, range 1–100.</summary>
        [DataMember] public int AttachmentMaxFileMB;
        /// <summary>单条消息附件数量上限，默认 5，范围 1–20。/ Maximum attachments per message; default 5, range 1–20.</summary>
        [DataMember] public int AttachmentMaxCount;
        /// <summary>附件保留天数，默认 30；0 表示不限制（不自动清理）。/ Days to keep attachments; default 30, 0 = unlimited (no automatic cleanup).</summary>
        [DataMember] public int AttachmentKeepDays;
        /// <summary>文本附件内联到任务正文的字数上限（单文件），默认 20000，范围 1000–200000。/ Characters of one text attachment inlined into a task; default 20000, range 1000–200000.</summary>
        [DataMember] public int AttachmentInlineMaxChars;

        // ---- 自动重启 / Auto restart ----
        /// <summary>
        /// AI 助手出现未处理异常、请求连续失败或长时间无响应时自动重建；默认开启。
        /// Rebuild the AI assistant automatically on an unhandled error, repeated request failures or a hang; on by default.
        /// </summary>
        [DataMember] public bool AgentAutoRestart;
        /// <summary>AI 助手无响应判定超时（秒），默认 120。/ Seconds without progress before the AI assistant counts as hung (default 120).</summary>
        [DataMember] public int AgentHangTimeoutSeconds;
        /// <summary>请求连续失败多少次后重建 AI 助手，默认 3。/ Consecutive request failures that trigger a rebuild (default 3).</summary>
        [DataMember] public int AgentFailureThreshold;
        /// <summary>
        /// 进程看门狗：VSManager 异常退出后自动重新启动；默认关闭。
        /// Process watchdog: restart VSManager after it exits abnormally; off by default.
        /// </summary>
        [DataMember] public bool ProcessWatchdogEnabled;
        /// <summary>防重启风暴：时间窗口内最多自动重启次数（AI 助手与进程分别计数），默认 3。/ Max automatic restarts per window (counted separately for the assistant and the process), default 3.</summary>
        [DataMember] public int AutoRestartMaxCount;
        /// <summary>防重启风暴的时间窗口（分钟），默认 5。/ Window of the restart limit in minutes (default 5).</summary>
        [DataMember] public int AutoRestartWindowMinutes;

        public const int DefaultAgentHangTimeoutSeconds = 120, DefaultAgentFailureThreshold = 3, DefaultAutoRestartMaxCount = 3, DefaultAutoRestartWindowMinutes = 5;

        /// <summary>把重启相关配置拉回有效区间（0 / 负数恢复默认）。/ Clamps the restart settings (0 / negative values restore the default).</summary>
        public void ClampRestart()
        {
            AgentHangTimeoutSeconds = AgentHangTimeoutSeconds <= 0 ? DefaultAgentHangTimeoutSeconds : Math.Min(Math.Max(30, AgentHangTimeoutSeconds), 3600);
            AgentFailureThreshold = AgentFailureThreshold <= 0 ? DefaultAgentFailureThreshold : Math.Min(AgentFailureThreshold, 20);
            AutoRestartMaxCount = AutoRestartMaxCount <= 0 ? DefaultAutoRestartMaxCount : Math.Min(AutoRestartMaxCount, 20);
            AutoRestartWindowMinutes = AutoRestartWindowMinutes <= 0 ? DefaultAutoRestartWindowMinutes : Math.Min(AutoRestartWindowMinutes, 1440);
        }

        // ---- 发送确认 / Send confirmation ----
        /// <summary>
        /// 粘贴 / 写入 Copilot 输入框后等待确认的超时（秒），默认 10，范围 2–120。
        /// Seconds to wait for the pasted / typed text to be confirmed in the Copilot input box (default 10, range 2–120).
        /// </summary>
        [DataMember] public int SendConfirmTimeoutSeconds;
        /// <summary>粘贴未能确认时是否在本次发送内自动重试，默认开启。/ Retry automatically within the same send when the paste cannot be confirmed (on by default).</summary>
        [DataMember] public bool SendAutoRetry;
        /// <summary>仅自动确认 Windows CR LF 的行尾标准化弹窗。/ Only auto-confirm the Windows CR LF normalization dialog.</summary>
        [DataMember] public bool SendAutoNormalizeLineEndings;
        /// <summary>自动关闭白名单中的单按钮完成通知；未知弹窗仍需确认。/ Dismiss allowlisted single-button notices; unknown dialogs remain manual.</summary>
        [DataMember] public bool SendAutoDismissNotices;
        /// <summary>自动重试次数，默认 1，范围 0–5。/ Number of automatic retries (default 1, range 0–5).</summary>
        [DataMember] public int SendRetryCount;
        /// <summary>
        /// 定位 Copilot 输入框的每轮超时（秒），默认 6，范围 1–60；超时前按 150 → 300 → 600 → 1000 毫秒退避轮询。
        /// Per-round timeout (seconds) for locating the Copilot input box (default 6, range 1–60); polls with 150 → 300 → 600 → 1000 ms back-off.
        /// </summary>
        [DataMember] public int SendLocateTimeoutSeconds;
        /// <summary>定位失败后刷新窗格并重试的次数，默认 1，范围 0–5（SendAutoRetry 关闭时不重试）。/ Retries after a failed locate, refreshing the pane (default 1, range 0–5; none when SendAutoRetry is off).</summary>
        [DataMember] public int SendLocateRetryCount;
        /// <summary>发送前关闭已保存文档标签页，默认关闭；未保存、状态未知及调试中均跳过。/ Close saved document tabs before sending; off by default, skipping unsaved, unknown and debugging states.</summary>
        [DataMember] public bool CloseVsDocumentsBeforeSend;
        /// <summary>文档标签页数量严格超过此值才清理，默认 10，范围 0–1000。/ Clean only when document tabs strictly exceed this threshold; default 10, range 0–1000.</summary>
        [DataMember] public int CloseVsDocumentsThreshold;
        public const int DefaultCloseVsDocumentsThreshold = 10;

        public const int DefaultSendConfirmTimeoutSeconds = PasteVerifier.DefaultTimeoutSeconds, DefaultSendRetryCount = 1;
        public const int DefaultSendLocateTimeoutSeconds = InputLocator.DefaultTimeoutSeconds, DefaultSendLocateRetryCount = InputLocator.DefaultRetryCount;

        /// <summary>把发送确认配置拉回有效区间（0 / 负数超时恢复默认）。/ Clamps the send confirmation settings (0 / negative timeout restores the default).</summary>
        public void ClampSend()
        {
            SendConfirmTimeoutSeconds = SendConfirmTimeoutSeconds <= 0 ? DefaultSendConfirmTimeoutSeconds : Math.Min(Math.Max(2, SendConfirmTimeoutSeconds), 120);
            SendRetryCount = Math.Min(Math.Max(0, SendRetryCount), 5);
            SendLocateTimeoutSeconds = SendLocateTimeoutSeconds <= 0 ? DefaultSendLocateTimeoutSeconds : Math.Min(SendLocateTimeoutSeconds, 60);
            SendLocateRetryCount = Math.Min(Math.Max(0, SendLocateRetryCount), 5);
            CloseVsDocumentsThreshold = CloseVsDocumentsThreshold < 0 ? DefaultCloseVsDocumentsThreshold : Math.Min(CloseVsDocumentsThreshold, 1000);
        }

        // ---- 解决方案登记与 VS 开关 / Solution registry and VS open / close ----
        /// <summary>AI 关闭 VS（close_vs）前是否弹窗确认，默认开启。/ Ask for confirmation before the AI closes a VS (close_vs); on by default.</summary>
        [DataMember] public bool SolutionCloseConfirm;
        /// <summary>open_solution 等待新 VS 窗口出现的最长秒数，默认 90，范围 10–600。/ Max seconds open_solution waits for the new VS window (default 90, range 10–600).</summary>
        [DataMember] public int SolutionOpenWaitSeconds;
        /// <summary>
        /// 目标 VS 打开后，等待多少秒（让解决方案加载完成）再推送暂存任务，默认 20，范围 0–300。
        /// Seconds to wait after the target VS opens (so the solution can finish loading) before pushing parked tasks (default 20, range 0–300).
        /// </summary>
        [DataMember] public int PendingVsSettleSeconds;
        /// <summary>任务暂存 / 自动推送时是否弹出通知并语音播报（语音还需开启语音播报），默认开启。/ Show a notification and announce by voice when a task is parked / pushed (voice also needs voice announcements on); on by default.</summary>
        [DataMember] public bool PendingVsNotify;

        public const int DefaultSolutionOpenWaitSeconds = 90, DefaultPendingVsSettleSeconds = 20;

        /// <summary>把解决方案相关配置拉回有效区间。/ Clamps the solution-related settings.</summary>
        public void ClampSolutions()
        {
            SolutionOpenWaitSeconds = SolutionOpenWaitSeconds <= 0 ? DefaultSolutionOpenWaitSeconds : Math.Min(Math.Max(10, SolutionOpenWaitSeconds), 600);
            PendingVsSettleSeconds = Math.Min(Math.Max(0, PendingVsSettleSeconds), 300);
        }

        // ---- 发布到 GitHub / Publish to GitHub ----
        /// <summary>要发布的本地仓库目录；为空时自动查找 VSManager.csproj 并定位到其所在仓库根目录（含 VSManager.slnx 或 .git）。/ Local repository folder; empty = locate VSManager.csproj and use its repository root (containing VSManager.slnx or .git).</summary>
        [DataMember] public string PublishRepoPath;
        /// <summary>仓库所有者（用户或组织）；为空时使用 Token 对应的用户。/ Repository owner (user or org); empty = the token's user.</summary>
        [DataMember] public string PublishOwner;
        /// <summary>仓库名。/ Repository name.</summary>
        [DataMember] public string PublishRepoName;
        /// <summary>新建仓库时的可见性：public / private。/ Visibility used when creating the repository.</summary>
        [DataMember] public string PublishVisibility;
        /// <summary>默认分支。/ Default branch.</summary>
        [DataMember] public string PublishBranch;
        /// <summary>提交作者名（不要填写真实姓名）。/ Commit author name (do not use a real name).</summary>
        [DataMember] public string PublishAuthorName;
        /// <summary>提交邮箱；为空时使用 GitHub 的 noreply 地址。/ Commit e-mail; empty = GitHub noreply address.</summary>
        [DataMember] public string PublishAuthorEmail;
        /// <summary>GitHub Token（DPAPI 加密）。/ GitHub token (DPAPI-encrypted).</summary>
        [DataMember] public string GitHubTokenProtected;

        public const string GitHubTokenEnvVar = "VSMANAGER_GITHUB_TOKEN";

        public string GitHubToken
        {
            get => Unprotect(GitHubTokenProtected);
            set => GitHubTokenProtected = Protect(value);
        }

        /// <summary>实际使用的 GitHub Token：设置优先，否则读取环境变量。/ Effective token: settings first, then the environment variable.</summary>
        public string EffectiveGitHubToken { get { string k = GitHubToken; return string.IsNullOrWhiteSpace(k) ? Env(GitHubTokenEnvVar) : k; } }

        public string AgentApiKey
        {
            get => Unprotect(AgentKeyProtected);
            set => AgentKeyProtected = Protect(value);
        }

        private static string Unprotect(string data)
        {
            if (string.IsNullOrEmpty(data)) return "";
            try
            {
                return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(data), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }

        private static string Protect(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(value.Trim()), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c) => SetDefaults();

        private void SetDefaults()
        {
            Layout = "上下";
            ActivateMoveMain = true;
            ActivateMoveTools = false;
            MonitorCopilot = true;
            Sound = true;
            Popup = true;
            MinimizeToTray = true;
            Hotkeys = true;
            PollMs = 1500;
            CopilotPaneKeyword = "Copilot";
            BusyButtonIds = "CancelButton";
            ShowChatSteps = true;
            BackgroundSend = true;
            AutoOpenChat = true;
            BackgroundSync = true;
            SidebarWidth = 0;
            WebPort = 8765;
            VoiceEnabled = true;
            VoiceIncludeName = true;
            VoiceAiSummary = true;
            VoiceTranslate = true;
            AsrEnabled = true;
            AsrResource = DoubaoAsr.DefaultResource;
            VoiceResource = DoubaoVoice.DefaultResource;
            VoiceSpeaker = DoubaoVoice.DefaultVoiceFor(DoubaoVoice.DefaultResource);
            VoiceLanguage = VoiceLanguages.Chinese;
            VoiceSpeakerEn = DoubaoVoice.DefaultVoiceFor(DoubaoVoice.DefaultResource, true);
            AgentEnabled = true;
            RestoreCopilotPane = true;
            AutoOpenCopilotPane = true;
            AgentEndpoint = AgentPresets.Default.Endpoint;
            AgentModel = AgentPresets.Default.Model;
            AgentConfirm = false;
            AgentAutoFollowUp = true;
            AgentScreenshotEnabled = true;
            AgentPowerShellEnabled = false;
            AgentIncludeSolutionRoots = true;
            AgentFileRoots = new List<string>();
            AgentMaxToolText = DefaultAgentMaxToolText;
            AgentMaxMessageText = DefaultAgentMaxMessageText;
            AgentMaxTaskText = DefaultAgentMaxTaskText;
            AgentMaxOutputTokens = DefaultAgentMaxOutputTokens;
            AgentMaxHistory = DefaultAgentMaxHistory;
            AgentMaxIterations = DefaultAgentMaxIterations;
            AgentMaxFileLines = DefaultAgentMaxFileLines;
            AgentMaxReadCount = DefaultAgentMaxReadCount;
            TaskPanelCollapsed = false;
            TaskListGroupByVs = true;
            TaskListGroupSort = TaskGrouping.SortByActivity;
            TaskListCollapsedGroups = new List<string>();
            WatchConversations = true;
            TaskHistoryLimit = 0;
            SkipFailedPredecessors = true;
            TaskNextId = 0;
            ArchiveEnabled = true;
            ArchiveRoot = Archive.DefaultRoot;
            ArchiveRetentionDays = 0;
            ExternalRestoreLimit = 50;
            ExternalRestoreHours = 24;
            AgentChatKeepDays = AgentChatLog.DefaultKeepDays;
            AgentChatMaxRecords = AgentChatLog.DefaultMaxRecords;
            VsMemoryAutoEnabled = false;
            VsMemoryThresholdMB = VsMemory.DefaultThresholdMB;
            VsMemoryAutoClean = false;
            AutoTrimVsMemory = false;
            AutoTrimIntervalMinutes = VsAutoMemoryTrimmer.DefaultIntervalMinutes;
            AutoTrimThresholdMB = 0;
            AttachmentMaxFileMB = AttachmentPolicy.DefaultMaxFileMB;
            AttachmentMaxCount = AttachmentPolicy.DefaultMaxCount;
            AttachmentKeepDays = AttachmentPolicy.DefaultKeepDays;
            AttachmentInlineMaxChars = AttachmentPolicy.DefaultInlineMaxChars;
            AgentAutoRestart = true;
            AgentHangTimeoutSeconds = DefaultAgentHangTimeoutSeconds;
            AgentFailureThreshold = DefaultAgentFailureThreshold;
            ProcessWatchdogEnabled = false;
            AutoRestartMaxCount = DefaultAutoRestartMaxCount;
            AutoRestartWindowMinutes = DefaultAutoRestartWindowMinutes;
            SendConfirmTimeoutSeconds = DefaultSendConfirmTimeoutSeconds;
            SendAutoRetry = true;
            SendAutoNormalizeLineEndings = true;
            SendAutoDismissNotices = true;
            SendRetryCount = DefaultSendRetryCount;
            SendLocateTimeoutSeconds = DefaultSendLocateTimeoutSeconds;
            SendLocateRetryCount = DefaultSendLocateRetryCount;
            CloseVsDocumentsBeforeSend = false;
            CloseVsDocumentsThreshold = DefaultCloseVsDocumentsThreshold;
            SolutionCloseConfirm = true;
            SolutionOpenWaitSeconds = DefaultSolutionOpenWaitSeconds;
            PendingVsSettleSeconds = DefaultPendingVsSettleSeconds;
            PendingVsNotify = true;
            AutoHideResentFailedTasks = true;
            AutoHideResentFailedNotify = true;
            HiddenResentTasks = new List<HiddenTaskMark>();
            PublishRepoPath = "";
            PublishOwner = "";
            PublishRepoName = "VSManager";
            PublishVisibility = "public";
            PublishBranch = "main";
            PublishAuthorName = "VSManager contributors";
            PublishAuthorEmail = "";
            Aliases = new List<AliasEntry>();
            VsNotes = new List<AliasEntry>();
        }

        public static string FilePath => Path.Combine(AppPaths.DataFolder, "settings.json");

        public static AppSettings Load()
        {
            lock (SaveLock)
            {
                var s = TryRead(FilePath) ?? TryRead(FilePath + ".bak");
                if (s == null)
                {
                    // 文件损坏时保留一份，避免被默认设置直接覆盖
                    try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
                    s = new AppSettings();
                }
                if (s.Aliases == null) s.Aliases = new List<AliasEntry>();
                if (s.VsNotes == null) s.VsNotes = new List<AliasEntry>();
                if (s.HiddenResentTasks == null) s.HiddenResentTasks = new List<HiddenTaskMark>();
                s.TaskListGroupSort = TaskGrouping.NormalizeSort(s.TaskListGroupSort);
                s.TaskListCollapsedGroups = TaskGrouping.NormalizeCollapsed(s.TaskListCollapsedGroups);
                NormalizeTitleKeys(s.Aliases);
                NormalizeTitleKeys(s.VsNotes);
                s.Migrate();
                s.ClampAgentQuota();
                // 负数容量按「不限制」处理 / Negative capacities mean "no limit"
                s.AgentChatKeepDays = Math.Min(Math.Max(0, s.AgentChatKeepDays), 36500);
                s.AgentChatMaxRecords = Math.Min(Math.Max(0, s.AgentChatMaxRecords), 1000000);
                s.VsMemoryThresholdMB = VsMemory.ClampThreshold(s.VsMemoryThresholdMB);
                s.AutoTrimIntervalMinutes = VsAutoMemoryTrimmer.ClampInterval(s.AutoTrimIntervalMinutes);
                s.AutoTrimThresholdMB = VsAutoMemoryTrimmer.ClampThreshold(s.AutoTrimThresholdMB);
                s.AttachmentMaxFileMB = AttachmentPolicy.ClampMaxFileMB(s.AttachmentMaxFileMB);
                s.AttachmentMaxCount = AttachmentPolicy.ClampMaxCount(s.AttachmentMaxCount);
                s.AttachmentKeepDays = AttachmentPolicy.ClampKeepDays(s.AttachmentKeepDays);
                s.AttachmentInlineMaxChars = AttachmentPolicy.ClampInlineMaxChars(s.AttachmentInlineMaxChars);
                s.ClampRestart();
                s.ClampSend();
                s.ClampSolutions();
                // 非法或缺失的语言值按中文处理 / Invalid or missing language values fall back to Chinese
                s.VoiceLanguage = VoiceLanguages.Normalize(s.VoiceLanguage);
                return s;
            }
        }

        private static AppSettings TryRead(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
                using (var fs = File.OpenRead(path))
                    return (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(fs);
            }
            catch { return null; }
        }

        /// <summary>旧版本默认的火山方舟且未填 Key 时，改为默认的 DeepSeek。</summary>
        private void Migrate()
        {
            if (string.IsNullOrEmpty(AgentKeyProtected) &&
                string.Equals((AgentEndpoint ?? "").TrimEnd('/'), "https://ark.cn-beijing.volces.com/api/v3", StringComparison.OrdinalIgnoreCase) &&
                AgentModel == "doubao-seed-1-6-250615")
            {
                AgentEndpoint = AgentPresets.Default.Endpoint;
                AgentModel = AgentPresets.Default.Model;
            }
        }

        private static readonly object SaveLock = new object();

        /// <summary>每次保存后触发：参数为错误信息，null 表示保存成功。</summary>
        public static event Action<string> Saved;

        /// <summary>先写临时文件再替换（保留 .bak），进程被强制结束也不会得到损坏的设置文件。</summary>
        public bool Save()
        {
            string error = null;
            lock (SaveLock)
            {
                // 替换失败（如被杀毒软件占用）时退回直接覆盖 / Falls back to a direct overwrite when the replace fails
                var r = AtomicFile.Write(FilePath, fs =>
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, System.Text.Encoding.UTF8, false, true))
                        new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(w, this);
                }, backupBeforeOverwrite: false, skipFallbackOnSerializationError: false);
                if (!r.Ok) error = (r.FallbackError ?? r.Error).Message;
            }
            try { Saved?.Invoke(error); } catch { }
            return error == null;
        }

        public string GetAlias(string key) =>
            Aliases.Find(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))?.Alias;

        public void SetAlias(string key, string alias)
        {
            Aliases.RemoveAll(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(alias)) Aliases.Add(new AliasEntry { Key = key, Alias = alias.Trim() });
        }

        /// <summary>旧版本用完整窗口标题作为未识别解决方案的键（随活动文档变化），统一改为稳定的解决方案名。</summary>
        private static void NormalizeTitleKeys(List<AliasEntry> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var e = list[i];
                if (e == null || string.IsNullOrEmpty(e.Key)) { list.RemoveAt(i); continue; }
                if (!e.Key.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) continue;
                string k = "title:" + VsService.TitleName(e.Key.Substring(6));
                if (k == e.Key) continue;
                if (list.Exists(x => x != e && string.Equals(x.Key, k, StringComparison.OrdinalIgnoreCase))) list.RemoveAt(i);
                else e.Key = k;
            }
        }

        public string GetNote(string key)
        {
            lock (VsNotes) return VsNotes.Find(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))?.Alias;
        }

        public void SetNote(string key, string note)
        {
            lock (VsNotes)
            {
                VsNotes.RemoveAll(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(note)) VsNotes.Add(new AliasEntry { Key = key, Alias = note.Trim() });
            }
        }
    }
}
