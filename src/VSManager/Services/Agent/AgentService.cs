using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using AIMessage = Microsoft.Extensions.AI.ChatMessage;
using AIRole = Microsoft.Extensions.AI.ChatRole;

namespace VSManager
{
    /// <summary>AI 总控助手需要主窗体提供的能力，均可在后台线程调用。</summary>
    public interface IAgentHost
    {
        IList<VsInstance> Instances { get; }
        string NameOf(VsInstance v);
        /// <summary>用户（或助手）为该 VS 记录的职责描述，没有时返回 null。</summary>
        string NoteOf(VsInstance v);
        Task<string> SetNote(VsInstance v, string note);
        Task<ChatTranscript> ReadChat(VsInstance v, int maxMessages);
        /// <summary>只把任务加入界面清单，统一调度后通知助手，禁止直发。/ Enqueue in the UI task list only; dispatch and notification are centralized, never send directly.</summary>
        Task<string> QueueTask(VsInstance v, string text);
        Task<string> ListTasks();
        Task<string> CancelTask(int id);
        Task<string> DebugAction(VsInstance v, string action);
        Task<string> InvokeChatButton(VsInstance v, string automationId, string name);
        Task<string> Activate(VsInstance v);
        Task<string> ErrorList(VsInstance v, int max);
        /// <summary>审批模式下请求用户确认。</summary>
        Task<bool> Confirm(string title, string detail);
        Task<string> DockPanes();
        /// <summary>解决方案登记表。/ The solution registry.</summary>
        SolutionRegistry Solutions { get; }
        /// <summary>已打开该登记解决方案的 VS，未打开时返回 null。/ The VS that has the registered solution open; null when not open.</summary>
        VsInstance FindOpenSolution(SolutionEntry e);
        /// <summary>暂存任务（目标未打开，状态「等待目标 VS」），打开后自动推送。返回给模型的说明文字。/ Parks a task until the target opens; returns text for the model.</summary>
        Task<string> ParkTask(SolutionEntry e, string text);
        /// <summary>启动 VS 打开解决方案，返回错误信息（null 表示已启动）。/ Starts VS with the solution; returns the error (null = started).</summary>
        Task<string> LaunchSolution(string path);
        /// <summary>关闭前检查，返回拒绝原因（null 表示可以关闭）。/ Pre-close check; returns the refusal reason (null = can close).</summary>
        Task<string> CheckCanClose(VsInstance v);
        /// <summary>温和关闭 VS（不强制结束进程），返回结果文字。/ Closes VS gently (never kills the process); returns the result text.</summary>
        Task<string> CloseVs(VsInstance v);
    }

    public sealed class AgentPreset
    {
        public string Name, Endpoint, Model, Models;
        public override string ToString() => Name;
    }

