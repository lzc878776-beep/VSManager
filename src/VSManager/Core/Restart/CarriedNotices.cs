using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace VSManager
{
    /// <summary>
    /// 重启时带到新进程的一条 AI 助手通知（通常是其他 VS 的任务完成 / 失败通知）。
    /// An AI assistant notice carried across a restart (usually a completion / failure notice from another VS).
    /// </summary>
    [DataContract]
    public sealed class CarriedNotice
    {
        [DataMember] public string Display;
        [DataMember] public string Content;
        [DataMember] public string Scope;
    }

    [DataContract]
    internal sealed class CarriedNoticeFile
    {
        [DataMember] public DateTime SavedUtc;
        [DataMember] public List<CarriedNotice> Notices = new List<CarriedNotice>();
    }

    /// <summary>
    /// 无感重启：重启前把尚未处理的助手通知写入 %APPDATA%\VSManager\restart-notices.json，新进程读取一次即删除并补发，
    /// 这样重启不必等待其他 VS 的任务或通知，也不会丢失它们的结果。
    /// Seamless restart: queued assistant notices are written to %APPDATA%\VSManager\restart-notices.json before a restart;
    /// the new process reads the file once, deletes it and re-delivers them, so a restart neither waits for tasks or
    /// notices of other VS instances nor loses their results.
    /// </summary>
    public static class CarriedNotices
    {
        /// <summary>超过该时间的文件视为陈旧，不再补发。/ Files older than this are stale and not re-delivered.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

        public static string FilePath => Path.Combine(AppPaths.DataFolder, "restart-notices.json");

        /// <summary>保存通知（为空时删除文件），失败时返回错误说明。/ Saves the notices (deletes the file when empty); returns an error on failure.</summary>
        public static string Save(IEnumerable<CarriedNotice> notices, DateTime nowUtc, string path = null)
        {
            path = path ?? FilePath;
            var list = (notices ?? Enumerable.Empty<CarriedNotice>()).Where(n => n != null && !string.IsNullOrEmpty(n.Content)).ToList();
            if (list.Count == 0) { Discard(path); return null; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var file = new CarriedNoticeFile { SavedUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), Notices = list };
                var r = AtomicFile.Write(path, stream =>
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                        new DataContractJsonSerializer(typeof(CarriedNoticeFile)).WriteObject(w, file);
                }, backupBeforeOverwrite: false, skipFallbackOnSerializationError: true);
                return r.Ok ? null : r.Error.GetType().Name + "：" + r.Error.Message;
            }
            catch (Exception ex) { return ex.GetType().Name + "：" + ex.Message; }
        }

        /// <summary>读取并删除（只消费一次）；没有文件、已过期或读取失败时返回空列表。/ Reads and deletes (consumed once); empty when absent, stale or unreadable.</summary>
        public static List<CarriedNotice> Take(DateTime nowUtc, out string error, string path = null)
        {
            error = null;
            path = path ?? FilePath;
            if (!File.Exists(path)) return new List<CarriedNotice>();
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Skip(3).ToArray();
                CarriedNoticeFile file;
                using (var r = JsonReaderWriterFactory.CreateJsonReader(data, XmlDictionaryReaderQuotas.Max))
                    file = (CarriedNoticeFile)new DataContractJsonSerializer(typeof(CarriedNoticeFile)).ReadObject(r);
                if (file == null || file.Notices == null || nowUtc - file.SavedUtc > MaxAge) return new List<CarriedNotice>();
                return file.Notices.Where(n => n != null && !string.IsNullOrEmpty(n.Content)).ToList();
            }
            catch (Exception ex)
            {
                error = "读取重启前的待处理通知失败 / Failed to read the notices carried over the restart: " + ex.Message;
                return new List<CarriedNotice>();
            }
            finally { Discard(path); }
        }

        /// <summary>删除文件。/ Deletes the file.</summary>
        public static void Discard(string path = null)
        {
            path = path ?? FilePath;
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".tmp"); } catch { }
        }
    }
}
