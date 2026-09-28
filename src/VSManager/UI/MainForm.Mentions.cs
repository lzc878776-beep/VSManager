using System;
using System.Diagnostics;
using System.Linq;

namespace VSManager
{
    public partial class MainForm : IExplicitTaskDispatchHost
    {
        private readonly VsMentionSession _mentionSession = new VsMentionSession();

        private VsMentionTarget[] MentionCandidates() => _instances.Select((v, i) =>
            new VsMentionTarget(v, i + 1, NameOf(v), NoteOf(v))).ToArray();

        VsInstance IExplicitTaskDispatchHost.FindExplicitTarget(QueuedTask task) =>
            _instances.FirstOrDefault(task.MatchesExplicitTarget);

        // 发送前读取真实进程与 DTE，防止轮询间隙复用 PID 或切换解决方案。/ Read the live process and DTE before sending, guarding PID reuse and solution changes between polls.
        private static bool MentionTargetStillLive(VsInstance target, string solutionPath, bool checkSolution)
        {
            try
            {
                if (target == null || target.StartTicks <= 0 || !Native.IsWindow(target.MainHwnd)) return false;
                Native.GetWindowThreadProcessId(target.MainHwnd, out uint pid);
                if (pid != target.Pid) return false;
                using (var process = Process.GetProcessById(target.Pid))
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.StartTicks) return false;
                if (!checkSolution) return true;
                if (target.Dte == null) return false;
                dynamic dte = target.Dte;
                string current = dte.Solution.FullName;
                if (string.IsNullOrEmpty(current) && dte.Solution.Projects.Count > 0)
                    current = dte.Solution.Projects.Item(1).FullName;
                return string.Equals(current ?? "", solutionPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private MentionSubmission SubmitChatMention(string text)
        {
            try
            {
                var request = _mentionSession.Resolve(text, _instances, _chat.Images.Count > 0);
                if (!request.Valid) return new MentionSubmission(null, request.Error ?? VsMentionSession.ChooseError);
                var files = _chat.Images.Select((image, i) => AttachmentStore.SaveBytes(image.PngBytes(),
                    "提及图片-Mention-" + (i + 1) + ".png", i,
                    AttachmentPolicy.ClampMaxCount(_settings.AttachmentMaxCount),
                    AttachmentPolicy.ClampMaxFileMB(_settings.AttachmentMaxFileMB))).ToArray();
                return SubmitMention(text, files);
            }
            catch (Exception ex)
            {
                return new MentionSubmission(null, "提及任务未接纳；文字与图片已保留 / Mention task not accepted; text and images retained: " + ex.Message);
            }
        }

        private MentionSubmission SubmitMention(string text, AttachmentRef[] attachments) => SubmitMention(text, attachments, false);

        private MentionSubmission SubmitMention(string text, AttachmentRef[] attachments, bool fromAgentPanel)
        {
            var request = _mentionSession.Resolve(text, _instances, attachments?.Length > 0);
            if (!request.Valid) return new MentionSubmission(null, request.Error ?? VsMentionSession.ChooseError);
            var target = _instances.FirstOrDefault(request.Target.Matches);
            if (!MentionTargetStillLive(target, request.Target.SolutionPath, false))
                return new MentionSubmission(null, VsMentionSession.MissingError);
            var result = _tasks.AddMention(target, request.Target, request.Body, attachments);
            if (!result.Accepted) { SetStatus(result.Message); return result; }
            var task = result.Task;
            string startNote = _dispatcher.AcceptQueued(task, "用户", fromAgentPanel);
            HideResentFailed(task);
            if (_taskPanel.Collapsed) _taskPanel.SetCollapsed(false);
            UpdateTaskTimer();
            _taskPanel.RefreshItems();
            string message = result.Message + "；" + startNote;
            if (task.HasAttachments) message += "\r\n" + AttachmentQueuedNote(task);
            SetStatus(message);
            SendLog.Event(task.VsName, message);
            _dispatcher.Pump();
            return new MentionSubmission(task, message);
        }
    }
}
