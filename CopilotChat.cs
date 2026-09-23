using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace VSManager
{
    public enum ChatRole { User, Assistant }

    public class ChatPart
    {
        public bool IsStep;
        public string Text;
    }

    public class ChatMessage
    {
        public ChatRole Role;
        public List<ChatPart> Parts = new List<ChatPart>();
    }

    public class ChatTranscript
    {
        public bool PaneFound;
        public string Title = "";
        public List<ChatMessage> Messages = new List<ChatMessage>();
        public string Signature = "";
        /// <summary>ReadTail 要求“未变化则跳过”且消息列表确实未变时为 true（此时 Messages 为空）。</summary>
        public bool Unchanged;
    }

    /// <summary>通过 UI Automation 读取 / 操作 VS 中 GitHub Copilot 对话窗格。</summary>
    public partial class CopilotChat
    {
        private readonly Func<AppSettings> _getSettings;
        private readonly Dictionary<int, AutomationElement> _panes = new Dictionary<int, AutomationElement>();
        private readonly Dictionary<int, IntPtr> _hosts = new Dictionary<int, IntPtr>();
        private readonly Dictionary<string, ChatMessage> _msgCache = new Dictionary<string, ChatMessage>();
        private readonly object _lock = new object();
        private Thread _thread;
        private volatile bool _running;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        /// <summary>当前需要同步对话内容的 VS。</summary>
        public volatile VsInstance Target;
        public volatile bool Paused;
        /// <summary>主窗口不可见时暂停同步，减少对 VS 的 UIA 调用。</summary>
        public volatile bool Hidden;

        /// <summary>手机网页遥控正在使用时返回 true：窗口隐藏时仍继续同步所有 VS 的对话。</summary>
        public Func<bool> RemoteActive;

        private volatile int _focusPid;
        private long _focusUntil;

        /// <summary>远程端正在查看该 VS：接下来一段时间内更频繁地同步它。</summary>
        public void Focus(int pid)
        {
            bool changed = _focusPid != pid;
            _focusPid = pid;
            Interlocked.Exchange(ref _focusUntil, DateTime.Now.AddSeconds(20).Ticks);
            if (changed) _wake.Set();
        }

        private bool IsFocused(VsInstance v) => v.Pid == _focusPid && DateTime.Now.Ticks < Interlocked.Read(ref _focusUntil);
        /// <summary>对话更新（选中的 VS，或后台同步的其他 VS）。在后台线程触发。</summary>
        public event Action<VsInstance, ChatTranscript> Updated;
        /// <summary>全部 VS 实例，用于后台同步未选中 VS 的对话。</summary>
        public Func<IList<VsInstance>> Instances;

        public CopilotChat(Func<AppSettings> getSettings) { _getSettings = getSettings; }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "CopilotChat" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop() { _running = false; _wake.Set(); }

        /// <summary>立即刷新一次（完整读取）。</summary>
        public void Poke() { _force = true; _wake.Set(); }

        /// <summary>窗格已被外部切回：清除未找到节流并立即重新同步。</summary>
        public void PaneRestored(int pid)
        {
            lock (_lock) { _missUntil.Remove(pid); }
            Poke();
        }

        /// <summary>请求在目标 VS 中打开 Copilot 对话窗格。</summary>
        public void RequestOpen() { _openRequested = true; _force = true; _wake.Set(); }

        private volatile bool _openRequested;
        /// <summary>正在自动打开对话窗格时触发（后台线程）。</summary>
        public event Action<VsInstance> Opening;

        private volatile bool _force;
        private VsInstance _lastVs;
        private string _lastQuick;
        private CopilotState _lastState;
        private DateTime _lastFull;

        private void Loop()
        {
            while (_running)
            {
                var vs = Target;
                if (vs != null && !Paused && !Hidden)
                {
                    try
                    {
                        // 完整读取开销较大：空闲且消息列表未变化时跳过
                        string quick = QuickSignature(vs, _force || vs != _lastVs);
                        // 选中新的 VS（或手动请求）时，若未找到对话窗格则自动在该 VS 中打开
                        bool wantOpen = _openRequested || (vs != _lastVs && (_getSettings()?.AutoOpenChat ?? false));
                        if (quick == null && wantOpen)
                        {
                            _openRequested = false;
                            Opening?.Invoke(vs);
                            if (OpenPane(vs, 6000) != null) quick = QuickSignature(vs, true);
                        }
                        _openRequested = false;
                        bool full = _force || vs != _lastVs || quick != _lastQuick || vs.Copilot == CopilotState.Busy ||
                                    vs.Copilot != _lastState || (DateTime.Now - _lastFull).TotalSeconds > 20;
                        if (full)
                        {
                            _force = false;
                            var state = vs.Copilot;
                            ChatTranscript t;
                            try { t = quick == null ? new ChatTranscript() : Read(vs); } catch { t = new ChatTranscript(); }
                            _lastVs = vs; _lastQuick = quick; _lastState = state; _lastFull = DateTime.Now;
                            if (vs == Target) Updated?.Invoke(vs, t);
                        }
                    }
                    catch { }
                }
                bool remote = false;
                try { remote = RemoteActive?.Invoke() ?? false; } catch { }
                if (!Paused && (!Hidden || remote) && (remote || (_getSettings()?.BackgroundSync ?? false)))
                {
                    // 窗口隐藏时选中的 VS 不会被上面的逻辑读取，因此同步全部 VS
                    try { SyncOne(Hidden ? null : vs); } catch { }
                }
                int wait = vs != null && vs.Copilot == CopilotState.Busy ? 1500 : 2000;
                _wake.WaitOne(wait);
            }
        }

        private class SyncState
        {
            public string Quick;
            public CopilotState State;
            public DateTime LastCheck, LastFull;
            public bool Known;
        }

        private readonly Dictionary<int, SyncState> _sync = new Dictionary<int, SyncState>();
        private int _syncCursor;

        /// <summary>
        /// 每轮只检查一个未选中的 VS（轮询），用轻量签名判断是否变化，变化时才完整读取并通过 Updated 通知缓存。
        /// 运行中的 VS 约 4 秒检查一次，空闲的约 10 秒；不会自动打开对话窗格。
        /// </summary>
        private void SyncOne(VsInstance target)
        {
            var all = Instances?.Invoke();
            if (all == null || all.Count == 0) return;
            var alive = new HashSet<int>();
            foreach (var x in all) alive.Add(x.Pid);
            foreach (var pid in new List<int>(_sync.Keys)) if (!alive.Contains(pid)) _sync.Remove(pid);

            var now = DateTime.Now;
            for (int n = 0; n < all.Count; n++)
            {
                var v = all[(_syncCursor + n) % all.Count];
                if (v == target) continue;
                if (!_sync.TryGetValue(v.Pid, out var st)) _sync[v.Pid] = st = new SyncState();
                double interval = IsFocused(v) ? (v.Copilot == CopilotState.Busy ? 1.5 : 3) : v.Copilot == CopilotState.Busy ? 4 : 10;
                bool stateChanged = st.Known && v.Copilot != st.State;
                if (st.Known && !stateChanged && (now - st.LastCheck).TotalSeconds < interval) continue;

                _syncCursor = (_syncCursor + n + 1) % all.Count;
                st.LastCheck = now;
                string quick = QuickSignature(v, false);
                bool full = !st.Known || quick != st.Quick || stateChanged || (quick != null && (now - st.LastFull).TotalSeconds > 60);
                var state = v.Copilot;
                if (full)
                {
                    ChatTranscript t;
                    try { t = quick == null ? new ChatTranscript() : Read(v); } catch { t = new ChatTranscript(); }
                    st.LastFull = now;
                    if (v != Target || Hidden) Updated?.Invoke(v, t);
                }
                st.Quick = quick; st.State = state; st.Known = true;
                return;
            }
        }

        /// <summary>轻量签名：消息数 + 最后一条消息名称 + 标题。未找到窗格返回 null。</summary>
        private string QuickSignature(VsInstance vs, bool force)
        {
            var pane = FindPane(vs, !force);
            if (pane == null) return null;
            var cr = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, AutomationElementMode = AutomationElementMode.None, TreeFilter = Automation.RawViewCondition };
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.ClassNameProperty);
            cr.Add(AutomationElement.AutomationIdProperty);
            var sb = new StringBuilder();
            using (cr.Activate())
            {
                var p = pane.GetUpdatedCache(cr);
                foreach (AutomationElement c in p.CachedChildren)
                {
                    if (c.Cached.AutomationId == "chatTitle") sb.Append(c.Cached.Name).Append('|');
                }
                var list = pane.FindFirst(TreeScope.Children, IdCond("PART_MainListView"));
                if (list != null)
                {
                    var kids = list.CachedChildren.Cast<AutomationElement>().Where(k => k.Cached.ClassName == "ChatMessageItem").ToList();
                    sb.Append(kids.Count).Append('|');
                    if (kids.Count > 0) sb.Append(kids[kids.Count - 1].Cached.Name);
                }
            }
            return sb.ToString();
        }

        private string Keyword
        {
            get
            {
                var k = _getSettings()?.CopilotPaneKeyword;
                return string.IsNullOrWhiteSpace(k) ? "Copilot" : k;
            }
        }

        #region 查找窗格

        private static readonly PropertyCondition GenericPaneCond = new PropertyCondition(AutomationElement.ClassNameProperty, "GenericPane");
        private static Condition IdCond(string id) => new PropertyCondition(AutomationElement.AutomationIdProperty, id);

        /// <summary>
        /// 查找对话列表。某些状态下（如 Copilot 运行中、停靠在文档区）ChatSessionList 的 IsControlElement 为 false，
        /// 默认的控件视图搜索会漏掉它，因此在原始视图中查找。
        /// </summary>
        private static AutomationElement FindList(AutomationElement pane)
        {
            var cr = new CacheRequest { TreeFilter = Automation.RawViewCondition, AutomationElementMode = AutomationElementMode.Full };
            cr.Add(AutomationElement.AutomationIdProperty);
            using (cr.Activate()) return pane.FindFirst(TreeScope.Children, IdCond("PART_MainListView"));
        }

        private readonly Dictionary<int, DateTime> _missUntil = new Dictionary<int, DateTime>();

        /// <param name="throttleMiss">为 true 时，最近未找到窗格的 VS 在冷却期内直接返回 null（完整搜索开销大）。</param>
        public AutomationElement FindPane(VsInstance vs, bool throttleMiss = false)
        {
            lock (_lock)
            {
                if (throttleMiss && !_panes.ContainsKey(vs.Pid) && _missUntil.TryGetValue(vs.Pid, out var until) && DateTime.Now < until)
                    return null;
                if (_panes.TryGetValue(vs.Pid, out var cached))
                {
                    try
                    {
                        if (cached.Current.Name != null && FindList(cached) != null)
                        {
                            T("使用缓存的对话窗格 " + Describe(cached));
                            return cached;
                        }
                    }
                    catch { }
                    _panes.Remove(vs.Pid);
                }
            }

            string kw = Keyword;
            AutomationElement best = null;
            IntPtr bestHost = IntPtr.Zero;
            int candidates = 0;
            foreach (var h in Native.GetProcessWindows(vs.Pid))
            {
                AutomationElement root;
                try { root = AutomationElement.FromHandle(h); } catch { continue; }
                AutomationElementCollection panes;
                try { panes = root.FindAll(TreeScope.Descendants, GenericPaneCond); } catch { continue; }
                foreach (AutomationElement p in panes)
                {
                    try
                    {
                        if ((p.Current.Name ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (FindList(p) == null) continue;
                        candidates++;
                        if (best == null || (!p.Current.IsOffscreen && best.Current.IsOffscreen)) { best = p; bestHost = h; }
                    }
                    catch { }
                }
                if (best != null && !SafeOffscreen(best)) break;
            }
            lock (_lock)
            {
                if (best != null) { _panes[vs.Pid] = best; _hosts[vs.Pid] = bestHost; _missUntil.Remove(vs.Pid); }
                else _missUntil[vs.Pid] = DateTime.Now.AddSeconds(10);
            }
            if (_trace != null)
                T(best == null ? "完整搜索：未找到对话窗格（关键字「" + kw + "」）"
                    : "完整搜索：找到 " + candidates + " 个对话窗格" + (candidates > 1 ? "（⚠ 多个，取可见的一个）" : "") + "，使用 " + Describe(best) + " host=0x" + bestHost.ToString("X"));
            return best;
        }

        private static bool SafeOffscreen(AutomationElement e)
        {
            try { return e.Current.IsOffscreen; } catch { return true; }
        }

        #endregion

        #region 读取对话

        public ChatTranscript Read(VsInstance vs, int maxMessages = 40)
        {
            var result = new ChatTranscript();
            var pane = FindPane(vs);
            if (pane == null) return result;
            result.PaneFound = true;

            try
            {
                var tcr = new CacheRequest { TreeFilter = Automation.RawViewCondition };
                tcr.Add(AutomationElement.AutomationIdProperty);
                using (tcr.Activate()) result.Title = pane.FindFirst(TreeScope.Children, IdCond("chatTitle"))?.Current.Name ?? "";
            }
            catch { }

            var list = FindList(pane);
            if (list == null) return result;

            var cr = new CacheRequest { TreeScope = TreeScope.Subtree, AutomationElementMode = AutomationElementMode.Full, TreeFilter = Automation.RawViewCondition };
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.ClassNameProperty);
            cr.Add(AutomationElement.AutomationIdProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(TextPattern.Pattern);
            AutomationElement tree;
            using (cr.Activate()) tree = list.GetUpdatedCache(cr);

            var items = tree.CachedChildren.Cast<AutomationElement>()
                .Where(e => e.Cached.ClassName == "ChatMessageItem").ToList();
            int start = Math.Max(0, items.Count - maxMessages);
            var used = new HashSet<string>();
            for (int i = start; i < items.Count; i++)
            {
                var item = items[i];
                var chat = Child(item, e => e.Cached.AutomationId == "Chat");
                if (chat == null) continue;
                var listBox = Child(chat, e => e.Cached.ControlType == ControlType.List);
                var lbis = listBox?.CachedChildren.Cast<AutomationElement>().ToList() ?? new List<AutomationElement>();
                bool assistant = Child(chat, e => e.Cached.ClassName == "Image" || e.Cached.Name == "GitHub Copilot"
                    || (e.Cached.AutomationId == "ResponseModelName" && !string.IsNullOrEmpty(e.Cached.Name))) != null;

                string key = $"{vs.Pid}|{i}|{item.Cached.Name}|{lbis.Count}";
                bool live = i >= items.Count - 2;
                ChatMessage msg;
                lock (_lock)
                {
                    if (live || !_msgCache.TryGetValue(key, out msg)) msg = null;
                }
                if (msg == null)
                {
                    msg = new ChatMessage { Role = assistant ? ChatRole.Assistant : ChatRole.User };
                    foreach (var lbi in lbis)
                    {
                        var part = ParsePart(lbi);
                        if (part != null) msg.Parts.Add(part);
                    }
                    if (msg.Parts.Count == 0 && !string.IsNullOrWhiteSpace(item.Cached.Name))
                        msg.Parts.Add(new ChatPart { Text = item.Cached.Name });
                    lock (_lock) _msgCache[key] = msg;
                }
                used.Add(key);
                result.Messages.Add(msg);
            }

            lock (_lock)
            {
                foreach (var k in _msgCache.Keys.Where(k => k.StartsWith(vs.Pid + "|") && !used.Contains(k)).ToList())
                    _msgCache.Remove(k);
            }

            var sb = new StringBuilder(result.Title);
            foreach (var m in result.Messages)
            {
                sb.Append('\u0001').Append((int)m.Role);
                foreach (var p in m.Parts) sb.Append('\u0002').Append(p.IsStep ? '1' : '0').Append(p.Text);
            }
            result.Signature = sb.ToString();
            return result;
        }

        /// <summary>
        /// 轻量读取：只取对话列表末尾的若干条消息（先浅层列出消息项，再只对末尾几项做子树缓存），
        /// 供后台监听各 VS 的对话使用，避免长对话每次完整遍历。不会自动打开窗格；未找到窗格时 PaneFound 为 false。
        /// </summary>
        private readonly Dictionary<int, string> _tailSig = new Dictionary<int, string>();

        public ChatTranscript ReadTail(VsInstance vs, int maxMessages = 2, bool skipIfUnchanged = false)
        {
            var result = new ChatTranscript();
            var pane = FindPane(vs, true);
            if (pane == null) return result;
            result.PaneFound = true;
            var list = FindList(pane);
            if (list == null) return result;

            var shallow = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, AutomationElementMode = AutomationElementMode.Full, TreeFilter = Automation.RawViewCondition };
            shallow.Add(AutomationElement.NameProperty);
            shallow.Add(AutomationElement.ClassNameProperty);
            List<AutomationElement> items;
            using (shallow.Activate())
                items = list.GetUpdatedCache(shallow).CachedChildren.Cast<AutomationElement>().Where(e => e.Cached.ClassName == "ChatMessageItem").ToList();
            // 浅层签名（消息数 + 最后一项名称）未变时跳过开销较大的子树读取
            string sig = items.Count + "|" + (items.Count > 0 ? items[items.Count - 1].Cached.Name : "");
            lock (_lock)
            {
                if (skipIfUnchanged && _tailSig.TryGetValue(vs.Pid, out var old) && old == sig) { result.Unchanged = true; return result; }
                _tailSig[vs.Pid] = sig;
            }

            var cr = new CacheRequest { TreeScope = TreeScope.Subtree, AutomationElementMode = AutomationElementMode.Full, TreeFilter = Automation.RawViewCondition };
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.ClassNameProperty);
            cr.Add(AutomationElement.AutomationIdProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(TextPattern.Pattern);
            var sb = new StringBuilder();
            for (int i = Math.Max(0, items.Count - maxMessages); i < items.Count; i++)
            {
                AutomationElement item;
                using (cr.Activate()) item = items[i].GetUpdatedCache(cr);
                var chat = Child(item, e => e.Cached.AutomationId == "Chat");
                if (chat == null) continue;
                var listBox = Child(chat, e => e.Cached.ControlType == ControlType.List);
                bool assistant = Child(chat, e => e.Cached.ClassName == "Image" || e.Cached.Name == "GitHub Copilot"
                    || (e.Cached.AutomationId == "ResponseModelName" && !string.IsNullOrEmpty(e.Cached.Name))) != null;
                var msg = new ChatMessage { Role = assistant ? ChatRole.Assistant : ChatRole.User };
                foreach (var lbi in listBox?.CachedChildren.Cast<AutomationElement>() ?? Enumerable.Empty<AutomationElement>())
                {
                    var part = ParsePart(lbi);
                    if (part != null) msg.Parts.Add(part);
                }
                if (msg.Parts.Count == 0 && !string.IsNullOrWhiteSpace(item.Cached.Name))
                    msg.Parts.Add(new ChatPart { Text = item.Cached.Name });
                result.Messages.Add(msg);
                sb.Append('\u0001').Append((int)msg.Role);
                foreach (var p in msg.Parts) sb.Append('\u0002').Append(p.Text);
            }
            result.Signature = items.Count + sb.ToString();
            return result;
        }

        private static AutomationElement Child(AutomationElement e, Func<AutomationElement, bool> pred)
        {
            try { return e.CachedChildren.Cast<AutomationElement>().FirstOrDefault(pred); } catch { return null; }
        }

        private static ChatPart ParsePart(AutomationElement lbi)
        {
            var doc = Child(lbi, e => e.Cached.ClassName == "FlowDocumentScrollViewer");
            if (doc != null)
            {
                // Copilot keeps the source Markdown on the item; TextPattern flattens its formatting.
                string text = lbi.Cached.Name;
                if (string.IsNullOrWhiteSpace(text) || text.StartsWith("System.", StringComparison.Ordinal))
                {
                    text = null;
                    try
                    {
                        if (doc.GetCachedPattern(TextPattern.Pattern) is TextPattern tp) text = tp.DocumentRange.GetText(-1);
                    }
                    catch (ElementNotAvailableException) { }
                    catch (InvalidOperationException) { }
                }
                return string.IsNullOrWhiteSpace(text) ? null : new ChatPart { Text = text.Trim() };
            }
            var group = Child(lbi, e => e.Cached.ControlType == ControlType.Group);
            string name = group?.Cached.Name;
            if (string.IsNullOrWhiteSpace(name)) name = lbi.Cached.Name;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("System.")) return null;
            return new ChatPart { IsStep = true, Text = name.Trim() };
        }

        #endregion

        #region 操作

        /// <summary>点击窗格中指定 AutomationId 的按钮（如 CancelButton / createNewThread）。</summary>
        public string InvokeButton(VsInstance vs, string automationId, string actionName)
        {
            var pane = FindPane(vs);
            if (pane == null) return "未找到 Copilot 对话窗格，请先在该 VS 中打开一次对话窗口";
            try
            {
                var btn = pane.FindFirst(TreeScope.Descendants, IdCond(automationId));
                if (btn == null) return $"当前无法{actionName}";
                ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Poke();
                return $"已{actionName}";
            }
            catch (Exception ex) { return $"{actionName}失败: {ex.Message}"; }
        }

        /// <summary>通过 DTE 命令在 VS 中打开对话窗格，并等待其出现在 UIA 树中。</summary>
        public AutomationElement OpenPane(VsInstance vs, int timeoutMs)
        {
            try
            {
                var t = DteWorker.Run(() => VsService.OpenCopilotChat(vs));
                if (!t.Wait(timeoutMs) || !t.Result) return null;
            }
            catch { return null; }
            lock (_lock) _missUntil.Remove(vs.Pid);
            var until = DateTime.Now.AddMilliseconds(timeoutMs);
            AutomationElement p;
            do
            {
                p = FindPane(vs);
                if (p != null && !SafeOffscreen(p)) return p;
                Thread.Sleep(300);
            } while (DateTime.Now < until);
            return p;
        }

        private static AutomationElement FindEdit(AutomationElement pane)
        {
            try { return pane?.FindFirst(TreeScope.Descendants, IdCond("WpfTextView")); } catch { return null; }
        }

        /// <summary>
        /// 把消息发送到 VS 的 Copilot 对话。优先后台发送（不切换窗口）；
        /// 无法后台聚焦输入框或消息包含换行时，退回到短暂激活 VS 并粘贴的方式。必须在 STA 线程调用。
        /// </summary>
        public string Send(VsInstance vs, string text, IntPtr returnTo, bool background, IReadOnlyList<ChatImage> images = null)
        {
            var trace = _trace = new SendLog.Trace(vs, text, images?.Count ?? 0, background);
            string r;
            try { r = SendCore(vs, text, returnTo, background, images); }
            catch (Exception ex) { T("异常：" + ex); r = "发送失败：" + ex.Message; }
            finally { _trace = null; }
            trace.Done(r);
            return r;
        }

        [ThreadStatic] private static SendLog.Trace _trace;

        private static void T(string s) => _trace?.Step(s);

        private static string Short(string s, int max = 40)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        private static string Describe(AutomationElement e)
        {
            try
            {
                var c = e.Current;
                var r = c.BoundingRectangle;
                return "「" + c.Name + "」offscreen=" + c.IsOffscreen + (r.IsEmpty ? " rect=空" : $" rect={(int)r.X},{(int)r.Y} {(int)r.Width}x{(int)r.Height}");
            }
            catch (Exception ex) { return "(元素已失效：" + ex.Message + ")"; }
        }

        private static bool HasCancel(AutomationElement pane)
        {
            try
            {
                var cr = new CacheRequest { TreeFilter = Automation.RawViewCondition };
                cr.Add(AutomationElement.AutomationIdProperty);
                AutomationElement btn;
                using (cr.Activate()) btn = pane.FindFirst(TreeScope.Children, IdCond("CancelButton"));
                // 与状态监控一致：隐藏（offscreen）的停止按钮不代表正在运行
                return btn != null && !btn.Current.IsOffscreen;
            }
            catch { return false; }
        }

        private static int ItemCount(AutomationElement pane)
        {
            try
            {
                var list = FindList(pane);
                if (list == null) return -1;
                var cr = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, TreeFilter = Automation.RawViewCondition };
                cr.Add(AutomationElement.ClassNameProperty);
                using (cr.Activate())
                    return list.GetUpdatedCache(cr).CachedChildren.Cast<AutomationElement>().Count(e => e.Cached.ClassName == "ChatMessageItem");
            }
            catch { return -1; }
        }

        private string LastUserText(VsInstance vs)
        {
            try
            {
                var m = Read(vs, 6).Messages.LastOrDefault(x => x.Role == ChatRole.User);
                return m == null ? "" : string.Concat(m.Parts.Select(p => p.Text));
            }
            catch { return null; }
        }

        /// <summary>输入框清空后，再确认对话中确实出现了新消息（或 Copilot 开始处理）。</summary>
        private bool ConfirmDelivered(VsInstance vs, AutomationElement pane, int itemsBefore, string text, int ms)
        {
            var until = DateTime.Now.AddMilliseconds(ms);
            int n;
            while (true)
            {
                if (HasCancel(pane)) { T("确认送达：出现停止按钮（Copilot 已开始处理）"); return true; }
                n = ItemCount(pane);
                if (itemsBefore >= 0 && n > itemsBefore) { T($"确认送达：对话条目 {itemsBefore} → {n}"); return true; }
                if (DateTime.Now >= until) break;
                Thread.Sleep(250);
            }
            // 对话列表是虚拟化的，条目数不一定增加；最后再读取一次最后一条用户消息（较慢）
            string want = Normalize(text);
            string head = want.Length > 24 ? want.Substring(0, 24) : want;
            string last = LastUserText(vs);
            if (last != null && head.Length > 0 && Normalize(last).Contains(head)) { T("确认送达：最后一条用户消息与发送内容一致"); return true; }
            T($"⚠ 未确认送达：{ms}ms 内未出现停止按钮，对话条目 {n}（发送前 {itemsBefore}），最后一条用户消息「{Short(last)}」");
            return false;
        }

        private string SendCore(VsInstance vs, string text, IntPtr returnTo, bool background, IReadOnlyList<ChatImage> images)
        {
            if (images != null && (images.Count > ChatImage.MaxCount || images.Any(image => image == null))) return "图片附件无效";
            bool hasImages = images != null && images.Count > 0;
            if (string.IsNullOrWhiteSpace(text) && !hasImages) return "消息为空";
            if (vs == null || !Native.IsWindow(vs.MainHwnd)) return "该 VS 已关闭";
            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

            var pane = FindPane(vs);
            if (pane == null || SafeOffscreen(pane))
            {
                T(pane == null ? "未找到对话窗格，尝试通过 DTE 打开" : "对话窗格不可见（offscreen），尝试通过 DTE 切回");
                pane = OpenPane(vs, 6000) ?? pane;
                if (pane != null) T("打开后窗格 " + Describe(pane));
            }
            if (pane == null) return "未找到 Copilot 对话窗格，自动打开失败（请确认该 VS 已安装并登录 GitHub Copilot）";
            var edit = FindEdit(pane);
            if (edit == null)
            {
                T("窗格中未找到输入框（WpfTextView），重新打开窗格");
                pane = OpenPane(vs, 3000) ?? pane;
                edit = FindEdit(pane);
            }
            if (edit == null) return "未找到 Copilot 输入框";
            T("输入框 " + Describe(edit) + " 焦点=" + HasFocus(edit) + " 现有草稿「" + Short(GetEditText(edit)) + "」 前台=" + ForegroundIs(vs));

            bool busyBefore = HasCancel(pane);
            int itemsBefore = ItemCount(pane);
            T($"发送前：对话条目 {itemsBefore}，停止按钮={busyBefore}");
            if (busyBefore) return "Copilot 仍在处理上一条消息（VS 中存在停止按钮），请等待完成或先停止后再发送";

            bool fgBefore = ForegroundIs(vs);
            string r = null;
            if (hasImages) r = SendImages(vs, pane, edit, text, images, returnTo);
            else
            {
                if (background && text.IndexOf('\n') < 0)
                {
                    try { r = SendBackground(vs, pane, edit, text); }
                    finally { Poke(); }
                    if (r == null) T("后台发送不可用，改为前台发送");
                }
                else if (background) T("消息包含换行，使用前台发送");
                if (r == null) r = SendForeground(vs, pane, edit, text, returnTo);
            }
            // 打开窗格等 DTE 命令可能把 VS 带到前台：发送后切回本工具，保持用户当前界面
            if (!fgBefore && returnTo != IntPtr.Zero && Native.IsWindow(returnTo) && ForegroundIs(vs))
            {
                T("发送后 VS 处于前台，切回本工具");
                Native.Activate(returnTo);
            }
            if (r.StartsWith("已发送") && !ConfirmDelivered(vs, pane, itemsBefore, hasImages && string.IsNullOrWhiteSpace(text) ? "" : text, 5000))
                r = "输入框已清空，但未在对话中确认到新消息，可能未送达，请在 VS 中查看（详见发送日志）";
            Poke();
            return r;
        }

        #region 后台发送

        private const int WM_ACTIVATE = 0x0006, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_CHAR = 0x0102,
            WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;

        [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref PT p);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gi);

        [StructLayout(LayoutKind.Sequential)] private struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RC { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RC rc; }

        private static void Msg(IntPtr h, int msg, int w) => SendMessageTimeout(h, msg, (IntPtr)w, IntPtr.Zero, 2 /* ABORTIFHUNG */, 2000, out _);

        private static void PostKey(IntPtr h, int vk, int scan, bool extended = false)
        {
            int l = 1 | (scan << 16) | (extended ? 1 << 24 : 0);
            PostMessageW(h, WM_KEYDOWN, (IntPtr)vk, (IntPtr)l);
            PostMessageW(h, WM_KEYUP, (IntPtr)vk, (IntPtr)(l | unchecked((int)0xC0000000)));
        }

        private static bool HasFocus(AutomationElement e)
        {
            try { return e.Current.HasKeyboardFocus; } catch { return false; }
        }

        private static bool WaitFocus(AutomationElement e, int ms)
        {
            var until = DateTime.Now.AddMilliseconds(ms);
            while (true)
            {
                if (HasFocus(e)) return true;
                if (DateTime.Now >= until) return false;
                Thread.Sleep(60);
            }
        }

        private static bool WaitText(AutomationElement e, Func<string, bool> pred, int ms)
        {
            var until = DateTime.Now.AddMilliseconds(ms);
            while (true)
            {
                var t = GetEditText(e);
                if (t != null && pred(t)) return true;
                if (DateTime.Now >= until) return false;
                Thread.Sleep(80);
            }
        }

        private static IntPtr FocusHwnd(IntPtr host)
        {
            uint tid = Native.GetWindowThreadProcessId(host, out _);
            var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
            return GetGUIThreadInfo(tid, ref gi) && gi.hwndFocus != IntPtr.Zero ? gi.hwndFocus : host;
        }

        private static void ClickElement(IntPtr host, AutomationElement e)
        {
            System.Windows.Rect r;
            try { r = e.Current.BoundingRectangle; } catch { return; }
            if (r.IsEmpty || r.Width < 2) return;
            var p = new PT { X = (int)(r.X + Math.Min(30, r.Width / 2)), Y = (int)(r.Y + r.Height / 2) };
            ScreenToClient(host, ref p);
            var lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
            PostMessageW(host, WM_MOUSEMOVE, IntPtr.Zero, lp);
            PostMessageW(host, WM_LBUTTONDOWN, (IntPtr)1, lp);
            PostMessageW(host, WM_LBUTTONUP, IntPtr.Zero, lp);
        }

        private static void FakeActivate(IntPtr host)
        {
            Msg(host, WM_ACTIVATE, 1);
            Msg(host, WM_SETFOCUS, 0);
        }

        /// <summary>
        /// 不切换前台窗口：向 VS 窗口发送“激活/获得焦点”消息，使其 WPF 输入框获得键盘焦点，
        /// 再用 WM_CHAR 逐字写入并回车发送。无法聚焦时返回 null。
        /// </summary>
        private string SendBackground(VsInstance vs, AutomationElement pane, AutomationElement edit, string text)
        {
            IntPtr host;
            lock (_lock) if (!_hosts.TryGetValue(vs.Pid, out host)) host = vs.MainHwnd;
            if (!Native.IsWindow(host)) host = vs.MainHwnd;
            bool realFg = ForegroundIs(vs);
            bool faked = false;
            try
            {
                T("后台：host=0x" + host.ToString("X") + " VS 在前台=" + realFg);
                if (!realFg) { FakeActivate(host); faked = true; }
                if (!WaitFocus(edit, 300))
                {
                    T("后台：模拟激活后输入框未获得焦点，执行 View.GitHub.Copilot.Chat");
                    // 对话窗格命令会把焦点移到输入框
                    try { DteWorker.Run(() => VsService.OpenCopilotChat(vs)).Wait(3000); } catch { }
                    if (faked) FakeActivate(host);
                    if (!WaitFocus(edit, 800))
                    {
                        T("后台：仍未获得焦点，模拟点击输入框");
                        ClickElement(host, edit);
                        if (!WaitFocus(edit, 600)) { T("后台：无法聚焦输入框"); return null; }
                    }
                }
                if (!realFg && ForegroundIs(vs)) realFg = true;

                IntPtr target = FocusHwnd(host);
                T("后台：输入框已聚焦，焦点窗口=0x" + target.ToString("X"));
                var cur = GetEditText(edit) ?? "";
                if (cur.Length > 0)
                {
                    T("后台：清除原有草稿 " + cur.Length + " 字");
                    for (int i = 0; i < cur.Length + 2; i++)
                    {
                        PostKey(target, 0x2E, 0x53, true); // Delete
                        PostKey(target, 0x08, 0x0E);       // Backspace
                    }
                    if (!WaitText(edit, t => t.Trim().Length == 0, 2000)) { T("后台：草稿未能清除"); return null; }
                }

                foreach (char c in text) PostMessageW(target, WM_CHAR, (IntPtr)c, (IntPtr)1);
                string want = Normalize(text);
                if (!WaitText(edit, t => Normalize(t) == want, 2500 + text.Length * 3))
                {
                    T("后台：写入后输入框内容「" + Short(GetEditText(edit)) + "」与消息不一致");
                    return "未能把消息完整写入 Copilot 输入框，已取消发送（内容保留在 VS 输入框中）";
                }
                if (!HasFocus(edit)) { T("后台：写入后输入框失去焦点"); return "Copilot 输入框失去焦点，已取消发送（内容保留在 VS 输入框中）"; }

                T("后台：内容已写入，发送 Enter");
                PostKey(target, 0x0D, 0x1C); // Enter
                if (WaitSent(pane, edit, 2000)) return "已发送";
                T("后台：Enter 后输入框未清空，尝试点击发送按钮");
                if (TryInvokeSend(pane) && WaitSent(pane, edit, 1500)) return "已发送";
                T("后台：仍未发送，输入框内容「" + Short(GetEditText(edit)) + "」");
                return "消息已填入输入框，但未能自动发送（请检查 VS 中的 Copilot 窗口）";
            }
            finally
            {
                if (faked && !ForegroundIs(vs))
                {
                    Msg(host, WM_KILLFOCUS, 0);
                    Msg(host, WM_ACTIVATE, 0);
                }
            }
        }

        private static bool WaitSent(AutomationElement pane, AutomationElement edit, int ms)
        {
            var until = DateTime.Now.AddMilliseconds(ms);
            while (true)
            {
                var t = GetEditText(edit);
                if (t != null && t.Trim().Length == 0) { T("输入框已清空"); return true; }
                if (HasCancel(pane)) { T("出现停止按钮"); return true; }
                if (DateTime.Now >= until) return false;
                Thread.Sleep(100);
            }
        }

        #endregion

        #region 前台发送（兜底）

        private const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_RETURN = 0x0D, VK_A = 0x41, VK_V = 0x56;
        private const uint KEYUP = 2;

        private static void Key(byte vk, bool up) => Native.keybd_event(vk, 0, up ? KEYUP : 0, UIntPtr.Zero);

        private static void Combo(byte mod, byte vk)
        {
            Key(mod, false); Key(vk, false); Key(vk, true); Key(mod, true);
        }

        private static string GetEditText(AutomationElement edit)
        {
            try { return ((TextPattern)edit.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1) ?? ""; }
            catch { return null; }
        }

        private static bool ForegroundIs(VsInstance vs)
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            return pid == (uint)vs.Pid;
        }

        /// <summary>短暂激活 VS，通过剪贴板粘贴后回车发送，再切回本工具。</summary>
        private string SendForeground(VsInstance vs, AutomationElement pane, AutomationElement edit, string text, IntPtr returnTo)
        {
            string oldClip = null;
            try { if (Clipboard.ContainsText()) oldClip = Clipboard.GetText(); } catch { }
            try { Clipboard.SetDataObject(text.Replace("\n", "\r\n"), true, 10, 50); }
            catch (Exception ex) { return "写入剪贴板失败: " + ex.Message; }

            try
            {
                // 松开可能仍按住的修饰键，避免组合成其他快捷键
                Key(VK_SHIFT, true); Key(VK_MENU, true); Key(VK_CONTROL, true);

                T("前台：激活 VS 窗口");
                Native.Activate(vs.MainHwnd);
                for (int i = 0; i < 10 && !ForegroundIs(vs); i++) Thread.Sleep(50);
                if (!ForegroundIs(vs)) { T("前台：激活失败，前台窗口=0x" + Native.GetForegroundWindow().ToString("X")); return "无法激活该 VS 窗口，发送已取消"; }

                try { edit.SetFocus(); } catch (Exception ex) { T("前台：SetFocus 异常 " + ex.Message); }
                if (!WaitFocus(edit, 600) || !ForegroundIs(vs)) { T("前台：输入框未获得焦点"); return "无法聚焦 Copilot 输入框，发送已取消"; }

                Combo(VK_CONTROL, VK_A);
                Thread.Sleep(60);
                Combo(VK_CONTROL, VK_V);
                string want = Normalize(text);
                // 粘贴未生效时不能回车：空输入框回车会被误判为“已发送”
                if (!WaitText(edit, t => Normalize(t) == want, 2500))
                {
                    T("前台：粘贴后输入框内容「" + Short(GetEditText(edit)) + "」与消息不一致");
                    return "未能确认消息已粘贴到 Copilot 输入框，发送已取消（请检查 VS 输入框）";
                }
                if (!ForegroundIs(vs) || !HasFocus(edit)) { T("前台：回车前焦点已改变"); return "发送前 VS 焦点已改变，发送已取消（内容保留在 VS 输入框中）"; }
                T("前台：内容已粘贴，发送 Enter");
                Key(VK_RETURN, false); Key(VK_RETURN, true);

                if (WaitSent(pane, edit, 1500)) return "已发送（已短暂切换到 VS）";
                T("前台：Enter 后输入框未清空，尝试点击发送按钮");
                if (TryInvokeSend(pane) && WaitSent(pane, edit, 1500)) return "已发送（已短暂切换到 VS）";
                T("前台：仍未发送，输入框内容「" + Short(GetEditText(edit)) + "」");
                return "消息已填入输入框，但未能自动发送（请检查 VS 中的 Copilot 窗口）";
            }
            finally
            {
                Thread.Sleep(100);
                try
                {
                    if (oldClip != null) Clipboard.SetDataObject(oldClip, true, 10, 50);
                    else Clipboard.Clear();
                }
                catch { }
                if (returnTo != IntPtr.Zero) Native.Activate(returnTo);
                Poke();
            }
        }

        private static string Normalize(string s) => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

        private static bool TryInvokeSend(AutomationElement pane)
        {
            try
            {
                foreach (AutomationElement b in pane.FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    string id = b.Current.AutomationId ?? "", name = b.Current.Name ?? "";
                    if (id.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("Submit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name == "发送" || name.StartsWith("发送", StringComparison.Ordinal) || name.Equals("Send", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!b.Current.IsEnabled) continue;
                        ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        #endregion

        #endregion
    }
}
