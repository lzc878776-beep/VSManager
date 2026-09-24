using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 主窗口的附件能力：AI 助手附件入队、带附件任务的发送（图片粘贴、文本内联、其他文件发送路径）、查看附件与过期清理。
    /// Attachment features of the main window: enqueueing AI tasks with attachments, sending them (images pasted, text inlined,
    /// other files as paths), viewing attachments and expiry cleanup.
    /// </summary>
    public partial class MainForm : IAgentAttachmentHost, ITaskAttachmentDispatchHost
    {
        private System.Threading.Timer _attachmentCleanupTimer;
        private int _attachmentCleanupRunning;

        Task<string> IAgentAttachmentHost.QueueTask(VsInstance v, string text, AttachmentRef[] attachments) =>
            OnUi(() => EnqueueTextTask(v, text, "AI", attachments));

        Task<string> IAgentAttachmentHost.ParkTask(SolutionEntry e, string text, AttachmentRef[] attachments) =>
            OnUi(() =>
            {
                string r = ParkTaskCore(e, text, attachments);
                return r;
            });

        /// <summary>入队提示：「本任务包含 N 张图片、M 个文件」。/ Enqueue notice: "this task includes N images and M files".</summary>
        private static string AttachmentQueuedNote(QueuedTask q)
        {
            string zh = AttachmentPolicy.CountText(q.Attachments), en = AttachmentPolicy.CountText(q.Attachments, true);
            int images = q.Attachments.Count(a => a.IsImage);
            string extra = images > AttachmentPolicy.MaxImagesPerSend
                ? $"；超过 {AttachmentPolicy.MaxImagesPerSend} 张的图片改为发送路径 / images beyond {AttachmentPolicy.MaxImagesPerSend} are sent as paths"
                : "";
            return $"📎 本任务包含 {zh}，发布时一并发送（图片粘贴到 Copilot，文本文件内联，其他文件发送路径）{extra} / "
                + $"This task includes {en}, sent together on dispatch (images pasted, text files inlined, other files as paths)";
        }

        /// <summary>
        /// 发送带附件的任务。任务文字已送达但图片未送达时，结果仍以「已发送」开头（任务不判失败），并记录诊断与提示用户。
        /// Sends a task with attachments. When the text is delivered but images are not, the result still starts with "已发送"
        /// (the task does not fail); diagnostics are recorded and the user is told.
        /// </summary>
        Task<string> ITaskAttachmentDispatchHost.SendTaskAsync(VsInstance v, QueuedTask t) => SendTaskCore(v, t);

        private async Task<string> SendTaskCore(VsInstance v, QueuedTask t, Func<bool> queueGuard = null)
        {
            string name = NameOf(v);
            int inline = AttachmentPolicy.ClampInlineMaxChars(_settings.AttachmentInlineMaxChars);
            var plan = AttachmentSendPlan.Build(t.Attachments, a => AttachmentStore.ReadText(a, inline), PathForTask,
                AttachmentPolicy.MaxImagesPerSend, inline);

            // 读取图片：缺失或无法解码的图片改为路径引用 / Load images: missing or undecodable images become path references
            var images = new List<ChatImage>();
            var sentImages = new List<AttachmentRef>();
            var unreadable = new List<string>();
            foreach (var a in plan.Images)
            {
                string path = AttachmentStore.FullPath(a);
                try
                {
                    if (path == null || !File.Exists(path)) throw new FileNotFoundException("附件文件不存在（可能已被清理）/ attachment file missing (possibly cleaned up)");
                    images.Add(ChatImage.FromFile(path));
                    sentImages.Add(a);
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is OutOfMemoryException || ex is UnauthorizedAccessException || ex is System.Runtime.InteropServices.ExternalException)
                {
                    unreadable.Add(AttachmentPolicy.ReferenceLine(a, PathForTask(a), "图片无法读取：" + ex.Message, "image unreadable"));
                    SendLog.Event(name, $"任务 #{t.Id} 附件图片无法读取 / image unreadable：{a.Describe()} {ex.GetType().Name} {ex.Message}");
                }
            }
            string unreadableText = string.Join("\n\n", unreadable);
            string body = AttachmentSendPlan.Compose(t.Text, plan.Body, unreadableText);
            SendLog.Event(name, $"任务 #{t.Id} 附件发送计划 / attachment plan：粘贴图片 / images {images.Count}，内联文本 / inlined {plan.Inlined.Count}，路径引用 / references {plan.References.Count + unreadable.Count}");
            SetStatus($"任务清单：#{t.Id} {TaskAttachmentStatus(t)}，正在发送到「{name}」/ sending to \"{name}\"");

            int lostImages = plan.Images.Count - sentImages.Count;
            string imageFailure = lostImages > 0 ? "图片文件无法读取 / image files unreadable" : null;
            string r;
            if (images.Count > 0)
            {
                r = await SendChatCore(v, DispatchTextOf(t, body), images, queueGuard);
                if (!SendRetryPolicy.IsDelivered(r) && IsPreSubmitImageFailure(r))
                {
                    // 图片在提交前失败（VS 草稿未改动）：改为只发送文字，并把图片路径附在正文中
                    // Images failed before submission (the VS draft is untouched): send text only, with the image paths in the body
                    SendLog.Event(name, $"任务 #{t.Id} 图片未能粘贴，改为只发送文字 / images could not be pasted, falling back to text only：{r}");
                    string refs = plan.ImageReferences(PathForTask, "图片未能粘贴到 Copilot", "image could not be pasted into Copilot");
                    string fallback = AttachmentSendPlan.Compose(t.Text, plan.Body, unreadableText, refs);
                    string reason = r;
                    r = await SendChatCore(v, DispatchTextOf(t, fallback), queueGuard: queueGuard);
                    if (SendRetryPolicy.IsDelivered(r))
                    {
                        lostImages = plan.Images.Count;
                        imageFailure = reason;
                    }
                    else r += "（图片 / images：" + reason + "）";
                }
            }
            else r = await SendChatCore(v, DispatchTextOf(t, body), queueGuard: queueGuard);

            if (!SendRetryPolicy.IsDelivered(r))
            {
                t.AttachmentNote = "未送达 / not delivered：" + r;
                SendLog.Event(name, $"任务 #{t.Id} 带附件发送失败 / send with attachments failed：{r}");
                return r;
            }
            int delivered = plan.Images.Count - lostImages;
            if (lostImages > 0)
            {
                // 文字已送达、图片未送达：不判任务失败，记录诊断并提示用户 / Text delivered, images not: not a task failure; record and tell the user
                t.AttachmentNote = $"任务文字已送达，但 {lostImages} 张图片未送达（{imageFailure}）；图片路径已附在正文中 / Text delivered, but {lostImages} image(s) were not ({imageFailure}); their paths were added to the body";
                SendLog.Event(name, $"任务 #{t.Id} {t.AttachmentNote}");
                AppLog.Write(AppLog.TasksFile, $"任务 #{t.Id} / Task #{t.Id}: {t.AttachmentNote}");
                NotifyTask(t, $"任务文字已送达，但 {lostImages} 张图片未送达，请在 VS 中手动补充", $"Task text delivered, but {lostImages} image(s) were not; please add them in VS manually");
                return $"已发送文字，但 {lostImages} 张图片未送达（{imageFailure}）；原结果 / original result：{r}";
            }
            t.AttachmentNote = delivered > 0
                ? $"{delivered} 张图片已随任务一并发送 / {delivered} image(s) sent with the task"
                    + (plan.Inlined.Count + plan.References.Count > 0 ? $"；{plan.Inlined.Count} 个文件内联、{plan.References.Count} 个发送路径 / {plan.Inlined.Count} inlined, {plan.References.Count} as paths" : "")
                : $"{plan.Inlined.Count} 个文件内联、{plan.References.Count} 个发送路径 / {plan.Inlined.Count} file(s) inlined, {plan.References.Count} sent as paths";
            SendLog.Event(name, $"任务 #{t.Id} 附件 / attachments：{t.AttachmentNote}");
            if (delivered > 0)
                NotifyTask(t, $"任务包含 {delivered} 张图片，已一并发送", $"The task includes {delivered} image{(delivered == 1 ? "" : "s")}, sent together");
            return r;
        }

        /// <summary>
        /// 图片在提交前就失败、VS 草稿未改动的结果（可以安全地改为只发送文字）。已粘贴过内容或存在草稿的失败不回退，避免与草稿混在一起。
        /// Results where images failed before submission and the VS draft is untouched (safe to fall back to text only). Failures
        /// after something was pasted, or with an existing draft, never fall back so nothing mixes with the draft.
        /// </summary>
        internal static bool IsPreSubmitImageFailure(string r) =>
            r != null && r.IndexOf("草稿", StringComparison.Ordinal) < 0 && !SendRetryPolicy.IsBlocked(r)
            && (r.EndsWith("未发送图片", StringComparison.Ordinal) || r.EndsWith("已取消图片发送", StringComparison.Ordinal)
                || r.StartsWith("无法备份剪贴板，未发送图片", StringComparison.Ordinal) || r == "图片附件无效");

        private static string DispatchTextOf(QueuedTask t, string body)
        {
            var copy = t.Clone();
            copy.Text = body;
            return TaskStateMachine.DispatchText(copy);
        }

        private static string PathForTask(AttachmentRef a) =>
            AttachmentStore.FullPath(a) ?? Path.Combine(AttachmentStore.Root, (a.RelPath ?? a.Name ?? "").Replace('/', '\\'));

        private static string TaskAttachmentStatus(QueuedTask t) =>
            $"本任务包含 {AttachmentPolicy.CountText(t.Attachments)} / includes {AttachmentPolicy.CountText(t.Attachments, true)}";

        /// <summary>任务清单右键「查看附件」。/ Task list context menu "View attachments".</summary>
        private void ShowTaskAttachments(QueuedTask t)
        {
            if (t == null || !t.HasAttachments) { SetStatus("该任务没有附件 / This task has no attachments"); return; }
            using (var f = new AttachmentListForm($"任务 #{t.Id} 的附件 / Attachments of task #{t.Id}", t.Attachments, t.AttachmentNote))
                f.ShowDialog(this);
        }

        /// <summary>启动后 1 分钟
        private void StartAttachmentCleanup()
        {
            if (_attachmentCleanupTimer != null) return;
            _attachmentCleanupTimer = new System.Threading.Timer(_ => { var ignored = RunAttachmentCleanup(false); }, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));
        }

        /// <summary>
        /// 清理超过保留天数的附件；仍被未结束任务引用的附件保留。返回结果摘要（未执行时返回 null）。
        /// Removes attachments older than the keep period; those still referenced by unfinished tasks are kept. Returns the
        /// summary (null when it did not run).
        /// </summary>
        internal async Task<string> RunAttachmentCleanup(bool manual)
        {
            if (Interlocked.Exchange(ref _attachmentCleanupRunning, 1) == 1) return null;
            try
            {
                if (IsDisposed) return null;
                var keep = await OnUi(() => _tasks.Items.Where(x => QueueStatus.Active(x.Status) && x.HasAttachments)
                    .SelectMany(x => x.Attachments).Where(a => a != null).Select(a => a.Id).ToList()).ConfigureAwait(false);
                int days = _settings.AttachmentKeepDays;
                if (days <= 0) return "附件保留天数为 0（不限制），未清理 / Keep days is 0 (unlimited); nothing removed";
                var result = await Task.Run(() => AttachmentStore.Cleanup(days, keep, DateTime.Now, manual)).ConfigureAwait(false);
                return result.Summary;
            }
            catch (Exception ex)
            {
                AppLog.Error(AttachmentStore.LogFile, "清理附件失败 / Attachment cleanup failed", ex);
                return "清理附件失败 / Attachment cleanup failed：" + ex.Message;
            }
            finally { Interlocked.Exchange(ref _attachmentCleanupRunning, 0); }
        }
    }
}