    public static class AgentPresets
    {
        public static readonly AgentPreset[] All =
        {
            new AgentPreset { Name = "DeepSeek", Endpoint = "https://api.deepseek.com", Model = "deepseek-flash", Models = "deepseek-flash（快速） / deepseek-v4-pro（更强）" },
            new AgentPreset { Name = "火山方舟 · 豆包", Endpoint = "https://ark.cn-beijing.volces.com/api/v3", Model = "doubao-seed-1-6-250615" },
            new AgentPreset { Name = "通义千问", Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1", Model = "qwen-plus" },
            new AgentPreset { Name = "Kimi（月之暗面）", Endpoint = "https://api.moonshot.cn/v1", Model = "moonshot-v1-32k" },
            new AgentPreset { Name = "OpenAI", Endpoint = "https://api.openai.com/v1", Model = "gpt-4o-mini" },
            new AgentPreset { Name = "本地 Ollama", Endpoint = "http://localhost:11434/v1", Model = "qwen2.5:7b" },
        };

        public static AgentPreset Default => All[0];

        public static AgentPreset Find(string endpoint)
        {
            string e = (endpoint ?? "").Trim().TrimEnd('/');
            var exact = All.FirstOrDefault(p => string.Equals(p.Endpoint.TrimEnd('/'), e, StringComparison.OrdinalIgnoreCase));
            if (exact != null || !Uri.TryCreate(e, UriKind.Absolute, out var u)) return exact;
            return All.FirstOrDefault(p => string.Equals(new Uri(p.Endpoint).Host, u.Host, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsDeepSeek(string endpoint) =>
            Uri.TryCreate((endpoint ?? "").Trim(), UriKind.Absolute, out var u) && u.Host.EndsWith("deepseek.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 侧边栏 AI 总控助手：基于 Microsoft.Extensions.AI（OpenAI 兼容接口）流式对话，
    /// 通过函数调用管理所有 VS 实例（查看状态、发布 Copilot 任务、等待结果、调试 / 生成）。
    /// RunAsync 必须在界面线程调用，Changed 事件也在界面线程触发。
    /// </summary>
    public sealed partial class AgentService : IDisposable, IRestartableAgent
    {
        /// <summary>
        /// 长期约束（写在代码中，重启后始终生效）：VSManager 是开源项目，对外内容不得透露个人信息，文档与注释中英双语。
        /// Permanent policy (kept in code so it survives restarts): VSManager is open source; public content must not reveal
        /// personal information, and documentation / code comments are bilingual (Chinese first, then English).
        /// </summary>
        public const string OpenSourcePolicy =
            "VSManager 是开源项目：任何对外内容（README、文档、代码注释、提交信息、发布说明）都不得透露个人信息——真实姓名、邮箱、机器名、用户名、本机盘符路径、公司 / 客户信息（示例路径用 %APPDATA% 等环境变量或通用示例代替）；" +
            "所有开源说明文档与代码注释一律提供中英两份（中文在前、英文在后，或并列呈现）。";

        /// <summary>发给 VSManager 项目的任务末尾自动附加的约束。/ Constraint appended to every task sent to the VSManager project.</summary>
        public const string OpenSourceTaskSuffix = "【开源约束】" + OpenSourcePolicy;

        /// <summary>长期约束的英文版（英文模式使用）。/ English version of the permanent policy (used in English mode).</summary>
        public const string OpenSourcePolicyEn =
            "VSManager is an open-source project: no public content (README, docs, code comments, commit messages, release notes) may reveal personal information - real names, e-mail addresses, machine names, user names, local drive paths, company / customer information (use environment variables such as %APPDATA% or generic examples for sample paths); " +
            "all open-source documentation and code comments must be provided in both Chinese and English (Chinese first, then English, or side by side).";

        /// <summary>英文模式下附加的约束。/ Constraint appended in English mode.</summary>
        public const string OpenSourceTaskSuffixEn = " [Open-source constraint] " + OpenSourcePolicyEn;
        private static readonly string[] DebugActions = { "go", "run", "break", "stop", "restart", "build", "rebuild", "cancelbuild" };

        // AI 额度：每次使用时读取当前设置，修改后立即生效
        private int Quota(string name, int value) => AppSettings.ClampQuota(name, value);
        private int MaxToolText => Quota(nameof(AppSettings.AgentMaxToolText), _settings().AgentMaxToolText);
        private int MaxMessageText => Quota(nameof(AppSettings.AgentMaxMessageText), _settings().AgentMaxMessageText);
        private int MaxTaskText => Quota(nameof(AppSettings.AgentMaxTaskText), _settings().AgentMaxTaskText);
        private int MaxOutputTokens => Quota(nameof(AppSettings.AgentMaxOutputTokens), _settings().AgentMaxOutputTokens);
        private int MaxHistory => Quota(nameof(AppSettings.AgentMaxHistory), _settings().AgentMaxHistory);
        private int MaxIterations => Quota(nameof(AppSettings.AgentMaxIterations), _settings().AgentMaxIterations);
        private int MaxFileLines => Quota(nameof(AppSettings.AgentMaxFileLines), _settings().AgentMaxFileLines);
        private int MaxReadCount => Quota(nameof(AppSettings.AgentMaxReadCount), _settings().AgentMaxReadCount);
        private int MaxNoteText => Math.Max(100, MaxTaskText / 3);

        private readonly IAgentHost _host;
        private readonly Func<AppSettings> _settings;
        private readonly List<AIMessage> _history = new List<AIMessage>();
        private readonly List<AITool> _tools;
        private IChatClient _client;
        private string _clientKey;
        private CancellationTokenSource _cts;

        public ChatTranscript Transcript { get; } = new ChatTranscript { PaneFound = true, Title = "AI 助手" };
        public bool Running { get; private set; }
        public string Activity { get; private set; } = "";
        public event Action Changed;

        #region 自动重启支持 / Auto-restart support

        /// <summary>AI 助手运行日志文件（%APPDATA%\VSManager\logs\）。/ Assistant runtime log file (under %APPDATA%\VSManager\logs\).</summary>
        public const string LogFile = "agent.log";

        /// <summary>
        /// 助手内部出现需要重建的故障（未处理异常、请求连续失败）时触发，在界面线程调用。
        /// Raised on the UI thread when the assistant hits a fault that calls for a rebuild (unhandled error, repeated request failures).
        /// </summary>
        public event Action<AgentFaultKind, string> Faulted;

        /// <summary>每次重建加 1；进行中的旧一轮对话发现代数变化后不再修改状态。/ Bumped on every rebuild; a stale run stops touching state once it changes.</summary>
        private int _generation;
        private long _lastProgressTicks = DateTime.Now.Ticks;
        private int _awaitingUser;

        /// <summary>连续失败的模型请求数（连接失败 / 超时 / 5xx），成功后清零。/ Consecutive failed model requests (connection / timeout / 5xx); reset on success.</summary>
        public int ConsecutiveFailures { get; private set; }

        /// <summary>最近一次有进展的时间（收到模型输出、工具心跳）。/ Time of the last progress (model output or a tool heartbeat).</summary>
        public DateTime LastProgress => new DateTime(Interlocked.Read(ref _lastProgressTicks));

        /// <summary>是否正在等待用户确认（此时不算无响应）。/ Whether it is waiting for the user to confirm (not a hang).</summary>
        public bool AwaitingUser => Volatile.Read(ref _awaitingUser) > 0;

        /// <summary>本次启动以来的重建次数。/ Rebuilds since launch.</summary>
        public int RestartCount { get; private set; }

        private void Touch() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.Now.Ticks);

        private static void Log(string text) => AppLog.Write(LogFile, text);

        private void RaiseFault(AgentFaultKind kind, string message)
        {
            try { Faulted?.Invoke(kind, message); }
            catch (Exception ex) { AppLog.Error(LogFile, "处理助手故障失败 / Fault handler failed", ex); }
        }

        /// <summary>
        /// 重建助手：停止并丢弃进行中的一轮对话、释放并重新创建模型客户端、复位运行状态；
        /// 保留界面对话、上下文与待处理的任务通知，重建后立即可用。必须在界面线程调用。
        /// Rebuilds the assistant: stops and abandons the current run, disposes and recreates the model client and resets the
        /// running state; the transcript, context and pending task notices are kept, so it is usable right away. UI thread only.
        /// </summary>
        public void Restart(string reason)
        {
            _generation++;
            bool wasRunning = Running;
            var cts = _cts;
            _cts = null;
            try { cts?.Cancel(); } catch { }
            var old = _client;
            _client = null;
            _clientKey = null;
            try { old?.Dispose(); } catch { }
            Running = false;
            Activity = "";
            ConsecutiveFailures = 0;
            Interlocked.Exchange(ref _awaitingUser, 0);
            Touch();
            RestartCount++;

            // 被打断的一轮：补一条说明，避免上下文以无回复的用户消息结尾 / Interrupted round: add a note so the context does not end with an unanswered user message
            if (_history.Count > 0 && _history[_history.Count - 1].Role == AIRole.User)
                _history.Add(new AIMessage(AIRole.Assistant, "（上一轮因 AI 助手重启而中断 / The previous round was interrupted by an assistant restart）"));

            string note = "⟳ AI 助手已重启 / AI assistant restarted：" + reason;
            var last = Transcript.Messages.LastOrDefault();
            if (wasRunning && last != null && last.Role == ChatRole.Assistant) last.Parts.Add(new ChatPart { Text = "\n\n" + note });
            else
            {
                var m = new ChatMessage { Role = ChatRole.Assistant };
                m.Parts.Add(new ChatPart { Text = note });
                Transcript.Messages.Add(m);
                TrimTranscript();
            }
            Record("notice", note);
            Log("重建 AI 助手 / Assistant rebuilt（第 " + RestartCount + " 次 / #" + RestartCount + "）：" + reason + (wasRunning ? "；已中止进行中的一轮 / the running round was aborted" : ""));
            Changed?.Invoke();
            if (_notices.Count > 0) ProcessNotices();
        }

        /// <summary>请求用户确认；等待期间不计入无响应超时。/ Asks the user to confirm; the wait does not count toward the hang timeout.</summary>
        private async Task<bool> ConfirmAsync(string title, string detail)
        {
            Interlocked.Increment(ref _awaitingUser);
            try { return await _host.Confirm(title, detail); }
            finally
            {
                if (Interlocked.Decrement(ref _awaitingUser) < 0) Interlocked.Exchange(ref _awaitingUser, 0);
                Touch();
            }
        }

        /// <summary>
        /// 是否属于重建客户端可能恢复的请求失败：连接失败、超时、408 / 5xx。Key 无效、404、429 等配置或额度问题不计入。
        /// Whether a request failure may be cured by a new client: connection failure, timeout, 408 / 5xx. Configuration or quota
        /// problems such as an invalid key, 404 or 429 are not counted.
        /// </summary>
        internal static bool IsTransientRequestFailure(Exception ex)
        {
            var e = ex is AggregateException ag && ag.InnerException != null ? ag.InnerException : ex;
            if (e is System.ClientModel.ClientResultException cr) return cr.Status == 0 || cr.Status == 408 || cr.Status >= 500;
            return e is System.Net.Http.HttpRequestException || e is System.Net.WebException || e is TimeoutException ||
                   e is System.IO.IOException || e is System.Net.Sockets.SocketException;
        }

        #endregion

        private readonly IAiClientFactory _clientFactory;

        public AgentService(IAgentHost host, Func<AppSettings> settings) : this(host, settings, null) { }

        /// <summary>可替换 AI 客户端工厂的构造函数（用于测试）。/ Constructor with a replaceable AI client factory (for tests).</summary>
        public AgentService(IAgentHost host, Func<AppSettings> settings, IAiClientFactory clientFactory)
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            _host = host;
            _settings = settings;
            _clientFactory = clientFactory ?? OpenAiClientFactory.Instance;
            _tools = new List<AITool>
            {
                AIFunctionFactory.Create((Func<string>)ListVs, "list_vs"),
                AIFunctionFactory.Create((Func<string, int, Task<string>>)ReadVsChat, "read_vs_chat"),
                AIFunctionFactory.Create((Func<string, string, string, Task<string>>)SendTask, "send_task"),
                AIFunctionFactory.Create((Func<string, int, CancellationToken, Task<string>>)WaitForVs, "wait_for_vs"),
                AIFunctionFactory.Create((Func<string, string, Task<string>>)DebugVs, "debug_vs"),
                AIFunctionFactory.Create((Func<string, int, Task<string>>)GetErrors, "get_errors"),
                AIFunctionFactory.Create((Func<string, Task<string>>)StopCopilot, "stop_copilot"),
                AIFunctionFactory.Create((Func<string, Task<string>>)NewCopilotThread, "new_copilot_thread"),
                AIFunctionFactory.Create((Func<string, Task<string>>)ActivateVs, "activate_vs"),
                AIFunctionFactory.Create((Func<Task<string>>)DockPanes, "dock_copilot_panes"),
                AIFunctionFactory.Create((Func<string, Task<string>>)OpenCopilot, "open_copilot"),
                AIFunctionFactory.Create((Func<string, string, Task<string>>)SetVsNote, "set_vs_note"),
                AIFunctionFactory.Create((Func<Task<string>>)ListTasks, "list_tasks"),
                AIFunctionFactory.Create((Func<int, Task<string>>)CancelTask, "cancel_task"),
                AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<string>>)ScanVsCode, "scan_vs_code"),
                AIFunctionFactory.Create((Func<string, string, string, Task<string>>)RequestImprovement, "request_vsmanager_improvement"),
                AIFunctionFactory.Create((Func<string, string, int, string, CancellationToken, Task<string>>)ReadVsFile, "read_vs_file"),
                AIFunctionFactory.Create((Func<string>)ListSolutions, "list_solutions"),
                AIFunctionFactory.Create((Func<string, CancellationToken, Task<string>>)OpenSolution, "open_solution"),
                AIFunctionFactory.Create((Func<string, Task<string>>)CloseVs, "close_vs"),
                AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<string>>)CaptureVsScreenshot, "capture_vs_screenshot"),
                AIFunctionFactory.Create((Func<string, string, bool, int, int, CancellationToken, Task<string>>)FindFiles, "find_files"),
                AIFunctionFactory.Create((Func<string, string, string, bool, int, int, int, CancellationToken, Task<string>>)SearchFileContents, "search_file_contents"),
                AIFunctionFactory.Create((Func<string, int, int, CancellationToken, Task<string>>)ReadFile, "read_file"),
                AIFunctionFactory.Create((Func<string, int, CancellationToken, Task<string>>)ListDirectory, "list_directory"),
            };
        }

