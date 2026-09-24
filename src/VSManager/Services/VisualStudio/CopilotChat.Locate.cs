using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace VSManager
{
    /// <summary>
    /// Copilot 输入框的分层定位：
    /// L1 WpfTextViewHost 下的 WpfTextView → L2 对话列表外最靠下的 WpfTextView → L3 窗格内最靠下的可编辑文本元素 →
    /// L4 旧方式（第一个 WpfTextView，须可编辑）→ L5 在输入框位置命中测试（绕过 WPF 未刷新的子元素缓存）。
    /// 全部在原始视图中查找，只读 / 禁用 / 对话列表内的元素一律排除；每层都把候选数量、名称、AutomationId、是否可编辑与耗时写入发送日志。
    /// Layered location of the Copilot input box:
    /// L1 WpfTextView under WpfTextViewHost → L2 lowest WpfTextView outside the conversation list → L3 lowest editable text
    /// element in the pane → L4 legacy (first WpfTextView, must be editable) → L5 hit test at the input position (bypasses a
    /// stale WPF children cache). Everything is searched in the raw view; read-only / disabled / in-list elements are excluded;
    /// each level logs the candidate count, names, AutomationIds, editability and elapsed time to the send log.
    /// </summary>
    public partial class CopilotChat
    {
        /// <summary>一次定位的结果。/ Result of one locate pass.</summary>
        private sealed class EditLocate
        {
            public AutomationElement Edit;
            public string Level;
            public string Blocked;
            public LocateOutcome Outcome;
            public readonly List<InputCandidate> Seen = new List<InputCandidate>();
            public readonly List<string> Log = new List<string>();
        }

        /// <summary>定位超时（毫秒），来自 SendLocateTimeoutSeconds。/ Locate timeout in ms, from SendLocateTimeoutSeconds.</summary>
        private int LocateTimeoutMs
        {
            get
            {
                int sec = _getSettings()?.SendLocateTimeoutSeconds ?? InputLocator.DefaultTimeoutSeconds;
                if (sec <= 0) sec = InputLocator.DefaultTimeoutSeconds;
                return Math.Min(Math.Max(1, sec), 60) * 1000;
            }
        }

        /// <summary>定位失败后的自动重试次数（受 SendAutoRetry 控制）。/ Automatic retries after a failed locate (governed by SendAutoRetry).</summary>
        private int LocateRetries
        {
            get
            {
                var s = _getSettings();
                if (s == null) return InputLocator.DefaultRetryCount;
                return s.SendAutoRetry ? Math.Min(Math.Max(0, s.SendLocateRetryCount), 5) : 0;
            }
        }

        private static readonly Condition EditOrDocumentCond = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));

        /// <summary>在原始视图中查找（控件视图会漏掉 IsControlElement=false 的元素）。/ Searches the raw view (the control view skips IsControlElement=false elements).</summary>
        private static List<AutomationElement> RawFindAll(AutomationElement root, TreeScope scope, Condition cond, int max = 200)
        {
            var cr = new CacheRequest { TreeFilter = Automation.RawViewCondition };
            cr.Add(AutomationElement.AutomationIdProperty);
            AutomationElementCollection found;
            using (cr.Activate()) found = root.FindAll(scope, cond);
            return found.Cast<AutomationElement>().Take(max).ToList();
        }

        private static InputCandidate Inspect(AutomationElement e, AutomationElement list, AutomationElement pane, int order)
        {
            var c = new InputCandidate { Tag = e, Order = order };
            try
            {
                var cur = e.Current;
                c.Name = cur.Name;
                c.AutomationId = cur.AutomationId;
                c.ClassName = cur.ClassName;
                c.Enabled = cur.IsEnabled;
                c.KeyboardFocusable = cur.IsKeyboardFocusable;
                c.Offscreen = cur.IsOffscreen;
                var r = cur.BoundingRectangle;
                c.Bottom = r.IsEmpty || double.IsInfinity(r.Bottom) || double.IsNaN(r.Bottom) ? double.NaN : r.Bottom;
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out object vp)) { c.ReadOnly = ((ValuePattern)vp).Current.IsReadOnly; c.HasTextPattern = true; }
                if (e.TryGetCurrentPattern(TextPattern.Pattern, out _)) c.HasTextPattern = true;
                c.InConversation = list != null && IsInside(e, list, pane);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException || ex is InvalidOperationException || ex is COMException || ex is ArgumentException)
            {
                c.Error = ex.GetType().Name;
            }
            return c;
        }

        private static string Summarize(IEnumerable<InputCandidate> items, int max = 4)
        {
            var list = items.ToList();
            if (list.Count == 0) return "[]";
            var s = string.Join("; ", list.Take(max).Select(c => c.ToString()));
            return "[" + s + (list.Count > max ? $"; …共/total {list.Count}" : "") + "]";
        }

        /// <summary>
        /// 按 L1–L5 定位输入框（单次，不等待）。<paramref name="pid"/> 为 0 时跳过 L5 命中测试。
        /// Locates the input through L1–L5 (single pass, no waiting). L5 hit testing is skipped when <paramref name="pid"/> is 0.
        /// </summary>
        private static EditLocate LocateEdit(AutomationElement pane, int pid)
        {
            var res = new EditLocate();
            if (pane == null) { res.Outcome = LocateOutcome.PaneNotFound; res.Log.Add("窗格为空 / pane is null"); return res; }

            AutomationElement list = null;
            try { list = FindList(pane); } catch (Exception ex) { res.Log.Add("读取对话列表失败 / reading the conversation list failed: " + ex.GetType().Name); }
            var hostRects = new List<Rect>();

            bool Level(string name, Func<List<InputCandidate>> collect, Func<List<InputCandidate>, InputCandidate> pick, string extra = null)
            {
                var sw = Stopwatch.StartNew();
                List<InputCandidate> cands;
                try { cands = collect() ?? new List<InputCandidate>(); }
                catch (Exception ex)
                {
                    res.Log.Add($"{name}：异常 / error {ex.GetType().Name}: {ex.Message}（{sw.ElapsedMilliseconds}ms）");
                    return false;
                }
                res.Seen.AddRange(cands);
                var hit = pick(cands);
                res.Log.Add($"{name}：{extra}候选 / candidates {cands.Count} {Summarize(cands)}（{sw.ElapsedMilliseconds}ms）→ " +
                            (hit != null ? "命中 / hit" : "未命中 / miss"));
                if (hit == null) return false;
                res.Edit = (AutomationElement)hit.Tag;
                res.Level = name;
                return true;
            }

            // L1：WpfTextViewHost（先找直接子元素，再找对话列表外的任意层级）/ WpfTextViewHost (direct child first, then any depth outside the list)
            int hostCount = 0;
            bool found = Level("L1 WpfTextViewHost", () =>
            {
                var hosts = RawFindAll(pane, TreeScope.Children, IdCond("WpfTextViewHost"));
                if (hosts.Count == 0)
                    hosts = RawFindAll(pane, TreeScope.Descendants, IdCond("WpfTextViewHost")).Where(h => list == null || !IsInside(h, list, pane)).ToList();
                hostCount = hosts.Count;
                var cands = new List<InputCandidate>();
                int order = 0;
                foreach (var h in hosts)
                {
                    try { var r = h.Current.BoundingRectangle; if (!r.IsEmpty && !double.IsInfinity(r.Width)) hostRects.Add(r); } catch { }
                    foreach (var v in RawFindAll(h, TreeScope.Subtree, IdCond("WpfTextView"), 10))
                        cands.Add(Inspect(v, list, pane, order++));
                }
                return cands;
            }, InputLocator.PickFirst);
            if (!found && res.Log.Count > 0) res.Log[res.Log.Count - 1] += $"（宿主 / hosts {hostCount}）";

            // L2：对话列表外最靠下的 WpfTextView / Lowest WpfTextView outside the conversation list
            if (!found)
                found = Level("L2 WpfTextView 排除对话列表 / outside list", () =>
                    RawFindAll(pane, TreeScope.Descendants, IdCond("WpfTextView")).Select((e, i) => Inspect(e, list, pane, i)).ToList(),
                    InputLocator.PickLowest);

            // L3：窗格内最靠下的可编辑文本元素（Edit / Document），排除隐藏元素与标题 / 下拉框内的文本框
            // Lowest editable text element (Edit / Document), excluding hidden ones and the title / picker text boxes
            if (!found)
                found = Level("L3 可编辑文本元素 / editable text", () =>
                    RawFindAll(pane, TreeScope.Descendants, EditOrDocumentCond).Select((e, i) => Inspect(e, list, pane, i))
                        .Where(c => !c.Offscreen && !c.InConversation && !InputLocator.IsKnownNonInput(c)).ToList(),
                    InputLocator.PickLowest);

            // L4：旧方式——控件视图中的第一个 WpfTextView，仍须可编辑 / Legacy: first WpfTextView in the control view, still must be editable
            if (!found)
                found = Level("L4 旧方式 / legacy first WpfTextView", () =>
                    pane.FindAll(TreeScope.Descendants, IdCond("WpfTextView")).Cast<AutomationElement>().Take(50)
                        .Select((e, i) => Inspect(e, list, pane, i)).ToList(),
                    InputLocator.PickFirst);

            // L5：在宿主 / 水印位置命中测试，绕过 WPF 未刷新的子元素缓存 / Hit test at the host / watermark position
            if (!found && pid > 0)
                found = Level("L5 位置命中测试 / hit test", () => HitTest(pane, list, pid, hostRects, res.Log), InputLocator.PickFirst);

            res.Outcome = found ? LocateOutcome.Found : InputLocator.Classify(true, res.Seen);
            return res;
        }

        /// <summary>
        /// 在 WpfTextViewHost 或水印「询问 Copilot」的位置做 FromPoint 命中测试，只接受属于目标 VS 进程的元素。
        /// FromPoint hit test at the WpfTextViewHost or the "Ask Copilot" watermark; only elements of the target VS process are accepted.
        /// </summary>
        private static List<InputCandidate> HitTest(AutomationElement pane, AutomationElement list, int pid, List<Rect> hostRects, List<string> notes = null)
        {
            var rects = new List<Rect>(hostRects);
            foreach (var w in RawFindAll(pane, TreeScope.Children, IdCond("PART_WatermarkTextBlock"), 2))
            {
                try { var r = w.Current.BoundingRectangle; if (!r.IsEmpty && !double.IsInfinity(r.Width)) rects.Add(r); } catch { }
            }
            var cands = new List<InputCandidate>();
            foreach (var r in rects.Take(3))
            {
                AutomationElement e;
                try { e = AutomationElement.FromPoint(new Point(r.Left + Math.Min(20, r.Width / 2), r.Top + r.Height / 2)); } catch { continue; }
                if (e == null) continue;
                try
                {
                    int hitPid = e.Current.ProcessId;
                    if (hitPid != pid)
                    {
                        // 该位置被其他窗口遮挡 / The spot is covered by another window
                        notes?.Add($"L5：({(int)r.Left},{(int)r.Top}) 处被其他进程的窗口遮挡 / covered by a window of another process (pid={hitPid})");
                        continue;
                    }
                }
                catch { continue; }
                // 命中的可能是宿主、水印或视图本身：向上最多 4 层、向下在子树中找 WpfTextView
                // The hit may be the host, the watermark or the view itself: look up to 4 levels up, then down the subtree
                AutomationElement view = null;
                var walker = TreeWalker.RawViewWalker;
                int up = 0;
                for (var a = e; a != null && up <= 4; a = SafeParent(walker, a), up++)
                {
                    try { if (a.Current.AutomationId == "WpfTextView") { view = a; break; } } catch { break; }
                    if (Automation.Compare(a, pane)) break;
                }
                if (view == null)
                {
                    try { view = RawFindAll(e, TreeScope.Subtree, IdCond("WpfTextView"), 1).FirstOrDefault(); } catch { }
                }
                if (view != null) cands.Add(Inspect(view, list, pane, cands.Count));
            }
            return cands;
        }

        private static AutomationElement SafeParent(TreeWalker walker, AutomationElement e)
        {
            try { return walker.GetParent(e); } catch { return null; }
        }

        /// <summary>
        /// 带退避轮询与自动重试的定位：每次轮询都丢弃缓存的窗格重新查找；一轮超时后（若允许重试）执行 View.GitHub.Copilot.Chat 刷新窗格再来一轮。
        /// Locating with back-off polling and automatic retries: every poll drops the cached pane and searches again; when a round
        /// times out (and retries are allowed) View.GitHub.Copilot.Chat refreshes the pane before another round.
        /// </summary>
        private EditLocate LocateWithRetry(VsInstance vs, ref AutomationElement pane, out int attempts)
        {
            int retries = LocateRetries, timeoutMs = LocateTimeoutMs;
            EditLocate res = null;
            attempts = 0;
            var total = Stopwatch.StartNew();
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                attempts++;
                if (attempt > 0)
                {
                    T($"定位输入框：自动重试 {attempt}/{retries}，丢弃缓存窗格并执行 View.GitHub.Copilot.Chat 刷新 / " +
                      "locate retry, dropping the cached pane and refreshing it with View.GitHub.Copilot.Chat");
                    ForgetPane(vs);
                    var p = OpenPane(vs, 4000);
                    if (p != null) pane = p;
                    // 刷新后仍可能停留在历史记录：按 AutoOpenCopilotPane 点「返回」/ Still on the history list after refreshing: press Back per AutoOpenCopilotPane
                    pane = LeaveHistoryIfNeeded(vs, pane);
                }
                var sw = Stopwatch.StartNew();
                for (int step = 0; ; step++)
                {
                    string blocked = BlockingDialogMessage(vs);
                    if (blocked != null) return new EditLocate { Blocked = blocked };
                    if (step > 0)
                    {
                        // 缓存的窗格元素可能已随窗格重建而过期 / The cached pane element may be outdated after the pane was rebuilt
                        ForgetPane(vs);
                        pane = FindPane(vs) ?? pane;
                    }
                    res = LocateEdit(pane, vs.Pid);
                    bool last = res.Edit != null || sw.ElapsedMilliseconds >= timeoutMs;
                    if (step == 0 || last)
                    {
                        T($"定位输入框 / locating input（第 {attempt + 1} 轮第 {step + 1} 次 / round {attempt + 1} poll {step + 1}，{sw.ElapsedMilliseconds}ms）：");
                        foreach (var line in res.Log) T("  " + line);
                    }
                    if (res.Edit != null)
                    {
                        T($"定位输入框：{res.Level} 命中，总耗时 / total {total.ElapsedMilliseconds}ms / input located");
                        return res;
                    }
                    if (last) break;
                    Thread.Sleep(InputLocator.NextDelayMs(step));
                }
                T($"⚠ 定位输入框失败 / locate failed：{res.Outcome}，本轮 / round {sw.ElapsedMilliseconds}ms");
            }
            T("窗格结构 / pane structure：" + DumpPane(pane));
            return res;
        }

        private void ForgetPane(VsInstance vs)
        {
            lock (_lock) { _panes.Remove(vs.Pid); _missUntil.Remove(vs.Pid); }
        }

        /// <summary>失败时记录窗格的直接子元素（原始视图），供排查结构变化。/ Logs the pane's direct raw children on failure to diagnose structure changes.</summary>
        private static string DumpPane(AutomationElement pane)
        {
            if (pane == null) return "（无窗格 / no pane）";
            var sb = new StringBuilder();
            try
            {
                int n = 0;
                var walker = TreeWalker.RawViewWalker;
                for (var c = walker.GetFirstChild(pane); c != null && n < 60; c = walker.GetNextSibling(c), n++)
                {
                    try
                    {
                        var cur = c.Current;
                        if (string.IsNullOrEmpty(cur.AutomationId) && string.IsNullOrEmpty(cur.Name) && cur.IsOffscreen) continue;
                        sb.Append("\r\n           ").Append(cur.ClassName).Append(" id=").Append(string.IsNullOrEmpty(cur.AutomationId) ? "-" : cur.AutomationId)
                          .Append(" 「").Append(Short(cur.Name, 24)).Append("」 off=").Append(cur.IsOffscreen).Append(" en=").Append(cur.IsEnabled);
                    }
                    catch (Exception ex) { sb.Append("\r\n           (失效 / stale: ").Append(ex.GetType().Name).Append(')'); }
                }
                if (sb.Length == 0) sb.Append("（无子元素 / no children）");
            }
            catch (Exception ex) { sb.Append("（读取失败 / unreadable: ").Append(ex.Message).Append('）'); }
            return sb.ToString();
        }
    }
}
