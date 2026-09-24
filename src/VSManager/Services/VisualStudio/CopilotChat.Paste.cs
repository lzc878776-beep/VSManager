using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 前台粘贴的确认逻辑：多手段确认（UI Automation 读取输入框 → 刷新元素重读 → 剪贴板 / 水印 / 焦点等旁证）、
    /// 带退避的轮询、可配置超时与自动重试，以及失败时的详细诊断。
    /// Confirmation of the foreground paste: several means of confirmation (read the input through UI Automation → re-read
    /// with a refreshed element → clipboard / watermark / focus as supporting evidence), back-off polling, a configurable
    /// timeout with automatic retries, and detailed diagnostics on failure.
    /// </summary>
    public partial class CopilotChat
    {
        /// <summary>粘贴确认超时（毫秒），来自设置 SendConfirmTimeoutSeconds。/ Paste confirmation timeout in ms, from SendConfirmTimeoutSeconds.</summary>
        private int ConfirmTimeoutMs
        {
            get
            {
                int sec = _getSettings()?.SendConfirmTimeoutSeconds ?? PasteVerifier.DefaultTimeoutSeconds;
                if (sec <= 0) sec = PasteVerifier.DefaultTimeoutSeconds;
                return Math.Min(Math.Max(2, sec), 120) * 1000;
            }
        }

        /// <summary>本次发送内的自动重试次数（关闭自动重试时为 0）。/ Automatic retries within one send (0 when auto retry is off).</summary>
        private int ConfirmRetries
        {
            get
            {
                var s = _getSettings();
                if (s == null) return AppSettings.DefaultSendRetryCount;
                return s.SendAutoRetry ? Math.Min(Math.Max(0, s.SendRetryCount), 5) : 0;
            }
        }

        /// <summary>一次粘贴尝试的结果与旁证。/ Outcome and supporting evidence of one paste attempt.</summary>
        private sealed class PasteReport
        {
            public string Want, Before, Current;
            public string Blocked;
            public PasteCheck Check;
            public long ElapsedMs;
            public int Polls;
            public bool Focused, Foreground;
            /// <summary>剪贴板内容与消息一致；null 表示无法读取。/ Clipboard equals the message; null = unreadable.</summary>
            public bool? ClipboardOk;
            /// <summary>粘贴期间剪贴板被其他程序改写。/ The clipboard was rewritten by another program during the paste.</summary>
            public bool ClipboardChanged;
            /// <summary>输入框水印（「询问 Copilot」）已隐藏，即输入框非空；null 表示无法判断。/ The input watermark is hidden (input not empty); null = unknown.</summary>
            public bool? WatermarkHidden;
            public int AttachmentsBefore = -1, AttachmentsAfter = -1;

            /// <summary>
            /// 「写进去了但确认不了」：文本读不到，但水印已隐藏、剪贴板内容正确、焦点仍在输入框。此时继续发送，由送达确认最终判定。
            /// "Written but not confirmable": the text is unreadable, but the watermark is hidden, the clipboard is right and the
            /// input still has focus. Sending continues and the delivery confirmation decides.
            /// </summary>
            public bool LikelyWritten =>
                Check == PasteCheck.Unreadable && WatermarkHidden == true && ClipboardOk == true && !ClipboardChanged && Focused && Foreground;

            public override string ToString() =>
                $"粘贴校验 / paste check：{Check}，耗时 / elapsed {ElapsedMs}ms（{Polls} 次读取 / reads），" +
                $"读取到 / read {PasteVerifier.Snippet(Current)}，粘贴前 / before {PasteVerifier.Snippet(Before)}，" +
                $"期望 / expected {PasteVerifier.Normalize(Want).Length} 字（规范化后 / normalized）";
        }

        /// <summary>写入剪贴板（带重试，剪贴板可能被其他程序短暂占用）。/ Writes the clipboard with retries (another program may hold it briefly).</summary>
        private static string SetClipboardText(string text)
        {
            try { Clipboard.SetDataObject(text, true, 20, 100); return null; }
            catch (Exception ex) when (ex is ExternalException || ex is ThreadStateException)
            {
                T("前台：写入剪贴板失败 / clipboard write failed：" + ex.Message);
                return "写入剪贴板失败（可能被其他程序占用）/ Could not write the clipboard (it may be in use by another program): " + ex.Message;
            }
        }

        /// <summary>
        /// 激活 VS（会恢复最小化窗口）并聚焦输入框；聚焦失败时执行 View.GitHub.Copilot.Chat 把对话窗格切到前台后再试。
        /// Activates VS (restoring a minimized window) and focuses the input; if that fails, runs View.GitHub.Copilot.Chat to
        /// bring the chat pane to the front and tries again.
        /// </summary>
        private string FocusForPaste(VsInstance vs, AutomationElement edit)
        {
            string blocked = BlockingDialogMessage(vs);
            if (blocked != null) return blocked;
            // 松开可能仍按住的修饰键，避免组合成其他快捷键 / Release modifiers that may still be down
            Key(VK_SHIFT, true); Key(VK_MENU, true); Key(VK_CONTROL, true);

            T("前台：激活 VS 窗口 / activating VS " + WindowState(vs));
            Native.Activate(vs.MainHwnd);
            for (int i = 0; i < 20 && !ForegroundIs(vs); i++) Thread.Sleep(50);
            blocked = BlockingDialogMessage(vs);
            if (blocked != null) return blocked;
            if (!ForegroundIs(vs))
            {
                T("前台：激活失败，前台窗口=0x" + Native.GetForegroundWindow().ToString("X"));
                return "无法激活该 VS 窗口，发送已取消 / Could not activate this VS window; send cancelled";
            }

            try { edit.SetFocus(); } catch (Exception ex) { T("前台：SetFocus 异常 " + ex.Message); }
            if (!WaitFocus(edit, 600))
            {
                blocked = BlockingDialogMessage(vs);
                if (blocked != null) return blocked;
                T("前台：输入框未获得焦点，执行 View.GitHub.Copilot.Chat 把对话窗格切到前台 / input not focused, bringing the chat pane to the front");
                try { DteWorker.Run(() => VsService.OpenCopilotChat(vs)).Wait(3000); } catch { }
                try { edit.SetFocus(); } catch (Exception ex) { T("前台：SetFocus 异常 " + ex.Message); }
                if (!WaitFocus(edit, 1000))
                {
                    T("前台：输入框未获得焦点 " + Describe(edit));
                    return "无法聚焦 Copilot 输入框，发送已取消 / Could not focus the Copilot input box; send cancelled";
                }
            }
            if (!ForegroundIs(vs)) return "发送前 VS 焦点已改变，发送已取消 / VS lost the foreground before sending; send cancelled";
            return null;
        }

        /// <summary>
        /// 全选后粘贴，并带退避轮询确认输入框内容；长文本仍在写入时延长等待（最多到 2 倍超时）。
        /// Selects all and pastes, then polls the input with back-off; waits longer (up to twice the timeout) while a long text is still arriving.
        /// </summary>
        private PasteReport PasteAndVerify(VsInstance vs, AutomationElement pane, ref AutomationElement edit, string text, int timeoutMs)
        {
            var rep = new PasteReport { Want = text };
            rep.AttachmentsBefore = SafeAttachmentCount(pane);
            rep.Before = GetEditText(edit);
            uint seq = GetClipboardSequenceNumber();
            var sw = Stopwatch.StartNew();

            rep.Blocked = BlockingDialogMessage(vs);
            if (rep.Blocked != null) return rep;
            if (!ForegroundIs(vs) || !HasFocus(edit)) { rep.Check = PasteCheck.NotWritten; return rep; }
            Combo(VK_CONTROL, VK_A);
            Thread.Sleep(60);
            rep.Blocked = BlockingDialogMessage(vs);
            if (rep.Blocked != null) return rep;
            if (!ForegroundIs(vs) || !HasFocus(edit)) { rep.Check = PasteCheck.NotWritten; return rep; }
            Combo(VK_CONTROL, VK_V);

            long deadline = timeoutMs, hardLimit = timeoutMs * 2L;
            int lastLen = -1, unreadable = 0;
            for (int step = 0; ; step++)
            {
                Thread.Sleep(PasteVerifier.NextDelayMs(step));
                rep.Blocked = BlockingDialogMessage(vs);
                if (rep.Blocked != null) return rep;
                rep.Current = GetEditText(edit);
                rep.Polls++;
                if (rep.Current == null && ++unreadable % 3 == 0)
                {
                    // 元素可能已失效（窗格重建），重新定位后再读 / The element may be stale (pane rebuilt): locate it again
                    var fresh = FindEdit(pane, vs.Pid);
                    if (fresh != null) { edit = fresh; rep.Current = GetEditText(edit); }
                }
                rep.Check = PasteVerifier.Classify(text, rep.Before, rep.Current);
                if (PasteVerifier.IsConfirmed(rep.Check)) break;

                long now = sw.ElapsedMilliseconds;
                if (rep.Check == PasteCheck.Pending && rep.Current.Length > lastLen && now + 1500 > deadline)
                    deadline = Math.Min(hardLimit, now + 1500);
                if (rep.Current != null) lastLen = rep.Current.Length;
                if (now >= deadline) break;
            }
            rep.ElapsedMs = sw.ElapsedMilliseconds;
            rep.Focused = HasFocus(edit);
            rep.Foreground = ForegroundIs(vs);
            rep.ClipboardChanged = GetClipboardSequenceNumber() != seq;
            if (!PasteVerifier.IsConfirmed(rep.Check))
            {
                rep.ClipboardOk = ClipboardHolds(text);
                rep.WatermarkHidden = WatermarkHidden(pane);
                rep.AttachmentsAfter = SafeAttachmentCount(pane);
            }
            return rep;
        }

        private static bool? ClipboardHolds(string text)
        {
            try { return Clipboard.ContainsText() && PasteVerifier.Normalize(Clipboard.GetText()) == PasteVerifier.Normalize(text); }
            catch (ExternalException) { return null; }
        }

        /// <summary>水印「询问 Copilot」是否隐藏（输入框非空）；找不到水印时返回 null。/ Whether the watermark is hidden (input not empty); null when not found.</summary>
        private static bool? WatermarkHidden(AutomationElement pane)
        {
            try
            {
                var cr = new CacheRequest { TreeFilter = Automation.RawViewCondition };
                cr.Add(AutomationElement.AutomationIdProperty);
                AutomationElement w;
                using (cr.Activate()) w = pane.FindFirst(TreeScope.Children, IdCond("PART_WatermarkTextBlock"));
                if (w == null) return null;
                var r = w.Current.BoundingRectangle;
                return w.Current.IsOffscreen || r.IsEmpty;
            }
            catch { return null; }
        }

        private static int SafeAttachmentCount(AutomationElement pane)
        {
            try { return AttachmentIds(pane).Count; } catch { return -1; }
        }

        /// <summary>VS 主窗口状态（最小化 / 最大化 / 可见 / 前台 / 所在屏幕与位置）。/ State of the VS main window.</summary>
        private static string WindowState(VsInstance vs)
        {
            try
            {
                IntPtr h = vs.MainHwnd;
                Native.GetWindowRect(h, out var r);
                var screen = Screen.FromHandle(h);
                return $"[最小化/minimized={Native.IsIconic(h)} 最大化/maximized={Native.IsZoomed(h)} 可见/visible={Native.IsWindowVisible(h)} " +
                       $"前台/foreground={ForegroundIs(vs)} 屏幕/screen={screen.DeviceName} rect={r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}]";
            }
            catch (Exception ex) { return "[窗口状态不可用 / window state unavailable: " + ex.Message + "]"; }
        }

        /// <summary>未能确认粘贴时写入发送日志的诊断信息。/ Diagnostics written to the send log when the paste is not confirmed.</summary>
        private static string Diagnose(VsInstance vs, AutomationElement pane, AutomationElement edit, PasteReport rep)
        {
            var sb = new StringBuilder();
            sb.Append("⚠ 粘贴确认诊断 / paste diagnostics：\r\n");
            sb.Append("           目标 VS / target：pid=").Append(vs.Pid).Append(" 「").Append(vs.DisplaySolution).Append("」 窗口 / window ").Append(WindowState(vs)).Append("\r\n");
            sb.Append("           对话窗格 / pane：").Append(Describe(pane)).Append("\r\n");
            sb.Append("           输入框 / input：").Append(Describe(edit)).Append(" 焦点/focus=").Append(rep.Focused).Append("\r\n");
            sb.Append("           判定 / verdict：").Append(rep.Check).Append("，耗时 / elapsed ").Append(rep.ElapsedMs).Append("ms，读取 / reads ").Append(rep.Polls).Append("\r\n");
            sb.Append("           读取到 / read：").Append(PasteVerifier.Snippet(rep.Current)).Append("\r\n");
            sb.Append("           剪贴板 / clipboard：内容一致/matches=").Append(rep.ClipboardOk?.ToString() ?? "?")
              .Append(" 被其他程序改写/changed by others=").Append(rep.ClipboardChanged).Append("\r\n");
            sb.Append("           水印已隐藏 / watermark hidden=").Append(rep.WatermarkHidden?.ToString() ?? "?")
              .Append("，附件 / attachments ").Append(rep.AttachmentsBefore).Append(" → ").Append(rep.AttachmentsAfter);
            return sb.ToString();
        }

        /// <summary>按失败类别给出可操作的提示。/ Actionable message per failure category.</summary>
        private static string PasteFailureMessage(PasteReport rep, int attempts)
        {
            string tried = $"已尝试 {attempts} 次 / tried {attempts} time(s)";
            string tail = "。详见发送日志 / See the send log for details";
            if (rep.ClipboardChanged && rep.ClipboardOk != true)
                return $"粘贴期间剪贴板被其他程序改写，发送已取消（{tried}）。请关闭剪贴板同步 / 管理类工具后重试 / " +
                       "The clipboard was changed by another program during the paste; send cancelled. Pause clipboard sync / manager tools and retry" + tail;
            switch (rep.Check)
            {
                case PasteCheck.NotWritten:
                    return $"消息没有写入 Copilot 输入框（输入框内容未变化，{tried}），发送已取消。请确认该 VS 未被模态对话框阻挡、Copilot 对话窗格可见 / " +
                           "The message was not written to the Copilot input box (unchanged); send cancelled. Make sure the VS is not blocked by a modal dialog and the Copilot pane is visible" + tail;
                case PasteCheck.Pending:
                    return $"消息未能在 {rep.ElapsedMs / 1000.0:0.#} 秒内完整粘贴（已写入 {PasteVerifier.Normalize(rep.Current).Length}/{PasteVerifier.Normalize(rep.Want).Length} 字，{tried}），发送已取消。" +
                           "可在「属性 → 发送确认」调大超时 / The paste did not finish in time; send cancelled. Increase the timeout in Settings → Send confirmation" + tail;
                case PasteCheck.Mismatch:
                case PasteCheck.NearMatch:
                    return $"Copilot 输入框内容与消息不一致（可能混入了原有草稿，{tried}），发送已取消，内容保留在 VS 输入框中 / " +
                           "The Copilot input differs from the message (an old draft may be mixed in); send cancelled, the text stays in the VS input box" + tail;
                default:
                    return $"无法读取 Copilot 输入框，未能确认粘贴结果（{tried}），为避免发送错误内容已取消发送；内容可能已在 VS 输入框中，请检查后手动发送或重试 / " +
                           "The Copilot input could not be read, so the paste could not be confirmed; send cancelled to avoid sending wrong text. The text may already be in the VS input box: check it, then send it manually or retry" + tail;
            }
        }
    }
}