        public string ModelName => (_settings().AgentModel ?? "").Trim();
        public string ProviderName => AgentPresets.Find(_settings().AgentEndpoint)?.Name ?? "自定义接口";

        public bool Configured
        {
            get
            {
                var s = _settings();
                return !string.IsNullOrWhiteSpace(s.AgentEndpoint) && !string.IsNullOrWhiteSpace(s.AgentModel) &&
                       (!string.IsNullOrWhiteSpace(s.EffectiveAgentApiKey) || IsLocal(s.AgentEndpoint));
            }
        }

        private static bool IsLocal(string endpoint) =>
            Uri.TryCreate((endpoint ?? "").Trim(), UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");

        private IChatClient Client()
        {
            var s = _settings();
            string endpoint = (s.AgentEndpoint ?? "").Trim(), model = (s.AgentModel ?? "").Trim(), key = (s.EffectiveAgentApiKey ?? "").Trim();
            if (endpoint.Length == 0 || model.Length == 0) throw new InvalidOperationException("请先在「属性 → AI 助手」中填写接口地址和模型");
            if (key.Length == 0)
            {
                if (!IsLocal(endpoint)) throw new InvalidOperationException("请先在「属性 → AI 助手」中填写 API Key");
                key = "local";
            }
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw new InvalidOperationException("接口地址无效：" + endpoint);
            int iterations = MaxIterations;
            string ck = endpoint + "|" + model + "|" + key + "|" + iterations;
            if (_client != null && ck == _clientKey) return _client;
            _client?.Dispose();
            var inner = _clientFactory.Create(uri, model, key);
            _client = new ChatClientBuilder(inner)
                .UseFunctionInvocation(null, f =>
                {
                    f.MaximumIterationsPerRequest = iterations;
                    f.AllowConcurrentInvocation = false;
                    f.IncludeDetailedErrors = true;
                })
                .Build();
            _clientKey = ck;
            return _client;
        }

        #region 对话

        /// <summary>用给定配置发送一条简短请求，返回 null 表示成功。</summary>
        public static async Task<string> TestAsync(string endpoint, string model, string key)
        {
            var s = new AppSettings { AgentEndpoint = endpoint, AgentModel = model, AgentApiKey = key };
            var tmp = new AgentService(null, () => s);
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    var r = await tmp.Client().GetResponseAsync(
                        new List<AIMessage> { new AIMessage(AIRole.User, "请只回复：OK") },
                        new ChatOptions { MaxOutputTokens = 256 }, cts.Token).ConfigureAwait(false);
                    return r.Messages.Count > 0 ? null : "模型没有返回内容";
                }
            }
            catch (OperationCanceledException) { return "请求超时（60 秒）"; }
            catch (Exception ex) { return Friendly(ex); }
            finally { tmp.Dispose(); }
        }

        /// <summary>
        /// 用已配置的模型把 Copilot 回答概括为不超过 30 字（英文 30 词）的播报语（不带工具、不写入对话记录）。
        /// Summarize a Copilot answer into a ≤30-character (Chinese) / ≤30-word (English) announcement (no tools, not recorded).
        /// </summary>
        public Task<string> SummarizeAsync(string answer, CancellationToken ct) => SummarizeAsync(answer, false, ct);

        public async Task<string> SummarizeAsync(string answer, bool english, CancellationToken ct)
        {
            var r = await Client().GetResponseAsync(new List<AIMessage>
            {
                new AIMessage(AIRole.System, Prompts.VoiceSummary(english)),
                new AIMessage(AIRole.User, answer)
            }, new ChatOptions { MaxOutputTokens = 120, Temperature = 0.2f }, ct).ConfigureAwait(false);
            return r.Text;
        }

        public void Stop() => _cts?.Cancel();

        public void Clear()
        {
            _cts?.Cancel();
            _history.Clear();
            lock (_attachments) _attachments.Clear();
            Transcript.Messages.Clear();
            Changed?.Invoke();
        }

        private readonly Queue<KeyValuePair<string, string>> _notices = new Queue<KeyValuePair<string, string>>();

        /// <summary>
        /// 界面对话最多保留的消息条数（完整记录已写入归档 chat\ai-*.jsonl，不受影响）。
        /// Max messages kept in the on-screen transcript (the full record is in the chat\ai-*.jsonl archive).
        /// </summary>
        public const int MaxTranscriptMessages = 200;

        private void TrimTranscript()
        {
            var list = Transcript.Messages;
            int extra = list.Count - MaxTranscriptMessages;
            if (extra > 0) list.RemoveRange(0, extra);
        }

        /// <summary>
        /// 系统通知（如任务完成）：开启自动跟进时作为一轮对话交给模型处理；否则只记入对话与上下文。
        /// 必须在界面线程调用；助手正在运行时排队，结束后依次处理。
        /// </summary>
        public void Notify(string display, string content)
        {
            _notices.Enqueue(new KeyValuePair<string, string>(display, content));
            if (!Running) ProcessNotices();
        }

        /// <summary>仅本机展示并记录，不加入模型上下文、不触发自动跟进；由 UI 线程调用。/ Display and record locally only, without model context or automatic follow-up; call on the UI thread.</summary>
        internal void ShowLocalNotice(string display, string content)
        {
            var message = new ChatMessage { Role = ChatRole.Assistant };
            message.Parts.Add(new ChatPart { Text = display + "\n" + content });
            Transcript.Messages.Add(message);
            TrimTranscript();
            Record("notice", display, content);
            Changed?.Invoke();
        }

