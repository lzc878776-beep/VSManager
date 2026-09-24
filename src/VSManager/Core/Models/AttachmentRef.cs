using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>附件类别。/ Attachment kinds.</summary>
    public static class AttachmentKind
    {
        /// <summary>图片：可随任务粘贴到 Copilot。/ Image: can be pasted into Copilot with a task.</summary>
        public const string Image = "image";
        /// <summary>文本与代码：发送前内联到任务正文。/ Text and code: inlined into the task body before sending.</summary>
        public const string Text = "text";
        /// <summary>其他文档：只发送路径引用。/ Other documents: only a path reference is sent.</summary>
        public const string File = "file";
    }

    /// <summary>
    /// 附件引用：只记录元数据与相对路径，不内嵌二进制内容。字段名即 tasks.json 与归档中的字段名。
    /// Attachment reference: metadata and a relative path only, never embedded binary content. Field names are the ones
    /// stored in tasks.json and the archives.
    /// </summary>
    [DataContract]
    public sealed class AttachmentRef
    {
        /// <summary>附件编号（时间戳 + 随机后缀，同时是存储文件名的主体）。/ Attachment id (timestamp + random suffix, also the stored file name stem).</summary>
        [DataMember] public string Id;
        /// <summary>原始文件名。/ Original file name.</summary>
        [DataMember] public string Name;
        /// <summary>字节数。/ Size in bytes.</summary>
        [DataMember] public long Size;
        /// <summary>类别：image / text / file。/ Kind: image / text / file.</summary>
        [DataMember] public string Kind;
        /// <summary>扩展名（小写，含点）。/ Extension (lower case, with the dot).</summary>
        [DataMember] public string Ext;
        /// <summary>SHA-256（十六进制小写）。/ SHA-256 (lower-case hex).</summary>
        [DataMember] public string Sha256;
        /// <summary>相对附件根目录的路径，例如 2025-01-31/xxx.png。/ Path relative to the attachment root, e.g. 2025-01-31/xxx.png.</summary>
        [DataMember] public string RelPath;
        [DataMember] public DateTime Created;

        public bool IsImage => Kind == AttachmentKind.Image;
        public bool IsText => Kind == AttachmentKind.Text;

        public AttachmentRef Clone() => (AttachmentRef)MemberwiseClone();

        /// <summary>界面与日志中的简短描述。/ Short description for the UI and logs.</summary>
        public string Describe() => $"{Name}（{AttachmentPolicy.FormatSize(Size)}，{AttachmentPolicy.KindText(Kind)}，sha256 {ShortHash}）";

        public string ShortHash => string.IsNullOrEmpty(Sha256) ? "-" : Sha256.Substring(0, Math.Min(12, Sha256.Length));

        /// <summary>对话记录中的 Markdown 链接。/ Markdown link used in the chat transcript.</summary>
        public string MarkdownLink() =>
            "[📎 " + AttachmentPolicy.EscapeMarkdown(Name) + "](" + AttachmentPolicy.LinkScheme + Id + ")"
            + $" · {AttachmentPolicy.FormatSize(Size)} · {AttachmentPolicy.KindText(Kind)} · sha256 {ShortHash}";
    }

    /// <summary>
    /// 附件的纯规则：类型识别、数量与大小限制、链接格式与任务正文拼接。不做文件读写，便于单元测试。
    /// Pure attachment rules: type detection, count and size limits, link format and task-body composition. No file IO,
    /// so it is unit-testable.
    /// </summary>
    public static class AttachmentPolicy
    {
        /// <summary>对话记录中附件链接的协议前缀。/ URI scheme prefix of attachment links in the transcript.</summary>
        public const string LinkScheme = "vsm-attachment:";

        public const int DefaultMaxFileMB = 10, DefaultMaxCount = 5, DefaultKeepDays = 30, DefaultInlineMaxChars = 20000;

        /// <summary>Copilot 单条消息可粘贴的图片上限（沿用现有图片发送能力）。/ Images per Copilot message (existing image-send limit).</summary>
        public static int MaxImagesPerSend => ChatImage.MaxCount;

        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

        public static readonly string[] TextExtensions =
        {
            ".txt", ".log", ".cs", ".xaml", ".json", ".xml", ".md", ".csv", ".tsv", ".ini", ".config", ".yml", ".yaml", ".toml",
            ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".sql", ".js", ".ts", ".jsx", ".tsx", ".css", ".scss", ".html", ".htm",
            ".py", ".java", ".c", ".h", ".cpp", ".hpp", ".cc", ".vb", ".fs", ".go", ".rs", ".razor", ".cshtml", ".resx",
            ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".props", ".targets", ".sln", ".slnx", ".editorconfig", ".diff", ".patch"
        };

        /// <summary>可选的常见文档（只发送路径引用）。/ Optional common documents (path reference only).</summary>
        public static readonly string[] DocumentExtensions = { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf", ".zip", ".7z" };

        /// <summary>GDI+ 可解码、能粘贴到 Copilot 的图片格式。/ Image formats GDI+ can decode for pasting into Copilot.</summary>
        public static readonly string[] PastableImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

        private static readonly Regex IdPattern = new Regex(@"^\d{8}-\d{9}-[0-9a-f]{6}$", RegexOptions.CultureInvariant);

        public static string NormalizeExt(string nameOrExt)
        {
            if (string.IsNullOrWhiteSpace(nameOrExt)) return "";
            string s = nameOrExt.Trim();
            int dot = s.LastIndexOf('.');
            if (dot < 0) return "";
            return s.Substring(dot).ToLowerInvariant();
        }

        /// <summary>按扩展名识别类别；不支持的类型返回 null。/ Kind by extension; null when unsupported.</summary>
        public static string Classify(string nameOrExt)
        {
            string ext = NormalizeExt(nameOrExt);
            if (ext.Length == 0) return null;
            if (ImageExtensions.Contains(ext)) return AttachmentKind.Image;
            if (TextExtensions.Contains(ext)) return AttachmentKind.Text;
            if (DocumentExtensions.Contains(ext)) return AttachmentKind.File;
            return null;
        }

        public static bool IsPastableImage(AttachmentRef a) => a != null && a.IsImage && PastableImageExtensions.Contains(a.Ext ?? "");

        public static bool IsValidId(string id) => id != null && IdPattern.IsMatch(id);

        /// <summary>生成附件编号：yyyyMMdd-HHmmssfff-6 位随机十六进制。/ Creates an id: yyyyMMdd-HHmmssfff-6 random hex digits.</summary>
        public static string NewId(DateTime now, Random random)
        {
            var bytes = new byte[3];
            (random ?? new Random()).NextBytes(bytes);
            return now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        /// <summary>编号对应的日期子目录（yyyy-MM-dd）。/ Date subfolder (yyyy-MM-dd) of an id.</summary>
        public static string DateFolderOf(string id) =>
            IsValidId(id) ? id.Substring(0, 4) + "-" + id.Substring(4, 2) + "-" + id.Substring(6, 2) : null;

        public static int ClampMaxFileMB(int v) => v <= 0 ? DefaultMaxFileMB : Math.Min(100, v);
        public static int ClampMaxCount(int v) => v <= 0 ? DefaultMaxCount : Math.Min(20, v);
        public static int ClampKeepDays(int v) => v < 0 ? DefaultKeepDays : Math.Min(3650, v);
        public static int ClampInlineMaxChars(int v) => v <= 0 ? DefaultInlineMaxChars : Math.Max(1000, Math.Min(200000, v));

        /// <summary>
        /// 检查能否再添加一个附件；可以时返回 null，否则返回中英双语的原因。
        /// Checks whether one more attachment may be added; null when allowed, otherwise a bilingual reason.
        /// </summary>
        public static string CheckAdd(int existingCount, string name, long size, int maxCount, int maxFileMB)
        {
            maxCount = ClampMaxCount(maxCount);
            maxFileMB = ClampMaxFileMB(maxFileMB);
            if (existingCount >= maxCount)
                return $"每条消息最多 {maxCount} 个附件，「{name}」未添加 / At most {maxCount} attachments per message; \"{name}\" was not added";
            if (Classify(name) == null)
                return $"不支持的文件类型「{NormalizeExt(name)}」：{name} / Unsupported file type \"{NormalizeExt(name)}\": {name}";
            if (size < 0) return $"无法读取文件大小：{name} / Cannot read the file size: {name}";
            if (size > (long)maxFileMB * 1024 * 1024)
                return $"「{name}」为 {FormatSize(size)}，超过单个附件上限 {maxFileMB} MB / \"{name}\" is {FormatSize(size)}, over the {maxFileMB} MB limit";
            return null;
        }

        public static string KindText(string kind)
        {
            switch (kind)
            {
                case AttachmentKind.Image: return "图片 / image";
                case AttachmentKind.Text: return "文本 / text";
                default: return "文件 / file";
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        }

        public static string EscapeMarkdown(string s) =>
            Regex.Replace(s ?? "", @"([\\`*_\[\]()<>#|!])", @"\$1");

        /// <summary>数量摘要，例如「2 张图片、1 个文件」。/ Count summary, e.g. "2 张图片、1 个文件".</summary>
        public static string CountText(IReadOnlyCollection<AttachmentRef> list, bool english = false)
        {
            int images = list?.Count(a => a.IsImage) ?? 0, files = (list?.Count ?? 0) - images;
            if (english)
            {
                var en = new List<string>();
                if (images > 0) en.Add(images + (images == 1 ? " image" : " images"));
                if (files > 0) en.Add(files + (files == 1 ? " file" : " files"));
                return string.Join(" and ", en);
            }
            var zh = new List<string>();
            if (images > 0) zh.Add(images + " 张图片");
            if (files > 0) zh.Add(files + " 个文件");
            return string.Join("、", zh);
        }

        /// <summary>代码块语言标记。/ Code-fence language tag.</summary>
        public static string FenceLanguage(string ext)
        {
            switch (NormalizeExt(ext))
            {
                case ".cs": return "csharp";
                case ".xaml": case ".xml": case ".config": case ".csproj": case ".vbproj": case ".fsproj": case ".vcxproj": case ".props": case ".targets": case ".resx": case ".slnx": return "xml";
                case ".json": return "json";
                case ".md": return "markdown";
                case ".ps1": case ".psm1": return "powershell";
                case ".bat": case ".cmd": return "bat";
                case ".sh": return "bash";
                case ".js": case ".jsx": return "javascript";
                case ".ts": case ".tsx": return "typescript";
                case ".py": return "python";
                case ".sql": return "sql";
                case ".html": case ".htm": case ".razor": case ".cshtml": return "html";
                case ".css": case ".scss": return "css";
                case ".yml": case ".yaml": return "yaml";
                case ".cpp": case ".hpp": case ".cc": case ".c": case ".h": return "cpp";
                case ".vb": return "vb";
                case ".diff": case ".patch": return "diff";
                default: return "";
            }
        }

        /// <summary>
        /// 生成内联文件块：文件名 + 代码块；超过 <paramref name="maxChars"/> 时截断并注明。围栏长度随内容中的反引号自动加长。
        /// Builds an inline file block: file name + code fence; truncated with a note beyond <paramref name="maxChars"/>. The
        /// fence grows with backtick runs found in the content.
        /// </summary>
        public static string FileBlock(AttachmentRef a, string content, int maxChars)
        {
            maxChars = ClampInlineMaxChars(maxChars);
            content = (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            bool truncated = content.Length > maxChars;
            if (truncated) content = content.Substring(0, maxChars);
            int run = 0, longest = 0;
            foreach (char c in content) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
            string fence = new string('`', Math.Max(3, longest + 1));
            var sb = new StringBuilder();
            sb.Append("📎 附件文件 / Attached file：").Append(a.Name).Append("（").Append(FormatSize(a.Size)).Append("）\n");
            sb.Append(fence).Append(FenceLanguage(a.Ext)).Append('\n').Append(content.TrimEnd('\n')).Append('\n').Append(fence);
            if (truncated)
                sb.Append("\n（内容过长，已截断，仅包含前 ").Append(maxChars).Append(" 字；完整文件：").Append(a.Name)
                  .Append(" / Content truncated to the first ").Append(maxChars).Append(" characters)");
            return sb.ToString();
        }

        /// <summary>只发送路径引用的文件行。/ Line for a file sent as a path reference only.</summary>
        public static string ReferenceLine(AttachmentRef a, string fullPath, string reasonZh, string reasonEn) =>
            $"📎 附件（{reasonZh}，未发送内容，请按路径查看）/ Attachment ({reasonEn}; content not sent, open it by path)：{a.Name}（{FormatSize(a.Size)}）→ {fullPath}";
    }

    /// <summary>
    /// 任务附件的发送计划：哪些图片粘贴到 Copilot、哪些文件内联到正文、哪些只发送路径。
    /// Send plan for task attachments: which images are pasted into Copilot, which files are inlined and which are sent as
    /// path references.
    /// </summary>
    public sealed class AttachmentSendPlan
    {
        /// <summary>要粘贴到 Copilot 的图片。/ Images to paste into Copilot.</summary>
        public readonly List<AttachmentRef> Images = new List<AttachmentRef>();
        /// <summary>只以路径引用发送的附件。/ Attachments sent as path references.</summary>
        public readonly List<AttachmentRef> References = new List<AttachmentRef>();
        /// <summary>内联到正文的文本文件。/ Text files inlined into the body.</summary>
        public readonly List<AttachmentRef> Inlined = new List<AttachmentRef>();
        /// <summary>追加到任务正文的附件内容（可能为空）。/ Attachment content appended to the task body (may be empty).</summary>
        public string Body = "";

        /// <summary>
        /// 生成发送计划。<paramref name="readText"/> 返回文本内容，二进制、缺失或读取失败返回 null；<paramref name="fullPath"/> 返回本机完整路径。
        /// Builds the plan. <paramref name="readText"/> returns the text content, or null for binary, missing or unreadable
        /// files; <paramref name="fullPath"/> returns the full local path.
        /// </summary>
        public static AttachmentSendPlan Build(IEnumerable<AttachmentRef> attachments, Func<AttachmentRef, string> readText,
            Func<AttachmentRef, string> fullPath, int maxImages, int inlineMaxChars)
        {
            var plan = new AttachmentSendPlan();
            var blocks = new List<string>();
            foreach (var a in attachments ?? Enumerable.Empty<AttachmentRef>())
            {
                if (a == null) continue;
                string path = fullPath?.Invoke(a) ?? a.RelPath ?? a.Name;
                if (a.IsImage)
                {
                    if (!AttachmentPolicy.IsPastableImage(a))
                    {
                        plan.References.Add(a);
                        blocks.Add(AttachmentPolicy.ReferenceLine(a, path, "该图片格式无法粘贴", "this image format cannot be pasted"));
                    }
                    else if (plan.Images.Count >= Math.Max(0, maxImages))
                    {
                        plan.References.Add(a);
                        blocks.Add(AttachmentPolicy.ReferenceLine(a, path, "超出单条消息图片上限", "over the per-message image limit"));
                    }
                    else plan.Images.Add(a);
                    continue;
                }
                string text = a.IsText ? readText?.Invoke(a) : null;
                if (text != null)
                {
                    plan.Inlined.Add(a);
                    blocks.Add(AttachmentPolicy.FileBlock(a, text, inlineMaxChars));
                }
                else
                {
                    plan.References.Add(a);
                    blocks.Add(a.IsText
                        ? AttachmentPolicy.ReferenceLine(a, path, "无法按文本读取", "not readable as text")
                        : AttachmentPolicy.ReferenceLine(a, path, "二进制或文档文件", "binary or document file"));
                }
            }
            plan.Body = string.Join("\n\n", blocks);
            return plan;
        }

        /// <summary>图片改为路径引用（图片发送失败后的纯文字回退）。/ Turns the images into path references (text-only fallback after an image failure).</summary>
        public string ImageReferences(Func<AttachmentRef, string> fullPath, string reasonZh, string reasonEn) =>
            string.Join("\n\n", Images.Select(a => AttachmentPolicy.ReferenceLine(a, fullPath?.Invoke(a) ?? a.RelPath, reasonZh, reasonEn)));

        /// <summary>
        /// 把附件内容插入任务正文：放在任务文本之后、回执说明之前，保证回执说明仍在消息末尾。
        /// Inserts attachment content into the task body: after the task text and before the receipt instruction, so the receipt
        /// instruction stays at the end.
        /// </summary>
        public static string Compose(string taskText, params string[] parts)
        {
            var extra = (parts ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (extra.Length == 0) return taskText ?? "";
            return (taskText ?? "").TrimEnd() + "\n\n" + string.Join("\n\n", extra) + "\n";
        }
    }
}
