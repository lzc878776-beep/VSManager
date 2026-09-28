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

                Key(VK_SHIFT, true); Key(VK_MENU, true); Key(VK_CONTROL, true);
                Native.Activate(vs.MainHwnd);
                for (int i = 0; i < 20 && !ForegroundIs(vs); i++) Thread.Sleep(50);
                if (!ForegroundIs(vs)) return "无法激活该 VS，未发送图片";
                edit.SetFocus();
                if (!WaitFocus(edit, 1000) || !ForegroundIs(vs)) return "无法聚焦 Copilot 输入框，未发送图片";
                string currentText = GetEditText(edit);
                if (!ManualChatProtection.IsEmptyInput(currentText)) return _queueGuard != null ? ManualChatProtection.WaitPrefix + ManualChatProtection.Reason(currentText == null ? ManualChatObservation.Unknown : ManualChatObservation.Draft) : "VS 输入框出现新草稿或无法读取，已取消图片发送";
                if (GetClipboardSequenceNumber() != clipboardVersion) return "剪贴板已被其他操作更改，已取消图片发送";

                string blocked = GuardQueueInput(vs, pane, edit);
                if (blocked != null) return blocked;
                string prompt = string.IsNullOrWhiteSpace(text) ? "请分析这些图片。" : text;
                // 直接写入剪贴板；此时尚未粘贴任何内容，被占用时稍后重试 / Direct write; nothing is pasted yet, so a busy clipboard retries later
                try { DirectClipboard.SetText(prompt.Replace("\n", "\r\n")); }
                catch (ExternalException ex)
                {
                    return SendRetryPolicy.ClipboardBusyPrefix + "剪贴板被其他程序占用，稍后自动重试，未发送图片 / The clipboard is busy; retrying later, images not sent: " + ex.Message;
                }
                clipboardVersion = GetClipboardSequenceNumber();
                clipboardChanged = true;
                if (!ForegroundIs(vs) || !HasFocus(edit)) return "输入焦点已改变，未发送图片";
                blocked = GuardQueueInput(vs, pane, edit, writing: true);
                if (blocked != null) return blocked;
                Combo(VK_CONTROL, VK_V);
                if (!WaitText(edit, value => PasteVerifier.IsConfirmed(PasteVerifier.Classify(prompt, null, value)), ConfirmTimeoutMs))
                    return "未能确认文字已粘贴，未发送（请检查 VS 草稿后重试）";

                var addedIds = new HashSet<string>();
                foreach (var image in images)
                {
                    if (!ForegroundIs(vs) || !HasFocus(edit) || GetClipboardSequenceNumber() != clipboardVersion)
                        return "焦点或剪贴板已改变，未发送（已粘贴的附件保留在 VS，请检查后重试）";
                    using (var bitmap = image.OpenBitmap()) DirectClipboard.SetImage(bitmap);
                    clipboardVersion = GetClipboardSequenceNumber();
                    if (!ForegroundIs(vs) || !HasFocus(edit)) return "输入焦点已改变，未发送（请检查 VS 草稿后重试）";
                    blocked = GuardQueueSubmit(vs, pane, edit, prompt, addedIds);
                    if (blocked != null) return blocked;
                    Combo(VK_CONTROL, VK_V);
                    var until = DateTime.UtcNow.AddSeconds(8);
                    bool confirmed = false;
                    do
                    {
                        Thread.Sleep(100);
                        HashSet<string> current;
                        if (_queueGuard != null)
                        {
                            if (!TryAttachmentIds(pane, out current))
                                return ManualChatProtection.UncertainPrefix + "无法读取附件，请检查草稿 / Cannot read attachments; inspect the draft";
                        }
                        else current = AttachmentIds(pane);
                        var added = current.Except(before).Except(addedIds).ToArray();
                        if (_queueGuard != null && (added.Length > 1 || !addedIds.IsSubsetOf(current)))
                            return ManualChatProtection.UncertainPrefix + "附件在上传时发生变化，请检查草稿 / Attachments changed during upload; inspect the draft";
                        if (added.Length == 1 && addedIds.All(current.Contains) && before.All(current.Contains))
                        {
                            addedIds.Add(added[0]);
                            confirmed = true;
                            break;
                        }
                    } while (DateTime.UtcNow < until && ForegroundIs(vs));
                    if (!confirmed)
                        return "未能确认图片附件已加入，未发送。请确认该 VS / 模型支持图片，并检查 VS 草稿后重试";
                    if (!ForegroundIs(vs)) return "前台窗口已改变，未发送（请检查 VS 草稿后重试）";
                    edit.SetFocus();
                    if (!WaitFocus(edit, 600)) return "无法重新聚焦输入框，未发送（请检查 VS 草稿后重试）";
                }

                var finalIds = AttachmentIds(pane);
                if (!ForegroundIs(vs) || !HasFocus(edit) || !addedIds.All(finalIds.Contains) ||
                    !PasteVerifier.IsConfirmed(PasteVerifier.Classify(prompt, null, GetEditText(edit))))
                    return "发送前输入内容或附件发生变化，已取消发送（请检查 VS 草稿）";

                blocked = GuardQueueSubmit(vs, pane, edit, prompt, addedIds);
                if (blocked != null) return blocked;
                // 只提交一次，延迟确认不能触发重复发送。/ Submit once; delayed acknowledgement must not cause duplicate prompts.
                if (!TryInvokeSend(pane)) return "图片已附加，但发送按钮不可用；请在 VS 中检查模型是否支持图片后发送";
                var sentUntil = DateTime.UtcNow.AddSeconds(5);
                do
                {
                    bool cancel = HasCancel(pane);
                    string remaining = GetEditText(edit);
                    if (cancel || (ManualChatProtection.IsEmptyInput(remaining) &&
                        !AttachmentIds(pane).Overlaps(addedIds)))
                        return "已发送文字和图片（已短暂切换到 VS）";
                    Thread.Sleep(100);
                } while (DateTime.UtcNow < sentUntil);
                return "已点击发送，但尚未确认成功。请在 VS 中查看，确认前不要重复发送";
            }
        }

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