        private async void ProcessNotices()
        {
            // async void 中的异常会抛到界面线程：在这里捕获并交给自动重启 / Exceptions in async void reach the UI thread: catch them here and hand over to auto-restart
            try
            {
                await Task.Delay(400);
                while (!Running && _notices.Count > 0)
                {
                    var n = _notices.Dequeue();
                    if (Configured && _settings().AgentAutoFollowUp) { await RunAsync(n.Value, n.Key); continue; }
                    var m = new ChatMessage { Role = ChatRole.Assistant };
                    m.Parts.Add(new ChatPart { Text = n.Key });
                    Transcript.Messages.Add(m);
                    TrimTranscript();
                    Record("notice", n.Key, n.Value);
                    _history.Add(new AIMessage(AIRole.User, n.Value));
                    TrimHistory();
                    Changed?.Invoke();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(LogFile, "处理任务通知时出错 / Error while processing notices", ex);
                RaiseFault(AgentFaultKind.Exception, ex.GetType().Name + "：" + OneLine(ex.Message, 160));
            }
        }

        /// <summary>
        /// 执行一轮对话。内部未处理异常不会抛出：记录日志、复位状态并触发 <see cref="Faulted"/>。
        /// Runs one round. Internal unhandled exceptions are not thrown: they are logged, the state is reset and <see cref="Faulted"/> is raised.
        /// </summary>
        public async Task RunAsync(string text, string display = null, IReadOnlyList<AttachmentRef> attachments = null)
        {
            int gen = _generation;
            try { await RunCoreAsync(text, display, gen, attachments); }
            catch (Exception ex)
            {
                AppLog.Error(LogFile, "对话内部异常 / Internal error in a round", ex);
                if (gen != _generation) return;
                Running = false;
                Activity = "";
                try { Changed?.Invoke(); } catch { }
                RaiseFault(AgentFaultKind.Exception, ex.GetType().Name + "：" + OneLine(ex.Message, 160));
            }
        }

        private async Task RunCoreAsync(string text, string display, int gen, IReadOnlyList<AttachmentRef> attachments = null)
        {
            text = (text ?? "").Trim();
            var files = (attachments ?? new AttachmentRef[0]).Where(a => a != null).ToArray();
            if (Running || (text.Length == 0 && files.Length == 0)) return;
            if (files.Length > 0) RememberAttachments(files);
            string links = AttachmentLinks(files);
            var user = new ChatMessage { Role = ChatRole.User };
            user.Parts.Add(new ChatPart { Text = (string.IsNullOrWhiteSpace(display) ? text : display) + links });
            var reply = new ChatMessage { Role = ChatRole.Assistant };
            Transcript.Messages.Add(user);
            Transcript.Messages.Add(reply);
            TrimTranscript();
            if (string.IsNullOrWhiteSpace(display)) Record("user", text + links);
            else Record("notice", display, text);

            IChatClient client;
            try { client = Client(); }
            catch (Exception ex)
            {
                AddText(reply, "⚠ " + ex.Message);
                Record("assistant", "", null, null, ex.Message);
                Changed?.Invoke();
                return;
            }

            Running = true;
            Activity = "思考中…";
            Touch();
            var cts = _cts = new CancellationTokenSource();
            Changed?.Invoke();

            _history.Add(new AIMessage(AIRole.User, files.Length > 0 ? ModelMessage(text, files) : text));
            var updates = new List<ChatResponseUpdate>();
            var calls = new Dictionary<string, string>();
            string error = null;
            Exception internalError = null;
            bool transientFailure = false;
            try
            {
                var messages = new List<AIMessage> { new AIMessage(AIRole.System, SystemPrompt()) };
                messages.AddRange(_history);
                var options = new ChatOptions { Tools = _tools, ToolMode = ChatToolMode.Auto, Temperature = 0.3f };
                int maxOut = MaxOutputTokens;
                if (maxOut > 0) options.MaxOutputTokens = maxOut;
                await foreach (var u in client.GetStreamingResponseAsync(messages, options, cts.Token))
                {
                    // 助手已重建：丢弃旧一轮的输出 / The assistant was rebuilt: drop the stale round's output
                    if (gen != _generation) break;
                    Touch();
                    updates.Add(u);
                    foreach (var c in u.Contents)
                    {
                        if (c is TextContent t && !string.IsNullOrEmpty(t.Text))
                        {
                            AddText(reply, t.Text);
                            Activity = "正在回复…";
                        }
                        else if (c is FunctionCallContent fc)
                        {
                            string step = DescribeCall(fc);
                            if (!string.IsNullOrEmpty(fc.CallId)) calls[fc.CallId] = step;
                            AddStep(reply, "⚙ " + step);
                            Activity = step + "…";
                        }
                        else if (c is FunctionResultContent fr)
                        {
                            string result = OneLine(fr.Exception?.Message ?? fr.Result?.ToString(), 160);
                            AddStep(reply, "↳ " + (result.Length == 0 ? "完成" : result));
                            Activity = "思考中…";
                        }
                    }
                    Changed?.Invoke();
                }
                if (gen == _generation) _history.AddMessages(updates);
            }
            catch (OperationCanceledException) { error = "已停止"; transientFailure = !cts.IsCancellationRequested; }
            catch (Exception ex)
            {
                error = Friendly(ex);
                transientFailure = IsTransientRequestFailure(ex);
                // 请求失败之外的异常视为助手内部故障 / Anything other than a request failure is an internal fault
                if (!transientFailure && !(ex is System.ClientModel.ClientResultException) && !(ex.InnerException is System.ClientModel.ClientResultException)) internalError = ex;
                AppLog.Error(LogFile, "模型请求失败 / Model request failed", ex);
            }
            finally
            {
                if (gen != _generation)
                {
                    // 已被重建取代：只释放资源，不再修改状态 / Superseded by a rebuild: release resources only, never touch state
                    if (_cts == cts) _cts = null;
                    cts.Dispose();
                }
                else
                {
                if (error != null)
                {
                    // 中断时只保留已生成的文字，避免把不完整的函数调用留在历史中
                    string partial = string.Concat(updates.Select(x => x.Text));
                    if (partial.Length > 0) _history.Add(new AIMessage(AIRole.Assistant, partial + "\n（" + error + "）"));
                    AddStep(reply, "⚠ " + error);
                }
                StripLeakedToolMarkup(reply);
                if (reply.Parts.Count == 0) AddText(reply, "（没有返回内容）");
                try
                {
                    Record("assistant",
                        string.Join("\n\n", reply.Parts.Where(p => !p.IsStep && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text.Trim())), null,
                        reply.Parts.Where(p => p.IsStep && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text.Trim()).ToList(), error);
                }
                catch { }
                Running = false;
                Activity = "";
                if (_cts == cts) _cts = null;
                cts.Dispose();
                TrimHistory();
                ConsecutiveFailures = error == null ? 0 : transientFailure ? ConsecutiveFailures + 1 : ConsecutiveFailures;
                Changed?.Invoke();
                if (_notices.Count > 0) ProcessNotices();
                }
            }
            if (gen != _generation) return;
            if (internalError != null)
                RaiseFault(AgentFaultKind.Exception, internalError.GetType().Name + "：" + OneLine(internalError.Message, 160));
            else if (transientFailure)
            {
                int threshold = _settings().AgentFailureThreshold;
                if (threshold <= 0) threshold = AppSettings.DefaultAgentFailureThreshold;
                if (ConsecutiveFailures >= threshold)
                    RaiseFault(AgentFaultKind.RequestFailures, "模型请求连续失败 " + ConsecutiveFailures + " 次 / " + ConsecutiveFailures + " consecutive request failures（" + error + "）");
            }
        }

        /// <summary>
        /// 同时写入归档（chat\ai-*.jsonl，可关闭）与本机对话记录（agent-chat.jsonl，供「对话记录」窗口查看）。
        /// Writes to both the archive (chat\ai-*.jsonl, optional) and the local chat history (agent-chat.jsonl, shown in the history window).
        /// </summary>
        private static void Record(string role, string content, string detail = null, IList<string> steps = null, string error = null)
        {
            try { Archive.Ai(role, content, detail, steps, error); } catch { }
            try { AgentChatLog.Append(role, content, detail, steps, error); } catch { }
        }

        private void TrimHistory()
        {
            // 保留最近 AgentMaxHistory 条消息，并确保不从工具结果中间截断
            int maxHistory = MaxHistory;
            while (_history.Count > maxHistory)
            {
                _history.RemoveAt(0);
                while (_history.Count > 0 && _history[0].Role != AIRole.User) _history.RemoveAt(0);
            }
        }

        /// <summary>
        /// 工具调用轮数用尽时，部分模型（如 DeepSeek）会把函数调用标记当作正文输出，去掉这些标记并提示用户。
        /// </summary>
        private static void StripLeakedToolMarkup(ChatMessage m)
        {
            bool leaked = false;
            foreach (var p in m.Parts.Where(x => !x.IsStep && x.Text != null).ToList())
            {
                int i = p.Text.IndexOf("DSML", StringComparison.Ordinal);
                if (i < 0) i = p.Text.IndexOf("<tool_call", StringComparison.Ordinal);
                if (i < 0) continue;
                int lt = p.Text.LastIndexOf('<', i);
                p.Text = p.Text.Substring(0, lt >= 0 ? lt : i).TrimEnd();
                if (p.Text.Length == 0) m.Parts.Remove(p);
                leaked = true;
            }
            if (leaked) m.Parts.Add(new ChatPart { IsStep = true, Text = "⚠ 本轮工具调用次数已达上限，已暂停；回复「继续」可接着执行" });
        }

        private static void AddText(ChatMessage m, string text)
        {
            var last = m.Parts.LastOrDefault();
            if (last != null && !last.IsStep) last.Text += text;
            else m.Parts.Add(new ChatPart { Text = text });
        }

        private static void AddStep(ChatMessage m, string text) => m.Parts.Add(new ChatPart { IsStep = true, Text = text });

        private string DescribeCall(FunctionCallContent fc)
        {
            string Arg(string k) => fc.Arguments != null && fc.Arguments.TryGetValue(k, out var v) && v != null ? v.ToString() : "";
            string vs = Arg("vs");
            string target = vs.Length > 0 ? "「" + TargetName(vs) + "」" : "";
            switch (fc.Name)
            {
                case "list_vs": return "查看所有 VS 状态";
                case "read_vs_chat": return "读取" + target + "的 Copilot 对话";
                case "send_task": return "向" + target + "发布任务：" + OneLine(Arg("task"), 60)
                    + (string.IsNullOrWhiteSpace(Arg("attachments")) ? "" : "（附件 / attachments：" + OneLine(Arg("attachments"), 40) + "）");
                case "wait_for_vs": return "等待" + target + "的 Copilot 完成";
                case "debug_vs": return target + "执行 " + ActionName(Arg("action"));
                case "get_errors": return "读取" + target + "的错误列表";
                case "stop_copilot": return "停止" + target + "的 Copilot";
                case "new_copilot_thread": return target + "新建 Copilot 线程";
                case "activate_vs": return "切换到" + target;
                case "dock_copilot_panes": return "把 Copilot 切换为工具窗模式";
                case "open_copilot": return "打开" + target + "的对话助手 / Open Copilot chat";
                case "request_vsmanager_improvement": return "请 VSManager 完善助手能力：" + OneLine(Arg("capability"), 50);
                case "list_tasks": return "查看任务清单";
                case "cancel_task": return "取消任务 #" + Arg("id");
                case "scan_vs_code": return "扫描授权文件元数据 / Scan granted file metadata";
                case "read_vs_file":
                case "read_file": return "读取并脱敏授权文件 / Read and redact granted file";
                case "find_files": return "查找授权文件名 / Find granted filenames";
                case "search_file_contents": return "搜索已脱敏文件内容 / Search redacted file contents";
                case "list_directory": return "列出授权目录 / List granted directory";
                case "set_vs_note": return "记录" + target + "的职责：" + OneLine(Arg("note"), 40);
                case "list_solutions": return "查看解决方案登记表 / List registered solutions";
                case "open_solution": return "打开解决方案「" + OneLine(Arg("solution"), 60) + "」/ Open solution";
                case "close_vs": return "关闭 VS「" + OneLine(Arg("target"), 60) + "」/ Close VS";
                case "capture_vs_screenshot": return "截图分析" + target + "（需预览批准）/ Screenshot analysis (approval required)";
                default: return fc.Name;
            }
        }

        private string TargetName(string vs) => Resolve(vs, out var v, out _) ? "#" + (Index(v) + 1) + " " + _host.NameOf(v) : vs;

        private static string Friendly(Exception ex)
        {
            var e = ex is AggregateException ag && ag.InnerException != null ? ag.InnerException : ex;
            if (e is System.ClientModel.ClientResultException cr)
            {
                switch (cr.Status)
                {
                    case 401: case 403: return $"API Key 无效或无权访问该模型（{cr.Status}）";
                    case 404: return "接口地址或模型名称不存在（404），请检查「属性 → AI 助手」";
                    case 429: return "请求过于频繁或额度不足（429）";
                    case 0:
                        var inner = cr.InnerException;
                        while (inner?.InnerException != null) inner = inner.InnerException;
                        return "无法连接到模型服务：" + OneLine(inner?.Message ?? cr.Message, 200);
                }
                return $"模型服务返回错误（{cr.Status}）：" + OneLine(cr.Message, 200);
            }
            if (e is TimeoutException) return "请求超时";
            return OneLine(e.Message, 240);
        }

        /// <summary>按语音语言选择整套中文或英文系统提示词（Prompts.AgentSystem）。/ Pick the full Chinese or English system prompt by voice language.</summary>
        private string SystemPrompt()
        {
            var s = _settings();
            return Prompts.AgentSystem(s.IsEnglishVoice, DateTime.Now, ListVs(), s.AgentInstructions, _host.Solutions.Count > 0 ? ListSolutions() : null, s.SkipFailedPredecessors);
        }

        #endregion

        #region 工具

        [Description("列出所有正在运行的 Visual Studio 实例：编号、名称、职责描述、打开的解决方案（及登记别名）、Copilot 状态、调试 / 生成状态。")]
        private string ListVs()
        {
            var list = _host.Instances.ToList();
            if (list.Count == 0) return "当前没有正在运行的 Visual Studio。";
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                sb.Append('#').Append(i + 1).Append(' ').Append(_host.NameOf(v));
                string note = _host.NoteOf(v);
                if (!string.IsNullOrEmpty(note)) sb.Append(" | 职责：").Append(OneLine(note, MaxNoteText));
                if (!string.IsNullOrEmpty(v.SolutionPath)) sb.Append(" | 解决方案：").Append(v.SolutionPath);
                var reg = _host.Solutions.Items.FirstOrDefault(e => _host.FindOpenSolution(e) == v);
                if (reg != null) sb.Append(" | 登记别名：").Append(reg.Alias);
                sb.Append(" | Copilot：").Append(CopilotText(v));
                sb.Append(" | 调试：").Append(DebugText(v));
                if (v.Building) sb.Append(" | 正在生成");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static string CopilotText(VsInstance v)
        {
            switch (v.Copilot)
            {
                case CopilotState.Busy: return "运行中（已 " + Duration(DateTime.Now - v.BusySince) + "）";
                case CopilotState.Idle:
                    return v.CompletedAt.HasValue
                        ? $"空闲（{v.CompletedAt.Value:HH:mm} 完成上一个任务，用时 {Duration(v.LastDuration)}）"
                        : "空闲";
                default: return "未检测到 Copilot 对话窗格";
            }
        }

        private static string DebugText(VsInstance v)
        {
            if (v.Dte == null) return "未知";
            switch (v.DebugMode)
            {
                case 1: return "未调试";
                case 2: return "已中断";
                case 3: return "调试运行中";
                default: return "未知";
            }
        }

        [Description("读取指定 VS 中 Copilot 对话的最近消息，用于了解其当前任务与结果。")]
        private async Task<string> ReadVsChat(
            [Description("VS 编号（如 \"1\"）或名称")] string vs,
            [Description("读取最近几条消息，默认 6（上限见「属性 → AI 额度」）")] int count = 6)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            var t = await _host.ReadChat(v, Math.Max(1, Math.Min(MaxReadCount, count)));
            return Format(t, MaxToolText, MaxMessageText);
        }

        [Description("只把任务加入界面任务清单，始终按编号排队，绝不直发或插队；目标未打开时暂存，任务结束后通知助手。Only enqueue in the visible task list, always in ID order; never send directly or jump the queue. Park tasks for closed targets and report their outcome.")]
        private async Task<string> SendTask(
            [Description("VS 编号（如 \"1\"）、名称，或登记的解决方案别名")] string vs,
            [Description("仅梳理语言的中文任务描述，单段不换行；保持原意与全部明确约束，不新增要求、验收标准、技术方案或范围，不把疑问改成命令；意图不完整先确认。Chinese task text with language cleanup only, one paragraph without line breaks; preserve intent and every explicit constraint, add no requirements, acceptance criteria, technical solutions or scope, and never turn questions into commands; clarify incomplete intent first.")] string task,
            [Description("可选：随任务发送的用户附件编号，逗号分隔；\"last\" 表示用户最近一条消息的全部附件。图片会粘贴到目标 Copilot，文本文件内联到正文，其他文件发送路径。Optional: ids of user attachments to send with the task, comma-separated; \"last\" means all attachments of the user's latest message. Images are pasted into the target Copilot, text files inlined, other files sent as paths.")] string attachments = null)
        {
            var files = ResolveTaskAttachments(attachments, out string attachmentError);
            if (attachmentError != null) return attachmentError;
            SolutionEntry parkFor = null;
            if (!Resolve(vs, out var v, out var err))
            {
                // 不是正在运行的 VS：按登记表别名解析，已打开则直接使用，未打开则暂存
                // Not a running VS: resolve it as a registry alias; use the VS if open, otherwise park the task
                var hit = LookupSolution(vs, out string lookupError);
                if (hit == null) return _host.Solutions.Count > 0 ? err + "\n" + lookupError : err;
                v = _host.FindOpenSolution(hit);
                if (v == null) parkFor = hit;
            }
            task = (task ?? "").Trim();
            if (task.Length == 0) return "任务内容为空";
            int maxTask = MaxTaskText;
            if (task.Length > maxTask)
                return $"任务文本过长（{task.Length} 字），超过单次任务上限 {maxTask} 字（「属性 → AI 额度」可调整）；请向用户确认处理方式，不得自行删减要求或拆分任务。/ Task text is too long ({task.Length} characters; limit {maxTask}, adjustable in Properties > AI quotas); ask the user how to proceed, without removing requirements or splitting tasks on your own.";
            // 多行消息只能前台粘贴（会短暂切到 VS）；后台模式下合并为一行，保持用户当前界面
            if (_settings().BackgroundSend && task.IndexOf('\n') >= 0)
                task = string.Join(" ", task.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            bool isVsManager = parkFor != null ? IsVsManagerPath(parkFor.Path) : IsVsManager(v);
            if (isVsManager && task.IndexOf("【开源约束】", StringComparison.Ordinal) < 0 && task.IndexOf("[Open-source constraint]", StringComparison.Ordinal) < 0)
                task += _settings().IsEnglishVoice ? OpenSourceTaskSuffixEn : OpenSourceTaskSuffix;
            string targetName = parkFor != null ? parkFor.Alias : _host.NameOf(v);
            string attachmentNote = files.Length == 0 ? "" : "\n\n" + TaskAttachmentNotice(files);
            if (_settings().AgentConfirm && !await ConfirmAsync("发布任务到「" + targetName + "」", task + attachmentNote))
                return "用户拒绝了该操作。";
            if (files.Length > 0)
            {
                if (!(_host is IAgentAttachmentHost attachmentHost))
                    return "当前环境不支持随任务发送附件 / Attachments cannot be sent with tasks in this environment";
                return parkFor != null ? await attachmentHost.ParkTask(parkFor, task, files) : await attachmentHost.QueueTask(v, task, files);
            }
            if (parkFor != null) return await _host.ParkTask(parkFor, task);
            return await _host.QueueTask(v, task);
        }

        [Description("查看任务清单：各任务的编号、目标 VS、状态（排队 / 执行中 / 已完成 / 失败 / 已取消）与结果摘要。")]
        private async Task<string> ListTasks() => Truncate(await _host.ListTasks(), MaxToolText);

        [Description("取消任务清单中排队的任务（执行中的任务只停止跟踪，不会停止 Copilot；需要停止请用 stop_copilot）。")]
        private Task<string> CancelTask([Description("任务编号，如 3")] int id) => _host.CancelTask(id);

        [Description("等待指定 VS 的 Copilot 完成当前任务，返回其最后一条回复。")]
        private async Task<string> WaitForVs(
            [Description("VS 编号（如 \"1\"）或名称")] string vs,
            [Description("最长等待秒数，默认 600")] int timeoutSeconds = 600,
            CancellationToken cancellationToken = default)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            var start = DateTime.Now;
            var deadline = start.AddSeconds(Math.Max(10, Math.Min(3600, timeoutSeconds)));
            bool sawBusy = v.Copilot == CopilotState.Busy;
            while (true)
            {
                Touch();
                if (v.Copilot == CopilotState.Busy) sawBusy = true;
                else if (sawBusy || DateTime.Now - start > TimeSpan.FromSeconds(10)) break;
                if (DateTime.Now > deadline)
                    return $"等待超时：「{_host.NameOf(v)}」的 Copilot 仍在运行（已 {Duration(DateTime.Now - v.BusySince)}）。";
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            var t = await _host.ReadChat(v, 2);
            var last = t?.Messages?.LastOrDefault(m => m.Role == ChatRole.Assistant);
            string reply = last == null ? "（未读取到回复）" : Truncate(string.Join("\n", last.Parts.Where(p => !p.IsStep).Select(p => p.Text)), MaxToolText);
            string head = sawBusy ? $"「{_host.NameOf(v)}」的 Copilot 已完成（用时 {Duration(v.LastDuration)}）。" : $"「{_host.NameOf(v)}」的 Copilot 当前空闲。";
            return head + "\n最后回复：\n" + reply;
        }

        [Description("对指定 VS 执行调试 / 生成操作。action 取值：go（开始或继续调试）、run（开始执行不调试）、break（全部中断）、stop（停止调试）、restart（重新启动调试）、build（生成解决方案）、rebuild（重新生成）、cancelbuild（取消生成）。")]
        private async Task<string> DebugVs(
            [Description("VS 编号（如 \"1\"）或名称")] string vs,
            [Description("go / run / break / stop / restart / build / rebuild / cancelbuild")] string action)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            action = (action ?? "").Trim().ToLowerInvariant();
            if (!DebugActions.Contains(action)) return "未知操作：" + action;
            if (_settings().AgentConfirm && !await ConfirmAsync("在「" + _host.NameOf(v) + "」中执行操作", ActionName(action)))
                return "用户拒绝了该操作。";
            return await _host.DebugAction(v, action);
        }

        [Description("读取指定 VS 错误列表中的错误与警告（通常在生成之后调用）。")]
        private async Task<string> GetErrors(
            [Description("VS 编号（如 \"1\"）或名称")] string vs,
            [Description("最多返回条数，默认 30")] int max = 30)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            return Truncate(await _host.ErrorList(v, Math.Max(1, Math.Min(MaxReadCount * 5, max))), MaxToolText);
        }

