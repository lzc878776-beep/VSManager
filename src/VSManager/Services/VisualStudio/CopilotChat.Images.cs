using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace VSManager
{
    public partial class CopilotChat
    {
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        /// <summary>写入剪贴板后等监听程序开始读取的时长。/ Time for clipboard listeners to start reading after a write.</summary>
        private const int ClipboardSettleMs = 150;
        /// <summary>等待监听程序释放剪贴板的上限。/ Upper bound for waiting until listeners release the clipboard.</summary>
        private const int ClipboardReleaseTimeoutMs = 2000;
        /// <summary>单张图片等待附件出现的总时长。/ Total time to wait for one image's attachment.</summary>
        private const int ImageConfirmTimeoutMs = 15000;
        /// <summary>这么久仍没有新附件时补粘一次。/ Paste once more when no attachment has appeared after this long.</summary>
        private const int RepasteAfterMs = 2500;
        /// <summary>单张图片最多补粘次数。/ Maximum re-pastes for one image.</summary>
        private const int MaxRepastes = 3;
        /// <summary>图片确认后、粘贴下一张前的间隔（VS 处理刚加入的图片期间会忽略粘贴）。/ Pause after an image is confirmed, before the next paste (VS ignores pastes while processing the image just added).</summary>
        private const int ImageSettleMs = 800;

        private string SendImages(VsInstance vs, AutomationElement pane, AutomationElement edit,
            string text, IReadOnlyList<ChatImage> images, IntPtr returnTo)
        {
            string existing = GetEditText(edit);
            if (existing == null) return _queueGuard != null ? ManualChatProtection.WaitPrefix + ManualChatProtection.Reason(ManualChatObservation.Unknown) : "无法读取 VS 输入框，未发送图片";
            if (ManualChatProtection.HasDraft(existing)) return _queueGuard != null ? ManualChatProtection.WaitPrefix + ManualChatProtection.Reason(ManualChatObservation.Draft) : "VS 输入框已有草稿，请先发送或清空后再发送图片（平台草稿已保留）";

            uint clipboardVersion = GetClipboardSequenceNumber();
            ClipboardBackup backup;
            try { backup = new ClipboardBackup(); }
            catch (Exception ex) when (ex is ExternalException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                // 剪贴板暂被占用：尚未改动 VS，稍后带图片重试，而不是退化为只发文字 / Clipboard busy: VS is untouched, so retry later with the images instead of falling back to text only
                return SendRetryPolicy.ClipboardBusyPrefix + "无法备份剪贴板，未发送图片，稍后自动重试 / Could not back up the clipboard; retrying later: " + ex.Message;
            }

            using (backup)
            {
                bool clipboardChanged = false;
                string result = null;
                try
                {
                    result = PasteAndSendImages(vs, pane, edit, text, images, ref clipboardVersion, ref clipboardChanged);
                }
                catch (Exception ex) when (ex is ExternalException || ex is ElementNotAvailableException || ex is InvalidOperationException)
                {
                    result = "图片发送失败：" + ex.Message + "（请检查 VS 中的附件与草稿后再重试）";
                }
                finally
                {
                    if (clipboardChanged && GetClipboardSequenceNumber() == clipboardVersion)
                    {
                        try { backup.Restore(); }
                        catch (ExternalException ex)
                        {
                            if (result == null) throw;
                            result += "；恢复剪贴板失败：" + ex.Message;
                        }
                    }
                    if (returnTo != IntPtr.Zero && ForegroundIs(vs)) Native.Activate(returnTo);
                    Poke();
                }
                return result;
            }
        }

        private string PasteAndSendImages(VsInstance vs, AutomationElement pane, AutomationElement edit,
            string text, IReadOnlyList<ChatImage> images, ref uint clipboardVersion, ref bool clipboardChanged)
        {
            var cache = new CacheRequest { TreeFilter = Automation.RawViewCondition };
            using (cache.Activate())
            {
                int before;
                if (_queueGuard != null)
                {
                    if (!TryAttachmentCount(pane, out before) || before != 0)
                        return ManualChatProtection.WaitPrefix + "附件不为空或无法确认 / Attachments are not empty or cannot be confirmed";
                }
                else before = AttachmentCount(pane);

                string prompt = string.IsNullOrWhiteSpace(text) ? "请分析这些图片。" : text;
                if (GetClipboardSequenceNumber() != clipboardVersion) return "剪贴板已被其他操作更改，已取消图片发送";
                // 与纯文字发送一致：先写剪贴板再激活 VS。剪贴板被监听程序占用时写入可能等待数秒，放在聚焦之前，
                // 聚焦后到粘贴之间就不会有长时间空档（之前正是这段空档里焦点丢失，导致带图任务一律失败）。
                // As with text-only sends, write the clipboard before activating VS. With a clipboard listener the write can
                // wait for seconds; doing it before focusing leaves no long gap between focus and paste (focus used to be
                // lost in that gap, so every task with images failed).
                try { DirectClipboard.SetText(prompt.Replace("\n", "\r\n")); }
                catch (ExternalException ex)
                {
                    return SendRetryPolicy.ClipboardBusyPrefix + "剪贴板被其他程序占用，稍后自动重试，未发送图片 / The clipboard is busy; retrying later, images not sent: " + ex.Message;
                }
                clipboardVersion = GetClipboardSequenceNumber();
                clipboardChanged = true;

                // 激活与聚焦沿用文字发送的流程（含切回对话窗格的兜底），并确认焦点稳定后再粘贴。
                // Activation and focus reuse the text-send flow (including the chat-pane fallback) and wait for focus to settle before pasting.
                string focusError = FocusForPaste(vs, edit) ?? SettleFocus(vs, edit);
                if (focusError != null) return PrePasteFocusFailure(focusError);
                string currentText = GetEditText(edit);
                if (!ManualChatProtection.IsEmptyInput(currentText)) return _queueGuard != null ? ManualChatProtection.WaitPrefix + ManualChatProtection.Reason(currentText == null ? ManualChatObservation.Unknown : ManualChatObservation.Draft) : "VS 输入框出现新草稿或无法读取，已取消图片发送";

                string blocked = GuardQueueInput(vs, pane, edit);
                if (blocked != null) return blocked;
                if (GetClipboardSequenceNumber() != clipboardVersion)
                {
                    // 尚未粘贴：剪贴板被改写时重新写入一次 / Nothing pasted yet: write again if the clipboard was replaced
                    try { DirectClipboard.SetText(prompt.Replace("\n", "\r\n")); }
                    catch (ExternalException ex)
                    {
                        return SendRetryPolicy.ClipboardBusyPrefix + "剪贴板被其他程序占用，稍后自动重试，未发送图片 / The clipboard is busy; retrying later, images not sent: " + ex.Message;
                    }
                    clipboardVersion = GetClipboardSequenceNumber();
                }
                if (!Refocus(vs, edit)) return PrePasteFocusFailure("输入焦点已改变，未发送图片");
                blocked = GuardQueueInput(vs, pane, edit, writing: true);
                if (blocked != null) return blocked;
                Combo(VK_CONTROL, VK_V);
                if (!WaitText(edit, value => PasteVerifier.IsConfirmed(PasteVerifier.Classify(prompt, null, value)), ConfirmTimeoutMs))
                    return "未能确认文字已粘贴，未发送（请检查 VS 草稿后重试）";

                // 附件按数量确认（见 ImagePasteCheck.CountAttachments：多张图片在 UI 自动化树里只显示为一项，须用 SizeOfSet 计数）。
                // Attachments are confirmed by count (see ImagePasteCheck.CountAttachments: several images show up as a single UIA
                // item, so they must be counted through SizeOfSet).
                int baseline = before;
                int confirmedCount = 0;
                for (int index = 0; index < images.Count; index++)
                {
                    var image = images[index];
                    string progress = $"（已加入 {confirmedCount}/{images.Count} 张，已粘贴的附件保留在 VS 草稿中，请检查后重试 / {confirmedCount}/{images.Count} images attached; pasted ones stay in the VS draft, inspect it before retrying）";
                    if (!Refocus(vs, edit) || GetClipboardSequenceNumber() != clipboardVersion)
                        return "焦点或剪贴板已改变，未发送" + progress;
                    bool released = WriteImage(image, ref clipboardVersion);
                    if (!Refocus(vs, edit)) return "输入焦点已改变，未发送" + progress;
                    // 粘贴前先确认附件数仍是已确认的数量，不能在数量不明时再粘 / Before pasting, confirm the count is still the confirmed one; never paste blind
                    string countError = ReadAttachmentCount(pane, out int current);
                    if (countError != null) return countError;
                    T($"图片 / image {index + 1}/{images.Count}：已写入剪贴板 / clipboard written，剪贴板空闲 / released={released}，当前附件 / attachments={current}");
                    if (current != baseline + confirmedCount)
                        return UnexpectedCount(baseline + confirmedCount, current, progress);
                    blocked = GuardQueueSubmit(vs, pane, edit, prompt, current);
                    if (blocked != null) return blocked;
                    Combo(VK_CONTROL, VK_V);
                    var started = DateTime.UtcNow;
                    var until = started.AddMilliseconds(ImageConfirmTimeoutMs);
                    int repastes = 0;
                    var lastPaste = started;
                    var state = ImagePasteState.Pending;
                    do
                    {
                        Thread.Sleep(100);
                        countError = ReadAttachmentCount(pane, out current);
                        if (countError != null) return countError;
                        state = ImagePasteCheck.Evaluate(baseline, confirmedCount, current);
                        if (state != ImagePasteState.Pending) break;
                        // 附件数没有递增：不直接重按 Ctrl+V，先清空剪贴板再重写这张图片，让 VS 读到全新的剪贴板内容；
                        // 重写后再确认一次附件数——上一次粘贴可能在等待期间才落地，此时再粘会重复加入。
                        // The count did not grow: instead of pressing Ctrl+V again, clear the clipboard and rewrite this image so VS
                        // reads fresh clipboard content; re-check the count after rewriting, since the previous paste may land meanwhile
                        // and pasting again would add a duplicate.
                        if (repastes < MaxRepastes && DateTime.UtcNow - lastPaste >= TimeSpan.FromMilliseconds(RepasteAfterMs))
                        {
                            if (!Refocus(vs, edit)) return "输入焦点已改变，未发送" + progress;
                            DirectClipboard.Clear();
                            DirectClipboard.WaitReleased(ClipboardSettleMs, ClipboardReleaseTimeoutMs);
                            WriteImage(image, ref clipboardVersion);
                            countError = ReadAttachmentCount(pane, out current);
                            if (countError != null) return countError;
                            state = ImagePasteCheck.Evaluate(baseline, confirmedCount, current);
                            if (state != ImagePasteState.Pending) break;
                            if (!Refocus(vs, edit)) return "输入焦点已改变，未发送" + progress;
                            repastes++;
                            T($"图片 / image {index + 1}/{images.Count}：{RepasteAfterMs} ms 内附件数未递增（仍为 {current}），已清空并重写剪贴板，第 {repastes} 次重新粘贴 / count did not grow (still {current}); clipboard cleared and rewritten, re-paste #{repastes}");
                            Combo(VK_CONTROL, VK_V);
                            lastPaste = DateTime.UtcNow;
                        }
                    } while (DateTime.UtcNow < until && ForegroundIs(vs));
                    if (state == ImagePasteState.Unexpected)
                        return UnexpectedCount(baseline + confirmedCount + 1, current, progress);
                    if (state != ImagePasteState.Confirmed)
                    {
                        double waited = (DateTime.UtcNow - started).TotalSeconds;
                        T($"图片 / image {index + 1}/{images.Count}：{waited:0.#} 秒内附件数未增长，中止 / attachment count stalled for {waited:0.#} s, aborting，附件 / attachments={current}，补粘 / re-pastes={repastes}，前台 / foreground={ForegroundIs(vs)}");
                        return $"第 {index + 1}/{images.Count} 张图片在 {waited:0.#} 秒内未能加入（附件数停在 {current}，补粘 {repastes} 次），已中止，未发送不完整的消息" + progress
                            + "。请确认该 VS / 模型支持图片 / Image could not be attached; aborted without sending an incomplete message";
                    }
                    confirmedCount = current - baseline;
                    T($"图片 / image {index + 1}/{images.Count}：已确认 / confirmed，附件 / attachments={current}" + (repastes > 0 ? $"（补粘 {repastes} 次 / after {repastes} re-paste(s)）" : ""));
                    if (!ForegroundIs(vs)) return "前台窗口已改变，未发送" + progress;
                    // 等 Copilot 处理完刚加入的图片再粘下一张 / Let Copilot finish the image just added before pasting the next one
                    Thread.Sleep(ImageSettleMs);
                    if (!Refocus(vs, edit)) return "无法重新聚焦输入框，未发送" + progress;
                }

                int finalCount = AttachmentCount(pane);
                if (!Refocus(vs, edit) || finalCount != baseline + confirmedCount ||
                    !PasteVerifier.IsConfirmed(PasteVerifier.Classify(prompt, null, GetEditText(edit))))
                    return "发送前输入内容或附件发生变化，已取消发送（请检查 VS 草稿）";

                blocked = GuardQueueSubmit(vs, pane, edit, prompt, finalCount);
                if (blocked != null) return blocked;
                // 只提交一次，延迟确认不能触发重复发送。/ Submit once; delayed acknowledgement must not cause duplicate prompts.
                Stage(SendStage.Submit);
                if (!TryInvokeSend(pane)) return "图片已附加，但发送按钮不可用；请在 VS 中检查模型是否支持图片后发送";
                var sentUntil = DateTime.UtcNow.AddSeconds(5);
                do
                {
                    bool cancel = HasCancel(pane);
                    string remaining = GetEditText(edit);
                    if (cancel || (ManualChatProtection.IsEmptyInput(remaining) &&
                        AttachmentCount(pane) <= baseline))
                        return "已发送文字和图片（已短暂切换到 VS）";
                    Thread.Sleep(100);
                } while (DateTime.UtcNow < sentUntil);
                return "已点击发送，但尚未确认成功。请在 VS 中查看，确认前不要重复发送";
            }
        }

        /// <summary>
        /// VS 被激活后会异步恢复它自己记住的焦点，可能把刚设置到输入框的焦点夺走：短暂等待，确认焦点稳定，必要时重新聚焦。
        /// After activation VS restores its own remembered focus asynchronously and may take focus away from the input just
        /// focused: wait briefly, confirm the focus is stable and refocus when needed.
        /// </summary>
        private static string SettleFocus(VsInstance vs, AutomationElement edit)
        {
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(150);
                if (!ForegroundIs(vs)) return "发送前 VS 焦点已改变，发送已取消 / VS lost the foreground before sending; send cancelled";
                if (HasFocus(edit)) return null;
                T("图片：输入框焦点被 VS 收回，重新聚焦 / images: VS took the input focus back, refocusing");
                try { edit.SetFocus(); } catch (Exception ex) { T("图片：SetFocus 异常 " + ex.Message); }
                WaitFocus(edit, 600);
            }
            return ForegroundIs(vs) && HasFocus(edit) ? null : "无法聚焦 Copilot 输入框，未发送图片 / Could not focus the Copilot input box; images not sent";
        }

        /// <summary>VS 仍在前台时确保输入框有焦点（只移动焦点，不改变输入内容）。/ Ensures the input has focus while VS is still in front (moves focus only, never changes the input).</summary>
        private static bool Refocus(VsInstance vs, AutomationElement edit)
        {
            if (!ForegroundIs(vs)) return false;
            if (HasFocus(edit)) return true;
            T("图片：输入框失去焦点，VS 仍在前台，重新聚焦 / images: input lost focus while VS is in front, refocusing");
            try { edit.SetFocus(); } catch (Exception ex) { T("图片：SetFocus 异常 " + ex.Message); return false; }
            return WaitFocus(edit, 600) && ForegroundIs(vs);
        }

        /// <summary>
        /// 粘贴前的激活 / 聚焦失败没有改动 VS：队列任务稍后自动重试（与纯文字发送一致），而不是判为投递失败；
        /// 直接发送保持以「未发送图片」结尾，仍可回退为只发文字。
        /// Activation / focus failures before pasting leave VS untouched: queued tasks retry automatically later (as text-only
        /// sends do) instead of being marked as delivery failures; direct sends keep the "未发送图片" (images not sent) ending so
        /// they can still fall back to text only.
        /// </summary>
        internal static string PrePasteFocusResult(string error, bool queued)
        {
            if (error == null || SendRetryPolicy.IsBlocked(error)) return error;
            if (queued) return SendRetryPolicy.UserBusyPrefix + error;
            return error.EndsWith("未发送图片", StringComparison.Ordinal) ? error : "输入焦点未就绪（" + error + "），未发送图片";
        }

        private string PrePasteFocusFailure(string error) => PrePasteFocusResult(error, _queueGuard != null);

        /// <summary>
        /// 写入图片并等剪贴板监听程序释放；期间剪贴板被改写则再写一次。返回是否确认已释放。
        /// 剪贴板监听程序收到变更后会短暂占用剪贴板；此时按 Ctrl+V，VS 读不到图片，这一张会被静默丢弃。
        /// Writes the image and waits for clipboard listeners to release it, writing once more if the clipboard was replaced
        /// meanwhile. Returns whether release was confirmed. Listeners briefly hold the clipboard after a change; a Ctrl+V at that
        /// moment reads nothing and the image is silently dropped.
        /// </summary>
        private static bool WriteImage(ChatImage image, ref uint clipboardVersion)
        {
            using (var bitmap = image.OpenBitmap()) DirectClipboard.SetImage(bitmap);
            clipboardVersion = GetClipboardSequenceNumber();
            bool released = DirectClipboard.WaitReleased(ClipboardSettleMs, ClipboardReleaseTimeoutMs);
            if (GetClipboardSequenceNumber() == clipboardVersion) return released;
            using (var bitmap = image.OpenBitmap()) DirectClipboard.SetImage(bitmap);
            clipboardVersion = GetClipboardSequenceNumber();
            return DirectClipboard.WaitReleased(ClipboardSettleMs, ClipboardReleaseTimeoutMs);
        }

        /// <summary>读取附件数；队列发送读取失败时返回待核实的错误。/ Reads the attachment count; for queued sends a failed read returns an uncertain-result error.</summary>
        private string ReadAttachmentCount(AutomationElement pane, out int count)
        {
            if (_queueGuard == null) { count = AttachmentCount(pane); return null; }
            return TryAttachmentCount(pane, out count) ? null
                : ManualChatProtection.UncertainPrefix + "无法读取附件，请检查草稿 / Cannot read attachments; inspect the draft";
        }

        private string UnexpectedCount(int expected, int actual, string progress) =>
            (_queueGuard != null ? ManualChatProtection.UncertainPrefix : "") +
            $"附件数量异常（期望 {expected}，实际 {actual}），已中止，未发送" + progress + " / Unexpected attachment count; aborted without sending";

        /// <summary>
        /// 附件区的用户附件数（不含「添加引用」按钮与活动文档）。多张粘贴的图片只显示为一个 UI 自动化元素，
        /// 因此用列表项的 SizeOfSet 计数，见 <see cref="ImagePasteCheck.CountAttachments"/>。
        /// User attachments in the attachment list (excluding the add-reference button and the active document). Several pasted
        /// images show up as one UIA element, so the list items' SizeOfSet is used; see <see cref="ImagePasteCheck.CountAttachments"/>.
        /// </summary>
        private static int AttachmentCount(AutomationElement pane)
        {
            var list = PaneChild(pane, "PART_AttachmentsList");
            if (list == null) return 0;
            var user = new HashSet<string>();
            var other = new HashSet<string>();
            int? setSize = null;
            foreach (AutomationElement item in list.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
            {
                bool addButton = item.FindFirst(TreeScope.Descendants, IdCond("PART_AttachmentsButton")) != null;
                string id = string.Join(".", item.GetRuntimeId());
                (IsUserAttachment(item.Current.Name, addButton) ? user : other).Add(id);
                if (setSize == null) setSize = SizeOfSet(item);
            }
            return ImagePasteCheck.CountAttachments(user.Count, other.Count, setSize);
        }

        private const int UIA_SizeOfSetPropertyId = 30153;
        [DllImport("UIAutomationCore.dll")] private static extern int UiaGetPropertyValue(SafeHandle node, int propertyId, out object value);
        private static readonly FieldInfo AutomationNodeField = typeof(AutomationElement).GetField("_hnode", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// 读取 UIA 的 SizeOfSet（WPF 列表项返回列表实际项数）。托管 UI 自动化客户端不认识该属性，只能通过元素的 UIA 节点调用
        /// UiaGetPropertyValue；任何失败都返回 null，由调用方退回可见数量。
        /// Reads UIA SizeOfSet (WPF list items report the real item count). The managed UIA client does not know this property,
        /// so UiaGetPropertyValue is called on the element's UIA node; any failure returns null and callers fall back to the visible count.
        /// </summary>
        private static int? SizeOfSet(AutomationElement item)
        {
            try
            {
                if (AutomationNodeField?.GetValue(item) is SafeHandle node && !node.IsInvalid && !node.IsClosed
                    && UiaGetPropertyValue(node, UIA_SizeOfSetPropertyId, out object value) >= 0 && value is int size && size > 0)
                    return size;
            }
            catch (Exception ex) { T("附件：读取 SizeOfSet 失败 / attachments: SizeOfSet read failed " + ex.GetType().Name); }
            return null;
        }

        private static readonly HashSet<string> ImplicitAttachmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "活动文档", "活动文件", "当前文档", "当前文件", "Active document", "Active file", "Current document", "Current file"
        };

        /// <summary>“添加引用”按钮与 VS 自动附带的活动文档不是用户附件。/ The add-reference button and VS's implicit active-document chip are not user attachments.</summary>
        internal static bool IsUserAttachment(string name, bool containsAddButton) =>
            !containsAddButton && !ImplicitAttachmentNames.Contains((name ?? "").Trim());

        private sealed class ClipboardBackup : IDisposable
        {
            private readonly DataObject _data = new DataObject();
            private readonly List<IDisposable> _owned = new List<IDisposable>();
            private readonly bool _empty;

            public ClipboardBackup()
            {
                var source = Clipboard.GetDataObject();
                _empty = source == null || source.GetFormats(false).Length == 0;
                try
                {
                    if (source == null) return;
                    foreach (string format in source.GetFormats(false))
                    {
                        object value = source.GetData(format, false);
                        if (value is Image image)
                        {
                            var copy = new Bitmap(image);
                            _owned.Add(copy);
                            value = copy;
                        }
                        else if (value is MemoryStream stream)
                        {
                            var copy = new MemoryStream(stream.ToArray());
                            _owned.Add(copy);
                            value = copy;
                        }
                        if (value != null) _data.SetData(format, false, value);
                    }
                }
                catch { Dispose(); throw; }
            }

            public void Restore()
            {
                if (_empty) { DirectClipboard.Clear(); return; }
                var formats = _data.GetFormats(false);
                // 常见的纯图片 / 纯文字内容直接写回（例如用户刚截的图），其他格式仍按原样恢复
                // Plain images / text (e.g. a fresh screenshot) are written back directly; other formats are restored as they were
                bool onlyImageOrText = formats.All(f => f == DataFormats.Bitmap || f == DataFormats.Dib || f == "DeviceIndependentBitmap" || f == "Format17" || f == "PNG"
                    || f == DataFormats.UnicodeText || f == DataFormats.Text || f == DataFormats.OemText || f == DataFormats.Locale || f == "System.String");
                if (onlyImageOrText && _data.GetData(DataFormats.Bitmap, false) is Image image) { DirectClipboard.SetImage(image); return; }
                if (onlyImageOrText && !formats.Any(f => f == DataFormats.Bitmap || f == DataFormats.Dib) && _data.GetData(DataFormats.UnicodeText, false) is string text)
                {
                    DirectClipboard.SetText(text);
                    return;
                }
                Clipboard.SetDataObject(_data, true, 30, 100);
            }

            public void Dispose()
            {
                foreach (var value in _owned) value.Dispose();
                _owned.Clear();
            }
        }
    }
}
