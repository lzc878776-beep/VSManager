using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace VSManager
{
    /// <summary>
    /// 解析 AI 给出的自定义布局 JSON。格式：数组（或 {"windows":[...]}），每项
    /// {"vs":"1","main":{"screen":1,"x":0,"y":0,"w":65,"h":100},"copilot":{...},"output":{...},"errorList":{...},"solutionExplorer":{...}}；
    /// 矩形也可写成 [screen,x,y,w,h]；x/y/w/h 为该屏工作区的百分比（0–100）。
    /// Parses the custom layout JSON produced by the AI. Format: an array (or {"windows":[...]}) of
    /// {"vs":"1","main":{"screen":1,"x":0,"y":0,"w":65,"h":100},"copilot":{...},"output":{...},"errorList":{...},"solutionExplorer":{...}};
    /// a rectangle may also be [screen,x,y,w,h]; x/y/w/h are percentages (0–100) of that screen's work area.
    /// </summary>
    public static class WorkspaceCustomLayoutParser
    {
        public static List<WorkspaceCustomPlacement> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("布局为空 / Layout is empty");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException ex) { throw new ArgumentException("布局不是有效 JSON / Layout is not valid JSON: " + ex.Message); }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && TryGet(root, "windows", out var windows)) root = windows;
                if (root.ValueKind != JsonValueKind.Array) throw new ArgumentException("布局应为数组 / Layout must be an array");
                var list = new List<WorkspaceCustomPlacement>();
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("每项应为对象 / Each item must be an object");
                    if (!TryGet(item, "vs", out var vs)) throw new ArgumentException("缺少 vs / missing vs");
                    string vsRef = vs.ValueKind == JsonValueKind.Number ? vs.GetRawText() : vs.ValueKind == JsonValueKind.String ? vs.GetString() : null;
                    if (string.IsNullOrWhiteSpace(vsRef)) throw new ArgumentException("vs 应为编号或名称 / vs must be a number or name");
                    list.Add(new WorkspaceCustomPlacement
                    {
                        VsRef = vsRef.Trim(),
                        Main = Rect(item, "main"),
                        Copilot = Rect(item, "copilot"),
                        Output = Rect(item, "output"),
                        ErrorList = Rect(item, "errorList"),
                        SolutionExplorer = Rect(item, "solutionExplorer"),
                    });
                }
                if (list.Count == 0) throw new ArgumentException("布局为空 / Layout is empty");
                return list;
            }
        }

        private static WorkspaceRectSpec Rect(JsonElement item, string name)
        {
            if (!TryGet(item, name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            try
            {
                if (e.ValueKind == JsonValueKind.Array)
                {
                    if (e.GetArrayLength() != 5) throw new ArgumentException();
                    return new WorkspaceRectSpec { Screen = (int)Num(e[0]), X = Num(e[1]), Y = Num(e[2]), W = Num(e[3]), H = Num(e[4]) };
                }
                if (e.ValueKind == JsonValueKind.Object)
                    return new WorkspaceRectSpec { Screen = (int)Num(Get(e, "screen")), X = Num(Get(e, "x")), Y = Num(Get(e, "y")), W = Num(Get(e, "w", "width")), H = Num(Get(e, "h", "height")) };
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException || ex is KeyNotFoundException) { }
            throw new ArgumentException(name + " 应为 {screen,x,y,w,h} 或 [screen,x,y,w,h] / " + name + " must be {screen,x,y,w,h} or [screen,x,y,w,h]");
        }

        private static double Num(JsonElement e) =>
            e.ValueKind == JsonValueKind.Number ? e.GetDouble()
            : e.ValueKind == JsonValueKind.String ? double.Parse(e.GetString().Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture)
            : throw new FormatException();

        private static JsonElement Get(JsonElement e, params string[] names)
        {
            foreach (var n in names) if (TryGet(e, n, out var v)) return v;
            throw new KeyNotFoundException();
        }

        private static bool TryGet(JsonElement e, string name, out JsonElement value)
        {
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
            value = default;
            return false;
        }
    }
}