        [Description("停止指定 VS 中正在运行的 Copilot 任务。")]
        private async Task<string> StopCopilot([Description("VS 编号（如 \"1\"）或名称")] string vs)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            return await _host.InvokeChatButton(v, "CancelButton", "停止 Copilot");
        }

        [Description("在指定 VS 的 Copilot 中新建对话线程（开始全新上下文）。")]
        private async Task<string> NewCopilotThread([Description("VS 编号（如 \"1\"）或名称")] string vs)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            if (v.Copilot == CopilotState.Busy) return "Copilot 正在运行，无法新建线程";
            return await _host.InvokeChatButton(v, "createNewThread", "新建对话线程");
        }

        [Description("把所有 VS 的 Copilot 对话窗格切换为停靠的工具窗口（不再作为文档标签被隐藏），当无法读取对话或监听状态时可调用。")]
        private Task<string> DockPanes() => _host.DockPanes();

        [Description("记录或更新指定 VS 的职责描述（负责的项目 / 模块 / 任务类型），之后会据此自动选择发布任务的目标。note 为空表示清除。")]
        private async Task<string> SetVsNote(
            [Description("VS 编号（如 \"1\"）或名称")] string vs,
            [Description("一句话职责描述，例如“负责订单导出模块（OrderExport）”")] string note)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            return await _host.SetNote(v, OneLine(note, MaxNoteText));
        }

        [Description("把完善助手能力的开发任务加入界面任务清单，按编号排队，不绕过清单直发。Enqueue assistant-improvement work in the visible task list by ID; never bypass the queue.")]
        private async Task<string> RequestImprovement(
            [Description("需要新增或完善的能力，一句话，例如“读取 VS 输出窗口的内容”")] string capability,
            [Description("用户的原始请求，以及现有工具为什么做不到")] string reason,
            [Description("建议的实现方式（可选），例如需要的工具名、参数与返回内容")] string suggestion = "")
        {
            capability = OneLine(capability, Math.Max(100, MaxTaskText / 2));
            if (capability.Length == 0) return "未说明需要完善的能力";
            var list = _host.Instances.ToList();
            var target = list.FirstOrDefault(IsVsManager);
            if (target == null)
                return "没有找到打开 VSManager 项目的 VS（需要在某个 VS 中打开 VSManager.csproj / VSManager.slnx）。请告诉用户手动打开后再试。";
            string task = "【AI 总控助手改进需求】请在 VSManager 项目中完善侧边栏 AI 总控助手（AgentService.cs：工具列表、工具实现与 SystemPrompt；宿主能力接口 IAgentHost 由 MainForm.cs 实现，界面相关在 AgentPanel.cs）。" +
                "需要新增 / 完善的能力：" + capability.TrimEnd('。', '.') + "。" +
                "用户原始请求与现有不足：" + OneLine(reason, MaxTaskText).TrimEnd('。', '.') + "。" +
                (string.IsNullOrWhiteSpace(suggestion) ? "" : "建议实现：" + OneLine(suggestion, MaxTaskText).TrimEnd('。', '.') + "。") +
                "要求：沿用现有工具的写法（AIFunctionFactory + Description 中文说明 + DescribeCall 步骤文字），工具在后台线程安全执行，" +
                "有副作用的操作遵守「AgentConfirm 审批」设置；不要破坏现有功能；构建时输出到临时目录（不要覆盖正在运行的 VSManager.exe），完成后汇报改动与使用方式。" + OpenSourceTaskSuffix;
            if (_settings().AgentConfirm && !await ConfirmAsync("提交改进需求到「" + _host.NameOf(target) + "」", task))
                return "用户拒绝了该操作。";
            return await _host.QueueTask(target, task);
        }

        private static bool IsVsManager(VsInstance v)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(v.SolutionPath ?? "");
            return string.Equals(name, "VSManager", StringComparison.OrdinalIgnoreCase) ||
                   (string.IsNullOrEmpty(v.SolutionPath) && VsService.TitleName(v.Title).Equals("VSManager", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsVsManagerPath(string path)
        {
            string name;
            try { name = System.IO.Path.GetFileNameWithoutExtension((path ?? "").Trim()); } catch { name = ""; }
            return string.Equals(name, "VSManager", StringComparison.OrdinalIgnoreCase);
        }

        #region 解决方案登记与 VS 开关 / Solution registry and VS open / close

        /// <summary>
        /// 按别名 / 同义词 / 路径解析登记表：唯一命中返回条目；多条候选或未命中时返回 null，并在 <paramref name="error"/> 中给出候选或已登记别名列表。
        /// Resolves the registry by alias / synonym / path: returns the entry on a unique hit; otherwise null with the
        /// candidates or the registered aliases in <paramref name="error"/>.
        /// </summary>
        private SolutionEntry LookupSolution(string query, out string error) => LookupSolution(query, out error, out _);

        private SolutionEntry LookupSolution(string query, out string error, out bool ambiguous)
        {
            error = null;
            var reg = _host.Solutions;
            var r = reg.Resolve(query);
            ambiguous = r.Ambiguous;
            if (r.Found) return r.Hit;
            if (r.Ambiguous)
            {
                error = $"「{query}」匹配到 {r.Candidates.Count} 个已登记的解决方案，请让用户选择其一，并用完整别名重试：\n" +
                        string.Join("\n", r.Candidates.Select(c => $"- 「{c.Entry.Alias}」→ {c.Entry.Path}（{c.Reason}）"));
                return null;
            }
            error = $"解决方案登记表中找不到「{query}」。已登记的别名：{reg.AliasListText()}";
            return null;
        }

        [Description("列出解决方案登记表：每条的别名、同义词、说明、解决方案路径、是否已打开及对应的 VS 编号。用户用口语名称（如「订单项目」）指代解决方案时先查这里。")]
        private string ListSolutions()
        {
            var items = _host.Solutions.Items;
            if (items.Count == 0) return "解决方案登记表为空（用户可在「属性 → 解决方案登记」或 VS 列表右键「登记此解决方案」中添加）。";
            var list = _host.Instances.ToList();
            var sb = new StringBuilder();
            foreach (var e in items)
            {
                sb.Append("「").Append(e.Alias).Append("」 → ").Append(e.Path);
                if (e.Synonyms.Count > 0) sb.Append(" | 同义词：").Append(e.SynonymText);
                if (!string.IsNullOrEmpty(e.Description)) sb.Append(" | 说明：").Append(OneLine(e.Description, MaxNoteText));
                var v = _host.FindOpenSolution(e);
                if (v != null) sb.Append(" | 已打开：#").Append(list.IndexOf(v) + 1).Append(' ').Append(_host.NameOf(v)).Append("（Copilot：").Append(CopilotText(v)).Append('）');
                else sb.Append(" | 未打开");
                if (e.DefaultVs > 0) sb.Append(" | 默认 VS #").Append(e.DefaultVs);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        [Description("按登记别名（支持同义词与模糊匹配）或解决方案完整路径（.sln / .slnx）打开解决方案。已打开时只激活对应的 VS 窗口，不会重复打开；否则启动 Visual Studio 并等待其窗口出现。打开后，等待该解决方案的暂存任务会自动推送。")]
        private async Task<string> OpenSolution(
            [Description("登记的别名 / 同义词，或解决方案完整路径")] string solution,
            CancellationToken cancellationToken = default)
        {
            string q = (solution ?? "").Trim().Trim('"');
            if (q.Length == 0) return "请提供要打开的解决方案别名或路径。已登记的别名：" + _host.Solutions.AliasListText();
            SolutionEntry entry = LookupSolution(q, out string lookupError, out bool ambiguous);
            if (ambiguous) return lookupError;
            if (entry == null && !SolutionMatcher.LooksLikePath(q)) return lookupError;
            string path = Environment.ExpandEnvironmentVariables((entry?.Path ?? q).Trim().Trim('"')).Trim().Trim('"');
            string label;
            try
            {
                string requestedPath = Environment.ExpandEnvironmentVariables(q).Trim().Trim('"');
                bool explicitPath = requestedPath.IndexOf('\\') >= 0 || requestedPath.IndexOf('/') >= 0 || requestedPath.IndexOf(':') >= 0;
                foreach (string candidate in explicitPath ? new[] { requestedPath, path } : new[] { path })
                {
                    string root = System.IO.Path.GetPathRoot(candidate);
                    if (string.IsNullOrEmpty(root) || root.Length < 3 || root.EndsWith(":"))
                        return "请提供解决方案的完整路径 / Provide a fully qualified solution path：" + candidate;
                }
                string extension = System.IO.Path.GetExtension(path);
                if (!string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                    return "仅支持 .sln 或 .slnx 解决方案文件 / Only .sln or .slnx solution files are supported：" + path;
                path = System.IO.Path.GetFullPath(path);
                label = entry?.Alias ?? System.IO.Path.GetFileNameWithoutExtension(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is System.IO.IOException || ex is System.Security.SecurityException)
            {
                return "解决方案路径无效 / Invalid solution path：" + ex.Message;
            }
            if (entry == null) entry = new SolutionEntry { Alias = label, Path = path };

            var open = _host.FindOpenSolution(entry);
            if (open != null)
            {
                await _host.Activate(open);
                return $"「{label}」已在 #{Index(open) + 1} {_host.NameOf(open)} 中打开，已激活该 VS 窗口（未重复打开）。";
            }
            if (!System.IO.File.Exists(path)) return $"解决方案文件不存在：{path}。请确认登记表中的路径（「属性 → 解决方案登记」）。";
            if (_settings().AgentConfirm && !await ConfirmAsync("打开解决方案「" + label + "」", path))
                return "用户拒绝了该操作。";
            string err = await _host.LaunchSolution(path);
            if (err != null) return err;

            int waitSeconds = Math.Max(10, _settings().SolutionOpenWaitSeconds);
            var deadline = DateTime.Now.AddSeconds(waitSeconds);
            while (DateTime.Now < deadline)
            {
                Touch();
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                var v = _host.FindOpenSolution(entry);
                if (v != null)
                    return $"已打开「{label}」（#{Index(v) + 1} {_host.NameOf(v)}）。解决方案可能仍在加载；等待它的暂存任务会在加载后自动推送。";
            }
            return $"已启动 Visual Studio 打开「{label}」，但 {waitSeconds} 秒内尚未识别到它的窗口（可能仍在启动或弹出了提示）。窗口出现后会自动识别，暂存任务也会自动推送。";
        }

        [Description("关闭指定的 Visual Studio（按登记别名或 VS 编号 / 名称）。关闭前会请用户确认（可在设置中关闭确认），并检查未保存的修改：有未保存修改、Copilot 正在运行、有执行中的任务、正在调试或生成时拒绝关闭并说明原因；只发送正常关闭请求，绝不强制结束进程。")]
        private async Task<string> CloseVs(
            [Description("登记的别名 / 同义词，或 VS 编号（如 \"2\"）/ 名称")] string target)
        {
            string q = (target ?? "").Trim();
            if (q.Length == 0) return "请指定要关闭的 VS（编号或登记别名）。\n" + ListVs();
            VsInstance v;
            string lookupError = null;
            bool ambiguous = false;
            bool numbered = int.TryParse(q.TrimStart('#').Trim().TrimEnd('号'), out _);
            var entry = numbered ? null : LookupSolution(q, out lookupError, out ambiguous);
            if (ambiguous) return lookupError;
            if (entry != null)
            {
                v = _host.FindOpenSolution(entry);
                if (v == null) return $"「{entry.Alias}」当前没有打开，无需关闭。";
            }
            else if (SolutionMatcher.LooksLikePath(q))
            {
                var matches = _host.Instances.Where(i => SolutionMatcher.SamePath(i.SolutionPath, q) ||
                    (string.IsNullOrWhiteSpace(i.SolutionPath) && SolutionMatcher.SamePath(i.LaunchPath, q))).ToList();
                if (matches.Count != 1) return lookupError + "\n" + ListVs();
                v = matches[0];
            }
            else if (!Resolve(q, out v, out var err))
                return err + (_host.Solutions.Count > 0 ? "\n" + lookupError : "");
            string name = _host.NameOf(v);
            string refuse = await _host.CheckCanClose(v);
            if (refuse != null) return refuse;
            if (_settings().SolutionCloseConfirm || _settings().AgentConfirm)
            {
                if (!await ConfirmAsync("关闭 VS「" + name + "」/ Close VS",
                        $"确定关闭「{name}」吗？已检查：没有未保存的修改。\n{v.SolutionPath}\n\nClose \"{name}\"? No unsaved changes were found."))
                    return "用户拒绝了该操作。";
            }
            return await _host.CloseVs(v);
        }

        #endregion

        [Description("把指定 VS 窗口切换到前台（会打断用户当前界面，仅在用户要求查看该 VS 时使用）。")]
        private async Task<string> ActivateVs([Description("VS 编号（如 \"1\"）或名称")] string vs)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            return await _host.Activate(v);
        }

        #endregion

        #region 辅助

        private int Index(VsInstance v) => _host.Instances.ToList().IndexOf(v);

        private bool Resolve(string vs, out VsInstance v, out string error)
        {
            v = null;
            var list = _host.Instances.ToList();
            error = null;
            if (list.Count == 0) { error = "当前没有正在运行的 Visual Studio。"; return false; }
            string s = (vs ?? "").Trim().TrimStart('#').Trim();
            if (s.EndsWith("号")) s = s.Substring(0, s.Length - 1);
            if (int.TryParse(s, out int n))
            {
                if (n >= 1 && n <= list.Count) { v = list[n - 1]; return true; }
                error = $"没有编号为 {n} 的 VS。\n" + ListVs();
                return false;
            }
            if (s.Length > 0)
            {
                var hits = list.Where(x => string.Equals(_host.NameOf(x), s, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0)
                    hits = list.Where(x => _host.NameOf(x).IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           (x.SolutionPath ?? "").IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 1) { v = hits[0]; return true; }
                if (hits.Count > 1) { error = $"「{s}」匹配到多个 VS，请使用编号。\n" + ListVs(); return false; }
            }
            error = $"找不到 VS「{vs}」。\n" + ListVs();
            return false;
        }

        private static string Format(ChatTranscript t, int max, int maxMessage)
        {
            if (t == null || !t.PaneFound) return "未找到该 VS 的 Copilot 对话窗格。";
            if (t.Messages.Count == 0) return "Copilot 对话为空。";
            var sb = new StringBuilder();
            foreach (var m in t.Messages)
            {
                string body = string.Join("\n", m.Parts.Where(p => !p.IsStep).Select(p => p.Text)).Trim();
                if (body.Length == 0) continue;
                sb.Append(m.Role == ChatRole.User ? "【用户】" : "【Copilot】").AppendLine(Truncate(body, maxMessage));
            }
            string s = sb.ToString();
            return s.Length > max ? "…" + s.Substring(s.Length - max) : s;
        }

        private static string ActionName(string action)
        {
            switch (action)
            {
                case "go": return "开始调试";
                case "run": return "开始执行（不调试）";
                case "break": return "全部中断";
                case "stop": return "停止调试";
                case "restart": return "重新启动调试";
                case "build": return "生成解决方案";
                case "rebuild": return "重新生成解决方案";
                case "cancelbuild": return "取消生成";
                default: return action;
            }
        }

        private static string Duration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{Math.Max(0, t.Seconds)}s";

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max) + "…";

        private static string OneLine(string s, int max) =>
            Truncate(System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim(), @"\s+", " "), max);

        #endregion

        public void Dispose()
        {
            _cts?.Cancel();
            _client?.Dispose();
            _client = null;
        }
    }
}
