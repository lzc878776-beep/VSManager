using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace VSManager
{
    /// <summary>物理屏幕坐标中的矩形（可序列化）。/ Serializable rectangle in physical screen coordinates.</summary>
    [DataContract]
    public sealed class VsSessionRect
    {
        [DataMember] public int X;
        [DataMember] public int Y;
        [DataMember] public int W;
        [DataMember] public int H;

        public Rectangle ToRectangle() => new Rectangle(X, Y, W, H);

        /// <summary>空矩形返回 null。/ Returns null for an empty rectangle.</summary>
        public static VsSessionRect From(Rectangle r) =>
            r.Width <= 0 || r.Height <= 0 ? null : new VsSessionRect { X = r.X, Y = r.Y, W = r.Width, H = r.Height };

        public override string ToString() => $"({X},{Y},{W}×{H})";
    }

    /// <summary>
    /// 记录中的一个 VS：解决方案路径、显示名称、主窗口位置（最大化时为所在屏工作区）以及浮动工具窗格的位置；
    /// 停靠中的窗格不记录（由 VS 自身的窗口布局管理）。
    /// One recorded VS: solution path, display name, main-window bounds (the screen's work area when maximized) and the bounds
    /// of floating tool panes; docked panes are not recorded (VS manages its own docking layout).
    /// </summary>
    [DataContract]
    public sealed class VsSessionEntry
    {
        [DataMember] public string SolutionPath;
        [DataMember] public string Name;
        [DataMember] public bool Maximized;
        [DataMember(EmitDefaultValue = false)] public VsSessionRect Main;
        [DataMember(EmitDefaultValue = false)] public VsSessionRect Copilot;
        [DataMember(EmitDefaultValue = false)] public VsSessionRect Output;
        [DataMember(EmitDefaultValue = false)] public VsSessionRect ErrorList;
        [DataMember(EmitDefaultValue = false)] public VsSessionRect SolutionExplorer;

        public bool HasSolution => !string.IsNullOrWhiteSpace(SolutionPath);
        public bool HasPanes => Copilot != null || Output != null || ErrorList != null || SolutionExplorer != null;
    }

    /// <summary>
    /// 「记录当前 VS」的结果：保存时间、当时的屏幕状态与各 VS 条目，存于 %APPDATA%\VSManager\vs-session.json。
    /// Result of "record current VS": save time, the display state at that moment and the VS entries, stored in %APPDATA%\VSManager\vs-session.json.
    /// </summary>
    [DataContract]
    public sealed class VsSessionRecord
    {
        [DataMember] public DateTime SavedUtc;
        [DataMember] public string DisplaySignature;
        [DataMember] public List<VsSessionEntry> Items = new List<VsSessionEntry>();
    }

    /// <summary>VS 会话记录的读写与打开计划（纯逻辑，便于测试）。/ Persistence and open planning for VS session records (pure logic, testable).</summary>
    public static class VsSessionStore
    {
        public static string FilePath => Path.Combine(AppPaths.DataFolder, "vs-session.json");

        /// <summary>保存记录；成功返回 null，否则返回错误。/ Saves the record; null on success, otherwise the error.</summary>
        public static string Save(VsSessionRecord record, string path = null)
        {
            if (record == null) return "记录为空 / Empty record";
            path = path ?? FilePath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var r = AtomicFile.Write(path, stream =>
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                        new DataContractJsonSerializer(typeof(VsSessionRecord)).WriteObject(w, record);
                }, backupBeforeOverwrite: false, skipFallbackOnSerializationError: true);
                return r.Ok ? null : r.Error.GetType().Name + "：" + r.Error.Message;
            }
            catch (Exception ex) { return ex.GetType().Name + "：" + ex.Message; }
        }

        /// <summary>读取记录；不存在或损坏时返回 null。/ Loads the record; null when missing or corrupt.</summary>
        public static VsSessionRecord Load(string path = null)
        {
            path = path ?? FilePath;
            try
            {
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Skip(3).ToArray();
                using (var r = JsonReaderWriterFactory.CreateJsonReader(data, XmlDictionaryReaderQuotas.Max))
                {
                    var record = (VsSessionRecord)new DataContractJsonSerializer(typeof(VsSessionRecord)).ReadObject(r);
                    if (record != null && record.Items == null) record.Items = new List<VsSessionEntry>();
                    return record;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 为每个条目找到已打开的实例下标（同一解决方案开了多个时按顺序一一对应），未打开或没有解决方案时为 -1。
        /// Finds the open-instance index for each entry (several copies of a solution pair up in order); -1 when not open or without a solution.
        /// </summary>
        public static int[] MatchOpen(IList<VsSessionEntry> entries, IList<string> openSolutionPaths)
        {
            var result = new int[entries?.Count ?? 0];
            var used = new HashSet<int>();
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = -1;
                var e = entries[i];
                if (e == null || !e.HasSolution || openSolutionPaths == null) continue;
                for (int j = 0; j < openSolutionPaths.Count; j++)
                {
                    if (used.Contains(j) || !SolutionMatcher.SamePath(openSolutionPaths[j], e.SolutionPath)) continue;
                    used.Add(j);
                    result[i] = j;
                    break;
                }
            }
            return result;
        }

        /// <summary>需要启动的条目下标：有解决方案、尚未打开且文件存在判断交给调用方。/ Indexes of entries to launch: with a solution and not open yet.</summary>
        public static List<int> ToLaunch(IList<VsSessionEntry> entries, IList<string> openSolutionPaths)
        {
            var match = MatchOpen(entries, openSolutionPaths);
            return Enumerable.Range(0, match.Length).Where(i => match[i] < 0 && entries[i] != null && entries[i].HasSolution).ToList();
        }

        /// <summary>记录的简短说明。/ Short description of a record.</summary>
        public static string Describe(VsSessionRecord record)
        {
            if (record == null || record.Items.Count == 0) return "没有记录 / No record";
            var lines = new List<string> { $"记录于 / Recorded at {record.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}，共 {record.Items.Count} 个 VS / {record.Items.Count} VS" };
            for (int i = 0; i < record.Items.Count; i++)
            {
                var e = record.Items[i];
                var panes = new List<string>();
                if (e.Copilot != null) panes.Add("Copilot" + e.Copilot);
                if (e.Output != null) panes.Add("Output" + e.Output);
                if (e.ErrorList != null) panes.Add("ErrorList" + e.ErrorList);
                if (e.SolutionExplorer != null) panes.Add("SolutionExplorer" + e.SolutionExplorer);
                lines.Add($"#{i + 1} {e.Name}：" + (e.HasSolution ? Path.GetFileName(e.SolutionPath) : "无解决方案 / no solution")
                    + (e.Main == null ? "；主窗口未记录 / main window not recorded" : (e.Maximized ? "；最大化 / maximized " : "；主窗口 / main ") + e.Main)
                    + (panes.Count > 0 ? "；浮动窗格 / floating panes: " + string.Join(", ", panes) : ""));
            }
            return string.Join("\n", lines);
        }
    }
}
