using System;
using System.IO;
using System.Runtime.Serialization;

namespace VSManager
{
    /// <summary>
    /// 原子写入的结果。/ Result of an atomic write.
    /// </summary>
    public sealed class AtomicWriteResult
    {
        /// <summary>首次写入（临时文件 + 替换）时的异常；成功时为 null。/ Exception from the primary write (temp file + replace); null on success.</summary>
        public Exception Error;
        /// <summary>退回直接覆盖时的异常。/ Exception from the direct-overwrite fallback.</summary>
        public Exception FallbackError;
        /// <summary>是否通过「直接覆盖」完成了写入。/ Whether the write succeeded through the direct-overwrite fallback.</summary>
        public bool FallbackSucceeded;

        public bool Ok => Error == null || FallbackSucceeded;
    }

    /// <summary>
    /// 先写临时文件再替换目标文件（上一版本保留为 .bak），进程被强制结束也不会留下半截文件。
    /// 替换失败（如文件被杀毒软件占用）时可退回直接覆盖。设置与任务清单共用这一实现。
    /// Writes to a temp file first and then replaces the target (keeping the previous version as .bak), so a killed
    /// process never leaves a half-written file. When the replace fails (e.g. the file is locked by an antivirus scanner)
    /// it can fall back to a direct overwrite. Shared by the settings and the task list.
    /// </summary>
    public static class AtomicFile
    {
        /// <param name="path">目标文件。/ Target file.</param>
        /// <param name="write">把内容写入给定的流（不要关闭该流）。/ Writes the content into the given stream (do not close it).</param>
        /// <param name="backupBeforeOverwrite">退回直接覆盖前先把目标复制为 .bak。/ Copy the target to .bak before the fallback overwrite.</param>
        /// <param name="skipFallbackOnSerializationError">序列化失败时（临时文件不完整）不覆盖目标。/ Do not overwrite the target when serialization failed (incomplete temp file).</param>
        /// <param name="fs">文件系统，默认为真实磁盘。/ File system, the real disk by default.</param>
        public static AtomicWriteResult Write(string path, Action<Stream> write, bool backupBeforeOverwrite,
            bool skipFallbackOnSerializationError, IFileSystem fs = null)
        {
            fs = fs ?? PhysicalFileSystem.Instance;
            var result = new AtomicWriteResult();
            string tmp = path + ".tmp", bak = path + ".bak";
            try
            {
                fs.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = fs.Create(tmp))
                {
                    write(stream);
                    (stream as FileStream)?.Flush(true);
                }
                if (fs.FileExists(path)) fs.Replace(tmp, path, bak);
                else fs.Move(tmp, path);
            }
            catch (Exception ex)
            {
                result.Error = ex;
                try
                {
                    if (fs.FileExists(tmp) && !(skipFallbackOnSerializationError && ex is SerializationException))
                    {
                        if (backupBeforeOverwrite && fs.FileExists(path)) fs.Copy(path, bak, true);
                        fs.Copy(tmp, path, true);
                        fs.Delete(tmp);
                        result.FallbackSucceeded = true;
                    }
                }
                catch (Exception ex2) { result.FallbackError = ex2; }
            }
            return result;
        }
    }
}
