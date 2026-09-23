using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace VSManager
{
    /// <summary>通过 UI Automation 检测 VS 中 GitHub Copilot 对话窗格是否正在生成（存在“取消/停止”按钮）。</summary>
    public class CopilotMonitor
    {
        private readonly Func<List<VsInstance>> _getInstances;
        private readonly Func<AppSettings> _getSettings;
        private Thread _thread;
        private volatile bool _running;

        public event Action<VsInstance, TimeSpan> Completed;
        public event Action<VsInstance> StateChanged;
        /// <summary>对话窗格被切走后已自动切回（第二个参数表示是否成功）。后台线程触发。</summary>
        public event Action<VsInstance, bool> PaneRestored;
        /// <summary>对话监听：读取到某个 VS 最近一轮对话（第三个参数表示是否仍在生成）。后台线程触发。</summary>
        public event Action<VsInstance, ChatTranscript, bool> Conversation;
        /// <summary>读取对话末尾若干条消息（由主窗体提供，复用 CopilotChat 的 UIA 读取）。</summary>
        public Func<VsInstance, bool, ChatTranscript> ReadTail;

        private sealed class WatchState
        {
            public DateTime LastRead;
            public CopilotState State;
            public int Failures;
        }
        private readonly Dictionary<int, WatchState> _watch = new Dictionary<int, WatchState>();

        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly Dictionary<int, int> _missCount = new Dictionary<int, int>();
        private readonly Dictionary<int, DateTime> _lastRestore = new Dictionary<int, DateTime>();

        public CopilotMonitor(Func<List<VsInstance>> getInstances, Func<AppSettings> getSettings)
        {
            _getInstances = getInstances;
            _getSettings = getSettings;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "CopilotMonitor" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop() => _running = false;

        private void Loop()
        {
            while (_running)
            {
                var s = _getSettings();
                if (s.MonitorCopilot)
                {
                    var all = _getInstances();
                    foreach (var vs in all)
                    {
                        if (!_running) break;
                        try { Update(vs, Restore(vs, s, Probe(vs, s))); } catch { }
                        if (s.WatchConversations) Watch(vs, s);
                    }
                    foreach (var pid in _watch.Keys.Where(p => !all.Any(v => v.Pid == p)).ToList()) _watch.Remove(pid);
                }
                Thread.Sleep(Math.Max(500, s.PollMs));
            }
        }

        /// <summary>
        /// 对话监听：与忙碌探测同一轮询节奏。生成中每 2 个轮询周期（至少 4 秒）读一次，状态变化时立即读，
        /// 空闲时每 6 秒做一次浅层检查（消息列表变化才完整读取）；连续读取失败时逐步退避，失败只影响该 VS。
        /// </summary>
        private void Watch(VsInstance vs, AppSettings s)
        {
            if (ReadTail == null || Conversation == null) return;
            if (!_watch.TryGetValue(vs.Pid, out var w)) _watch[vs.Pid] = w = new WatchState { State = (CopilotState)(-1) };
            var state = vs.Copilot;
            if (state == CopilotState.Unknown) { w.State = state; return; }
            var now = DateTime.Now;
            double interval = state == CopilotState.Busy ? Math.Max(4.0, 2 * Math.Max(500, s.PollMs) / 1000.0) : 6;
            interval *= 1 + Math.Min(w.Failures, 5);
            bool stateChanged = state != w.State;
            if (!stateChanged && (now - w.LastRead).TotalSeconds < interval) return;
            w.State = state;
            w.LastRead = now;
            try
            {
                // 空闲且状态未变时，只有消息列表变化（有人提问）才完整读取
                var t = ReadTail(vs, state == CopilotState.Idle && !stateChanged);
                if (t == null || !t.PaneFound) { w.Failures++; return; }
                w.Failures = 0;
                if (!t.Unchanged) Conversation(vs, t, state == CopilotState.Busy);
            }
            catch { w.Failures++; }
        }

        private void Update(VsInstance vs, CopilotState state)
        {
            var old = vs.Copilot;
            if (state == CopilotState.Busy)
            {
                vs.IdleCount = 0;
                if (old != CopilotState.Busy)
                {
                    vs.BusySince = DateTime.Now;
                    vs.CompletedAt = null;
                    vs.CompletionUnseen = false;
                    vs.Copilot = CopilotState.Busy;
                    StateChanged?.Invoke(vs);
                }
                return;
            }

            if (old == CopilotState.Busy)
            {
                // 连续两次检测为空闲才判定完成，避免 UI 刷新瞬间的误报
                if (state == CopilotState.Idle && ++vs.IdleCount >= 2)
                {
                    vs.LastDuration = DateTime.Now - vs.BusySince;
                    vs.CompletedAt = DateTime.Now;
                    vs.Copilot = CopilotState.Idle;
                    vs.IdleCount = 0;
                    StateChanged?.Invoke(vs);
                    Completed?.Invoke(vs, DateTime.Now - vs.BusySince);
                }
                return;
            }

            if (old != state)
            {
                vs.Copilot = state;
                StateChanged?.Invoke(vs);
            }
        }

        /// <summary>
        /// 对话窗格停靠在文档区时，切到其他标签页后 VS 会把它移出 UIA 树，导致无法监听。
        /// 曾经找到过窗格、连续两次找不到且用户不在该 VS 中操作时，用 DTE 命令把它切回当前。
        /// </summary>
        private CopilotState Restore(VsInstance vs, AppSettings s, CopilotState state)
        {
            if (state != CopilotState.Unknown)
            {
                _seen.Add(vs.Pid);
                _missCount[vs.Pid] = 0;
                return state;
            }
            _missCount.TryGetValue(vs.Pid, out int miss);
            _missCount[vs.Pid] = ++miss;
            if (!s.RestoreCopilotPane || !_seen.Contains(vs.Pid) || miss < 2 || vs.Dte == null) return state;
            if (_lastRestore.TryGetValue(vs.Pid, out var last) && (DateTime.Now - last).TotalSeconds < 15) return state;
            if (IsForeground(vs)) return state;
            _lastRestore[vs.Pid] = DateTime.Now;
            bool ok = false;
            try
            {
                var t = DteWorker.Run(() => VsService.OpenCopilotChat(vs));
                if (t.Wait(5000) && t.Result)
                {
                    for (int i = 0; i < 6 && state == CopilotState.Unknown; i++)
                    {
                        Thread.Sleep(400);
                        state = Probe(vs, s);
                    }
                    ok = state != CopilotState.Unknown;
                }
            }
            catch { }
            if (ok) _missCount[vs.Pid] = 0;
            PaneRestored?.Invoke(vs, ok);
            return state;
        }

        /// <summary>用户正在该 VS 中操作（前台窗口属于该进程）时不打扰。</summary>
        private static bool IsForeground(VsInstance vs)
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            return pid == (uint)vs.Pid;
        }

        public static CopilotState Probe(VsInstance vs, AppSettings s)
        {
            var keyword = string.IsNullOrWhiteSpace(s.CopilotPaneKeyword) ? "Copilot" : s.CopilotPaneKeyword;
            var busyIds = (s.BusyButtonIds ?? "CancelButton")
                .Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).ToArray();

            bool foundPane = false;
            var paneCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane);
            foreach (var h in Native.GetProcessWindows(vs.Pid))
            {
                AutomationElement root;
                try { root = AutomationElement.FromHandle(h); } catch { continue; }
                AutomationElementCollection panes;
                try { panes = root.FindAll(TreeScope.Descendants, paneCond); } catch { continue; }
                foreach (AutomationElement pane in panes)
                {
                    string name;
                    try { name = pane.Current.Name ?? ""; } catch { continue; }
                    if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foundPane = true;
                    foreach (var id in busyIds)
                    {
                        try
                        {
                            var btn = pane.FindFirst(TreeScope.Descendants, new AndCondition(
                                new PropertyCondition(AutomationElement.AutomationIdProperty, id),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                            if (btn != null && !btn.Current.IsOffscreen) return CopilotState.Busy;
                        }
                        catch { }
                    }
                }
            }
            return foundPane ? CopilotState.Idle : CopilotState.Unknown;
        }
    }
}
