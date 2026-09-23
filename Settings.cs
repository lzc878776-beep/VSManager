using System;
using System.Collections.Generic;
using System.IO;
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
        [DataMember] public string AgentEndpoint;
        [DataMember] public string AgentModel;
        [DataMember] public string AgentKeyProtected;
        /// <summary>附加到系统提示词的自定义要求。</summary>
        [DataMember] public string AgentInstructions;
        /// <summary>审批模式：发布任务 / 调试等操作执行前弹窗确认。</summary>
        [DataMember] public bool AgentConfirm;
        /// <summary>任务清单中的任务完成后，自动让 AI 助手跟进（汇报结果 / 发布后续任务）。</summary>
        [DataMember] public bool AgentAutoFollowUp;

        // ---- AI 额度（单次上限，默认值为旧硬编码值 ×20）----
        /// <summary>单次工具返回文本上限（字符）：读取对话 / 等待结果 / 错误列表 / 读取文件；代码扫描为其 1.5 倍。</summary>
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
        }
        [DataMember] public bool TaskPanelCollapsed;
        /// <summary>监听各 VS 中手动进行的 Copilot 对话并显示在任务清单中。</summary>
        [DataMember] public bool WatchConversations;
        /// <summary>任务清单保留的已结束任务条数上限；0 或负数表示全部保留（默认）。</summary>
        [DataMember] public int TaskHistoryLimit;
        /// <summary>下一个任务编号，保证清除历史后编号也不重复。</summary>
        [DataMember] public int TaskNextId;
        /// <summary>
        /// 任务清单「清除已完成」的时间点：此前完成的任务 / 对话只在界面中隐藏，tasks.json 与归档保持不变；null 表示未清除。
        /// Time of the last "Clear completed": items completed before it are only hidden in the UI; tasks.json and the archive are untouched. null = never cleared.
        /// </summary>
        [DataMember] public DateTime? TaskListClearedAt;
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

        // ---- 发布到 GitHub / Publish to GitHub ----
        /// <summary>要发布的本地仓库目录；为空时自动查找 VSManager.csproj 所在目录。/ Local repository folder; empty = auto-detect the folder containing VSManager.csproj.</summary>
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
            AgentEndpoint = AgentPresets.Default.Endpoint;
            AgentModel = AgentPresets.Default.Model;
            AgentConfirm = false;
            AgentAutoFollowUp = true;
            AgentMaxToolText = DefaultAgentMaxToolText;
            AgentMaxMessageText = DefaultAgentMaxMessageText;
            AgentMaxTaskText = DefaultAgentMaxTaskText;
            AgentMaxOutputTokens = DefaultAgentMaxOutputTokens;
            AgentMaxHistory = DefaultAgentMaxHistory;
            AgentMaxIterations = DefaultAgentMaxIterations;
            AgentMaxFileLines = DefaultAgentMaxFileLines;
            AgentMaxReadCount = DefaultAgentMaxReadCount;
            TaskPanelCollapsed = false;
            WatchConversations = true;
            TaskHistoryLimit = 0;
            TaskNextId = 0;
            ArchiveEnabled = true;
            ArchiveRoot = Archive.DefaultRoot;
            ArchiveRetentionDays = 0;
            ExternalRestoreLimit = 50;
            ExternalRestoreHours = 24;
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

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VSManager", "settings.json");

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
                NormalizeTitleKeys(s.Aliases);
                NormalizeTitleKeys(s.VsNotes);
                s.Migrate();
                s.ClampAgentQuota();
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
                string tmp = FilePath + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, System.Text.Encoding.UTF8, false, true))
                            new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(w, this);
                        fs.Flush(true);
                    }
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", true);
                    else File.Move(tmp, FilePath);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    try
                    {
                        // 替换失败（如被杀毒软件占用）时退回直接覆盖
                        if (File.Exists(tmp)) { File.Copy(tmp, FilePath, true); File.Delete(tmp); error = null; }
                    }
                    catch (Exception ex2) { error = ex2.Message; }
                }
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
