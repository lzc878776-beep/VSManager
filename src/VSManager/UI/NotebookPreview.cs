using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VSManager
{
    internal sealed class NotebookPreview : UserControl
    {
        private readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Background, AllowExternalDrop = false };
        private readonly Label _message = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "正在准备阅读视图… / Preparing preview…", BackColor = Theme.Background, ForeColor = Theme.TextSecondary };
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private string _html = "";
        private bool _ready, _initializing, _initialNavigation;
        private bool _scrollTop;
        public event Action<string> Error;
        /// <summary>点击笔记间相对链接（原始链接目标）。/ A relative note link was clicked (raw link target).</summary>
        public event Action<string> NoteLinkRequested;
        /// <summary>双击阅读视图，请求进入编辑。/ The preview was double-clicked to request editing.</summary>
        public event Action EditRequested;

        public NotebookPreview()
        {
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Regular;
            Controls.Add(_web);
            Controls.Add(_message);
        }

        public void Render(string html, bool scrollTop)
        {
            _html = html ?? "";
            _scrollTop |= scrollTop;
            Flush();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!DesignMode && !_initializing) InitializeBrowser();
        }

        private async void InitializeBrowser()
        {
            _initializing = true;
            try
            {
                var environment = await TranscriptView.SharedEnvironment();
                if (IsDisposed) return;
                await _web.EnsureCoreWebView2Async(environment);
                if (IsDisposed) return;
                var core = _web.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.NavigationStarting += (s, e) =>
                {
                    if (_initialNavigation && (e.Uri == "about:blank" || e.Uri.StartsWith("data:text/html", StringComparison.Ordinal)))
                    { _initialNavigation = false; return; }
                    e.Cancel = true;
                    if (e.IsUserInitiated) OpenExternal(e.Uri);
                };
                core.FrameNavigationStarting += (s, e) => e.Cancel = true;
                core.NewWindowRequested += (s, e) => { e.Handled = true; if (e.IsUserInitiated) OpenExternal(e.Uri); };
                core.DownloadStarting += (s, e) => e.Cancel = true;
                core.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (s, e) =>
                {
                    if (!e.Request.Uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                        e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                };
                core.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        var message = _json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(e.WebMessageAsJson);
                        if (message != null && message.TryGetValue("note", out var note) && note is string target && NotebookMarkdown.IsNoteLink(target))
                            NoteLinkRequested?.Invoke(target);
                        else if (message != null && message.ContainsKey("edit"))
                            EditRequested?.Invoke();
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                };
                core.ProcessFailed += (s, e) => ShowError("阅读视图进程退出，请关闭并重新打开笔记本。编辑内容不受影响。 / Preview process failed; reopen the notebook. Your editor content is preserved.");
                core.NavigationCompleted += (s, e) =>
                {
                    if (!e.IsSuccess) { ShowError("阅读视图加载失败。 / Preview failed to load: " + e.WebErrorStatus); return; }
                    _ready = true;
                    _message.Visible = false;
                    Flush();
                };
                _initialNavigation = true;
                core.NavigateToString(Page());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowError("请安装 Microsoft Edge WebView2 Runtime 后重新打开笔记本。仍可编辑和保存 Markdown。 / Install WebView2 Runtime and reopen. Markdown editing and saving remain available.");
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidOperationException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                if (!IsDisposed) ShowError("阅读视图初始化失败 / Preview initialization failed: " + ex.Message);
            }
        }

        private void Flush()
        {
            if (!_ready || IsDisposed) return;
            try
            {
                _web.CoreWebView2.PostWebMessageAsJson(_json.Serialize(new { html = _html, top = _scrollTop }));
                _scrollTop = false;
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
            {
                ShowError("阅读视图更新失败 / Preview update failed: " + ex.Message);
            }
        }

        private void ShowError(string message)
        {
            _ready = false;
            _message.Text = message;
            _message.ForeColor = Theme.Danger;
            _message.Visible = true;
            _message.BringToFront();
            Error?.Invoke(message);
        }

        private void OpenExternal(string target)
        {
            if (!NotebookMarkdown.IsWebLink(target)) return;
            if (MessageBox.Show(this, "在浏览器中打开外部链接？ / Open external link?\r\n" + target,
                "外部链接 / External link", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { Error?.Invoke("无法打开链接 / Cannot open link: " + ex.Message); }
        }

        private static string Page()
        {
            string nonce = Guid.NewGuid().ToString("N");
            string palette = "--bg:" + ColorTranslator.ToHtml(Theme.Background) +
                ";--surface:" + ColorTranslator.ToHtml(Theme.Surface) +
                ";--surface-alt:" + ColorTranslator.ToHtml(Theme.SurfaceAlt) +
                ";--elevated:" + ColorTranslator.ToHtml(Theme.Elevated) +
                ";--border:" + ColorTranslator.ToHtml(Theme.Border) +
                ";--divider:" + ColorTranslator.ToHtml(Theme.Divider) +
                ";--text:" + ColorTranslator.ToHtml(Theme.Text) +
                ";--secondary:" + ColorTranslator.ToHtml(Theme.TextSecondary) +
                ";--muted:" + ColorTranslator.ToHtml(Theme.TextMuted) +
                ";--accent:" + ColorTranslator.ToHtml(Theme.Accent) +
                ";--accent-light:" + ColorTranslator.ToHtml(Theme.AccentLight) +
                ";--accent-text:" + ColorTranslator.ToHtml(Theme.AccentText) +
                ";--selected:" + ColorTranslator.ToHtml(Theme.RowSelected) +
                ";--ok-fg:" + ColorTranslator.ToHtml(Theme.IdleFg) + ";--ok-bg:" + ColorTranslator.ToHtml(Theme.IdleBg) + ";--ok-dot:" + ColorTranslator.ToHtml(Theme.IdleDot) +
                ";--busy-fg:" + ColorTranslator.ToHtml(Theme.BusyFg) + ";--busy-bg:" + ColorTranslator.ToHtml(Theme.BusyBg) + ";--busy-dot:" + ColorTranslator.ToHtml(Theme.BusyDot) +
                ";--unv-fg:" + ColorTranslator.ToHtml(Theme.UnverifiedFg) + ";--unv-bg:" + ColorTranslator.ToHtml(Theme.UnverifiedBg) + ";--unv-dot:" + ColorTranslator.ToHtml(Theme.UnverifiedDot) +
                ";--none-fg:" + ColorTranslator.ToHtml(Theme.NoneFg) + ";--none-bg:" + ColorTranslator.ToHtml(Theme.NoneBg) + ";--none-dot:" + ColorTranslator.ToHtml(Theme.NoneDot) +
                ";--warn:" + ColorTranslator.ToHtml(Theme.Warning) + ";--danger:" + ColorTranslator.ToHtml(Theme.Danger) + ";--danger-bg:#3C161A;";
            return @"<!doctype html><html><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<meta http-equiv='Content-Security-Policy' content=""default-src 'none'; img-src data:; style-src 'nonce-{{nonce}}'; script-src 'nonce-{{nonce}}'; base-uri 'none'; form-action 'none'"">
<style nonce='{{nonce}}'>
:root{color-scheme:dark;{{palette}}}*{box-sizing:border-box;scrollbar-color:var(--border) var(--bg);scrollbar-width:thin}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.8 '{{font}}','Segoe UI',sans-serif}
main{max-width:820px;margin:0 auto;padding:56px 64px 120px;overflow-wrap:anywhere}h1{font-size:34px;line-height:1.3;margin:0 0 28px;font-weight:700;letter-spacing:-.6px}
h2{font-size:23px;margin:32px 0 12px;line-height:1.4}h3{font-size:19px;margin:24px 0 8px}p{margin:10px 0}ul,ol{padding-left:25px}li{margin:3px 0}
a{color:var(--accent-text);text-underline-offset:3px}a:hover{color:var(--text)}a:focus-visible{outline:2px solid var(--accent);outline-offset:3px}blockquote{border-left:3px solid var(--accent);margin:20px 0;padding:2px 18px;color:var(--secondary)}
code{font:13px/1.7 Consolas,monospace;background:var(--accent-light);color:var(--accent-text);padding:2px 5px;border-radius:4px}pre{padding:18px 22px;background:var(--surface-alt);border-radius:5px;overflow:auto}pre code{padding:0;color:var(--text);background:none}
table{border-collapse:collapse;display:block;overflow:auto;margin:20px 0}th,td{border:1px solid var(--border);padding:8px 13px;text-align:left}th{background:var(--surface)}
img{max-width:100%;height:auto;border-radius:3px;display:block;margin:22px 0}hr{border:0;border-top:1px solid var(--divider);margin:28px 0}input[type=checkbox]{accent-color:var(--accent);pointer-events:none}
.image-placeholder{color:var(--secondary);font-size:13px}::selection{background:var(--selected);color:var(--text)}
.note-card{background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:8px 14px 10px;margin:8px 0;width:100%;max-width:420px;line-height:1.5;--pf:var(--none-fg);--pb:var(--none-bg);--pd:var(--none-dot);--nf:var(--muted)}
h1+.note-card,h2+.note-card,h3+.note-card{margin-top:4px}.note-card+:not(.note-card){margin-top:20px}
.nc-done{--pf:var(--ok-fg);--pb:var(--ok-bg);--pd:var(--ok-dot)}.nc-needs-user{--pf:var(--warn);--pb:var(--ok-bg);--pd:var(--warn);--nf:var(--warn)}.nc-unverified{--pf:var(--unv-fg);--pb:var(--unv-bg);--pd:var(--unv-dot)}
.nc-running{--pf:var(--busy-fg);--pb:var(--busy-bg);--pd:var(--busy-dot)}.nc-waiting,.nc-info{--pf:var(--accent-text);--pb:var(--accent-light);--pd:var(--accent)}.nc-failed{--pf:var(--danger);--pb:var(--danger-bg);--pd:var(--danger);--nf:var(--danger)}
.nc-head{display:flex;align-items:center;gap:8px;min-width:0;font-size:12px;line-height:20px;color:var(--muted)}.nc-pill{flex:none;display:inline-flex;align-items:center;gap:5px;height:20px;padding:0 9px;border-radius:999px;background:var(--pb);color:var(--pf);font-size:11.5px;font-weight:600;white-space:nowrap}
.nc-pill i{width:6px;height:6px;border-radius:50%;background:var(--pd)}.nc-meta{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-variant-numeric:tabular-nums}.nc-time{flex:none;margin-left:auto;white-space:nowrap;font-variant-numeric:tabular-nums}
.nc-title{margin-top:6px;font-size:14px;font-weight:600;line-height:1.45;color:var(--text)}.nc-text{margin-top:2px;font-size:12.5px;line-height:1.55;color:var(--secondary);display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
.nc-head+.nc-text{margin-top:6px}.nc-note{margin-top:4px;font-size:12px;color:var(--nf);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.ncs-compact{padding:6px 12px}.ncs-compact .nc-head>.nc-title{flex:1 1 auto;min-width:0;margin:0;font-size:13px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.ncs-compact .nc-meta{flex:none}.ncs-compact .nc-text{margin-top:2px;font-size:12px;-webkit-line-clamp:1}.ncs-compact .nc-note{margin-top:2px}
.ncs-numbered{display:flex;gap:12px;align-items:flex-start}.nc-num{flex:none;min-width:30px;padding-top:1px;font-size:20px;font-weight:700;line-height:1.2;color:var(--pd);font-variant-numeric:tabular-nums;text-align:center}.nc-body{flex:1 1 auto;min-width:0}
.ncs-noted .nc-text{display:block;-webkit-line-clamp:unset}.ncs-noted .nc-note{margin-top:8px;padding:6px 10px;border-left:3px solid var(--pd);border-radius:0 6px 6px 0;background:var(--surface-alt);color:var(--secondary);line-height:1.55;white-space:normal}
.ncs-accent{border-left:4px solid var(--pd);border-radius:4px 8px 8px 4px}
::-webkit-scrollbar{width:10px;height:10px}::-webkit-scrollbar-track{background:var(--bg)}::-webkit-scrollbar-thumb{background:var(--border);border-radius:5px}::-webkit-scrollbar-thumb:hover{background:var(--elevated)}
@media(max-width:600px){main{padding:32px 24px 80px}h1{font-size:28px}}
</style></head><body><main id='note'></main><script nonce='{{nonce}}'>
document.addEventListener('click',e=>{const a=e.target.closest&&e.target.closest('a[data-note]');if(!a)return;e.preventDefault();window.chrome.webview.postMessage({note:a.getAttribute('data-note')});});
document.addEventListener('dblclick',e=>{if(e.target.closest&&e.target.closest('a'))return;window.chrome.webview.postMessage({edit:true});});
window.chrome.webview.addEventListener('message',e=>{const y=window.scrollY;document.getElementById('note').innerHTML=e.data.html;window.scrollTo(0,e.data.top?0:y);});
</script></body></html>".Replace("{{nonce}}", nonce).Replace("{{palette}}", palette).Replace("{{font}}", Theme.FontName);
        }
    }
}
