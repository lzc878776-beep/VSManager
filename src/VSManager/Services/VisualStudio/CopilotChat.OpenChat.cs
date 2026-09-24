using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace VSManager
{
    /// <summary>
    /// 「真正打开对话助手」：窗格存在不等于能输入——它可能自动隐藏、被覆盖，或停留在聊天历史列表（此时对话列表与输入框都不可见）。
    /// 降级链：DTE 显示工具窗口（View.GitHub.Copilot.Chat，取消自动隐藏、设为可见）→ UIA 聚焦窗格 → 点击「返回」退出历史记录 → 校验输入框可编辑 → 可选聚焦。
    /// "Really open the chat": finding the pane is not enough — it may be auto-hidden, covered, or stuck on the chat history list
    /// (conversation and input offscreen). Fallback chain: DTE shows the tool window (View.GitHub.Copilot.Chat, unpin auto-hide,
    /// make visible) → UIA focuses the pane → press "Back" to leave history → verify the input is editable → optionally focus it.
    /// </summary>
    public partial class CopilotChat
    {
        /// <summary>是否在窗格未真正打开时自动打开（AutoOpenCopilotPane）。/ Whether to auto-open when the pane is not really open (AutoOpenCopilotPane).</summary>
        private bool AutoOpenPaneEnabled => _getSettings()?.AutoOpenCopilotPane ?? true;

        /// <summary>
        /// 打开目标 VS 的 Copilot 对话并聚焦输入框（会把 VS 切到前台），供 AI 工具 open_copilot 使用。必须在 STA 或 MTA 后台线程调用，不得在界面线程调用。
        /// Opens the Copilot chat of the target VS and focuses its input (brings VS to the front); used by the open_copilot AI tool.
        /// Call on a background STA or MTA thread, never on the UI thread.
        /// </summary>
        public CopilotPaneOpenResult OpenChat(VsInstance vs)
        {
            var r = EnsureChatOpen(vs, true, 8000, out _);
            Poke();
            return r;
        }

        /// <summary>
        /// 确保窗格显示当前会话且输入框可编辑。<paramref name="interactive"/> 为 true 时允许激活 VS 并聚焦（UIA 聚焦窗格、聚焦输入框）；发送流程传 false，不抢前台。
        /// Ensures the pane shows the current conversation with an editable input. When <paramref name="interactive"/> is true VS may be
        /// activated and focused (UIA pane focus, input focus); the send flow passes false and never steals the foreground.
        /// </summary>
        private CopilotPaneOpenResult EnsureChatOpen(VsInstance vs, bool interactive, int timeoutMs, out AutomationElement pane)
        {
            var r = new CopilotPaneOpenResult();
            var sw = Stopwatch.StartNew();
            void S(string s) { r.Step($"+{sw.ElapsedMilliseconds}ms {s}"); T("打开对话助手 / open chat：" + s); }
            pane = null;
            try
            {
                if (vs == null || !Native.IsWindow(vs.MainHwnd)) { S("VS 已关闭 / VS is closed"); return r; }
                if ((r.Blocked = BlockingDialogMessage(vs)) != null) { S("被弹窗拦截 / blocked by a dialog：" + r.Blocked); return r; }

                ForgetPane(vs);
                pane = FindPane(vs);
                r.Candidates = _lastPaneCandidates;
                r.Initial = r.Final = Observe(pane, out string detail);
                S($"初始 / initial：{CopilotPaneModes.Describe(r.Initial)}，候选 / candidates {r.Candidates}，{detail}");

                // 1) DTE：显示工具窗口 / DTE: show the tool window
                if (r.Final == CopilotPaneMode.NotFound || r.Final == CopilotPaneMode.Hidden || r.Final == CopilotPaneMode.NoInput)
                {
                    r.UsedDte = true;
                    bool? autoHide = null; string diag = null; bool ok = false;
                    try
                    {
                        var t = DteWorker.Run(() => { bool k = VsService.ShowCopilotChatWindow(vs, Keyword, out var a, out var d); autoHide = a; diag = d; return k; });
                        ok = t.Wait(Math.Min(timeoutMs, 6000)) && t.Result;
                        if (!t.IsCompleted) diag = "DTE 超时 / DTE timed out";
                    }
                    catch (Exception ex) { diag = "DTE 异常 / DTE error: " + (ex.InnerException ?? ex).Message; }
                    r.WasAutoHide = autoHide;
                    S($"DTE {VsService.CopilotChatCommand}：{(ok ? "成功 / ok" : "失败 / failed")}，{diag}");
                    pane = WaitPane(vs, pane, Math.Min(timeoutMs, 4000), m => m != CopilotPaneMode.NotFound && m != CopilotPaneMode.Hidden, r, S);
                }

                // 2) UIA：聚焦窗格使自动隐藏 / 被覆盖的窗格显示出来 / UIA: focus the pane so an auto-hidden or covered pane is shown
                if (r.Final == CopilotPaneMode.Hidden && interactive && pane != null)
                {
                    try
                    {
                        Native.Activate(vs.MainHwnd);
                        pane.SetFocus();
                        S("UIA 聚焦窗格 / UIA focused the pane");
                    }
                    catch (Exception ex) { S("UIA 聚焦窗格失败 / UIA pane focus failed：" + ex.GetType().Name); }
                    pane = WaitPane(vs, pane, 2000, m => m != CopilotPaneMode.Hidden, r, S);
                }
                if ((r.Blocked = BlockingDialogMessage(vs)) != null) { S("被弹窗拦截 / blocked by a dialog：" + r.Blocked); return r; }

                // 3) 退出历史记录模式 / Leave history mode
                if (r.Final == CopilotPaneMode.History) pane = LeaveHistory(vs, pane, r, S);

                // 4) 校验输入框 / Verify the input
                if (pane == null || r.Final == CopilotPaneMode.NotFound || r.Final == CopilotPaneMode.Hidden || r.Final == CopilotPaneMode.History)
                {
                    S("校验失败 / verification failed：" + CopilotPaneModes.Describe(r.Final));
                    return r;
                }
                var loc = LocateEdit(pane, vs.Pid);
                foreach (var line in loc.Log) S("  " + line);
                if (loc.Edit == null) { S("未找到可编辑的输入框 / no editable input：" + loc.Outcome); return r; }
                r.Ok = true;
                r.Final = CopilotPaneMode.Conversation;

                // 5) 前置并聚焦输入框 / Bring to the front and focus the input
                if (interactive)
                {
                    try
                    {
                        Native.Activate(vs.MainHwnd);
                        loc.Edit.SetFocus();
                        r.Focused = WaitFocus(loc.Edit, 1000);
                    }
                    catch (Exception ex) { S("聚焦输入框失败 / input focus failed：" + ex.GetType().Name); }
                    S("输入框焦点 / input focused=" + r.Focused);
                }
                return r;
            }
            catch (Exception ex)
            {
                S("异常 / error：" + ex.GetType().Name + ": " + ex.Message);
                return r;
            }
            finally
            {
                r.ElapsedMs = sw.ElapsedMilliseconds;
                lock (_lock) _missUntil.Remove(vs?.Pid ?? 0);
            }
        }

        /// <summary>发送流程：窗格停留在历史记录时点击「返回」。返回（可能已重新查找的）窗格。/ Send flow: presses "Back" when the pane is on the history list; returns the (possibly re-found) pane.</summary>
        private AutomationElement LeaveHistoryIfNeeded(VsInstance vs, AutomationElement pane)
        {
            if (pane == null || !AutoOpenPaneEnabled) return pane;
            if (Observe(pane, out _) != CopilotPaneMode.History) return pane;
            var r = new CopilotPaneOpenResult { Final = CopilotPaneMode.History };
            var sw = Stopwatch.StartNew();
            return LeaveHistory(vs, pane, r, s => T($"退出历史记录 / leave history +{sw.ElapsedMilliseconds}ms：{s}"));
        }

        private AutomationElement LeaveHistory(VsInstance vs, AutomationElement pane, CopilotPaneOpenResult r, Action<string> log)
        {
            AutomationElement back = null;
            try { back = RawFindAll(pane, TreeScope.Descendants, IdCond(CopilotPaneModes.BackToChatId), 1).FirstOrDefault(); } catch { }
            if (back == null) { log("未找到「返回」按钮 / Back button not found"); return pane; }
            try
            {
                ((InvokePattern)back.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                log("已点击「返回」/ pressed Back (" + CopilotPaneModes.BackToChatId + ")");
            }
            catch (Exception ex) { log("点击「返回」失败 / pressing Back failed：" + ex.GetType().Name); return pane; }
            pane = WaitPane(vs, pane, 3000, m => m != CopilotPaneMode.History, r, log);
            r.LeftHistory = r.Final != CopilotPaneMode.History;
            return pane;
        }

        /// <summary>轮询窗格状态直到满足条件或超时，结果写入 <paramref name="r"/>.Final。/ Polls the pane state until the predicate holds or time runs out; stores it in <paramref name="r"/>.Final.</summary>
        private AutomationElement WaitPane(VsInstance vs, AutomationElement pane, int ms, Func<CopilotPaneMode, bool> done,
            CopilotPaneOpenResult r, Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            string detail = null;
            for (int step = 0; ; step++)
            {
                Thread.Sleep(InputLocator.NextDelayMs(step));
                // 窗格缺失或不可见时才丢弃缓存完整搜索（开销大）；否则复用仍有效的缓存元素
                // Drop the cache for a full (expensive) search only when the pane is missing or hidden; otherwise reuse the still-valid cached element
                if (pane == null || r.Final == CopilotPaneMode.NotFound || r.Final == CopilotPaneMode.Hidden) ForgetPane(vs);
                pane = FindPane(vs) ?? pane;
                r.Candidates = Math.Max(r.Candidates, _lastPaneCandidates);
                r.Final = Observe(pane, out detail);
                if (done(r.Final) || sw.ElapsedMilliseconds >= ms) break;
            }
            log($"状态 / state：{CopilotPaneModes.Describe(r.Final)}（{sw.ElapsedMilliseconds}ms），{detail}");
            return pane;
        }

        /// <summary>读取窗格的显示状态（「返回」、对话列表、输入框是否可见）。/ Reads the pane display state (visibility of Back, the list and the input).</summary>
        private static CopilotPaneMode Observe(AutomationElement pane, out string detail)
        {
            if (pane == null) { detail = "无窗格 / no pane"; return CopilotPaneMode.NotFound; }
            bool off = SafeOffscreen(pane);
            bool? back = null, list = null, input = null;
            try { var b = RawFindAll(pane, TreeScope.Descendants, IdCond(CopilotPaneModes.BackToChatId), 1).FirstOrDefault(); if (b != null) back = !SafeOffscreen(b); } catch { }
            try { var l = FindList(pane); if (l != null) list = !SafeOffscreen(l); } catch { }
            try
            {
                var hosts = RawFindAll(pane, TreeScope.Children, IdCond("WpfTextViewHost"), 3);
                if (hosts.Count > 0)
                {
                    input = false;
                    foreach (var h in hosts)
                        if (RawFindAll(h, TreeScope.Subtree, IdCond("WpfTextView"), 3).Any(v => !SafeOffscreen(v))) { input = true; break; }
                }
            }
            catch { }
            detail = $"窗格可见 / pane visible={!off} 返回 / back={V(back)} 对话列表 / list={V(list)} 输入框 / input={V(input)}";
            return CopilotPaneModes.Classify(true, off, back, list, input);
        }

        private static string V(bool? b) => b.HasValue ? (b.Value ? "可见/on" : "隐藏/off") : "无/none";
    }
}
