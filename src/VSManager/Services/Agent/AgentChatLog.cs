using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VSManager
{
    /// <summary>
    /// AI 助手对话记录中的一条。
    /// One entry of the AI assistant chat history.
    /// </summary>
    public sealed class AgentChatRecord
    {
        /// <summary>本地时间。/ Local time.</summary>
        public DateTime Time;
        /// <summary>user（用户）/ assistant（AI）/ notice（任务完成等系统通知）。/ user / assistant / notice (task notifications).</summary>
        public string Role;
        public string Text;
        /// <summary>通知发给 AI 的完整内容（可选）。/ Full content sent to the AI for a notice (optional).</summary>
        public string Detail;
        /// <summary>AI 本轮的工具调用步骤（可选）。/ Tool-call steps of the AI turn (optional).</summary>
        public List<string> Steps = new List<string>();
        public string Error;
        /// <summary>关联的任务编号（可选）。/ Related task ids (optional).</summary>
        public List<int> Tasks = new List<int>();

        public bool IsUser => Role == AgentChatLog.RoleUser;
    }

    /// <summary>
    /// AI 助手对话记录：只保存在本机 %APPDATA%\VSManager\agent-chat.jsonl（每行一条 JSON，与 settings.json / tasks.json 分开）。
    /// 每条记录追加写入并立即刷盘，异常退出最多丢失正在写的那一行；读取时跳过损坏的行。
    /// 容量按「保留天数」与「最多条数」裁剪最旧的记录：用临时文件整体重写后原子替换，只影响本文件，不影响任务清单与归档。
    /// AI assistant chat history, stored only on this machine in %APPDATA%\VSManager\agent-chat.jsonl (one JSON object per line,
    /// separate from settings.json / tasks.json). Each record is appended and flushed to disk immediately, so a crash loses at most
    /// the line being written; damaged lines are skipped when reading. The oldest records are trimmed by "days to keep" and
    /// "max records": the file is rewritten to a temp file and atomically replaced. This affects only this file, never the task
    /// list or the archive.
    /// </summary>
    public static class AgentChatLog
    {
        public const string RoleUser = "user", RoleAssistant = "assistant", RoleNotice = "notice";
        public const string FileName = "agent-chat.jsonl";

        /// <summary>默认保留天数与条数。/ Default days and records to keep.</summary>
        public const int DefaultKeepDays = 30, DefaultMaxRecords = 2000;

        /// <summary>每追加多少条检查一次容量。/ Check the capacity every N appends.</summary>
        private const int TrimEvery = 100;

        private static readonly object Gate = new object();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly Regex TaskRef = new Regex(@"(?:任务|task)\s*#\s*(\d{1,9})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static int _appends;
        private static string _path;

        /// <summary>容量设置（天数, 条数）；0 表示不按该项限制。/ Capacity settings (days, records); 0 = no limit on that axis.</summary>
        public static Func<Tuple<int, int>> Limits = () => Tuple.Create(DefaultKeepDays, DefaultMaxRecords);

        /// <summary>成功追加一条记录后触发（在调用线程上）。/ Raised after a record was appended (on the calling thread).</summary>
        public static event Action Appended;

        /// <summary>最近一次读写失败的原因；null 表示正常。/ Last I/O error; null when fine.</summary>
        public static string LastError { get; private set; }

        public static string FilePath
        {
            get => _path ?? Path.Combine(Path.GetDirectoryName(AppSettings.FilePath), FileName);
            set => _path = value;
        }

        /// <summary>从文本中提取「任务 #12」形式的任务编号。/ Extracts task ids written as "任务 #12" / "task #12".</summary>
        public static List<int> FindTaskIds(params string[] texts)
        {
            var ids = new List<int>();
            foreach (var t in texts)
            {
                if (string.IsNullOrEmpty(t)) continue;
                foreach (Match m in TaskRef.Matches(t))
                    if (int.TryParse(m.Groups[1].Value, out int id) && id > 0 && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// 追加一条记录（界面线程调用，写入很小，同步完成）。失败只记录原因，不抛出。
        /// Appends a record (called on the UI thread; the write is small and synchronous). Failures are recorded, never thrown.
        /// </summary>
        public static void Append(string role, string text, string detail = null, IList<string> steps = null, string error = null)
        {
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(error)) return;
            var r = new AgentChatRecord { Time = DateTime.Now, Role = role, Text = text ?? "", Error = string.IsNullOrEmpty(error) ? null : error };
            if (!string.IsNullOrWhiteSpace(detail) && detail != text) r.Detail = detail;
            if (steps != null) r.Steps = steps.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            r.Tasks = FindTaskIds(new[] { r.Text, r.Detail }.Concat(r.Steps).ToArray());
            Append(r);
        }

        public static void Append(AgentChatRecord r)
        {
            if (r == null) return;
            string line = Json.Serialize(ToDict(r));
            bool trim;
            lock (Gate)
            {
                try
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        var bytes = Utf8.GetBytes(line + "\n");
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(true);
                    }
                    LastError = null;
                }
                catch (Exception ex) { LastError = ex.Message; return; }
                trim = ++_appends % TrimEvery == 0;
            }
            try { Appended?.Invoke(); } catch { }
            if (trim) TrimAsync();
        }

        /// <summary>读取全部记录（按时间正序，跳过损坏的行）。/ Reads all records in chronological order, skipping damaged lines.</summary>
        public static List<AgentChatRecord> ReadAll()
        {
            var list = new List<AgentChatRecord>();
            lock (Gate)
            {
                try
                {
                    string path = FilePath;
                    if (!File.Exists(path)) return list;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Utf8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            var r = Parse(line);
                            if (r != null) list.Add(r);
                        }
                    }
                }
                catch (Exception ex) { LastError = ex.Message; }
            }
            // 追加顺序即时间顺序；时钟回拨时按时间稳定排序 / Append order is chronological; stable-sort in case the clock moved back
            return list.Select((r, i) => new { r, i }).OrderBy(x => x.r.Time).ThenBy(x => x.i).Select(x => x.r).ToList();
        }

        /// <summary>后台执行容量裁剪。/ Runs the capacity trim in the background.</summary>
        public static Task TrimAsync() => Task.Run(() => { try { Trim(); } catch { } });

        /// <summary>
        /// 按保留天数与最多条数裁剪最旧的记录；无需裁剪时不写文件。返回删除的条数。
        /// Trims the oldest records by days and count; the file is not touched when nothing needs trimming. Returns the removed count.
        /// </summary>
        public static int Trim()
        {
            var limits = SafeLimits();
            int days = limits.Item1, max = limits.Item2;
            if (days <= 0 && max <= 0) return 0;
            lock (Gate)
            {
                string path = FilePath;
                if (!File.Exists(path)) return 0;
                var lines = new List<KeyValuePair<DateTime, string>>();
                int damaged = 0;
                try
                {
                    foreach (var line in File.ReadAllLines(path, Utf8))
                    {
                        var r = Parse(line);
                        if (r == null) { if (line.Trim().Length > 0) damaged++; continue; }
                        lines.Add(new KeyValuePair<DateTime, string>(r.Time, line));
                    }
                }
                catch (Exception ex) { LastError = ex.Message; return 0; }

                var keep = lines;
                if (days > 0)
                {
                    var cutoff = DateTime.Now.AddDays(-days);
                    keep = keep.Where(x => x.Key >= cutoff).ToList();
                }
                if (max > 0 && keep.Count > max) keep = keep.Skip(keep.Count - max).ToList();
                int removed = lines.Count - keep.Count;
                if (removed == 0 && damaged == 0) return 0;

                string tmp = path + ".tmp";
                try
                {
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var w = new StreamWriter(fs, Utf8))
                    {
                        foreach (var x in keep) { w.Write(x.Value); w.Write('\n'); }
                        w.Flush();
                        fs.Flush(true);
                    }
                    File.Replace(tmp, path, null);
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    try { File.Delete(tmp); } catch { }
                    return 0;
                }
                return removed;
            }
        }

        private static Tuple<int, int> SafeLimits()
        {
            try { var t = Limits(); return Tuple.Create(Math.Max(0, t.Item1), Math.Max(0, t.Item2)); }
            catch { return Tuple.Create(DefaultKeepDays, DefaultMaxRecords); }
        }

        private static Dictionary<string, object> ToDict(AgentChatRecord r)
        {
            var d = new Dictionary<string, object>
            {
                ["time"] = r.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                ["role"] = r.Role ?? RoleAssistant,
                ["text"] = r.Text ?? ""
            };
            if (!string.IsNullOrEmpty(r.Detail)) d["detail"] = r.Detail;
            if (r.Steps != null && r.Steps.Count > 0) d["steps"] = r.Steps.ToArray();
            if (!string.IsNullOrEmpty(r.Error)) d["error"] = r.Error;
            if (r.Tasks != null && r.Tasks.Count > 0) d["tasks"] = r.Tasks.ToArray();
            return d;
        }

        /// <summary>解析一行；缺字段时尽量读取，无法解析返回 null。/ Parses a line leniently; returns null when unreadable.</summary>
        public static AgentChatRecord Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{') return null;
            try
            {
                if (!(Json.DeserializeObject(line) is Dictionary<string, object> d)) return null;
                var r = new AgentChatRecord
                {
                    Role = Str(d, "role") ?? RoleAssistant,
                    Text = Str(d, "text") ?? "",
                    Detail = Str(d, "detail"),
                    Error = Str(d, "error")
                };
                string time = Str(d, "time");
                if (time == null || !DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out r.Time)) return null;
                if (d.TryGetValue("steps", out var s) && s is System.Collections.IEnumerable steps && !(s is string))
                    foreach (var x in steps) if (x is string t) r.Steps.Add(t);
                if (d.TryGetValue("tasks", out var k) && k is System.Collections.IEnumerable ids && !(k is string))
                    foreach (var x in ids) { try { r.Tasks.Add(Convert.ToInt32(x, CultureInfo.InvariantCulture)); } catch { } }
                return r;
            }
            catch { return null; }
        }

        private static string Str(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;
    }
}
