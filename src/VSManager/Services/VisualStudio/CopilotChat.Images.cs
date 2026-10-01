using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
                HashSet<string> before;
                if (_queueGuard != null)
                {
                    if (!TryAttachmentIds(pane, out before) || before.Count != 0)
                        return ManualChatProtection.WaitPrefix + "附件不为空或无法确认 / Attachments are not empty or cannot be confirmed";
                }
                else before = AttachmentIds(pane);

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

                // 附件按数量确认：附件列表重新渲染时 UI 自动化的 RuntimeId 可能变化，按 ID 比对会把本程序刚粘贴的图片误判为「被改动」。
                // Attachments are confirmed by count: UI Automation RuntimeIds may change when the chip list re-renders, so an ID
                // comparison would mistake the images this program just pasted for "changed" ones.
                int baseline = before.Count;
                int confirmedCount = 0;
                var addedIds = new HashSet<string>();
                for (int index = 0; index < images.Count; index++)
                {
                    var image = images[index];
                    if (!Refocus(vs, edit) || GetClipboardSequenceNumber() != clipboardVersion)
                        return "焦点或剪贴板已改变，未发送（已粘贴的附件保留在 VS，请检查后重试）";
                    using (var bitmap = image.OpenBitmap()) DirectClipboard.SetImage(bitmap);
                    clipboardVersion = GetClipboardSequenceNumber();
                    // 剪贴板监听程序收到变更后会短暂占用剪贴板；此时按 Ctrl+V，VS 读不到图片，这一张会被静默丢弃。
                    // Clipboard listeners briefly hold the clipboard after a change; a Ctrl+V at that moment reads nothing and the image is silently dropped.
                    bool released = DirectClipboard.WaitReleased(ClipboardSettleMs, ClipboardReleaseTimeoutMs);
                    if (GetClipboardSequenceNumber() != clipboardVersion)
                    {
                        using (var bitmap = image.OpenBitmap()) DirectClipboard.SetImage(bitmap);
                        clipboardVersion = GetClipboardSequenceNumber();
                        released = DirectClipboard.WaitReleased(ClipboardSettleMs, ClipboardReleaseTimeoutMs);
                    }
                    T($"图片 / image {index + 1}/{images.Count}：已写入剪贴板 / clipboard written，剪贴板空闲 / released={released}，当前附件 / attachments={baseline + confirmedCount}");
                    if (!Refocus(vs, edit)) return "输入焦点已改变，未发送（请检查 VS 草稿后重试）";
                    blocked = GuardQueueSubmit(vs, pane, edit, prompt, addedIds);
                    if (blocked != null) return blocked;
                    Combo(VK_CONTROL, VK_V);
                    var started = DateTime.UtcNow;
                    var until = started.AddMilliseconds(ImageConfirmTimeoutMs);
                    bool repasted = false;
                    int repastes = 0;
                    var lastPaste = started;
                    var state = ImagePasteState.Pending;
                    HashSet<string> current = null;
                    do
                    {
                        Thread.Sleep(100);
                        if (_queueGuard != null)
                        {
                            if (!TryAttachmentIds(pane, out current))
                                return ManualChatProtection.UncertainPrefix + "无法读取附件，请检查草稿 / Cannot read attachments; inspect the draft";
                        }
                        else current = AttachmentIds(pane);
                        state = ImagePasteCheck.Evaluate(baseline, confirmedCount, current.Count);
                        if (state != ImagePasteState.Pending) break;
                        // 一直没有新附件：这次粘贴没被 VS 接收（未加入任何内容），重新写入并再粘贴。
                        // Still no new attachment: VS did not take this paste (nothing was added), so write and paste again.
                        // 多图时 VS 可能还在处理上一张而忽略这次粘贴：每次补粘前重新写入图片（新的剪贴板版本），最多补粘 MaxRepastes 次。
                        // With several images VS may still be busy with the previous one and ignore this paste: rewrite the image
                        // (fresh clipboard version) before each re-paste, up to MaxRepastes times.
                        if (repastes < MaxRepastes && DateTime.UtcNow - lastPaste >= TimeSpan.FromMilliseconds(RepasteAfterMs) && current.Count == baseline + confirmedCount)
                        {
                            if (!Refocus(vs, edit)) return "输入焦点已改变，未发送（已粘贴的附件保留在 VS，请检查后重试）";
                            using (var bitmap = image.OpenBitmap()) DirectClipboard.SetImage(bitmap);
                            clipboardVersion = GetClipboardSequenceNumber();
                            DirectClipboard.WaitReleased(ClipboardSettleMs, ClipboardReleaseTimeoutMs);
                            if (!Refocus(vs, edit)) return "输入焦点已改变，未发送（已粘贴的附件保留在 VS，请检查后重试）";
                            repastes++;
                            T($"图片 / image {index + 1}/{images.Count}：{RepasteAfterMs} ms 内未出现附件，第 {repastes} 次重新粘贴 / no attachment yet, re-paste #{repastes}");
                            Combo(VK_CONTROL, VK_V);
                            lastPaste = DateTime.UtcNow;
                            repasted = true;
                        }
                    } while (DateTime.UtcNow < until && ForegroundIs(vs));
                    if (state == ImagePasteState.Unexpected)
                        return (_queueGuard != null ? ManualChatProtection.UncertainPrefix : "") + $"附件数量异常（期望 {baseline + confirmedCount + 1}，实际 {current?.Count}），请检查草稿 / Unexpected attachment count; inspect the draft";
                    if (state != ImagePasteState.Confirmed)
                    {
                        T($"图片 / image {index + 1}/{images.Count}：未能确认附件 / not confirmed，附件 / attachments={current?.Count}，前台 / foreground={ForegroundIs(vs)}");
                        return $"未能确认第 {index + 1}/{images.Count} 张图片附件已加入，未发送。请确认该 VS / 模型支持图片，并检查 VS 草稿后重试";
                    }
                    confirmedCount = current.Count - baseline;
                    addedIds = new HashSet<string>(current.Except(before));
                    T($"图片 / image {index + 1}/{images.Count}：已确认 / confirmed，附件 / attachments={current.Count}" + (repasted ? "（补粘 / after re-paste）" : ""));
                    if (!ForegroundIs(vs)) return "前台窗口已改变，未发送（请检查 VS 草稿后重试）";
                    // 等 Copilot 处理完刚加入的图片再粘下一张 / Let Copilot finish the image just added before pasting the next one
                    Thread.Sleep(ImageSettleMs);
                    if (!Refocus(vs, edit)) return "无法重新聚焦输入框，未发送（请检查 VS 草稿后重试）";
                }

                var finalIds = AttachmentIds(pane);
                if (!Refocus(vs, edit) || finalIds.Count != baseline + confirmedCount ||
                    !PasteVerifier.IsConfirmed(PasteVerifier.Classify(prompt, null, GetEditText(edit))))
                    return "发送前输入内容或附件发生变化，已取消发送（请检查 VS 草稿）";
                addedIds = new HashSet<string>(finalIds.Except(before));

                blocked = GuardQueueSubmit(vs, pane, edit, prompt, addedIds);
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
                        AttachmentIds(pane).Count <= baseline))
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

        private static HashSet<string> AttachmentIds(AutomationElement pane)
        {
            var result = new HashSet<string>();
            var list = PaneChild(pane, "PART_AttachmentsList");
            if (list == null) return result;
            foreach (AutomationElement item in list.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
            {
                bool addButton = item.FindFirst(TreeScope.Descendants, IdCond("PART_AttachmentsButton")) != null;
                if (IsUserAttachment(item.Current.Name, addButton)) result.Add(string.Join(".", item.GetRuntimeId()));
            }
            return result;
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
