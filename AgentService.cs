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
        /// <summary>发布任务到该 VS 的 Copilot，成功时返回以“已发送”开头的文本。</summary>
        Task<string> SendTask(VsInstance v, string text);
        /// <summary>把任务加入任务清单：目标空闲时立即发布，忙碌时排队，完成后通知助手。返回给模型的说明文字。</summary>
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
    public sealed class AgentService : IDisposable
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

        public AgentService(IAgentHost host, Func<AppSettings> settings)
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            _host = host;
            _settings = settings;
            _tools = new List<AITool>
            {
                AIFunctionFactory.Create((Func<string>)ListVs, "list_vs"),
                AIFunctionFactory.Create((Func<string, int, Task<string>>)ReadVsChat, "read_vs_chat"),
                AIFunctionFactory.Create((Func<string, string, Task<string>>)SendTask, "send_task"),
                AIFunctionFactory.Create((Func<string, int, CancellationToken, Task<string>>)WaitForVs, "wait_for_vs"),
                AIFunctionFactory.Create((Func<string, string, Task<string>>)DebugVs, "debug_vs"),
                AIFunctionFactory.Create((Func<string, int, Task<string>>)GetErrors, "get_errors"),
                AIFunctionFactory.Create((Func<string, Task<string>>)StopCopilot, "stop_copilot"),
                AIFunctionFactory.Create((Func<string, Task<string>>)NewCopilotThread, "new_copilot_thread"),
                AIFunctionFactory.Create((Func<string, Task<string>>)ActivateVs, "activate_vs"),
                AIFunctionFactory.Create((Func<Task<string>>)DockPanes, "dock_copilot_panes"),
                AIFunctionFactory.Create((Func<string, string, Task<string>>)SetVsNote, "set_vs_note"),
                AIFunctionFactory.Create((Func<Task<string>>)ListTasks, "list_tasks"),
                AIFunctionFactory.Create((Func<int, Task<string>>)CancelTask, "cancel_task"),
                AIFunctionFactory.Create((Func<string, string, Task<string>>)ScanVsCode, "scan_vs_code"),
                AIFunctionFactory.Create((Func<string, string, string, Task<string>>)RequestImprovement, "request_vsmanager_improvement"),
                AIFunctionFactory.Create((Func<string, string, int, string, Task<string>>)ReadVsFile, "read_vs_file"),
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
            var options = new OpenAI.OpenAIClientOptions { Endpoint = uri, NetworkTimeout = TimeSpan.FromMinutes(5) };
            // DeepSeek 默认开启思考模式，带工具调用时要求回传 reasoning_content（OpenAI SDK 不支持），因此关闭思考
            if (AgentPresets.IsDeepSeek(endpoint) || model.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0)
                options.AddPolicy(new JsonBodyPolicy(o => { if (o["messages"] != null && o["thinking"] == null) o["thinking"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "disabled" }; }),
                    System.ClientModel.Primitives.PipelinePosition.PerCall);            var inner = new OpenAI.Chat.ChatClient(model, new System.ClientModel.ApiKeyCredential(key), options).AsIChatClient();
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

        private async void ProcessNotices()
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
                Archive.Ai("notice", n.Key, n.Value);
                _history.Add(new AIMessage(AIRole.User, n.Value));
                TrimHistory();
                Changed?.Invoke();
            }
        }

        public async Task RunAsync(string text, string display = null)
        {
            text = (text ?? "").Trim();
            if (Running || text.Length == 0) return;
            var user = new ChatMessage { Role = ChatRole.User };
            user.Parts.Add(new ChatPart { Text = string.IsNullOrWhiteSpace(display) ? text : display });
            var reply = new ChatMessage { Role = ChatRole.Assistant };
            Transcript.Messages.Add(user);
            Transcript.Messages.Add(reply);
            TrimTranscript();
            if (string.IsNullOrWhiteSpace(display)) Archive.Ai("user", text);
            else Archive.Ai("notice", display, text);

            IChatClient client;
            try { client = Client(); }
            catch (Exception ex)
            {
                AddText(reply, "⚠ " + ex.Message);
                Archive.Ai("assistant", "", null, null, ex.Message);
                Changed?.Invoke();
                return;
            }

            Running = true;
            Activity = "思考中…";
            var cts = _cts = new CancellationTokenSource();
            Changed?.Invoke();

            _history.Add(new AIMessage(AIRole.User, text));
            var messages = new List<AIMessage> { new AIMessage(AIRole.System, SystemPrompt()) };
            messages.AddRange(_history);
            var updates = new List<ChatResponseUpdate>();
            var calls = new Dictionary<string, string>();
            string error = null;
            try
            {
                var options = new ChatOptions { Tools = _tools, ToolMode = ChatToolMode.Auto, Temperature = 0.3f };
                int maxOut = MaxOutputTokens;
                if (maxOut > 0) options.MaxOutputTokens = maxOut;
                await foreach (var u in client.GetStreamingResponseAsync(messages, options, cts.Token))
                {
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
                _history.AddMessages(updates);
            }
            catch (OperationCanceledException) { error = "已停止"; }
            catch (Exception ex) { error = Friendly(ex); }
            finally
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
                    Archive.Ai("assistant",
                        string.Join("\n\n", reply.Parts.Where(p => !p.IsStep && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text.Trim())), null,
                        reply.Parts.Where(p => p.IsStep && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text.Trim()).ToList(), error);
                }
                catch { }
                Running = false;
                Activity = "";
                if (_cts == cts) _cts = null;
                cts.Dispose();
                TrimHistory();
                Changed?.Invoke();
                if (_notices.Count > 0) ProcessNotices();
            }
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
                case "send_task": return "向" + target + "发布任务：" + OneLine(Arg("task"), 60);
                case "wait_for_vs": return "等待" + target + "的 Copilot 完成";
                case "debug_vs": return target + "执行 " + ActionName(Arg("action"));
                case "get_errors": return "读取" + target + "的错误列表";
                case "stop_copilot": return "停止" + target + "的 Copilot";
                case "new_copilot_thread": return target + "新建 Copilot 线程";
                case "activate_vs": return "切换到" + target;
                case "dock_copilot_panes": return "把 Copilot 切换为工具窗模式";
                case "request_vsmanager_improvement": return "请 VSManager 完善助手能力：" + OneLine(Arg("capability"), 50);
                case "list_tasks": return "查看任务清单";
                case "cancel_task": return "取消任务 #" + Arg("id");
                case "scan_vs_code": return "扫描" + target + "的代码结构";
                case "read_vs_file": return "查看" + target + "的 " + OneLine(Arg("path"), 60);
                case "set_vs_note": return "记录" + target + "的职责：" + OneLine(Arg("note"), 40);
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
            return Prompts.AgentSystem(s.IsEnglishVoice, DateTime.Now, ListVs(), s.AgentInstructions);
        }

        #endregion

        #region 工具

        [Description("列出所有正在运行的 Visual Studio 实例：编号、名称、职责描述、解决方案、Copilot 状态、调试 / 生成状态。")]
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

        [Description("向指定 VS 的 GitHub Copilot 发布一项任务。任务记入任务清单：VS 空闲时立即发送；正忙时自动排队，空闲后自动发布；完成后会通知你。")]
        private async Task<string> SendTask(
            [Description("VS 编号（如 \"1\"）或名称")] string vs,
            [Description("发给 Copilot 的完整任务描述")] string task)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            task = (task ?? "").Trim();
            if (task.Length == 0) return "任务内容为空";
            int maxTask = MaxTaskText;
            if (task.Length > maxTask)
                return $"任务文本过长（{task.Length} 字），超过单次任务上限 {maxTask} 字（「属性 → AI 额度」可调整）。请精简后重试或拆分为多个任务。";
            // 多行消息只能前台粘贴（会短暂切到 VS）；后台模式下合并为一行，保持用户当前界面
            if (_settings().BackgroundSend && task.IndexOf('\n') >= 0)
                task = string.Join(" ", task.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            if (IsVsManager(v) && task.IndexOf("【开源约束】", StringComparison.Ordinal) < 0 && task.IndexOf("[Open-source constraint]", StringComparison.Ordinal) < 0)
                task += _settings().IsEnglishVoice ? OpenSourceTaskSuffixEn : OpenSourceTaskSuffix;
            if (_settings().AgentConfirm && !await _host.Confirm("发布任务到「" + _host.NameOf(v) + "」", task))
                return "用户拒绝了该操作。";
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
            if (_settings().AgentConfirm && !await _host.Confirm("在「" + _host.NameOf(v) + "」中执行操作", ActionName(action)))
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

        [Description("当用户的需求超出 AI 总控助手现有工具的能力时，把“需要新增 / 完善的能力”作为开发任务发给打开 VSManager 项目的 VS，让其 Copilot 完善本助手。")]
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
            if (target.Copilot == CopilotState.Busy) return $"「{_host.NameOf(target)}」的 Copilot 正在运行，稍后再提交改进需求。";
            string task = "【AI 总控助手改进需求】请在 VSManager 项目中完善侧边栏 AI 总控助手（AgentService.cs：工具列表、工具实现与 SystemPrompt；宿主能力接口 IAgentHost 由 MainForm.cs 实现，界面相关在 AgentPanel.cs）。" +
                "需要新增 / 完善的能力：" + capability.TrimEnd('。', '.') + "。" +
                "用户原始请求与现有不足：" + OneLine(reason, MaxTaskText).TrimEnd('。', '.') + "。" +
                (string.IsNullOrWhiteSpace(suggestion) ? "" : "建议实现：" + OneLine(suggestion, MaxTaskText).TrimEnd('。', '.') + "。") +
                "要求：沿用现有工具的写法（AIFunctionFactory + Description 中文说明 + DescribeCall 步骤文字），工具在后台线程安全执行，" +
                "有副作用的操作遵守「AgentConfirm 审批」设置；不要破坏现有功能；构建时输出到临时目录（不要覆盖正在运行的 VSManager.exe），完成后汇报改动与使用方式。" + OpenSourceTaskSuffix;
            if (_settings().AgentConfirm && !await _host.Confirm("提交改进需求到「" + _host.NameOf(target) + "」", task))
                return "用户拒绝了该操作。";
            string r = await _host.SendTask(target, task);
            return r.StartsWith("已发送")
                ? $"已把改进需求发给「#{list.IndexOf(target) + 1} {_host.NameOf(target)}」的 Copilot。完成后需要重启 VSManager 才能使用新能力。"
                : "提交失败：" + r;
        }

        private static bool IsVsManager(VsInstance v)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(v.SolutionPath ?? "");
            return string.Equals(name, "VSManager", StringComparison.OrdinalIgnoreCase) ||
                   (string.IsNullOrEmpty(v.SolutionPath) && VsService.TitleName(v.Title).Equals("VSManager", StringComparison.OrdinalIgnoreCase));
        }

        [Description("扫描指定 VS 的解决方案目录，返回代码结构概要：项目及目标框架 / 引用、目录分布、README 摘要、主要代码文件与类型。用于了解该 VS 负责什么。")]
        private async Task<string> ScanVsCode(
            [Description("VS 编号（如 \"1\"）或名称")] string vs,
            [Description("可选：未能获取解决方案路径时，指定要扫描的源码目录")] string folder = "")
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            string root = CodeScanner.Root(string.IsNullOrWhiteSpace(folder) ? v.SolutionPath : folder);
            if (root == null) return $"未获取到「{_host.NameOf(v)}」的解决方案路径（可能尚未打开解决方案或 DTE 暂不可用）。可询问用户源码目录后通过 folder 参数指定。";
            string sln = string.IsNullOrWhiteSpace(folder) ? v.SolutionPath : null;
            int maxScan = (int)Math.Min(int.MaxValue, MaxToolText * 3L / 2);
            return await Task.Run(() => CodeScanner.Scan(root, maxScan, sln)).ConfigureAwait(false);
        }

        [Description("读取指定 VS 解决方案目录内的文件内容（带行号，每次行数与字数有上限，超出时提示用 startLine 继续），或列出某个子目录。")]
        private async Task<string> ReadVsFile(
            [Description("VS 编号（如 \"1\"）或名称")] string vs,
            [Description("相对于解决方案目录的文件或目录路径，例如 \"src/Program.cs\"")] string path,
            [Description("起始行号，默认 1")] int startLine = 1,
            [Description("可选：未能获取解决方案路径时，与 scan_vs_code 相同的源码目录")] string folder = "")
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            string root = CodeScanner.Root(string.IsNullOrWhiteSpace(folder) ? v.SolutionPath : folder);
            if (root == null) return $"未获取到「{_host.NameOf(v)}」的解决方案路径，请通过 folder 参数指定源码目录。";
            int maxLines = MaxFileLines, maxText = MaxToolText;
            return await Task.Run(() => CodeScanner.ReadFile(root, path, startLine, maxLines, maxText)).ConfigureAwait(false);
        }

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
