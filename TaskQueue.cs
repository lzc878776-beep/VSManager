using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VSManager
{
	public static class QueueStatus
	{
		public const string Waiting = "waiting", Sending = "sending", Running = "running", Done = "done", Failed = "failed", Cancelled = "cancelled";
		public static bool Active(string s) => s == Waiting || s == Sending || s == Running;
		public static bool Known(string s) => Active(s) || s == Done || s == Failed || s == Cancelled;
	}

	/// <summary>任务清单中的一项：发给某个 VS Copilot 的任务，目标忙碌时排队，空闲后自动发布。</summary>
	[DataContract]
	public sealed class QueuedTask
	{
		[DataMember] public int Id;
		[DataMember] public string VsKey;
		[DataMember] public string VsName;
		[DataMember] public string Text;
		[DataMember] public string Source;
		[DataMember] public string Status;
		[DataMember] public DateTime Created;
		[DataMember] public DateTime? Started;
		[DataMember] public DateTime? Finished;
		[DataMember] public string Result;
		[DataMember] public string Error;
		[DataMember] public int Attempts;

		/// <summary>运行期：执行中是否观察到 Copilot 忙碌；下次重试时间。</summary>
		[IgnoreDataMember] public bool SawBusy;
		[IgnoreDataMember] public DateTime NextTry;

		public bool FromAgent => Source == "AI";
	}

	/// <summary>
	/// 任务清单：持久化到 %APPDATA%\VSManager\tasks.json（上一版本保存在 tasks.json.bak），只在界面线程修改。
	/// 默认保留全部历史；保存失败时保留内存中的清单、记录日志并由界面提示，之后自动重试。
	/// </summary>
	public sealed class TaskQueue
	{
		private readonly List<QueuedTask> _items = new List<QueuedTask>();
		private readonly AppSettings _settings;
		private readonly object _saveLock = new object();
		private int _nextId = 1;
		private bool _dirty;
		private DateTime _lastSaveAttempt;
		/// <summary>上次归档时各任务的状态，用于在 Commit 时找出新增 / 变化 / 移除的任务。</summary>
		private readonly Dictionary<int, QueuedTask> _archived = new Dictionary<int, QueuedTask>();

		public event Action Changed;

		public static string FilePath => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath), "tasks.json");
		public static string LogPath => Path.Combine(Archive.AppLogFolder, "tasks.log");

		public IReadOnlyList<QueuedTask> Items => _items;

		/// <summary>最近一次保存失败的原因；保存成功后为 null。</summary>
		public string SaveError { get; private set; }

		/// <summary>启动时读取任务记录遇到的问题（文件损坏、部分记录无法识别等），没有问题时为 null。</summary>
		public string LoadWarning { get; private set; }

		public TaskQueue(AppSettings settings)
		{
			_settings = settings;
			Load();
			foreach (var t in _items) _archived[t.Id] = Copy(t);
			AppDomain.CurrentDomain.ProcessExit += (s, e) => { if (_dirty) Save(); };
		}

		#region 读取

		private void Load()
		{
			var problems = new List<string>();
			string main = FilePath, bak = FilePath + ".bak";
			string mainProblem = null;
			bool mainExists = File.Exists(main);
			if (mainExists) _items.AddRange(ReadTolerant(main, problems, out mainProblem));

			// 主文件缺失或读取不完整：从备份中补回主文件中没有的任务
			if ((!mainExists || mainProblem != null) && File.Exists(bak))
			{
				var ids = new HashSet<int>(_items.Select(t => t.Id));
				var fromBak = ReadTolerant(bak, problems, out _).Where(t => t.Id == 0 || !ids.Contains(t.Id)).ToList();
				if (fromBak.Count > 0)
				{
					_items.AddRange(fromBak);
					problems.Add("已从备份 tasks.json.bak 恢复 " + fromBak.Count + " 条任务");
				}
			}
			if (mainProblem != null)
			{
				problems.Insert(0, "tasks.json " + mainProblem);
				// 保留损坏的原文件，避免下次保存时被覆盖而无法人工找回
				try { File.Copy(main, Path.Combine(Path.GetDirectoryName(main), "tasks.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true); } catch { }
			}

			foreach (var t in _items)
				if (t.Status == QueueStatus.Sending) t.Status = QueueStatus.Waiting; // 上次退出时正在发送：重新排队

			// 编号去重：缺失或重复的编号重新分配，保证之后新建的任务编号不与历史重复
			int max = Math.Max(_items.Count == 0 ? 0 : _items.Max(t => t.Id), Math.Max(0, _settings.TaskNextId - 1));
			var seen = new HashSet<int>();
			int fixedIds = 0;
			foreach (var t in _items.OrderBy(t => t.Created))
				if (t.Id <= 0 || !seen.Add(t.Id)) { t.Id = ++max; seen.Add(t.Id); fixedIds++; }
			if (fixedIds > 0) problems.Add(fixedIds + " 条任务的编号缺失或重复，已重新编号");
			_nextId = max + 1;
			_items.Sort((a, b) => a.Id.CompareTo(b.Id));

			if (problems.Count > 0)
			{
				LoadWarning = string.Join("；", problems);
				Log("读取任务记录：" + LoadWarning + "（共恢复 " + _items.Count + " 条）");
				_dirty = true;
			}
		}

		/// <summary>尽量多地读取任务：单条记录字段缺失或类型不符只影响该条（或该字段），文件截断时保留截断前的记录。</summary>
		private static List<QueuedTask> ReadTolerant(string path, List<string> problems, out string problem)
		{
			var list = new List<QueuedTask>();
			problem = null;
			int bad = 0;
			try
			{
				byte[] data = File.ReadAllBytes(path);
				if (data.Length == 0) { problem = "为空文件"; return list; }
				// 手工编辑（如记事本）可能加上 UTF-8 BOM，JSON 读取器不接受
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

		private static QueuedTask ParseTask(XElement e)
		{
			string S(string name) => e.Element(name) is XElement x && x.Attribute("type")?.Value != "null" ? x.Value : null;
			int I(string name) => int.TryParse(S(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

			var t = new QueuedTask
			{
				Id = I("Id"), VsKey = S("VsKey"), VsName = S("VsName"), Text = S("Text"), Source = S("Source"),
				Status = S("Status"), Started = ParseDate(S("Started")), Finished = ParseDate(S("Finished")),
				Result = S("Result"), Error = S("Error"), Attempts = Math.Max(0, I("Attempts"))
			};
			if (string.IsNullOrWhiteSpace(t.Text)) return null;
			t.Created = ParseDate(S("Created")) ?? t.Started ?? t.Finished ?? DateTime.Now;
			if (string.IsNullOrEmpty(t.VsName)) t.VsName = string.IsNullOrEmpty(t.VsKey) ? "（未知 VS）" : Path.GetFileNameWithoutExtension(t.VsKey);
			if (t.VsKey == null) t.VsKey = "";
			if (!QueueStatus.Known(t.Status))
			{
				// 状态无法识别时不自动发布，避免误执行；用户可右键「重新排队」
				t.Error = "记录中的状态「" + (t.Status ?? "缺失") + "」无法识别" + (string.IsNullOrEmpty(t.Error) ? "" : "；" + t.Error);
				t.Status = QueueStatus.Cancelled;
				if (!t.Finished.HasValue) t.Finished = t.Created;
			}
			if (!QueueStatus.Active(t.Status) && !t.Finished.HasValue) t.Finished = t.Started ?? t.Created;
			return t;
		}

		/// <summary>兼容 DataContractJsonSerializer 的 /Date(毫秒+时区)/ 与 ISO 8601 两种格式。</summary>
		private static DateTime? ParseDate(string s)
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

		#endregion

		public QueuedTask Find(int id) => _items.FirstOrDefault(t => t.Id == id);

		public QueuedTask Add(string vsKey, string vsName, string text, string source)
		{
			var t = new QueuedTask
			{
				Id = _nextId++, VsKey = vsKey, VsName = vsName, Text = text, Source = source,
				Status = QueueStatus.Waiting, Created = DateTime.Now
			};
			_items.Add(t);
			// 编号计数同时记在设置中，清除历史后新任务也不会复用旧编号
			_settings.TaskNextId = _nextId;
			_settings.Save();
			Commit();
			return t;
		}

		/// <summary>同一 VS 中排在该任务前面的未完成任务数。</summary>
		public int Ahead(QueuedTask task) =>
			_items.Count(t => t != task && t.VsKey == task.VsKey && QueueStatus.Active(t.Status) && (t.Status != QueueStatus.Waiting || t.Id < task.Id));

		public bool Remove(int id)
		{
			var t = Find(id);
			if (t == null || t.Status == QueueStatus.Sending) return false;
			_items.Remove(t);
			Commit();
			return true;
		}

		public void Commit()
		{
			// 历史上限：TaskHistoryLimit <= 0 表示保留全部（默认）
			int limit = _settings.TaskHistoryLimit;
			if (limit > 0)
			{
				var old = _items.Where(t => !QueueStatus.Active(t.Status)).OrderByDescending(t => t.Finished ?? t.Created).Skip(limit).ToList();
				foreach (var t in old) _items.Remove(t);
			}
			ArchiveChanges();
			_dirty = true;
			Save();
			Changed?.Invoke();
		}

		/// <summary>把自上次提交以来新增、状态变化、被移除的任务追加到任务流水归档。</summary>
		private void ArchiveChanges()
		{
			try
			{
				var alive = new HashSet<int>();
				foreach (var t in _items)
				{
					alive.Add(t.Id);
					_archived.TryGetValue(t.Id, out var prev);
					string evt;
					if (prev == null) evt = "created";
					else if (prev.Status == t.Status && prev.Attempts == t.Attempts && prev.Result == t.Result && prev.Error == t.Error && prev.Text == t.Text && prev.VsKey == t.VsKey) continue;
					else if (t.Status == QueueStatus.Waiting && prev.Status != QueueStatus.Waiting) evt = "retry";
					else if (prev.Status == t.Status) evt = "update";
					else evt = t.Status;
					Archive.TaskEvent(t, evt);
					_archived[t.Id] = Copy(t);
				}
				foreach (var id in _archived.Keys.Where(k => !alive.Contains(k)).ToList())
				{
					Archive.TaskEvent(_archived[id], "removed");
					_archived.Remove(id);
				}
			}
			catch (Exception ex) { Log("归档任务流水失败：" + ex.Message); }
		}

		private static QueuedTask Copy(QueuedTask t) => new QueuedTask
		{
			Id = t.Id, VsKey = t.VsKey, VsName = t.VsName, Text = t.Text, Source = t.Source, Status = t.Status, Created = t.Created,
			Started = t.Started, Finished = t.Finished, Result = t.Result, Error = t.Error, Attempts = t.Attempts
		};

		/// <summary>保存失败后定期重试（由界面计时器调用）。</summary>
		public void RetrySaveIfNeeded()
		{
			if (!_dirty || SaveError == null || (DateTime.Now - _lastSaveAttempt).TotalSeconds < 10) return;
			if (Save()) Changed?.Invoke();
		}

		/// <summary>立即写盘（退出程序时调用）。失败不会清空内存中的清单。</summary>
		public bool Save()
		{
			string error = null;
			lock (_saveLock)
			{
				_lastSaveAttempt = DateTime.Now;
				string tmp = FilePath + ".tmp";
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
					var snapshot = _items.ToList();
					using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
					{
						using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true))
							new DataContractJsonSerializer(typeof(List<QueuedTask>)).WriteObject(w, snapshot);
						fs.Flush(true);
					}
					if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", true);
					else File.Move(tmp, FilePath);
				}
				catch (Exception ex)
				{
					error = ex.GetType().Name + "：" + ex.Message;
					try
					{
						// 替换失败（如被杀毒软件占用）时退回直接覆盖；临时文件不完整时不覆盖主文件
						if (File.Exists(tmp) && !(ex is SerializationException))
						{
							if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
							File.Copy(tmp, FilePath, true);
							File.Delete(tmp);
							error = null;
						}
					}
					catch (Exception ex2) { error += "；直接覆盖也失败：" + ex2.Message; }
				}
				bool wasFailing = SaveError != null;
				SaveError = error;
				if (error == null)
				{
					_dirty = false;
					if (wasFailing) Log("保存已恢复正常（" + _items.Count + " 条）");
				}
				else if (!wasFailing) Log("保存任务清单失败：" + error + "（内存中的 " + _items.Count + " 条任务已保留，将自动重试）");
			}
			return error == null;
		}

		private static void Log(string text)
		{
			try
			{
				Directory.CreateDirectory(Archive.AppLogFolder);
				File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + "\r\n", Encoding.UTF8);
			}
			catch { }
		}
	}
}
