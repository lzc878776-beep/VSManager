using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VSManager
{
    /// <summary>
    /// 任务清单的持久化接口：只负责读写，不含业务规则。
    /// Persistence interface of the task list: reading and writing only, no business rules.
    /// </summary>
    public interface ITaskStore
    {
        /// <summary>读取全部任务；遇到的问题追加到 <paramref name="problems"/>。/ Reads all tasks; problems found are appended to <paramref name="problems"/>.</summary>
        List<QueuedTask> Load(List<string> problems);

        /// <summary>保存全部任务，返回错误信息；成功时返回 null。/ Saves all tasks and returns the error, or null on success.</summary>
        string Save(IList<QueuedTask> items);
    }

    /// <summary>
    /// tasks.json 存储（上一版本保存在 tasks.json.bak）。尽量多地读取：单条记录字段缺失或类型不符只影响该条（或该字段），
    /// 文件截断时保留截断前的记录；主文件损坏时从备份补回，并保留一份损坏的原文件。
    /// tasks.json store (the previous version is kept as tasks.json.bak). Reads as much as possible: a missing or mistyped
    /// field only affects that record (or field) and a truncated file keeps the records before the cut; when the main file
    /// is damaged the missing tasks are restored from the backup and a copy of the damaged file is kept.
    /// </summary>
    public sealed class JsonTaskStore : ITaskStore
    {
        private readonly string _path;
        private readonly IFileSystem _fs;

        public JsonTaskStore(string path, IFileSystem fs = null)
        {
            _path = path;
            _fs = fs ?? PhysicalFileSystem.Instance;
        }

        public string FilePath => _path;

        public List<QueuedTask> Load(List<string> problems)
        {
            var items = new List<QueuedTask>();
            string main = _path, bak = _path + ".bak";
            string mainProblem = null;
            bool mainExists = _fs.FileExists(main);
            if (mainExists) items.AddRange(ReadTolerant(main, problems, out mainProblem));

            // 主文件缺失或读取不完整：从备份中补回主文件中没有的任务 / Main file missing or incomplete: restore missing tasks from the backup
            if ((!mainExists || mainProblem != null) && _fs.FileExists(bak))
            {
                var ids = new HashSet<int>(items.Select(t => t.Id));
                var fromBak = ReadTolerant(bak, problems, out _).Where(t => t.Id == 0 || !ids.Contains(t.Id)).ToList();
                if (fromBak.Count > 0)
                {
                    items.AddRange(fromBak);
                    problems.Add("已从备份 tasks.json.bak 恢复 " + fromBak.Count + " 条任务");
                }
            }
            if (mainProblem != null)
            {
                problems.Insert(0, "tasks.json " + mainProblem);
                // 保留损坏的原文件，避免下次保存时被覆盖而无法人工找回 / Keep the damaged file so it is not lost on the next save
                try { _fs.Copy(main, Path.Combine(Path.GetDirectoryName(main), "tasks.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true); } catch { }
            }
            return items;
        }

        public string Save(IList<QueuedTask> items)
        {
            var snapshot = items.ToList();
            var r = AtomicFile.Write(_path, stream =>
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                    new DataContractJsonSerializer(typeof(List<QueuedTask>)).WriteObject(w, snapshot);
            }, backupBeforeOverwrite: true, skipFallbackOnSerializationError: true, fs: _fs);
            if (r.Ok) return null;
            string error = r.Error.GetType().Name + "：" + r.Error.Message;
            if (r.FallbackError != null) error += "；直接覆盖也失败：" + r.FallbackError.Message;
            return error;
        }

        private List<QueuedTask> ReadTolerant(string path, List<string> problems, out string problem)
        {
            var list = new List<QueuedTask>();
            problem = null;
            int bad = 0;
            try
            {
                byte[] data = _fs.ReadAllBytes(path);
                if (data.Length == 0) { problem = "为空文件"; return list; }
                // 手工编辑（如记事本）可能加上 UTF-8 BOM，JSON 读取器不接受 / Hand edits may add a UTF-8 BOM that the JSON reader rejects
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Skip(3).ToArray();
                using (var r = JsonReaderWriterFactory.CreateJsonReader(data, XmlDictionaryReaderQuotas.Max))
                {
                    r.MoveToContent();
                    while (!r.EOF)
                    {
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "item" && r.GetAttribute("type") == "object")
                        {
                            var e = (XElement)XNode.ReadFrom(r);
                            var t = ParseTask(e);
                            if (t != null) list.Add(t); else bad++;
                            continue;
                        }
                        r.Read();
                    }
                }
            }
            catch (Exception ex) { problem = "读取不完整（" + ex.Message + "），已保留可识别的 " + list.Count + " 条"; }
            if (bad > 0) problems.Add(Path.GetFileName(path) + " 中有 " + bad + " 条记录缺少任务内容，已跳过");
            return list;
        }

        /// <summary>解析单条任务；缺少任务内容时返回 null。/ Parses one task; returns null when the task text is missing.</summary>
        internal static QueuedTask ParseTask(XElement e)
        {
            string S(string name) => e.Element(name) is XElement x && x.Attribute("type")?.Value != "null" ? x.Value : null;
            int I(string name) => int.TryParse(S(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

            var t = new QueuedTask
            {
                Id = I("Id"), VsKey = S("VsKey"), VsName = S("VsName"), Text = S("Text"), Source = S("Source"),
                Status = S("Status"), Started = ParseDate(S("Started")), Finished = ParseDate(S("Finished")),
                Result = S("Result"), Error = S("Error"), Attempts = Math.Max(0, I("Attempts")), Target = S("Target")
            };
            if (string.IsNullOrWhiteSpace(t.Text)) return null;
            t.Created = ParseDate(S("Created")) ?? t.Started ?? t.Finished ?? DateTime.Now;
            if (string.IsNullOrEmpty(t.VsName)) t.VsName = string.IsNullOrEmpty(t.VsKey) ? "（未知 VS）" : Path.GetFileNameWithoutExtension(t.VsKey);
            if (t.VsKey == null) t.VsKey = "";
            if (!QueueStatus.Known(t.Status))
            {
                // 状态无法识别时不自动发布，避免误执行；用户可右键「重新排队」
                // Unknown status: never auto-publish; the user can requeue it from the context menu
                t.Error = "记录中的状态「" + (t.Status ?? "缺失") + "」无法识别" + (string.IsNullOrEmpty(t.Error) ? "" : "；" + t.Error);
                t.Status = QueueStatus.Cancelled;
                if (!t.Finished.HasValue) t.Finished = t.Created;
            }
            if (!QueueStatus.Active(t.Status) && !t.Finished.HasValue) t.Finished = t.Started ?? t.Created;
            return t;
        }

        /// <summary>兼容 DataContractJsonSerializer 的 /Date(毫秒+时区)/ 与 ISO 8601 两种格式。/ Accepts both /Date(ms+zone)/ and ISO 8601.</summary>
        internal static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("/Date(", StringComparison.Ordinal))
            {
                int end = 6;
                if (end < s.Length && s[end] == '-') end++;
                while (end < s.Length && char.IsDigit(s[end])) end++;
                if (long.TryParse(s.Substring(6, end - 6), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                    try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime(); } catch { }
                return null;
            }
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d.ToLocalTime() : (DateTime?)null;
        }
    }
}
