using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VSManager
{
    /// <summary>附件清理结果。/ Attachment cleanup result.</summary>
    public sealed class AttachmentCleanupResult
    {
        public int Deleted;
        public long Bytes;
        public int Kept;
        public int Failed;
        public string Summary => $"已清理 {Deleted} 个过期附件（{AttachmentPolicy.FormatSize(Bytes)}），保留仍被任务引用的 {Kept} 个，失败 {Failed} 个 / "
            + $"Removed {Deleted} expired attachment(s) ({AttachmentPolicy.FormatSize(Bytes)}), kept {Kept} still referenced by tasks, {Failed} failed";
    }

    /// <summary>
    /// 本机附件存储：%APPDATA%\VSManager\attachments\yyyy-MM-dd\，文件名为「时间戳-随机后缀.扩展名」。
    /// 只在本机保存，不参与提交与上传；每次保存、打开与清理都记入 logs\attachments.log。
    /// Local attachment store: %APPDATA%\VSManager\attachments\yyyy-MM-dd\, file names are "timestamp-random.ext".
    /// Stored locally only, never committed or uploaded; every save, open and cleanup is written to logs\attachments.log.
    /// </summary>
    public static class AttachmentStore
    {
        public const string LogFile = "attachments.log";
        private static readonly Random Rng = new Random();
        private static readonly object Gate = new object();

        /// <summary>附件根目录。/ Attachment root folder.</summary>
        public static string Root => Path.Combine(AppPaths.DataFolder, "attachments");

        private static void Log(string text) => AppLog.Write(LogFile, text);

        private static string NewId(DateTime now) { lock (Gate) return AttachmentPolicy.NewId(now, Rng); }

        /// <summary>
        /// 复制本机文件到附件目录；类型或大小不符时抛出 <see cref="InvalidDataException"/>（消息为中英双语原因）。
        /// Copies a local file into the attachment folder; throws <see cref="InvalidDataException"/> (bilingual reason) when the
        /// type or size is not allowed.
        /// </summary>
        public static AttachmentRef Import(string sourcePath, int existingCount, int maxCount, int maxFileMB)
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists) throw new InvalidDataException("文件不存在：" + info.Name + " / File not found: " + info.Name);
            string error = AttachmentPolicy.CheckAdd(existingCount, info.Name, info.Length, maxCount, maxFileMB);
            if (error != null) throw new InvalidDataException(error);
            using (var src = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return Save(src, info.Name, (long)AttachmentPolicy.ClampMaxFileMB(maxFileMB) * 1024 * 1024);
        }

        /// <summary>保存内存中的数据（例如剪贴板截图的 PNG）。/ Saves in-memory data (e.g. the PNG of a clipboard screenshot).</summary>
        public static AttachmentRef SaveBytes(byte[] data, string name, int existingCount, int maxCount, int maxFileMB)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            string error = AttachmentPolicy.CheckAdd(existingCount, name, data.Length, maxCount, maxFileMB);
            if (error != null) throw new InvalidDataException(error);
            using (var src = new MemoryStream(data, false))
                return Save(src, name, data.LongLength);
        }

        private static AttachmentRef Save(Stream src, string name, long maxBytes)
        {
            var sw = Stopwatch.StartNew();
            var now = DateTime.Now;
            string ext = AttachmentPolicy.NormalizeExt(name);
            string id = NewId(now);
            string folder = AttachmentPolicy.DateFolderOf(id);
            string rel = folder + "/" + id + ext;
            string dir = Path.Combine(Root, folder);
            Directory.CreateDirectory(dir);
            string full = Path.Combine(dir, id + ext);
            long size = 0;
            string hash;
            try
            {
                using (var sha = SHA256.Create())
                using (var dst = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        size += n;
                        if (size > maxBytes) throw new InvalidDataException($"「{name}」超过单个附件大小上限 / \"{name}\" exceeds the size limit");
                        sha.TransformBlock(buffer, 0, n, null, 0);
                        dst.Write(buffer, 0, n);
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    hash = string.Concat(sha.Hash.Select(b => b.ToString("x2")));
                }
            }
            catch
            {
                try { File.Delete(full); } catch { }
                throw;
            }
            var a = new AttachmentRef
            {
                Id = id, Name = Path.GetFileName(name), Size = size, Kind = AttachmentPolicy.Classify(ext), Ext = ext,
                Sha256 = hash, RelPath = rel, Created = now
            };
            Log($"保存附件 / saved：{a.Id} 「{a.Name}」 {AttachmentPolicy.FormatSize(size)} {a.Kind} sha256={hash} → attachments/{rel}（{sw.ElapsedMilliseconds} ms）");
            return a;
        }

        /// <summary>
        /// 解析附件的本机完整路径；只接受附件根目录内的路径，越界或文件缺失返回 null。
        /// Resolves the full local path; only paths inside the attachment root are accepted; null when outside or missing.
        /// </summary>
        public static string FullPath(AttachmentRef a)
        {
            if (a == null || !AttachmentPolicy.IsValidId(a.Id)) return null;
            string ext = AttachmentPolicy.NormalizeExt(a.Ext ?? a.RelPath ?? "");
            if (ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return Inside(Path.Combine(Root, AttachmentPolicy.DateFolderOf(a.Id), a.Id + ext));
        }

        /// <summary>按编号查找附件文件（用于对话记录中的链接）。/ Finds an attachment file by id (for transcript links).</summary>
        public static string FindById(string id)
        {
            if (!AttachmentPolicy.IsValidId(id)) return null;
            string dir = Path.Combine(Root, AttachmentPolicy.DateFolderOf(id));
            try
            {
                if (!Directory.Exists(dir)) return null;
                return Directory.GetFiles(dir, id + ".*").Select(Inside).FirstOrDefault(p => p != null && File.Exists(p));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        private static string Inside(string path)
        {
            try
            {
                string root = Path.GetFullPath(Root).TrimEnd('\\') + "\\";
                string full = Path.GetFullPath(path);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        /// <summary>
        /// 按文本读取附件：检测 BOM，默认 UTF-8；含 NUL 字节或解码失败视为二进制，返回 null。最多读取 <paramref name="maxChars"/> + 1 字，用于判断是否截断。
        /// Reads an attachment as text: BOM detection, UTF-8 by default; NUL bytes or decoding failures count as binary and return
        /// null. Reads at most <paramref name="maxChars"/> + 1 characters so the caller can tell whether it was truncated.
        /// </summary>
        public static string ReadText(AttachmentRef a, int maxChars)
        {
            string path = FullPath(a);
            if (path == null || !File.Exists(path)) return null;
            try
            {
                byte[] head = new byte[8192];
                int n;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    n = fs.Read(head, 0, head.Length);
                bool bom = n >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF));
                if (!bom && Array.IndexOf(head, (byte)0, 0, n) >= 0) return null;
                var utf8 = new UTF8Encoding(false, true);
                using (var reader = new StreamReader(path, utf8, true))
                {
                    var buffer = new char[Math.Max(1, maxChars) + 1];
                    int read = reader.ReadBlock(buffer, 0, buffer.Length);
                    return new string(buffer, 0, read);
                }
            }
            catch (DecoderFallbackException)
            {
                // 非 UTF-8 文本（例如 GBK）：按系统默认编码读取 / Non-UTF-8 text (e.g. GBK): read with the system default encoding
                try
                {
                    using (var reader = new StreamReader(path, Encoding.Default, true))
                    {
                        var buffer = new char[Math.Max(1, maxChars) + 1];
                        int read = reader.ReadBlock(buffer, 0, buffer.Length);
                        return new string(buffer, 0, read);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        /// <summary>在资源管理器中定位附件（找不到时打开附件目录）。/ Locates the attachment in Explorer (opens the folder when missing).</summary>
        public static string Reveal(string id, bool open = false)
        {
            string path = FindById(id);
            try
            {
                if (path == null)
                {
                    Directory.CreateDirectory(Root);
                    Process.Start("explorer.exe", "\"" + Root + "\"");
                    Log($"打开附件失败 / open failed：{id} 不存在（可能已被清理）/ missing (possibly cleaned up)");
                    return "附件不存在，可能已被清理；已打开附件目录 / Attachment not found (possibly cleaned up); opened the attachment folder";
                }
                // 文本与脚本类文件一律用记事本打开，绝不按关联程序执行（例如 .bat / .ps1）
                // Text and script files always open in Notepad and are never executed through file associations (e.g. .bat / .ps1)
                if (open && AttachmentPolicy.Classify(path) == AttachmentKind.Text) Process.Start("notepad.exe", "\"" + path + "\"");
                else if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else Process.Start("explorer.exe", "/select,\"" + path + "\"");
                Log($"{(open ? "打开附件 / opened" : "定位附件 / revealed")}：{id}");
                return null;
            }
            catch (Exception ex)
            {
                Log($"打开附件失败 / open failed：{id} {ex.GetType().Name} {ex.Message}");
                return "无法打开附件 / Cannot open the attachment：" + ex.Message;
            }
        }

        /// <summary>打开附件根目录。/ Opens the attachment root folder.</summary>
        public static void OpenRoot()
        {
            Directory.CreateDirectory(Root);
            Process.Start("explorer.exe", "\"" + Root + "\"");
        }

        /// <summary>
        /// 清理超过 <paramref name="keepDays"/> 天的附件（0 表示不限制，不清理）；<paramref name="keepIds"/> 中的附件（仍被未结束任务引用）保留。
        /// 每个文件删除前先写日志。
        /// Removes attachments older than <paramref name="keepDays"/> days (0 = unlimited, nothing removed); attachments in
        /// <paramref name="keepIds"/> (still referenced by unfinished tasks) are kept. Each file is logged before deletion.
        /// </summary>
        public static AttachmentCleanupResult Cleanup(int keepDays, ICollection<string> keepIds, DateTime now, bool manual = false)
        {
            var result = new AttachmentCleanupResult();
            keepDays = AttachmentPolicy.ClampKeepDays(keepDays);
            if (keepDays == 0 || !Directory.Exists(Root)) return result;
            var cutoff = now.Date.AddDays(-keepDays);
            var keep = new HashSet<string>(keepIds ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(Root))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day) || day >= cutoff) continue;
                foreach (var file in SafeFiles(dir))
                {
                    string id = Path.GetFileNameWithoutExtension(file);
                    if (keep.Contains(id)) { result.Kept++; continue; }
                    long len = 0;
                    try { len = new FileInfo(file).Length; } catch { }
                    Log($"{(manual ? "手动" : "自动")}清理附件 / {(manual ? "manual" : "auto")} cleanup：删除 / deleting {Path.GetFileName(dir)}/{Path.GetFileName(file)}（{AttachmentPolicy.FormatSize(len)}，保留 {keepDays} 天 / keep {keepDays} days）");
                    try { File.Delete(file); result.Deleted++; result.Bytes += len; }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        result.Failed++;
                        Log($"删除附件失败 / delete failed：{Path.GetFileName(file)} {ex.Message}");
                    }
                }
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
            if (result.Deleted + result.Failed + result.Kept > 0) Log(result.Summary);
            return result;
        }

        private static string[] SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new string[0]; }
        }
    }
}
