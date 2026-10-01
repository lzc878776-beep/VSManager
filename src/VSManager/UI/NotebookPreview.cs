using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
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
        // 当前显示的页面标识、Markdown 原文（\n 换行）与块位置；原文未变时不重新渲染，以免打断正在进行的就地编辑。
        // Key, Markdown source (\n newlines) and block spans being shown; an unchanged source is not re-rendered so in-place editing is not interrupted.
        private string _key, _source, _raw;
        private int[][] _spans;
        public event Action<string> Error;
        /// <summary>点击笔记间相对链接（原始链接目标）。/ A relative note link was clicked (raw link target).</summary>
        public event Action<string> NoteLinkRequested;
        /// <summary>双击不可就地编辑的页面（如任务记录），请求打开 Markdown 源码。/ A page that cannot be edited in place (e.g. a task journal) was double-clicked to request the Markdown source.</summary>
        public event Action EditRequested;
        /// <summary>在渲染视图中就地编辑后生成的 Markdown（页面标识, 原文）。/ Markdown produced by in-place editing in the rendered view (page key, source).</summary>
        public event Action<string, string> MarkdownEdited;
        /// <summary>在渲染视图中粘贴了图片。/ Images were pasted into the rendered view.</summary>
        public event Action ImagePasteRequested;
        /// <summary>渲染视图中的可编辑区域是否有焦点。/ Whether the editable area of the rendered view has focus.</summary>
        internal bool EditorFocused { get; private set; }

        public NotebookPreview()
        {
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Regular;
            Controls.Add(_web);
            Controls.Add(_message);
        }

        public void Render(string html, bool scrollTop) => Render(html, scrollTop, null, null, null);

        /// <summary>
        /// 显示页面；spans 不为空时可在渲染视图中直接编辑。同一页面原文未变时跳过，保留光标与撤销记录。
        /// Shows a page; it is editable in place when spans is given. The same page with an unchanged source is skipped to keep the caret and undo history.
        /// </summary>
        public void Render(string html, bool scrollTop, string key, string source, int[][] spans)
        {
            string normalized = source == null ? null : NotebookStore.NormalizeNewLines(source, "\n");
            if (!scrollTop && spans != null && _spans != null && key == _key && normalized == _source) return;
            _html = html ?? "";
            _key = key;
            _spans = spans;
            _source = normalized;
            _raw = source;
            _scrollTop |= scrollTop;
            Flush();
        }

        /// <summary>把焦点放到渲染视图的可编辑区域末尾。/ Focuses the end of the rendered view's editable area.</summary>
        public void FocusEditor()
        {
            if (!_ready || IsDisposed) return;
            _web.Focus();
            Post(new { focus = true });
        }

        /// <summary>在渲染视图的光标处插入图片（Markdown 目标, data URI）。/ Inserts images at the rendered view's caret (Markdown target, data URI).</summary>
        public void InsertImages(IEnumerable<KeyValuePair<string, string>> images)
        {
            var list = images.Select(i => new { md = i.Key, data = i.Value }).ToArray();
            if (list.Length > 0) Post(new { images = list });
        }

        private void Post(object message)
        {
            if (!_ready || IsDisposed) return;
            try { _web.CoreWebView2.PostWebMessageAsJson(_json.Serialize(message)); }
            catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
            {
                ShowError("阅读视图更新失败 / Preview update failed: " + ex.Message);
            }
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
                        var message = _json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
                        if (message == null) return;
                        if (message.TryGetValue("note", out var note) && note is string target && NotebookMarkdown.IsNoteLink(target))
                            NoteLinkRequested?.Invoke(target);
                        else if (message.TryGetValue("open", out var open) && open is string link)
                            OpenExternal(link);
                        else if (message.TryGetValue("md", out var md) && md is string markdown && message.TryGetValue("key", out var key) && key is string page)
                        {
                            if (page == _key) _source = NotebookStore.NormalizeNewLines(markdown, "\n");
                            MarkdownEdited?.Invoke(page, markdown);
                        }
                        else if (message.TryGetValue("focused", out var focused) && focused is bool hasFocus)
                            EditorFocused = hasFocus;
                        else if (message.ContainsKey("pasteImage"))
                            ImagePasteRequested?.Invoke();
                        else if (message.ContainsKey("edit"))
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
                _web.CoreWebView2.PostWebMessageAsJson(_json.Serialize(new { html = _html, top = _scrollTop, key = _key ?? "", src = _raw ?? "", spans = _spans, editable = _spans != null }));
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
main.has-journal{max-width:1100px;padding:32px 32px 100px}.task-journal [hidden]{display:none!important}
.tj-filters{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:12px}.tj-filters button{font:inherit;font-size:13px;line-height:1.6;color:var(--secondary);background:var(--surface);border:1px solid var(--border);border-radius:6px;padding:5px 12px;cursor:pointer}
.tj-filters button[aria-pressed=true]{color:var(--accent-text);background:var(--accent-light);border-color:var(--accent)}.tj-filters button:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
.tj-summary{font-size:13px;color:var(--secondary);white-space:nowrap;overflow:auto;margin:0 0 28px}.tj-summary span{color:var(--muted)}.tj-empty{color:var(--secondary)}
.tj-day{margin:28px 0}.tj-day h1{margin:0 0 20px}.tj-project{margin:0 0 28px}.tj-project h2{font-size:18px;margin:0 0 10px}.tj-count{font-variant-numeric:tabular-nums}
.tj-scroll{overflow-x:auto;border:1px solid var(--border);border-radius:8px}.tj-table{display:table;table-layout:fixed;width:100%;min-width:680px;margin:0;line-height:1.6;font-size:13px}
.tj-time{width:104px}.tj-status{width:224px}.tj-duration{width:125px}.tj-table th,.tj-table td{border:0;border-bottom:1px solid var(--divider);padding:10px 12px}.tj-table th{font-size:12px;color:var(--secondary);white-space:nowrap}.tj-table tbody tr:last-child td{border-bottom:0}
.task-journal{--pf:var(--none-fg);--pb:var(--none-bg);--pd:var(--none-dot)}.tj-table tbody tr{cursor:pointer}
.tj-table tbody tr:hover,.tj-table tbody tr:focus-within{background:var(--surface)}.tj-table td:first-child,.tj-table td:last-child{white-space:nowrap;font-variant-numeric:tabular-nums;color:var(--secondary)}
.tj-task a{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;text-decoration:none;color:var(--text)}.tj-task a:hover{color:var(--accent-text)}
::-webkit-scrollbar{width:10px;height:10px}::-webkit-scrollbar-track{background:var(--bg)}::-webkit-scrollbar-thumb{background:var(--border);border-radius:5px}::-webkit-scrollbar-thumb:hover{background:var(--elevated)}
main[contenteditable=true]{outline:none;min-height:100vh;caret-color:var(--accent-text)}main[contenteditable=true]:empty::before{content:attr(data-placeholder);color:var(--muted)}
main[contenteditable=true] input[type=checkbox]{pointer-events:auto;cursor:pointer}main[contenteditable=true] .note-card,main[contenteditable=true] img{cursor:default}
@media(max-width:600px){main,main.has-journal{padding:32px 24px 80px}h1{font-size:28px}}
</style></head><body><main id='note'></main><script nonce='{{nonce}}'>
const journalFilters=new Map();
function filterJournal(journal,status){
journalFilters.set(journal.dataset.journal,status);
journal.querySelectorAll('[data-status-filter]').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.statusFilter===status)));
journal.querySelectorAll('[data-task-status]').forEach(r=>r.hidden=status!=='all'&&r.dataset.taskStatus!==status);
journal.querySelectorAll('.tj-project').forEach(g=>{const count=g.querySelectorAll('tbody tr:not([hidden])').length;g.hidden=count===0;g.querySelector('.tj-count').textContent=count;});
journal.querySelectorAll('.tj-day').forEach(d=>d.hidden=!d.querySelector('.tj-project:not([hidden])'));
journal.querySelector('.tj-empty').hidden=!!journal.querySelector('tbody tr:not([hidden])');
}
document.addEventListener('click',e=>{
if(!e.target.closest)return;
const filter=e.target.closest('[data-status-filter]');if(filter){filterJournal(filter.closest('.task-journal'),filter.dataset.statusFilter);return;}
if(doc.editable&&note.contains(e.target)){
// 就地编辑时单击页面链接直接跳转；网页链接单击只定位光标，Ctrl+单击才打开 / While editing in place page links open on click; web links only place the caret unless Ctrl+Clicked
if(e.target.matches('input[type=checkbox]')){e.preventDefault();return;}
const link=e.target.closest('a');if(!link)return;e.preventDefault();if(!link.dataset.note&&!e.ctrlKey)return;
if(link.dataset.note)post({note:link.dataset.note});else if(/^https?:/i.test(link.getAttribute('href')||''))post({open:link.getAttribute('href')});
return;}
const row=e.target.closest('tr[data-task-status]');const a=e.target.closest('a[data-note]')||(row&&row.querySelector('a[data-note]'));
if(!a)return;e.preventDefault();post({note:a.getAttribute('data-note')});
});
document.addEventListener('dblclick',e=>{if(doc.editable||(e.target.closest&&e.target.closest('a,button,.tj-table')))return;post({edit:true});});
const note=document.getElementById('note');
let doc={key:'',src:'',spans:[],snap:{},editable:false},timer=0,lastRange=null;
document.execCommand('defaultParagraphSeparator',false,'p');
function post(m){window.chrome.webview.postMessage(m);}
function prep(){
if(!doc.editable){note.removeAttribute('contenteditable');return;}
note.contentEditable='true';note.spellcheck=false;note.dataset.placeholder='在这里输入… / Start typing…';
note.querySelectorAll('.note-card,[data-raw],.image-placeholder').forEach(x=>x.contentEditable='false');
note.querySelectorAll('input[type=checkbox]').forEach(c=>{c.removeAttribute('disabled');c.tabIndex=-1;});
note.querySelectorAll('a:not([data-note])').forEach(a=>a.title='Ctrl+单击打开 / Ctrl+Click to open');
doc.snap={};for(const x of note.children)if(x.dataset.b!=null)doc.snap[x.dataset.b]=x.outerHTML;
}
function schedule(){if(!doc.editable)return;clearTimeout(timer);timer=setTimeout(flush,150);}
function flush(){if(!timer)return;clearTimeout(timer);timer=0;post({md:serialize(),key:doc.key});}
// —— 把编辑后的 DOM 转回 Markdown：未改动的块沿用原文 / Turn the edited DOM back into Markdown: untouched blocks keep their source ——
const BLOCK=/^(P|DIV|H[1-6]|UL|OL|LI|BLOCKQUOTE|PRE|HR|TABLE|SECTION|ARTICLE|HEADER|FOOTER|FIGURE)$/;
let inTable=false;
function groups(c){const out=[];let run=null;for(const n of Array.from(c.childNodes)){if(n.nodeType===1&&BLOCK.test(n.tagName)){run=null;out.push({el:n});}else{if(!run){run={inl:[]};out.push(run);}run.inl.push(n);}}return out;}
function serialize(){
const parts=[],used=new Set();
for(const g of groups(note)){
if(g.el){const b=g.el.dataset.b;
if(b!=null&&!used.has(b)&&doc.spans[b]&&(doc.snap[b]===g.el.outerHTML||g.el.contentEditable==='false')){used.add(b);parts.push({b:+b});continue;}
const s=blockMd(g.el);if(s)parts.push({md:s});}
else{const s=para(g.inl);if(s)parts.push({md:s});}
}
let out='';
parts.forEach((p,i)=>{
if(p.b==null){out+=(i?'\n\n':'')+p.md;return;}
const sp=doc.spans[p.b],prev=parts[i-1];
if(!prev)out+=p.b===0?doc.src.slice(0,sp[0]):'';
else if(prev.b===p.b-1)out+=doc.src.slice(doc.spans[prev.b][1],sp[0]);
else out+='\n\n';
out+=doc.src.slice(sp[0],sp[1]);
});
const last=parts[parts.length-1];
if(last&&last.b!=null&&last.b===doc.spans.length-1)out+=doc.src.slice(doc.spans[last.b][1]);else if(out)out+='\n';
return out;
}
function blocks(c,tight){return groups(c).map(g=>g.el?blockMd(g.el):para(g.inl)).filter(s=>s!=='').join(tight?'\n':'\n\n');}
function blockMd(el){
const t=el.tagName;
if(el.dataset.raw!=null||el.classList.contains('note-card'))return '';
if(/^H[1-6]$/.test(t)){const s=para(Array.from(el.childNodes)).replace(/\\\n|\n/g,' ');return s?'#'.repeat(+t[1])+' '+s:'';}
if(t==='UL'||t==='OL')return list(el);
if(t==='BLOCKQUOTE'){const s=blocks(el);return s?s.split('\n').map(l=>l?'> '+l:'>').join('\n'):'';}
if(t==='PRE')return fence(el);
if(t==='HR')return '---';
if(t==='TABLE')return table(el);
return blocks(el);
}
function para(nodes){return lineEsc(nodes.map(inl).join('')).replace(/^(?:\s|\\\n)+|(?:\s|\\\n)+$/g,'');}
function lineEsc(s){return s.split('\n').map(l=>l.replace(/^[ \t]+/,'').replace(/^(#{1,6}|[>+-])(?=[ \t]|$)/,'\\$1').replace(/^(\d{1,9})([.)])(?=[ \t]|$)/,'$1\\$2').replace(/^(=+|-+)[ \t]*$/,'\\$&')).join('\n');}
function esc(s){
s=s.replace(/\u00a0/g,' ').replace(/[\\`*\[\]<~^]/g,'\\$&').replace(/&(?=#?\w+;)/g,'\\&').replace(/==/g,'\\=\\=').replace(/\+\+/g,'\\+\\+')
.replace(/_/g,(m,i,x)=>/[\p{L}\p{N}]/u.test(x[i-1]||'')&&/[\p{L}\p{N}]/u.test(x[i+1]||'')?'_':'\\_');
return inTable?s.replace(/\|/g,'\\|'):s;}
function wrap(m,s){const x=s.match(/^(\s*)([\s\S]*?)(\s*)$/);return x[2]?x[1]+m+x[2]+m+x[3]:s;}
function code(s){s=s.replace(/\u00a0/g,' ');if(!s)return '';let f='`';while(s.includes(f))f+='`';const pad=/^`|`$|^ [\s\S]* $/.test(s)?' ':'';return f+pad+s+pad+f;}
function dest(u){return /[\s()<>]/.test(u)?'<'+u.replace(/[<>\n]/g,encodeURIComponent)+'>':u;}
function title(n){return n.dataset.title?' \u0022'+n.dataset.title.replace(/[\u0022\\]/g,'\\$&')+'\u0022':'';}
function linkMd(n,kids){const target=n.dataset.md!=null?n.dataset.md:(n.dataset.note||n.getAttribute('href')||'');if(!kids.trim()||!target)return kids;if(n.dataset.auto&&n.textContent===target)return '<'+target+'>';return '['+kids+']('+dest(target)+title(n)+')';}
function inl(n){
if(n.nodeType===3)return esc(n.nodeValue);
if(n.nodeType!==1)return '';
const t=n.tagName,kids=()=>Array.from(n.childNodes).map(inl).join('');
if(n.dataset.md!=null&&(t==='IMG'||n.classList.contains('image-placeholder')))return '!['+esc(n.dataset.alt||'')+']('+dest(n.dataset.md)+title(n)+')';
switch(t){
case 'BR':return '\\\n';
case 'STRONG':case 'B':return wrap('**',kids());
case 'EM':case 'I':return wrap('*',kids());
case 'DEL':case 'S':case 'STRIKE':return wrap('~~',kids());
case 'SUB':return wrap('~',kids());
case 'SUP':return wrap('^',kids());
case 'INS':return wrap('++',kids());
case 'MARK':return wrap('==',kids());
case 'CODE':return code(n.textContent);
case 'INPUT':case 'IMG':case 'SCRIPT':case 'STYLE':return '';
case 'A':return linkMd(n,kids());
case 'SPAN':return n.classList.contains('md-link')?linkMd(n,kids()):kids();
default:return kids();
}
}
function indent(s,w){const pad=' '.repeat(w);return s.split('\n').map((l,i)=>i&&l?pad+l:l).join('\n');}
function list(el){
const ol=el.tagName==='OL';let n=ol?(parseInt(el.getAttribute('start')||'1',10)||1):0,w=2;
const loose=Array.from(el.children).some(li=>Array.from(li.children).some(c=>c.tagName==='P'));
const items=[];
for(const c of Array.from(el.children)){
if(c.tagName==='UL'||c.tagName==='OL'){const s=list(c);if(!s)continue;if(items.length)items[items.length-1]+='\n'+' '.repeat(w)+indent(s,w);else items.push(s);continue;}
const marker=ol?(n++)+'. ':'- ';w=marker.length;
const cb=c.querySelector(':scope>input[type=checkbox],:scope>p:first-child>input[type=checkbox]');
const body=(cb?(cb.hasAttribute('checked')?'[x] ':'[ ] '):'')+blocks(c,!loose);
items.push((marker+indent(body,w)).replace(/\s+$/,''));
}
return items.join(loose?'\n\n':'\n');
}
function table(el){
const rows=Array.from(el.rows);if(!rows.length)return '';
const cols=Math.max(...rows.map(r=>r.cells.length));
inTable=true;
try{
const line=r=>{const a=Array.from(r.cells).map(c=>para(Array.from(c.childNodes)).replace(/\\\n|\n/g,' '));while(a.length<cols)a.push('');return '| '+a.join(' | ')+' |';};
const al=Array.from({length:cols},(_,i)=>{const c=rows[0].cells[i];const m=c?/text-align:\s*(\w+)/.exec(c.getAttribute('style')||''):null;const a=c?(m?m[1]:c.getAttribute('align')||''):'';return a==='center'?':---:':a==='right'?'---:':a==='left'?':---':'---';});
return [line(rows[0]),'| '+al.join(' | ')+' |'].concat(rows.slice(1).map(line)).join('\n');
}finally{inTable=false;}
}
function fence(pre){const c=pre.querySelector('code')||pre;const lang=((c.className||'').match(/language-(\S+)/)||[])[1]||'';const t=c.innerText.replace(/\n$/,'');let f='```';while(t.includes(f))f+='`';return f+lang+'\n'+t+'\n'+f;}
// —— 编辑操作 / Editing behaviour ——
function topBlock(n){while(n&&n.parentNode!==note)n=n.parentNode;return n&&n.nodeType===1?n:null;}
function up(n,tag){for(;n&&n!==note;n=n.parentNode)if(n.nodeName===tag)return n;return null;}
function caret(node,offset){const r=document.createRange();r.setStart(node,offset);r.collapse(true);const s=getSelection();s.removeAllRanges();s.addRange(r);}
function caretEnd(el){const r=document.createRange();r.selectNodeContents(el);r.collapse(false);const s=getSelection();s.removeAllRanges();s.addRange(r);}
function emptyP(){const p=document.createElement('p');p.appendChild(document.createElement('br'));return p;}
function atStart(el,s){const r=document.createRange();r.selectNodeContents(el);r.setEnd(s.anchorNode,s.anchorOffset);return r.toString()===''&&!r.cloneContents().querySelector('img');}
function retag(el,tag){const n=document.createElement(tag);while(el.firstChild)n.appendChild(el.firstChild);el.replaceWith(n);caret(n,0);}
function focusEnd(){if(!doc.editable)return;note.focus();let last=note.lastElementChild;if(!last||last.contentEditable==='false'||!/^(P|H[1-6])$/.test(last.tagName)){last=emptyP();note.appendChild(last);}caretEnd(last);}
function autoFormat(){
// 行首输入 #、-、1.、>、- [ ] 后按空格即转为标题、列表、引用、待办 / Typing #, -, 1., > or - [ ] then a space at a line start makes a heading, list, quote or to-do
const s=getSelection();if(!s.rangeCount||!s.isCollapsed)return;
const blk=topBlock(s.anchorNode);if(!blk||!/^(P|DIV)$/.test(blk.tagName)||blk.contentEditable==='false')return;
const r=document.createRange();r.setStart(blk,0);r.setEnd(s.anchorNode,s.anchorOffset);
const t=r.toString().replace(/\u00a0/g,' ');let m,outer,inner,task=null;
if(m=t.match(/^(#{1,6}) $/))outer=inner=document.createElement('h'+m[1].length);
else if(m=t.match(/^[-*+] \[([ xX]?)\] $/)){outer=document.createElement('ul');inner=outer.appendChild(document.createElement('li'));inner.className='task-list-item';task=document.createElement('input');task.type='checkbox';task.className='task-list-item-checkbox';task.tabIndex=-1;if(/x/i.test(m[1]))task.setAttribute('checked','');}
else if(/^[-*+] $/.test(t)){outer=document.createElement('ul');inner=outer.appendChild(document.createElement('li'));}
else if(m=t.match(/^(\d{1,9})[.)] $/)){outer=document.createElement('ol');if(m[1]!=='1')outer.setAttribute('start',m[1]);inner=outer.appendChild(document.createElement('li'));}
else if(t==='> '){outer=document.createElement('blockquote');inner=outer.appendChild(document.createElement('p'));}
else return;
r.deleteContents();
while(blk.firstChild)inner.appendChild(blk.firstChild);
let text=null;if(task){text=document.createTextNode(' ');inner.prepend(task,text);}
if(!inner.textContent.trim()&&!inner.querySelector('img,br'))inner.appendChild(document.createElement('br'));
blk.replaceWith(outer);if(text)caret(text,1);else caret(inner,0);
}
note.addEventListener('input',e=>{if(!doc.editable)return;if(e.inputType==='insertText'&&e.data===' ')autoFormat();schedule();});
note.addEventListener('keydown',e=>{
if(!doc.editable||e.isComposing)return;
const s=getSelection();if(!s.rangeCount)return;
const pre=up(s.anchorNode,'PRE'),blk=topBlock(s.anchorNode);
if(e.key==='Enter'&&!e.shiftKey&&!e.ctrlKey&&!e.altKey){
if(pre){
e.preventDefault();
const whole=document.createRange();whole.selectNodeContents(pre);const a=whole.cloneRange(),b=whole.cloneRange();a.setStart(s.anchorNode,s.anchorOffset);b.setEnd(s.anchorNode,s.anchorOffset);
// 代码块末尾空行再按 Enter 退出代码块 / Enter on an empty last line leaves the code block
if(s.isCollapsed&&/\n$/.test(b.toString())&&/^\n?$/.test(a.toString())){const c=pre.querySelector('code')||pre;c.textContent=c.textContent.replace(/\n+$/,'')+'\n';const p=emptyP();pre.after(p);caret(p,0);schedule();}
else document.execCommand('insertText',false,'\n');
return;}
if(blk&&blk.tagName==='P'){const m=blk.textContent.replace(/\u00a0/g,' ').match(/^```\s*([\w+#.-]*)\s*$/);
if(m){e.preventDefault();const p=document.createElement('pre'),c=p.appendChild(document.createElement('code'));if(m[1])c.className='language-'+m[1];c.textContent='\n';blk.replaceWith(p);caret(c.firstChild,0);schedule();}}
return;}
if(e.key==='Tab'){
if(up(s.anchorNode,'LI')){e.preventDefault();document.execCommand(e.shiftKey?'outdent':'indent');schedule();}
else if(pre){e.preventDefault();document.execCommand('insertText',false,'    ');}
return;}
if(e.key==='Backspace'&&s.isCollapsed&&blk&&!pre){
if(/^H[1-6]$/.test(blk.tagName)&&atStart(blk,s)){e.preventDefault();retag(blk,'p');schedule();}
else if(blk.tagName==='BLOCKQUOTE'&&atStart(blk,s)){e.preventDefault();const first=blk.firstElementChild;blk.replaceWith(...Array.from(blk.childNodes));if(first)caret(first,0);schedule();}
}
});
note.addEventListener('mousedown',e=>{
if(!doc.editable)return;
const t=e.target;
if(t.matches&&t.matches('input[type=checkbox]')){e.preventDefault();t.toggleAttribute('checked');t.checked=t.hasAttribute('checked');schedule();return;}
if(t!==note)return;
// 点击正文下方空白处时在末尾继续输入 / Clicking the blank area below the content continues typing at the end
const last=note.lastElementChild;if(last&&e.clientY<=last.getBoundingClientRect().bottom)return;
e.preventDefault();focusEnd();
});
note.addEventListener('paste',e=>{
if(!doc.editable||!e.clipboardData)return;
e.preventDefault();
const text=e.clipboardData.getData('text/plain');
if(text){document.execCommand('insertText',false,text.replace(/\r\n?/g,'\n'));return;}
if(Array.from(e.clipboardData.items||[]).some(i=>i.kind==='file'))post({pasteImage:true});
});
note.addEventListener('focus',()=>post({focused:true}));
note.addEventListener('blur',()=>{flush();post({focused:false});});
window.addEventListener('blur',flush);
document.addEventListener('selectionchange',()=>{const s=getSelection();if(s.rangeCount&&note.contains(s.anchorNode))lastRange=s.getRangeAt(0).cloneRange();});
function insertImages(list){
if(!doc.editable)return;
let r=lastRange&&note.contains(lastRange.startContainer)?lastRange:null;
if(!r){const p=emptyP();note.appendChild(p);r=document.createRange();r.selectNodeContents(p);}
r.deleteContents();
const frag=document.createDocumentFragment();let last=null;
for(const i of list){const img=document.createElement('img');img.src=i.data;img.alt='Local image';img.dataset.md=i.md;img.dataset.alt='图片 / Image';frag.appendChild(img);last=img;}
if(!last)return;
r.insertNode(frag);note.focus();const s=getSelection();const after=document.createRange();after.setStartAfter(last);after.collapse(true);s.removeAllRanges();s.addRange(after);
schedule();
}
window.chrome.webview.addEventListener('message',e=>{
const d=e.data;
if(d.images){insertImages(d.images);return;}
if(d.focus){focusEnd();return;}
clearTimeout(timer);timer=0;lastRange=null;
const y=window.scrollY;
doc={key:d.key||'',src:d.src||'',spans:d.spans||[],snap:{},editable:!!d.editable};
note.innerHTML=d.html;const journal=note.querySelector('.task-journal');note.classList.toggle('has-journal',!!journal);if(journal)filterJournal(journal,journalFilters.get(journal.dataset.journal)||'all');
prep();
window.scrollTo(0,d.top?0:y);
});
</script></body></html>".Replace("{{nonce}}", nonce).Replace("{{palette}}", palette).Replace("{{font}}", Theme.FontName);
        }
    }
}
