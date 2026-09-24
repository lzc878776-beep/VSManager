using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 可选宿主能力：把带附件的任务加入任务清单（或暂存）。未实现时 send_task 的附件参数会被拒绝。
    /// Optional host capability: enqueue (or park) a task with attachments. Without it, the attachments parameter of send_task
    /// is rejected.
    /// </summary>
    public interface IAgentAttachmentHost
    {
        Task<string> QueueTask(VsInstance v, string text, AttachmentRef[] attachments);
        Task<string> ParkTask(SolutionEntry e, string text, AttachmentRef[] attachments);
    }

    public sealed partial class AgentService
    {
        /// <summary>本次对话中用户提供的附件（按编号）。/ Attachments provided by the user in this conversation (by id).</summary>
        private readonly Dictionary<string, AttachmentRef> _attachments = new Dictionary<string, AttachmentRef>(StringComparer.OrdinalIgnoreCase);
        private string[] _lastAttachmentIds = new string[0];

        /// <summary>当前设置（界面读取附件上限等）。/ Current settings (the UI reads attachment limits from it).</summary>
        public AppSettings CurrentSettings => _settings();

        /// <summary>单个文本附件发给模型的字数上限。/ Characters of one text attachment shown to the model.</summary>
        private const int ModelInlineChars = 12000;

        internal void RememberAttachments(IReadOnlyList<AttachmentRef> files)
        {
            lock (_attachments)
            {
                foreach (var a in files) _attachments[a.Id] = a.Clone();
                _lastAttachmentIds = files.Select(a => a.Id).ToArray();
            }
        }

        /// <summary>对话记录与归档中附件的链接行（不含内容）。/ Link lines for attachments in the transcript and archive (no content).</summary>
        internal static string AttachmentLinks(IReadOnlyList<AttachmentRef> files)
        {
            if (files == null || files.Count == 0) return "";
            return "\n\n" + string.Join("\n", files.Select(a => a.MarkdownLink()));
        }

        /// <summary>
        /// 发给模型的用户消息：文字 + 附件清单 + 文本附件内容（截断）。图片内容不发给模型，只列出元数据，由 send_task 转发给 Copilot。
        /// User message sent to the model: text + attachment manifest + text attachment content (truncated). Image content is not
        /// sent to the model, only metadata; send_task forwards images to Copilot.
        /// </summary>
        internal static string ModelMessage(string text, IReadOnlyList<AttachmentRef> files, Func<AttachmentRef, int, string> readText = null)
        {
            readText = readText ?? AttachmentStore.ReadText;
            var sb = new StringBuilder(string.IsNullOrWhiteSpace(text) ? "（用户只发送了附件 / The user only sent attachments）" : text);
            sb.Append("\n\n[用户附件 / User attachments] 共 ").Append(files.Count).Append(" 个；需要随任务发送给 VS 时，在 send_task 的 attachments 参数中填写编号或 \"last\"。")
              .Append(" / ").Append(files.Count).Append(" attachment(s); to send them with a task, pass their ids or \"last\" in the attachments parameter of send_task.");
            foreach (var a in files)
                sb.Append("\n- id=").Append(a.Id).Append(" | ").Append(a.Name).Append(" | ").Append(AttachmentPolicy.KindText(a.Kind))
                  .Append(" | ").Append(AttachmentPolicy.FormatSize(a.Size)).Append(" | sha256 ").Append(a.ShortHash);
            if (files.Any(a => a.IsImage))
                sb.Append("\n（图片内容不会发送给你，只能随任务转发给 VS 的 Copilot / Image content is not shown to you; it can only be forwarded to the VS Copilot with a task）");
            foreach (var a in files.Where(a => a.IsText))
            {
                string content = readText(a, ModelInlineChars);
                sb.Append("\n\n");
                sb.Append(content == null
                    ? "📎 " + a.Name + "：无法按文本读取 / not readable as text"
                    : AttachmentPolicy.FileBlock(a, content, ModelInlineChars));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 解析 send_task 的附件参数：空 = 无附件；"last" / "all" = 用户最近一条消息的附件；否则为逗号分隔的编号。
        /// Parses the attachments argument of send_task: empty = none; "last" / "all" = attachments of the latest user message;
        /// otherwise comma-separated ids.
        /// </summary>
        internal AttachmentRef[] ResolveTaskAttachments(string spec, out string error)
        {
            error = null;
            spec = (spec ?? "").Trim();
            if (spec.Length == 0 || spec.Equals("none", StringComparison.OrdinalIgnoreCase)) return new AttachmentRef[0];
            lock (_attachments)
            {
                IEnumerable<string> ids = spec.Equals("last", StringComparison.OrdinalIgnoreCase) || spec.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? _lastAttachmentIds
                    : spec.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
                var list = new List<AttachmentRef>();
                foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!_attachments.TryGetValue(id, out var a))
                    {
                        error = $"找不到附件「{id}」：只能引用用户在本次对话中提供的附件 / Attachment \"{id}\" not found: only attachments the user provided in this conversation can be used";
                        return new AttachmentRef[0];
                    }
                    list.Add(a.Clone());
                }
                if (list.Count == 0) error = "用户最近一条消息没有附件 / The latest user message has no attachments";
                int max = AttachmentPolicy.ClampMaxCount(_settings().AttachmentMaxCount);
                if (list.Count > max) error = $"单个任务最多 {max} 个附件 / At most {max} attachments per task";
                return error == null ? list.ToArray() : new AttachmentRef[0];
            }
        }

        /// <summary>任务附件提示，例如「本任务包含 2 张图片、1 个文件」。/ Task attachment notice, e.g. "this task includes 2 images and 1 file".</summary>
        internal static string TaskAttachmentNotice(IReadOnlyCollection<AttachmentRef> files) =>
            $"本任务包含 {AttachmentPolicy.CountText(files)}：{string.Join("、", files.Select(a => a.Name))} / This task includes {AttachmentPolicy.CountText(files, true)}";
    }
}
