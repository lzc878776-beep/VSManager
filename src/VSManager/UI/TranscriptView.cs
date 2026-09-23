using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VSManager
{
    /// <summary>A local-only Markdown transcript. Call ResetView when changing the selected VS.</summary>
    public class TranscriptView : UserControl
    {
        private const int MaxCopyLength = 65536;
        private const int MaxLinkLength = 4096;
        private const int MaxHostMessageLength = 400000;
        private readonly object _gate = new object();
        private readonly WebView2 _web = new WebView2();
        private readonly Label _status = new Label();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
            .DisableHtml().UsePipeTables().UseAutoLinks().UseSoftlineBreakAsHardlineBreak().Build();
        private readonly List<CachedMessage> _cache = new List<CachedMessage>();
        private CoreWebView2 _core;
        private State _latest = new State { Empty = "请在左侧选择一个 VS" };
        private long _revision;
        private string _done = "";
        private string _activity = "", _pending = "";
        private long _generation;
        private bool _handleReady;
        private bool _scheduled;
        private bool _initializing;
        private bool _ready;
        private bool _allowInitialNavigation;
        private string _initialNavigationUri;
        private bool _disposed;

        /// <summary>助手消息的标签名称。</summary>
        public string AssistantLabel { get; set; } = "Copilot";

        /// <summary>
        /// 重置视图后滚动到顶部而不是底部（用于倒序显示的历史记录）。
        /// Scroll to the top instead of the bottom after a reset (used for newest-first history).
        /// </summary>
        public bool ScrollTopOnReset { get; set; }

        public TranscriptView()
        {
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Regular;
            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Theme.Background;
            _web.AllowExternalDrop = false;
            _web.Visible = false;
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleCenter;
            _status.BackColor = Theme.Background;
            _status.ForeColor = Theme.TextSecondary;
            _status.Font = Theme.Regular;
            _status.UseMnemonic = false;
            _status.Text = "正在初始化对话视图…";
            _status.AccessibleName = "对话视图状态";
            Controls.Add(_web);
            Controls.Add(_status);
            AccessibleName = "Copilot 对话记录";
        }

        /// <summary>Queues a snapshot; only the newest snapshot is sent after browser initialization.</summary>
        public void Render(ChatTranscript transcript, bool showSteps)
        {
            long generation;
            lock (_gate)
            {
                if (_disposed) return;
                generation = _generation;
            }
            if (transcript == null)
            {
                Queue(new State { Empty = "当前对话为空" }, false, generation);
                return;
            }
            var state = new State();
            if (transcript.Messages != null)
            {
                foreach (var message in transcript.Messages.ToArray())
                {
                    if (message == null) continue;
                    var source = new SourceMessage { User = message.Role == ChatRole.User };
                    if (message.Parts != null)
                    {
                        foreach (var part in message.Parts.ToArray())
                            if (part != null && (!part.IsStep || showSteps))
                                source.Parts.Add(new SourcePart { Step = part.IsStep, Text = part.Text ?? "" });
                    }
                    state.Messages.Add(source);
                }
            }
            if (state.Messages.Count == 0)
                state.Empty = string.IsNullOrEmpty(transcript.Title)
                    ? "当前对话为空\n在下方输入内容，开始与 Copilot 对话"
                    : "「" + transcript.Title + "」\n当前没有可显示的消息";
            Queue(state, false, generation);
        }

        public void SetEmpty(string message)
        {
            Queue(new State { Empty = message ?? "" }, false);
        }

        public void SetCompletion(string text)
        {
            text = string.IsNullOrEmpty(text) ? "" : text;
            lock (_gate)
            {
                if (_disposed || _done == text) return;
                _done = text;
                _latest.Revision = ++_revision;
            }
            ScheduleRender();
        }

        /// <summary>对话末尾的进度提示（发送中 / 接收中，带动画）与尚未出现在对话中的待发送消息。</summary>
        public void SetActivity(string activity, string pending)
        {
            activity = activity ?? "";
            pending = pending ?? "";
            lock (_gate)
            {
                if (_disposed || (_activity == activity && _pending == pending)) return;
                _activity = activity;
                _pending = pending;
                _latest.Revision = ++_revision;
            }
            ScheduleRender();
        }

        /// <summary>Clears the transcript and invalidates queued work; the next render scrolls to bottom.</summary>
        public void ResetView()
        {
            Queue(new State { Empty = "" }, true);
        }

        private void Queue(State state, bool reset, long? expectedGeneration = null)
        {
            lock (_gate)
            {
                if (_disposed || (expectedGeneration.HasValue && expectedGeneration.Value != _generation)) return;
                if (reset) _generation++;
                state.Generation = _generation;
                state.Revision = ++_revision;
                _latest = state;
            }
            ScheduleRender();
        }

        private void ScheduleRender()
        {
            lock (_gate)
            {
                if (_disposed || !_handleReady || _scheduled) return;
                _scheduled = true;
            }
            try { BeginInvoke((Action)FlushLatest); }
            catch (InvalidOperationException)
            {
                lock (_gate) _scheduled = false;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            lock (_gate) _handleReady = true;
            if (DesignMode) return;
            HookForm();
            InitializeBrowser();
            ScheduleRender();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            lock (_gate) { _handleReady = false; _scheduled = false; }
            base.OnHandleDestroyed(e);
        }

        /// <summary>
        /// WebView2 用户数据目录（所有对话视图共用）。/ WebView2 user data folder shared by all transcript views.
        /// </summary>
        public static string UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VSManager", "WebView2", "Transcript");

        /// <summary>
        /// 浏览器进程参数：所有对话视图共用一个渲染进程（页面只加载本地内容，外部导航已拦截，可关闭站点隔离），并关闭独立 GPU 进程（纯文本页面用软件渲染即可），显著降低常驻内存。
        /// Browser arguments: one shared renderer (local content only, external navigation is blocked, so site isolation is not needed) and no separate GPU process (software raster is enough for text).
        /// </summary>
        public static string BrowserArguments = "--renderer-process-limit=1 --process-per-site --disable-site-isolation-trials --disable-gpu --disable-gpu-compositing --in-process-gpu";

        private static System.Threading.Tasks.Task<CoreWebView2Environment> _sharedEnvironment;

        /// <summary>所有对话视图共用同一个 WebView2 环境（同一组浏览器进程）。/ One environment for all views.</summary>
        private static System.Threading.Tasks.Task<CoreWebView2Environment> SharedEnvironment()
        {
            var task = _sharedEnvironment;
            if (task == null || task.IsFaulted || task.IsCanceled)
            {
                var options = new CoreWebView2EnvironmentOptions(BrowserArguments);
                _sharedEnvironment = task = CoreWebView2Environment.CreateAsync(null, UserDataFolder, options);
            }
            return task;
        }

        /// <summary>
        /// 视图不可见（隐藏到托盘、最小化、面板收起）时把 WebView2 内存目标降为 Low，可见时恢复 Normal。
        /// Lower the WebView2 memory target while the view is not visible; restore it when shown.
        /// </summary>
        private void ApplyMemoryLevel()
        {
            if (_core == null || _disposed) return;
            var form = FindForm();
            bool shown = Visible && form != null && form.Visible && form.WindowState != FormWindowState.Minimized;
            var level = shown ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
            try { if (_core.MemoryUsageTargetLevel != level) _core.MemoryUsageTargetLevel = level; } catch { }
        }

        private Form _hostForm;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            ApplyMemoryLevel();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            HookForm();
        }

        private void HookForm()
        {
            var form = FindForm();
            if (form == _hostForm) return;
            if (_hostForm != null) _hostForm.Resize -= HostFormResize;
            _hostForm = form;
            if (_hostForm != null) _hostForm.Resize += HostFormResize;
        }

        private void HostFormResize(object sender, EventArgs e) => ApplyMemoryLevel();

        private async void InitializeBrowser()
        {
            if (_initializing || _disposed) return;
            _initializing = true;
            try
            {
                var environment = await SharedEnvironment();
                if (_disposed) return;
                await _web.EnsureCoreWebView2Async(environment);
                if (_disposed) return;
                _core = _web.CoreWebView2;
                ApplyMemoryLevel();
                var settings = _core.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.AreDefaultScriptDialogsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsBuiltInErrorPageEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.AreHostObjectsAllowed = false;
                settings.IsWebMessageEnabled = true;
                _core.NavigationStarting += NavigationStarting;
                _core.FrameNavigationStarting += FrameNavigationStarting;
                _core.NewWindowRequested += NewWindowRequested;
                _core.PermissionRequested += PermissionRequested;
                _core.DownloadStarting += DownloadStarting;
                _core.WebMessageReceived += WebMessageReceived;
                _core.NavigationCompleted += NavigationCompleted;
                _core.ProcessFailed += ProcessFailed;
                _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                _core.WebResourceRequested += WebResourceRequested;
                string page = LoadPage();
                _initialNavigationUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(page));
                _allowInitialNavigation = true;
                _core.NavigateToString(page);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowError("无法显示对话：未安装 Microsoft Edge WebView2 Runtime。\n请手动安装 Evergreen WebView2 Runtime 后重启 VSManager；本程序不会自动下载。", true);
            }
            catch (Exception ex)
            {
                ShowError("对话视图初始化失败：" + ex.Message + "\n请确认 WebView2 Runtime 可用，并重启 VSManager。", true);
            }
        }

        private static string LoadPage()
        {
            using (var stream = typeof(TranscriptView).Assembly.GetManifestResourceStream("VSManager.Web.transcript.html"))
            {
                if (stream == null) throw new InvalidOperationException("缺少本地对话页面资源。");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd()
                        .Replace("{{nonce}}", Guid.NewGuid().ToString("N"))
                        .Replace("{{background}}", ColorTranslator.ToHtml(Theme.Background))
                        .Replace("{{surface}}", ColorTranslator.ToHtml(Theme.Surface))
                        .Replace("{{border}}", ColorTranslator.ToHtml(Theme.Border))
                        .Replace("{{text}}", ColorTranslator.ToHtml(Theme.Text))
                        .Replace("{{secondary}}", ColorTranslator.ToHtml(Theme.TextSecondary))
                        .Replace("{{muted}}", ColorTranslator.ToHtml(Theme.TextMuted))
                        .Replace("{{user}}", ColorTranslator.ToHtml(Theme.AccentHover))
                        .Replace("{{userText}}", ColorTranslator.ToHtml(Theme.AccentText))
                        .Replace("{{assistant}}", ColorTranslator.ToHtml(Theme.IdleFg))
                        .Replace("{{font}}", Theme.FontName);
            }
        }

        private void FlushLatest()
        {
            State state;
            string done, activity, pending;
            lock (_gate)
            {
                _scheduled = false;
                if (_disposed || !_ready) return;
                state = _latest;
                done = _done;
                activity = _activity;
                pending = _pending;
            }
            try
            {
                var messages = new List<object>();
                for (int i = 0; i < state.Messages.Count; i++)
                {
                    var source = state.Messages[i];
                    CachedMessage cached = i < _cache.Count ? _cache[i] : null;
                    if (cached == null || !SameMessage(cached.Source, source))
                    {
                        cached = new CachedMessage { Source = source, Html = RenderMessage(source) };
                        if (i < _cache.Count) _cache[i] = cached;
                        else _cache.Add(cached);
                    }
                    messages.Add(new { key = i.ToString(CultureInfo.InvariantCulture), user = source.User, html = cached.Html });
                }
                if (_cache.Count > messages.Count) _cache.RemoveRange(messages.Count, _cache.Count - messages.Count);
                string json = _json.Serialize(new
                {
                    type = "render", revision = state.Revision, generation = state.Generation,
                    empty = state.Empty ?? "", done, activity, pending, messages, bot = AssistantLabel ?? "Copilot", top = ScrollTopOnReset
                });
                lock (_gate)
                {
                    // Rendering is synchronous on the UI thread; a newer worker snapshot must win.
                    if (_disposed || state != _latest) return;
                    _core.PostWebMessageAsJson(json);
                }
            }
            catch (Exception ex) { ShowError("对话渲染失败：" + ex.Message, false); }
        }

        private string RenderMessage(SourceMessage source)
        {
            var html = new StringBuilder();
            foreach (var part in source.Parts)
            {
                if (part.Step)
                    html.Append("<div class=\"step\">› ").Append(WebUtility.HtmlEncode(part.Text)).Append("</div>");
                else if (source.User)
                    html.Append("<div class=\"plain\">").Append(WebUtility.HtmlEncode(part.Text)).Append("</div>");
                else
                {
                    using (var writer = new StringWriter(CultureInfo.InvariantCulture))
                    {
                        var renderer = new HtmlRenderer(writer);
                        _pipeline.Setup(renderer);
                        var defaultLink = renderer.ObjectRenderers.FindExact<Markdig.Renderers.Html.Inlines.LinkInlineRenderer>();
                        if (defaultLink != null) renderer.ObjectRenderers.Remove(defaultLink);
                        renderer.ObjectRenderers.Insert(0, new SafeLinkRenderer());
                        renderer.Render(Markdown.Parse(part.Text, _pipeline));
                        html.Append("<div class=\"markdown\">").Append(writer.ToString()).Append("</div>");
                    }
                }
            }
            return html.ToString();
        }

        private static bool SameMessage(SourceMessage a, SourceMessage b)
        {
            if (a.User != b.User || a.Parts.Count != b.Parts.Count) return false;
            for (int i = 0; i < a.Parts.Count; i++)
                if (a.Parts[i].Step != b.Parts[i].Step || a.Parts[i].Text != b.Parts[i].Text) return false;
            return true;
        }

        private sealed class SafeLinkRenderer : HtmlObjectRenderer<LinkInline>
        {
            protected override void Write(HtmlRenderer renderer, LinkInline link)
            {
                string target = link.GetDynamicUrl != null ? link.GetDynamicUrl() : link.Url;
                Uri uri;
                bool enabled = !link.IsImage && TryWebUri(target, out uri);
                if (enabled) renderer.Write("<a href=\"").Write(WebUtility.HtmlEncode(target)).Write("\" rel=\"noreferrer noopener\">");
                renderer.WriteChildren(link);
                if (enabled) renderer.Write("</a>");
            }
        }

        private static bool TryWebUri(string value, out Uri uri)
        {
            uri = null;
            return !string.IsNullOrWhiteSpace(value) && value.Length <= MaxLinkLength &&
                !value.Any(char.IsControl) && Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
                !string.IsNullOrEmpty(uri.Host) && uri.IsWellFormedOriginalString();
        }

        private void WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_disposed || e.Source != "about:blank") return;
            try
            {
                string raw = e.WebMessageAsJson;
                if (raw == null || raw.Length > MaxHostMessageLength) return;
                var parser = new JavaScriptSerializer { MaxJsonLength = MaxHostMessageLength, RecursionLimit = 8 };
                var message = parser.DeserializeObject(raw) as Dictionary<string, object>;
                object kind;
                if (message == null || message.Count > 4 || !message.TryGetValue("type", out kind)) return;
                string type = kind as string;
                if (type == "ready")
                {
                    _ready = true;
                    _web.Visible = true;
                    _status.Visible = false;
                    ScheduleRender();
                    return;
                }
                object number;
                if (!message.TryGetValue("revision", out number)) return;
                long revision = Convert.ToInt64(number, CultureInfo.InvariantCulture);
                lock (_gate) if (revision != _latest.Revision) return;
                object content;
                if (!message.TryGetValue("value", out content)) return;
                string value = content as string;
                if (value == null) return;
                if (type == "copy" && value.Length > 0 && value.Length <= MaxCopyLength)
                {
                    Clipboard.SetText(value);
                    _status.Visible = false;
                }
                else if (type == "link")
                {
                    Uri uri;
                    if (!TryWebUri(value, out uri)) throw new InvalidOperationException("仅支持有效的 HTTP/HTTPS 链接。");
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    _status.Visible = false;
                }
                else if (type == "error" && value.Length <= 1024)
                    ShowError("对话视图：" + value, false);
            }
            catch (Exception ex) { ShowError("对话操作失败：" + ex.Message, false); }
        }

        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (_allowInitialNavigation && (e.Uri == "about:blank" || e.Uri == _initialNavigationUri))
            {
                _allowInitialNavigation = false;
                return;
            }
            e.Cancel = true;
        }

        private void FrameNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e) { e.Cancel = true; }
        private void NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; }
        private void PermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; }
        private void DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; }

        private void WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            e.Response = _core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
        }

        private void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess && !_disposed)
                ShowError("无法加载本地对话页面：" + e.WebErrorStatus, true);
        }

        private void ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _ready = false;
            ShowError("WebView2 对话视图已停止（" + e.ProcessFailedKind + "）。请重启 VSManager。", true);
        }

        private void ShowError(string text, bool full)
        {
            if (_disposed) return;
            if (full) { _ready = false; _web.Visible = false; }
            _status.Dock = full ? DockStyle.Fill : DockStyle.Bottom;
            _status.Height = Dpi.S(60);
            _status.ForeColor = Theme.Danger;
            _status.Text = text;
            _status.Visible = true;
            _status.BringToFront();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                lock (_gate) { _disposed = true; _handleReady = false; _latest = new State(); }
                if (_hostForm != null) { _hostForm.Resize -= HostFormResize; _hostForm = null; }
                _ready = false;
                _cache.Clear();
                if (_core != null)
                {
                    _core.NavigationStarting -= NavigationStarting;
                    _core.FrameNavigationStarting -= FrameNavigationStarting;
                    _core.NewWindowRequested -= NewWindowRequested;
                    _core.PermissionRequested -= PermissionRequested;
                    _core.DownloadStarting -= DownloadStarting;
                    _core.WebMessageReceived -= WebMessageReceived;
                    _core.NavigationCompleted -= NavigationCompleted;
                    _core.ProcessFailed -= ProcessFailed;
                    _core.WebResourceRequested -= WebResourceRequested;
                }
            }
            base.Dispose(disposing);
        }

        private sealed class State
        {
            public long Revision;
            public long Generation;
            public string Empty;
            public readonly List<SourceMessage> Messages = new List<SourceMessage>();
        }

        private sealed class SourceMessage
        {
            public bool User;
            public readonly List<SourcePart> Parts = new List<SourcePart>();
        }

        private sealed class SourcePart
        {
            public bool Step;
            public string Text;
        }

        private sealed class CachedMessage
        {
            public SourceMessage Source;
            public string Html;
        }
    }
}
