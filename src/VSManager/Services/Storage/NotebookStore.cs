using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VSManager
{
    /// <summary>笔记页面：既有正文，也可以包含子页面（文件夹与笔记合一）。/ A notebook page: it has content and may contain subpages (folders and notes merged).</summary>
    internal sealed class NotebookEntry
    {
        /// <summary>页面编号；根节点为空字符串。/ Page id; empty string for the root.</summary>
        public string Path { get; set; }
        public string Name { get; set; }
        public bool HasChildren => Children.Count > 0;
        public List<NotebookEntry> Children { get; } = new List<NotebookEntry>();
    }

    internal sealed class NotebookDocument
    {
        public string Path { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public string Version { get; set; }
        public string NewLine { get; set; } = "\n";
    }

    internal sealed class NotebookConflictException : IOException
    {
        public NotebookConflictException() : base("页面已在别处修改或删除。 / Page changed or deleted elsewhere.") { }
    }

    /// <summary>
    /// 基于 SQLite 的笔记库（%APPDATA%\VSManager\Notebooks\notebook.db）：页面树、正文、图片与废纸篓都保存在数据库中；
    /// 首次打开时一次性导入旧版 Markdown 目录，原文件保留作备份。
    /// SQLite-backed notebook library (%APPDATA%\VSManager\Notebooks\notebook.db): page tree, content, images and trash live in
    /// the database; the legacy Markdown folder is imported once on first open and the original files are kept as a backup.
    /// </summary>
    internal sealed class NotebookStore
    {
        internal const int MaxNoteBytes = 4 * 1024 * 1024;
        internal const int MaxTitleLength = 120;
        internal const string LinkPrefix = "page:";
        private static readonly Regex IdPattern = new Regex("^[0-9a-f]{32}$", RegexOptions.Compiled);
        private readonly string _connection;
        public static string DefaultRoot => System.IO.Path.Combine(AppPaths.DataFolder, "Notebooks");
        public string Root { get; }
        public string DatabasePath => System.IO.Path.Combine(Root, "notebook.db");

        public NotebookStore(string root = null)
        {
            Root = System.IO.Path.GetFullPath(root ?? DefaultRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            Directory.CreateDirectory(Root);
            CheckReparse(Root);
            // 不使用连接池，关闭后立即释放文件句柄。/ No pooling so the file handle is released as soon as a connection closes.
            _connection = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10 }.ToString();
            Run(c =>
            {
                Exec(c, @"CREATE TABLE IF NOT EXISTS pages(id TEXT PRIMARY KEY, parent_id TEXT, title TEXT NOT NULL, body TEXT NOT NULL DEFAULT '',
                    sort INTEGER NOT NULL DEFAULT 0, created TEXT NOT NULL, updated TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1, deleted_at TEXT);
                    CREATE INDEX IF NOT EXISTS ix_pages_parent ON pages(parent_id, deleted_at);
                    CREATE TABLE IF NOT EXISTS assets(name TEXT PRIMARY KEY, page_id TEXT, mime TEXT NOT NULL, data BLOB NOT NULL, created TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);");
                if (Scalar(c, "SELECT value FROM meta WHERE key='legacy_imported'") == null)
                {
                    using var tx = c.BeginTransaction();
                    ImportLegacyFolder(c, tx, Root, null, 0);
                    Exec(c, "INSERT INTO meta(key, value) VALUES('legacy_imported', @v)", tx, ("@v", Now()));
                    tx.Commit();
                }
                return 0;
            });
        }

        // ---- 页面树 / Page tree ----

        public List<NotebookEntry> LoadTree(string query = "")
        {
            string q = (query ?? "").Trim();
            var rows = Run(c =>
            {
                var list = new List<(string Id, string Parent, string Title, int Sort, string Body)>();
                using var cmd = Command(c, "SELECT id, parent_id, title, sort, " + (q.Length > 0 ? "body" : "''") + " FROM pages WHERE deleted_at IS NULL");
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
                return list;
            });
            var ids = new HashSet<string>(rows.Select(r => r.Id));
            var byParent = rows.GroupBy(r => r.Parent != null && ids.Contains(r.Parent) ? r.Parent : "")
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Sort).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList());
            return Build("", q, byParent, 0);
        }

        private static List<NotebookEntry> Build(string parent, string query, Dictionary<string, List<(string Id, string Parent, string Title, int Sort, string Body)>> byParent, int depth)
        {
            var result = new List<NotebookEntry>();
            if (depth > 64 || !byParent.TryGetValue(parent, out var children)) return result;
            foreach (var row in children)
            {
                var entry = new NotebookEntry { Path = row.Id, Name = row.Title };
                bool titleMatch = query.Length == 0 || row.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                entry.Children.AddRange(Build(row.Id, titleMatch ? "" : query, byParent, depth + 1));
                bool matches = titleMatch || row.Body.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                if (matches || entry.Children.Count > 0) result.Add(entry);
            }
            return result;
        }

        /// <summary>按标题查找直接子页面（不区分大小写）。/ Finds a direct child page by title (case-insensitive).</summary>
        public string FindChild(string parent, string title)
        {
            string p = ParentOrNull(parent);
            return Run(c => ChildId(c, null, p, title, null));
        }

        /// <summary>从根到该页面的标题链。/ Title chain from the root to the page.</summary>
        public IReadOnlyList<string> TitlePath(string id)
        {
            var titles = new List<string>();
            Run(c =>
            {
                string current = id;
                for (int i = 0; i < 64 && !string.IsNullOrEmpty(current); i++)
                {
                    using var cmd = Command(c, "SELECT parent_id, title FROM pages WHERE id=@id AND deleted_at IS NULL", null, ("@id", current));
                    using var r = cmd.ExecuteReader();
                    if (!r.Read()) break;
                    titles.Insert(0, r.GetString(1));
                    current = r.IsDBNull(0) ? null : r.GetString(0);
                }
                return 0;
            });
            return titles;
        }

        public string ParentOf(string id)
        {
            ValidateId(id);
            return Run(c => Scalar(c, "SELECT parent_id FROM pages WHERE id=@id AND deleted_at IS NULL", null, ("@id", id)) as string) ?? "";
        }

        // ---- 读写 / Read and write ----

        /// <summary>批量读取直接子页的元数据头部（最多 8192 字符），供任务表格使用。/ Reads direct-child metadata headers (up to 8192 characters) in one query for task tables.</summary>
        internal IReadOnlyList<NotebookDocument> ReadChildHeaders(string parent)
        {
            ValidateId(parent);
            return Run(c =>
            {
                var result = new List<NotebookDocument>();
                using var cmd = Command(c, "SELECT id, title, substr(body, 1, 8192) FROM pages WHERE parent_id=@parent AND deleted_at IS NULL", null, ("@parent", parent));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string text = reader.GetString(2);
                    int section = text.IndexOf("\n## ", StringComparison.Ordinal);
                    result.Add(new NotebookDocument { Path = reader.GetString(0), Title = reader.GetString(1), Text = section < 0 ? text : text.Substring(0, section) });
                }
                return result;
            });
        }

        public NotebookDocument Read(string id)
        {
            ValidateId(id);
            return Run(c =>
            {
                using var cmd = Command(c, "SELECT title, body, version FROM pages WHERE id=@id AND deleted_at IS NULL", null, ("@id", id));
                using var r = cmd.ExecuteReader();
                if (!r.Read()) throw new IOException("页面不存在或已移入废纸篓。 / Page not found or moved to trash.");
                return new NotebookDocument { Path = id, Title = r.GetString(0), Text = r.GetString(1), Version = r.GetInt64(2).ToString(CultureInfo.InvariantCulture) };
            });
        }

        public void Save(NotebookDocument document, string text)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            ValidateId(document.Path);
            string normalized = NormalizeNewLines(text ?? "", "\n");
            if (Encoding.UTF8.GetByteCount(normalized) > MaxNoteBytes) throw new IOException("笔记超过 4 MB，请拆分保存。 / Note exceeds 4 MB; split it into smaller notes.");
            Run(c =>
            {
                object current = Scalar(c, "SELECT version FROM pages WHERE id=@id AND deleted_at IS NULL", null, ("@id", document.Path));
                if (current == null || Convert.ToInt64(current, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) != document.Version) throw new NotebookConflictException();
                if (normalized == document.Text) return 0;
                int changed = Exec(c, "UPDATE pages SET body=@b, version=version+1, updated=@u WHERE id=@id AND version=@v AND deleted_at IS NULL", null,
                    ("@b", normalized), ("@u", Now()), ("@id", document.Path), ("@v", long.Parse(document.Version, CultureInfo.InvariantCulture)));
                if (changed != 1) throw new NotebookConflictException();
                document.Text = normalized;
                document.Version = (long.Parse(document.Version, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
                return 0;
            });
        }

        /// <summary>在父页面下新建页面（父为空表示根）。/ Creates a page under a parent (empty parent means root).</summary>
        public string CreatePage(string parent, string title, string text = null)
        {
            ValidateTitle(title);
            string p = ParentOrNull(parent);
            string body = NormalizeNewLines(text ?? "# " + title + "\n\n", "\n");
            if (Encoding.UTF8.GetByteCount(body) > MaxNoteBytes) throw new IOException("笔记超过 4 MB。 / Note exceeds 4 MB.");
            return Run(c =>
            {
                using var tx = c.BeginTransaction();
                if (p != null && Scalar(c, "SELECT 1 FROM pages WHERE id=@id AND deleted_at IS NULL", tx, ("@id", p)) == null)
                    throw new IOException("父页面不存在。 / Parent page not found.");
                if (ChildId(c, tx, p, title, null) != null) throw new IOException("同级已有同名页面。 / A sibling page with this title already exists.");
                string id = Insert(c, tx, p, title, body);
                tx.Commit();
                return id;
            });
        }

        public string Rename(string id, string title)
        {
            ValidateId(id);
            ValidateTitle(title);
            return Run(c =>
            {
                using (var tx = c.BeginTransaction())
                {
                    object parent = Scalar(c, "SELECT IFNULL(parent_id, '') FROM pages WHERE id=@id AND deleted_at IS NULL", tx, ("@id", id));
                    if (parent == null) throw new IOException("页面不存在或已移入废纸篓。 / Page not found or moved to trash.");
                    string p = (string)parent == "" ? null : (string)parent;
                    if (ChildId(c, tx, p, title, id) != null) throw new IOException("同级已有同名页面。 / A sibling page with this title already exists.");
                    Exec(c, "UPDATE pages SET title=@t, updated=@u WHERE id=@id", tx, ("@t", title), ("@u", Now()), ("@id", id));
                    tx.Commit();
                    return id;
                }
            });
        }

        /// <summary>把页面及其子页面移入废纸篓（数据库内标记，可恢复）。/ Moves a page and its subpages to the trash (marked in the database, recoverable).</summary>
        public string Trash(string id)
        {
            ValidateId(id);
            Run(c => Exec(c, @"WITH RECURSIVE tree(id) AS (SELECT id FROM pages WHERE id=@id AND deleted_at IS NULL
                UNION SELECT p.id FROM pages p JOIN tree t ON p.parent_id=t.id WHERE p.deleted_at IS NULL)
                UPDATE pages SET deleted_at=@d WHERE id IN (SELECT id FROM tree)", null, ("@id", id), ("@d", Now())));
            return id;
        }

        // ---- 图片 / Images ----

        public string ImportImage(string pageId, string source)
        {
            ValidateId(pageId);
            string extension = System.IO.Path.GetExtension(source).ToLowerInvariant();
            if (!IsImage(extension)) throw new IOException("支持 PNG、JPG、GIF、WebP、BMP 图片。 / Supported: PNG, JPG, GIF, WebP, BMP.");
            return ImportImage(pageId, ReadBytes(source), extension);
        }

        /// <summary>导入内存中的图片（例如剪贴板截图）。/ Imports in-memory image data (e.g. a clipboard screenshot).</summary>
        public string ImportImage(string pageId, byte[] bytes, string extension)
        {
            ValidateId(pageId);
            extension = (extension ?? "").ToLowerInvariant();
            if (!IsImage(extension)) throw new IOException("支持 PNG、JPG、GIF、WebP、BMP 图片。 / Supported: PNG, JPG, GIF, WebP, BMP.");
            if (bytes == null || bytes.Length == 0) throw new IOException("图片为空。 / Image is empty.");
            if (bytes.Length > MaxNoteBytes) throw new IOException("文件超过 4 MB。 / File exceeds 4 MB.");
            string name = Guid.NewGuid().ToString("N") + extension;
            Run(c => Exec(c, "INSERT INTO assets(name, page_id, mime, data, created) VALUES(@n, @p, @m, @d, @c)", null,
                ("@n", name), ("@p", pageId), ("@m", Mime(extension)), ("@d", bytes), ("@c", Now())));
            return "attachments/" + name;
        }

        public string ImageData(string pageId, string target)
        {
            if (string.IsNullOrWhiteSpace(target) || Uri.TryCreate(target, UriKind.Absolute, out _)) return null;
            string path = Uri.UnescapeDataString(target).Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains(":") || path.Split('/').Any(s => s == ".."))
                throw new ArgumentException("图片路径不能超出笔记库。 / Image path is outside the notebook library.");
            string name = path.Substring(path.LastIndexOf('/') + 1);
            if (!IsImage(System.IO.Path.GetExtension(name).ToLowerInvariant())) return null;
            return Run(c =>
            {
                using (var cmd = Command(c, "SELECT mime, data FROM assets WHERE name=@n", null, ("@n", name)))
                using (var r = cmd.ExecuteReader())
                    return r.Read() ? "data:" + r.GetString(0) + ";base64," + Convert.ToBase64String((byte[])r.GetValue(1)) : null;
            });
        }

        // ---- 链接 / Links ----

        /// <summary>
        /// 解析笔记链接：page:编号，或相对当前页面所在层级的「标题/标题.md」路径；目标不存在时返回 null。
        /// Resolves a note link: page:id, or a "title/title.md" path relative to the current page's level; null when missing.
        /// </summary>
        public string ResolveLink(string fromPage, string target)
        {
            if (!NotebookMarkdown.IsNoteLink(target)) return null;
            if (target.StartsWith(LinkPrefix, StringComparison.Ordinal))
            {
                string id = target.Substring(LinkPrefix.Length).ToLowerInvariant();
                return Run(c => Scalar(c, "SELECT 1 FROM pages WHERE id=@id AND deleted_at IS NULL", null, ("@id", id))) != null ? id : null;
            }
            string relative = Uri.UnescapeDataString(target.Split('#', '?')[0]).Replace('\\', '/');
            string current = string.IsNullOrEmpty(fromPage) ? null : ParentOrNull(ParentOf(fromPage));
            var parts = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part == ".") continue;
                if (part == "..")
                {
                    if (current == null) throw new ArgumentException("链接不能超出笔记库。 / Link is outside the notebook library.");
                    current = ParentOrNull(ParentOf(current));
                    continue;
                }
                if (i == parts.Length - 1 && part.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) part = part.Substring(0, part.Length - 3);
                string parent = current;
                current = Run(c => ChildId(c, null, parent, part, null));
                if (current == null) return null;
            }
            return current;
        }

        // ---- 导出 / Export ----

        /// <summary>把所有页面导出为 Markdown 目录（含图片），返回导出目录。/ Exports all pages as a Markdown folder (with images) and returns it.</summary>
        public string ExportMarkdown()
        {
            string folder = System.IO.Path.Combine(Root, "exports", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            ExportLevel(LoadTree(), folder);
            return folder;
        }

        private void ExportLevel(List<NotebookEntry> entries, string folder)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                string name = SafeFileName(entry.Name);
                for (int i = 2; !used.Add(name); i++) name = SafeFileName(entry.Name) + " (" + i + ")";
                string text = Read(entry.Path).Text;
                File.WriteAllText(System.IO.Path.Combine(folder, name + ".md"), NormalizeNewLines(text, "\r\n"), new UTF8Encoding(false));
                foreach (Match m in Regex.Matches(text, @"attachments/([0-9A-Za-z]+\.[A-Za-z]+)"))
                {
                    string asset = m.Groups[1].Value;
                    string data = ImageData(entry.Path, "attachments/" + asset);
                    if (data == null) continue;
                    string assets = System.IO.Path.Combine(folder, "attachments");
                    Directory.CreateDirectory(assets);
                    File.WriteAllBytes(System.IO.Path.Combine(assets, asset), Convert.FromBase64String(data.Substring(data.IndexOf(',') + 1)));
                }
                if (entry.HasChildren)
                {
                    string child = System.IO.Path.Combine(folder, name);
                    Directory.CreateDirectory(child);
                    ExportLevel(entry.Children, child);
                }
            }
        }

        // ---- 旧版导入 / Legacy import ----

        private void ImportLegacyFolder(SqliteConnection c, SqliteTransaction tx, string folder, string parent, int depth)
        {
            if (depth > 64) return;
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                string name = System.IO.Path.GetFileName(path);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (directory && name.Equals("attachments", StringComparison.OrdinalIgnoreCase)) { ImportLegacyAssets(c, tx, path); continue; }
                if (directory && depth == 0 && name.Equals("exports", StringComparison.OrdinalIgnoreCase)) continue;
                if (directory) folders[name] = path;
                else if (IsMarkdown(path)) notes[System.IO.Path.GetFileNameWithoutExtension(path)] = path;
            }
            // 同名的目录与笔记合并为一个页面。/ A folder and a note with the same name merge into one page.
            foreach (string title in folders.Keys.Union(notes.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsValidTitle(title)) continue;
                string body = notes.TryGetValue(title, out var note) ? ReadLegacy(note) : "";
                string id = Insert(c, tx, parent, title, NormalizeNewLines(body, "\n"));
                if (folders.TryGetValue(title, out var sub)) ImportLegacyFolder(c, tx, sub, id, depth + 1);
            }
        }

        private static void ImportLegacyAssets(SqliteConnection c, SqliteTransaction tx, string folder)
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (!IsImage(extension) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                byte[] bytes;
                try { bytes = ReadBytes(file); } catch (IOException) { continue; }
                Exec(c, "INSERT OR IGNORE INTO assets(name, page_id, mime, data, created) VALUES(@n, NULL, @m, @d, @c)", tx,
                    ("@n", System.IO.Path.GetFileName(file)), ("@m", Mime(extension)), ("@d", bytes), ("@c", Now()));
            }
        }

        private static string ReadLegacy(string path)
        {
            try
            {
                byte[] bytes = ReadBytes(path);
                using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), true);
                return reader.ReadToEnd();
            }
            catch (IOException) { return ""; }
        }

        // ---- 数据库辅助 / Database helpers ----

        private T Run<T>(Func<SqliteConnection, T> action)
        {
            try
            {
                using var c = new SqliteConnection(_connection);
                c.Open();
                return action(c);
            }
            catch (SqliteException ex) { throw new IOException("笔记数据库错误 / Notebook database error: " + ex.Message, ex); }
        }

        private static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction tx = null, params (string Name, object Value)[] args)
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = tx;
            foreach (var a in args) cmd.Parameters.AddWithValue(a.Name, a.Value ?? DBNull.Value);
            return cmd;
        }

        private static int Exec(SqliteConnection c, string sql, SqliteTransaction tx = null, params (string Name, object Value)[] args)
        {
            using var cmd = Command(c, sql, tx, args);
            return cmd.ExecuteNonQuery();
        }

        private static object Scalar(SqliteConnection c, string sql, SqliteTransaction tx = null, params (string Name, object Value)[] args)
        {
            using var cmd = Command(c, sql, tx, args);
            object value = cmd.ExecuteScalar();
            return value == DBNull.Value ? null : value;
        }

        private static string Insert(SqliteConnection c, SqliteTransaction tx, string parent, string title, string body)
        {
            string id = Guid.NewGuid().ToString("N");
            string now = Now();
            Exec(c, "INSERT INTO pages(id, parent_id, title, body, sort, created, updated, version) VALUES(@id, @p, @t, @b, 0, @n, @n, 1)", tx,
                ("@id", id), ("@p", parent), ("@t", title), ("@b", body), ("@n", now));
            return id;
        }

        private static string ChildId(SqliteConnection c, SqliteTransaction tx, string parent, string title, string except)
        {
            using var cmd = Command(c, parent == null
                ? "SELECT id, title FROM pages WHERE parent_id IS NULL AND deleted_at IS NULL"
                : "SELECT id, title FROM pages WHERE parent_id=@p AND deleted_at IS NULL", tx, ("@p", parent));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (r.GetString(0) != except && string.Equals(r.GetString(1), title, StringComparison.OrdinalIgnoreCase)) return r.GetString(0);
            return null;
        }

        private static string ParentOrNull(string parent)
        {
            if (string.IsNullOrEmpty(parent)) return null;
            ValidateId(parent);
            return parent;
        }

        internal static bool IsPageId(string id) => id != null && IdPattern.IsMatch(id);

        private static void ValidateId(string id)
        {
            if (!IsPageId(id)) throw new ArgumentException("需要有效的页面编号。 / A valid page id is required.");
        }

        private static bool IsValidTitle(string title) => !string.IsNullOrWhiteSpace(title) && title == title.Trim() && title.Length <= MaxTitleLength
            && title.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0;

        private static void ValidateTitle(string title)
        {
            if (!IsValidTitle(title)) throw new ArgumentException("标题无效：不能为空、首尾不能有空白、不能换行，最长 120 字。 / Invalid title: non-empty, no surrounding spaces or line breaks, up to 120 characters.");
        }

        private static string SafeFileName(string title)
        {
            var sb = new StringBuilder();
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            foreach (char ch in title) sb.Append(invalid.Contains(ch) ? '_' : ch);
            string name = sb.ToString().Trim().TrimEnd('.');
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (name.Length == 0 || new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) || Regex.IsMatch(stem, "^(COM|LPT)[0-9]$")) name = "_" + name;
            return name.Length > 80 ? name.Substring(0, 80) : name;
        }

        internal static string NormalizeNewLines(string text, string newLine) =>
            (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", newLine);

        private static byte[] ReadBytes(string path)
        {
            CheckReparse(path);
            if (new FileInfo(path).Length > MaxNoteBytes) throw new IOException("文件超过 4 MB。 / File exceeds 4 MB.");
            return File.ReadAllBytes(path);
        }

        private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        private static bool IsMarkdown(string path) => System.IO.Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);
        private static bool IsImage(string extension) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }.Contains(extension);
        private static string Mime(string extension) => "image/" + (extension == ".jpg" || extension == ".jpeg" ? "jpeg" : extension.TrimStart('.'));
        private static void CheckReparse(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("笔记库不支持符号链接或目录联接。 / Symbolic links and junctions are not supported.");
        }
    }
}