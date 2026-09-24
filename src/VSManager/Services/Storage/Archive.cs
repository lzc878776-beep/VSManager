using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VSManager
{
    /// <summary>
    /// 历史记录统一归档：任务流水、AI 总控助手对话、各 VS 的 Copilot 对话与发送日志长期保存到 ArchiveRoot。
    /// 目录结构：tasks\tasks-yyyyMMdd.jsonl、chat\ai-yyyyMMdd.jsonl、chat\vs-名称-yyyyMMdd.jsonl、logs\send-yyyyMMdd.log；
    /// 按天滚动，单文件超过 MaxFileBytes 时分卷为 xxx-yyyyMMdd.2.jsonl …。只追加、默认不删除。
    /// 所有写入在后台线程串行执行，失败只记录到 %APPDATA%\VSManager\logs\tasks.log，不影响主流程。
    /// </summary>
    public static class Archive
    {
        /// <summary>环境变量：未在 settings.json 中指定 ArchiveRoot 时使用的归档根目录。</summary>
        public const string RootEnvVar = "VSMANAGER_ARCHIVE_ROOT";
        /// <summary>默认归档根目录：环境变量 VSMANAGER_ARCHIVE_ROOT，否则 %APPDATA%\VSManager\archive。</summary>
        public static string DefaultRoot
        {
            get
            {
                string env = Environment.GetEnvironmentVariable(RootEnvVar);
                return string.IsNullOrWhiteSpace(env) ? FallbackRoot : Environment.ExpandEnvironmentVariables(env.Trim());
            }
        }
        public const long MaxFileBytes = 20L * 1024 * 1024;
        /// <summary>同一轮对话生成中时，两次更新记录的最小间隔（完成时总会立即写入）。</summary>
        private static readonly TimeSpan GeneratingThrottle = TimeSpan.FromSeconds(15);

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>(new ConcurrentQueue<Action>());
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly Dictionary<string, int> Volumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, TurnState>> Turns = new Dictionary<string, Dictionary<string, TurnState>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ConfigLock = new object();
        private static volatile string _root;
        private static string _configured;
        private static DateTime _lastFailLog;
        private static int _retentionDays;

        static Archive()
        {
            var worker = new Thread(Run) { IsBackground = true, Name = "Archive" };
            worker.Start();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush(3000, true);
        }

        /// <summary>生效的归档根目录；未启用或不可用时为 null。</summary>
        public static string Root => _root;
        public static bool Enabled => _root != null;
        /// <summary>配置的目录不可用、已回退等问题；正常时为 null。</summary>
        public static string Warning { get; private set; }

        public static string FallbackRoot => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath), "archive");
        /// <summary>程序自身日志（tasks.log 等）固定目录，不随归档目录变化。</summary>
        public static string AppLogFolder => AppPaths.LogFolder;

        #region 配置

        /// <summary>按设置确定归档目录（不存在则创建；不可写时回退到 %APPDATA%\VSManager\archive）。返回警告信息或 null。</summary>
        public static string Configure(AppSettings s)
        {
            lock (ConfigLock)
            {
                _retentionDays = Math.Max(0, s.ArchiveRetentionDays);
                string want = string.IsNullOrWhiteSpace(s.ArchiveRoot) ? DefaultRoot : Environment.ExpandEnvironmentVariables(s.ArchiveRoot.Trim());
                string sig = s.ArchiveEnabled + "|" + want;
                if (sig == _configured && (_root == null || Directory.Exists(_root))) { StartCleanup(); return Warning; }
                _configured = sig;

                string warning = null, root = null;
                if (s.ArchiveEnabled)
                {
                    string err = Probe(want);
                    if (err == null) root = Path.GetFullPath(want);
                    else
                    {
                        string fb = FallbackRoot, err2 = Probe(fb);
                        if (err2 == null)
                        {
                            root = fb;
                            warning = "归档目录「" + want + "」不可用（" + err + "），已改用 " + fb;
                        }
                        else warning = "归档目录「" + want + "」与备用目录均不可用（" + err + "；" + err2 + "），暂停归档";
                    }
                }
                if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase))
                    Log(root == null ? "归档已停用" + (warning != null ? "：" + warning : "") : "归档目录：" + root + (warning != null ? "（" + warning + "）" : ""));
                _root = root;
                Warning = warning;
                StartCleanup();
                return warning;
            }
        }

        private static string Probe(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string drive = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(drive) && !Directory.Exists(drive)) return "磁盘 " + drive.TrimEnd('\\') + " 不存在";
                foreach (var sub in new[] { "tasks", "chat", "logs" }) Directory.CreateDirectory(Path.Combine(full, sub));
                string test = Path.Combine(full, ".write-test");
                File.WriteAllText(test, DateTime.Now.ToString("O"));
                File.Delete(test);
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + "：" + ex.Message; }
        }

        private static DateTime _lastCleanup;

        /// <summary>保留天数 > 0 时删除超期的归档与发送日志；0 表示永久保留（默认）。每天最多执行一次。</summary>
        private static void StartCleanup()
        {
            int days = _retentionDays;
            if (days <= 0 || (DateTime.Now - _lastCleanup).TotalHours < 12) return;
            _lastCleanup = DateTime.Now;
            string root = _root;
            Enqueue(() =>
            {
                var limit = DateTime.Now.Date.AddDays(-days);
                var dirs = new List<string> { AppLogFolder };
                if (root != null) dirs.AddRange(new[] { "tasks", "chat", "logs" }.Select(x => Path.Combine(root, x)));
                int n = 0;
                foreach (var d in dirs)
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*-*.*"))
                    {
                        string name = Path.GetFileName(f);
                        bool ours = d == AppLogFolder ? name.StartsWith("send-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                            : name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
                        if (!ours || File.GetLastWriteTime(f) >= limit) continue;
                        try { File.Delete(f); n++; } catch { }
                    }
                }
                if (n > 0) Log("按保留天数 " + days + " 天清理了 " + n + " 个历史文件");
            });
        }

        #endregion

        #region 写入

        private static void Enqueue(Action a)
        {
            try { if (!Queue.IsAddingCompleted) Queue.Add(a); } catch (InvalidOperationException) { }
        }

        private static void Run()
        {
            foreach (var a in Queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { Fail("归档写入异常", ex); }
            }
        }

        /// <summary>等待已排队的记录写完；pending=true 时同时写出被节流暂存的生成中对话（退出程序时）。</summary>
        public static bool Flush(int timeoutMs, bool pending = false)
        {
            if (Thread.CurrentThread.Name == "Archive") return true;
            using (var done = new ManualResetEventSlim(false))
            {
                Enqueue(() => { if (pending) { FlushPendingTurns(); FlushPendingManual(); } try { done.Set(); } catch (ObjectDisposedException) { } });
                try { return done.Wait(timeoutMs); } catch (ObjectDisposedException) { return true; }
            }
        }

        /// <summary>当前（当天、当前分卷）文件路径；未启用归档时为 null。</summary>
        public static string CurrentFile(string sub, string prefix, string ext)
        {
            string root = _root;
            return root == null ? null : Target(root, sub, prefix, ext, DateTime.Now, 0);
        }

        private static string Target(string root, string sub, string prefix, string ext, DateTime day, long adding)
        {
            string dir = Path.Combine(root, sub);
            string stem = Path.Combine(dir, prefix + "-" + day.ToString("yyyyMMdd"));
            int vol;
            lock (Volumes) { if (!Volumes.TryGetValue(stem, out vol)) vol = 1; }
            while (true)
            {
                string path = vol == 1 ? stem + ext : stem + "." + vol + ext;
                long len = 0;
                try { var fi = new FileInfo(path); if (fi.Exists) len = fi.Length; } catch { }
                if (len == 0 || len + adding <= MaxFileBytes) { lock (Volumes) Volumes[stem] = vol; return path; }
                vol++;
            }
        }

        /// <summary>追加一段文本到 sub\prefix-yyyyMMdd(.n)ext（在后台线程执行）。</summary>
        public static void Append(string sub, string prefix, string ext, string text)
        {
            if (_root == null || string.IsNullOrEmpty(text)) return;
            var at = DateTime.Now;
            Enqueue(() => WriteNow(sub, prefix, ext, text, at));
        }

        private static void WriteNow(string sub, string prefix, string ext, string text, DateTime at)
        {
            string root = _root;
            if (root == null) return;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, sub));
                string path = Target(root, sub, prefix, ext, at, Utf8.GetByteCount(text));
                File.AppendAllText(path, text, Utf8);
            }
            catch (Exception ex) { Fail("写入 " + sub + "\\" + prefix + " 失败", ex); }
        }

        private static void AppendJson(string sub, string prefix, Dictionary<string, object> record, DateTime at)
        {
            string line;
            try { line = Json.Serialize(record) + "\n"; }
            catch (Exception ex) { Fail("序列化归档记录失败", ex); return; }
            WriteNow(sub, prefix, ".jsonl", line, at);
        }

        private static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff");
        private static string Time(DateTime? t) => t.HasValue ? Time(t.Value) : null;

        #endregion

        #region 任务流水

        /// <summary>任务新增 / 状态变化 / 移除：追加一行到 tasks\tasks-yyyyMMdd.jsonl。</summary>
        public static void TaskEvent(QueuedTask t, string evt)
        {
            if (_root == null || t == null) return;
            var at = DateTime.Now;
            var r = new Dictionary<string, object>
            {
                ["time"] = Time(at),
                ["event"] = evt,
                ["id"] = t.Id,
                ["vs"] = t.VsName,
                ["vsKey"] = t.VsKey,
                ["source"] = t.Source,
                ["status"] = t.Status,
                ["statusText"] = StatusText(t.Status),
                ["text"] = t.Text,
                ["result"] = t.Result,
                ["error"] = t.Error,
                ["attempts"] = t.Attempts,
                ["target"] = t.Target,
                ["created"] = Time(t.Created),
                ["started"] = Time(t.Started),
                ["finished"] = Time(t.Finished)
            };
            // 附件只归档引用信息（编号、原始文件名、大小、类型、哈希、相对路径），不含内容
            // Attachments are archived as references only (id, original name, size, kind, hash, relative path), never content
            if (t.HasAttachments)
            {
                r["attachments"] = t.Attachments.Where(a => a != null).Select(a => new Dictionary<string, object>
                {
                    ["id"] = a.Id, ["name"] = a.Name, ["size"] = a.Size, ["kind"] = a.Kind, ["sha256"] = a.Sha256,
                    ["path"] = "attachments/" + a.RelPath
                }).ToList();
                if (!string.IsNullOrEmpty(t.AttachmentNote)) r["attachmentNote"] = t.AttachmentNote;
            }
            Enqueue(() => AppendJson("tasks", "tasks", r, at));
        }

        private static string StatusText(string s)
        {
            switch (s)
            {
                case QueueStatus.Waiting: return "排队中";
                case QueueStatus.WaitingVs: return "等待目标 VS";
                case QueueStatus.Sending: return "发送中";
                case QueueStatus.Running: return "执行中";
                case QueueStatus.Done: return "已完成";
                case QueueStatus.Failed: return "失败";
                case QueueStatus.Cancelled: return "已取消";
                default: return s;
            }
        }

        #endregion

        #region AI 总控助手对话

        /// <summary>AI 助手对话：role 为 user / assistant / notice（任务完成等系统通知）。</summary>
        public static void Ai(string role, string content, string detail = null, IList<string> steps = null, string error = null)
        {
            if (_root == null || string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(error)) return;
            var at = DateTime.Now;
            var r = new Dictionary<string, object> { ["time"] = Time(at), ["role"] = role, ["content"] = content ?? "" };
            if (!string.IsNullOrWhiteSpace(detail) && detail != content) r["detail"] = detail;
            if (steps != null && steps.Count > 0) r["steps"] = steps.ToArray();
            if (!string.IsNullOrEmpty(error)) r["error"] = error;
            Enqueue(() => AppendJson("chat", "ai", r, at));
        }

        #endregion

        #region 各 VS 的 Copilot 对话

        private sealed class TurnState
        {
            public string Sig;
            public bool Generating;
            public DateTime LastWrite;
            public Dictionary<string, object> Pending;
            public string PendingPrefix;
        }

        private sealed class Turn
        {
            public string Question, Answer;
            public bool Generating;
        }

        /// <summary>
        /// 通过 UI Automation 读到的某个 VS 的对话：每轮（用户提问 + Copilot 回答）以 turn 标识写入 chat\vs-名称-yyyyMMdd.jsonl；
        /// 内容未变不重复写，内容变化以 update=true 追加。
        /// </summary>
        public static void VsChat(string vsKey, string vsName, ChatTranscript t, bool busy)
        {
            if (_root == null || t == null || !t.PaneFound || t.Unchanged || t.Messages == null || t.Messages.Count == 0) return;
            var turns = new List<Turn>();
            Turn cur = null;
            foreach (var m in t.Messages)
            {
                string text = string.Join("\n\n", m.Parts.Where(p => !p.IsStep && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text.Trim()));
                if (m.Role == ChatRole.User)
                {
                    cur = new Turn { Question = text, Answer = "" };
                    if (text.Length > 0) turns.Add(cur);
                }
                else if (cur != null && text.Length > 0) cur.Answer = cur.Answer.Length == 0 ? text : cur.Answer + "\n\n" + text;
            }
            if (turns.Count == 0) return;
            turns[turns.Count - 1].Generating = busy;
            string key = string.IsNullOrWhiteSpace(vsKey) ? vsName : vsKey;
            string prefix = "vs-" + SafeName(vsName);
            var at = DateTime.Now;
            Enqueue(() => WriteTurns(key, vsName, prefix, turns, at));
        }

        private static void WriteTurns(string key, string vsName, string prefix, List<Turn> turns, DateTime at)
        {
            var states = StatesFor(key, prefix);
            foreach (var turn in turns)
            {
                string id = Hash(key + "\n" + Squash(turn.Question), 12);
                string sig = Hash(turn.Question + "\u0001" + turn.Answer, 16) + (turn.Generating ? "~" : "");
                states.TryGetValue(id, out var st);
                if (st != null && st.Sig == sig) { st.Pending = null; continue; }
                var r = new Dictionary<string, object>
                {
                    ["time"] = Time(at),
                    ["vs"] = vsName,
                    ["vsKey"] = key,
                    ["turn"] = id,
                    ["update"] = st != null,
                    ["generating"] = turn.Generating,
                    ["question"] = turn.Question,
                    ["answer"] = turn.Answer
                };
                if (st == null) states[id] = st = new TurnState();
                // 生成中的内容变化较快：限制写入频率，最新内容先暂存，完成或超过间隔时再写
                if (turn.Generating && st.Generating && at - st.LastWrite < GeneratingThrottle)
                {
                    st.Pending = r;
                    st.PendingPrefix = prefix;
                    continue;
                }
                AppendJson("chat", prefix, r, at);
                st.Sig = sig;
                st.Generating = turn.Generating;
                st.LastWrite = at;
                st.Pending = null;
            }
        }

        private static void FlushPendingTurns()
        {
            foreach (var states in Turns.Values)
                foreach (var st in states.Values)
                {
                    if (st.Pending == null) continue;
                    AppendJson("chat", st.PendingPrefix, st.Pending, DateTime.Now);
                    st.Pending = null;
                    st.LastWrite = DateTime.Now;
                }
        }

        /// <summary>首次遇到某个 VS 时，从最近两天的归档中恢复已写入的轮次，重启后不重复写入相同内容。</summary>
        private static Dictionary<string, TurnState> StatesFor(string key, string prefix)
        {
            if (Turns.TryGetValue(key, out var states)) return states;
            Turns[key] = states = new Dictionary<string, TurnState>();
            string root = _root;
            if (root == null) return states;
            try
            {
                string dir = Path.Combine(root, "chat");
                if (!Directory.Exists(dir)) return states;
                var days = new[] { DateTime.Now.AddDays(-1), DateTime.Now }.Select(d => prefix + "-" + d.ToString("yyyyMMdd")).ToList();
                var files = Directory.GetFiles(dir, prefix + "-*.jsonl")
                    .Where(f => days.Any(d => Path.GetFileName(f).StartsWith(d + ".", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(f => File.GetLastWriteTime(f));
                foreach (var f in files)
                    foreach (var line in File.ReadLines(f, Utf8))
                    {
                        if (line.Length < 2) continue;
                        try
                        {
                            if (!(Json.DeserializeObject(line) is Dictionary<string, object> d)) continue;
                            string id = d.TryGetValue("turn", out var i) ? i as string : null;
                            if (id == null) continue;
                            string q = d.TryGetValue("question", out var qo) ? qo as string ?? "" : "";
                            string a = d.TryGetValue("answer", out var ao) ? ao as string ?? "" : "";
                            bool gen = d.TryGetValue("generating", out var g) && g is bool b && b;
                            states[id] = new TurnState { Sig = Hash(q + "\u0001" + a, 16) + (gen ? "~" : ""), Generating = gen, LastWrite = DateTime.MinValue };
                        }
                        catch { }
                    }
            }
            catch (Exception ex) { Fail("读取已有 VS 对话归档失败", ex); }
            return states;
        }

        #region 任务清单中的 VS 手动对话

        private sealed class ManualState
        {
            public string Sig;
            public DateTime LastWrite;
            public Dictionary<string, object> Pending;
            public string PendingPrefix;
        }

        private static readonly Dictionary<string, ManualState> Manual = new Dictionary<string, ManualState>(StringComparer.Ordinal);

        /// <summary>对话轮次标识（与 VsChat 的 turn 一致）：VS + 去空白后的提问。</summary>
        public static string TurnId(string vsKey, string vsName, string question) =>
            Hash((string.IsNullOrWhiteSpace(vsKey) ? vsName : vsKey) + "\n" + Squash(question), 12);

        /// <summary>
        /// 任务清单中的手动对话条目：首次出现、内容更新、状态结束时追加 kind=manual 记录到 chat\vs-名称-日期.jsonl；
        /// 状态与内容未变不重复写，生成中的更新按间隔节流。
        /// </summary>
        public static void ManualChat(ExternalChat c, bool removed = false)
        {
            if (_root == null || c == null) return;
            if (c.ArchiveId == null) c.ArchiveId = TurnId(c.VsKey, c.VsName, c.Question) + "-" + c.Started.ToString("yyyyMMddHHmmss");
            string status = removed ? "removed" : c.Generating ? "generating" : c.Stopped ? "stopped" : c.Interrupted ? "interrupted" : "done";
            var at = DateTime.Now;
            var r = new Dictionary<string, object>
            {
                ["time"] = Time(at),
                ["kind"] = "manual",
                ["vs"] = c.VsName,
                ["vsKey"] = string.IsNullOrWhiteSpace(c.VsKey) ? c.VsName : c.VsKey,
                ["turn"] = TurnId(c.VsKey, c.VsName, c.Question),
                ["entry"] = c.ArchiveId,
                ["status"] = status,
                ["statusText"] = removed ? "已从清单移除" : c.StatusText,
                ["generating"] = c.Generating,
                ["question"] = c.Question ?? "",
                ["answer"] = c.Answer ?? "",
                ["started"] = Time(c.Started),
                ["finished"] = Time(c.Finished)
            };
            string sig = Hash((c.Question ?? "") + "\u0001" + (c.Answer ?? ""), 16) + "|" + status;
            string prefix = "vs-" + SafeName(c.VsName);
            bool generating = c.Generating;
            Enqueue(() =>
            {
                if (!Manual.TryGetValue((string)r["entry"], out var st)) Manual[(string)r["entry"]] = st = new ManualState();
                if (st.Sig == sig) { st.Pending = null; return; }
                if (generating && st.Sig != null && st.Sig.EndsWith("|generating") && at - st.LastWrite < GeneratingThrottle)
                {
                    st.Pending = r;
                    st.PendingPrefix = prefix;
                    return;
                }
                r["update"] = st.Sig != null;
                AppendJson("chat", prefix, r, at);
                st.Sig = sig;
                st.LastWrite = at;
                st.Pending = null;
            });
        }

        private static void FlushPendingManual()
        {
            foreach (var st in Manual.Values)
            {
                if (st.Pending == null) continue;
                st.Pending["update"] = true;
                AppendJson("chat", st.PendingPrefix, st.Pending, DateTime.Now);
                st.Pending = null;
            }
        }

        /// <summary>
        /// 从归档恢复最近的手动对话（每个条目取最后一条记录），按开始时间倒序最多 limit 条、hours 小时内。
        /// 返回的条目均为已结束状态；同时记住已写入的内容，避免重复写入。
        /// </summary>
        public static List<ExternalChat> RestoreManual(int hours, int limit, int timeoutMs = 5000)
        {
            var result = new List<ExternalChat>();
            string root = _root;
            if (root == null || limit <= 0) return result;
            if (hours <= 0) hours = 24;
            var since = DateTime.Now.AddHours(-hours);
            using (var done = new ManualResetEventSlim(false))
            {
                Enqueue(() =>
                {
                    try
                    {
                        string dir = Path.Combine(root, "chat");
                        if (!Directory.Exists(dir)) return;
                        var days = new HashSet<string>();
                        for (var d = since.Date; d <= DateTime.Now.Date; d = d.AddDays(1)) days.Add(d.ToString("yyyyMMdd"));
                        var latest = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                        foreach (var f in Directory.GetFiles(dir, "vs-*.jsonl"))
                        {
                            // 文件名形如 vs-名称-yyyyMMdd.jsonl 或 vs-名称-yyyyMMdd.2.jsonl
                            string stem = Path.GetFileNameWithoutExtension(f);
                            int dot = stem.LastIndexOf('.');
                            if (dot > 0 && int.TryParse(stem.Substring(dot + 1), out _)) stem = stem.Substring(0, dot);
                            if (stem.Length < 9 || !days.Contains(stem.Substring(stem.Length - 8))) continue;
                            foreach (var line in File.ReadLines(f, Utf8))
                            {
                                if (line.IndexOf("\"kind\":\"manual\"", StringComparison.Ordinal) < 0) continue;
                                try
                                {
                                    if (Json.DeserializeObject(line) is Dictionary<string, object> rec && rec.TryGetValue("entry", out var e) && e is string id)
                                        latest[id] = rec;
                                }
                                catch { }
                            }
                        }
                        var local = new List<ExternalChat>();
                        foreach (var kv in latest)
                        {
                            var rec = kv.Value;
                            string S(string k) => rec.TryGetValue(k, out var v) ? v as string : null;
                            if (!DateTime.TryParse(S("started"), out var started) || started < since) continue;
                            string status = S("status") ?? "done";
                            if (status == "removed") continue;
                            var c = new ExternalChat
                            {
                                ArchiveId = kv.Key, VsKey = S("vsKey"), VsName = S("vs") ?? "VS", Question = S("question") ?? "", Answer = S("answer") ?? "",
                                Started = started, Restored = true, Stopped = status == "stopped", Interrupted = status == "generating" || status == "interrupted"
                            };
                            c.Key = Squash(c.Question);
                            c.Finished = DateTime.TryParse(S("finished"), out var fin) ? fin : DateTime.TryParse(S("time"), out var tm) ? tm : started;
                            local.Add(c);
                            string sig = Hash(c.Question + "\u0001" + c.Answer, 16) + "|" + status;
                            Manual[kv.Key] = new ManualState { Sig = sig, LastWrite = DateTime.MinValue };
                        }
                        lock (result) result.AddRange(local);
                    }
                    catch (Exception ex) { Fail("恢复手动对话失败", ex); }
                    finally { try { done.Set(); } catch (ObjectDisposedException) { } }
                });
                try { done.Wait(timeoutMs); } catch (ObjectDisposedException) { }
            }
            lock (result)
                return result.OrderByDescending(c => c.Started).Take(limit).ToList();
        }

        #endregion

        private static string Squash(string s) => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

        private static string Hash(string s, int len)
        {
            using (var sha = SHA1.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                return string.Concat(b.Select(x => x.ToString("x2"))).Substring(0, len);
            }
        }

        /// <summary>VS 名称转为可用作文件名的形式。</summary>
        public static string SafeName(string name)
        {
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars()) { ' ', '.' };
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim()) sb.Append(bad.Contains(c) ? '_' : c);
            string s = sb.ToString().Trim('_');
            if (s.Length > 40) s = s.Substring(0, 40);
            return s.Length == 0 ? "unknown" : s;
        }

        #endregion

        #region 失败记录

        private static void Fail(string what, Exception ex)
        {
            // 同类失败每分钟最多记录一次，避免磁盘故障时刷屏
            if ((DateTime.Now - _lastFailLog).TotalSeconds < 60) return;
            _lastFailLog = DateTime.Now;
            Log(what + "：" + ex.GetType().Name + "：" + ex.Message);
        }

        private static void Log(string text) => AppLog.Write(AppLog.TasksFile, "[归档] " + text);

        #endregion
    }
}
