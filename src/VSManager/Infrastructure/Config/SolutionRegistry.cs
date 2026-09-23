using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VSManager
{
    /// <summary>
    /// 解决方案登记表：保存在 %APPDATA%\VSManager\solutions.json（原子写入，保留上一版本 solutions.json.bak）。线程安全，读取返回副本。
    /// Solution registry stored in %APPDATA%\VSManager\solutions.json (atomic write, previous version kept as
    /// solutions.json.bak). Thread-safe; reads return copies.
    /// </summary>
    public sealed class SolutionRegistry
    {
        [DataContract]
        private sealed class FileModel
        {
            [DataMember(Order = 0)] public string _comment = "VSManager 解决方案登记表 / VSManager solution registry";
            [DataMember(Order = 1)] public List<SolutionEntry> Solutions = new List<SolutionEntry>();
        }

        private readonly object _lock = new object();
        private List<SolutionEntry> _items = new List<SolutionEntry>();

        public SolutionRegistry(string path = null) { FilePath = path ?? DefaultPath; }

        public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "solutions.json");

        public string FilePath { get; }

        /// <summary>最近一次读取或保存的问题（null 表示正常）。/ The last load or save problem (null = fine).</summary>
        public string LastError { get; private set; }

        /// <summary>登记表变化时触发（任意线程）。/ Raised when the registry changes (any thread).</summary>
        public event Action Changed;

        /// <summary>全部登记（副本）。/ All entries (copies).</summary>
        public List<SolutionEntry> Items { get { lock (_lock) return _items.Select(e => e.Clone()).ToList(); } }

        public int Count { get { lock (_lock) return _items.Count; } }

        /// <summary>读取登记表；文件不存在时为空，主文件损坏时尝试备份。/ Loads the registry; empty when missing, falls back to the backup when damaged.</summary>
        public SolutionRegistry Load()
        {
            var list = TryRead(FilePath, out string err);
            if (list == null && File.Exists(FilePath + ".bak")) list = TryRead(FilePath + ".bak", out _);
            lock (_lock) _items = Sanitize(list ?? new List<SolutionEntry>());
            LastError = err;
            return this;
        }

        private static List<SolutionEntry> TryRead(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? null : new List<SolutionEntry>();
                byte[] data = File.ReadAllBytes(path);
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Skip(3).ToArray();
                using (var ms = new MemoryStream(data))
                    return ((FileModel)new DataContractJsonSerializer(typeof(FileModel)).ReadObject(ms))?.Solutions ?? new List<SolutionEntry>();
            }
            catch (Exception ex)
            {
                error = "读取 solutions.json 失败 / Failed to read solutions.json：" + ex.Message;
                try { File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
                return null;
            }
        }

        /// <summary>去掉空别名 / 空路径，整理同义词，别名重复时保留第一条。/ Drops entries without alias or path, tidies synonyms, keeps the first of duplicate aliases.</summary>
        internal static List<SolutionEntry> Sanitize(IEnumerable<SolutionEntry> items)
        {
            var result = new List<SolutionEntry>();
            foreach (var e in items ?? Enumerable.Empty<SolutionEntry>())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Alias) || string.IsNullOrWhiteSpace(e.Path)) continue;
                var c = e.Clone();
                c.Alias = c.Alias.Trim();
                c.Path = c.Path.Trim().Trim('"');
                c.Description = string.IsNullOrWhiteSpace(c.Description) ? null : c.Description.Trim();
                c.Synonyms = SolutionEntry.ParseSynonyms(string.Join("、", c.Synonyms ?? new List<string>()));
                c.DefaultVs = Math.Max(0, c.DefaultVs);
                if (result.Any(x => string.Equals(x.Alias, c.Alias, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(c);
            }
            return result;
        }

        /// <summary>整体替换并保存，返回错误信息（null 表示成功）。/ Replaces everything and saves; returns the error (null = success).</summary>
        public string Replace(IEnumerable<SolutionEntry> items)
        {
            lock (_lock) _items = Sanitize(items);
            return Save();
        }

        /// <summary>新增或按别名覆盖一条并保存。/ Adds or overwrites (by alias) one entry and saves.</summary>
        public string Upsert(SolutionEntry e)
        {
            lock (_lock)
            {
                var list = _items.Where(x => !string.Equals(x.Alias, (e?.Alias ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                list.Add(e);
                _items = Sanitize(list);
            }
            return Save();
        }

        /// <summary>按别名解析（见 <see cref="SolutionMatcher"/>）。/ Resolves by alias (see <see cref="SolutionMatcher"/>).</summary>
        public SolutionLookup Resolve(string query) => SolutionMatcher.Resolve(Items, query);

        /// <summary>按路径查找登记。/ Finds an entry by path.</summary>
        public SolutionEntry FindByPath(string path) => Items.FirstOrDefault(e => SolutionMatcher.SamePath(e.Path, path));

        /// <summary>已登记别名列表文本（用于报错提示）。/ The registered aliases as text (for error messages).</summary>
        public string AliasListText()
        {
            var items = Items;
            if (items.Count == 0) return "（登记表为空，可在「属性 → 解决方案登记」中添加 / The registry is empty; add entries in Settings → Solution registry）";
            return string.Join("、", items.Select(e => "「" + e.Alias + "」" + (e.Synonyms.Count > 0 ? "（" + e.SynonymText + "）" : "")));
        }

        public string Save()
        {
            FileModel model;
            lock (_lock) model = new FileModel { Solutions = _items.Select(e => e.Clone()).ToList() };
            var r = AtomicFile.Write(FilePath, stream =>
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(stream, System.Text.Encoding.UTF8, false, true))
                    new DataContractJsonSerializer(typeof(FileModel)).WriteObject(w, model);
            }, backupBeforeOverwrite: true, skipFallbackOnSerializationError: true);
            LastError = r.Ok ? null : "保存 solutions.json 失败 / Failed to save solutions.json：" + (r.FallbackError ?? r.Error).Message;
            try { Changed?.Invoke(); } catch { }
            return LastError;
        }
    }
}
