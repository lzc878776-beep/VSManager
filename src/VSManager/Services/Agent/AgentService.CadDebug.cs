using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 记录 CAD 调试图纸的宿主能力（按解决方案保存）。/ Host capability that records the CAD debug drawing (stored per solution).
    /// </summary>
    public interface IAgentCadDebugHost
    {
        /// <summary>读取已记录的图纸路径，没有时返回 null。/ Returns the recorded drawing path, or null.</summary>
        string GetCadDrawing(VsInstance v);
        /// <summary>记录或清除（drawing 为空）图纸并保存设置，返回结果文字。/ Records or clears (empty drawing) the drawing, saves settings and returns the result text.</summary>
        Task<string> SetCadDrawing(VsInstance v, string drawing);
    }

    public sealed partial class AgentService
    {
        /// <summary>步骤文字中显示的图纸名：附件编号原样显示，路径只显示文件名。/ Drawing label for step text: attachment ids as-is, paths by file name only.</summary>
        private static string CadDrawingLabel(string drawing)
        {
            string d = (drawing ?? "").Trim().Trim('"');
            try { return d.IndexOfAny(new[] { '\\', '/' }) >= 0 ? Path.GetFileName(d) : d; }
            catch (ArgumentException) { return d; }
        }

        /// <summary>把工具参数解析为图纸完整路径：文件路径或用户附件编号（"last" 为最近一条消息中的图纸附件）。/ Resolves the argument to a full drawing path: a file path or a user attachment id ("last" = the drawing attachment of the latest message).</summary>
        internal string ResolveCadDrawing(string drawing, out string error)
        {
            error = null;
            string d = Environment.ExpandEnvironmentVariables((drawing ?? "").Trim().Trim('"').Trim());
            if (d.IndexOfAny(new[] { '\\', '/' }) < 0 && !CadDebugPlan.IsDrawingPath(d))
            {
                var files = ResolveTaskAttachments(d, out string attachmentError);
                if (attachmentError != null) { error = attachmentError; return null; }
                var hit = files.FirstOrDefault(a => CadDebugPlan.IsDrawingPath(a.Name));
                if (hit == null) { error = "附件中没有 .dwg / .dxf 图纸 / No .dwg / .dxf drawing among the attachments"; return null; }
                d = AttachmentStore.FullPath(hit);
                if (d == null) { error = "图纸附件不可用 / The drawing attachment is unavailable"; return null; }
            }
            string full;
            try { full = Path.GetFullPath(d); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                error = "图纸路径无效 / Invalid drawing path"; return null;
            }
            if (!CadDebugPlan.IsDrawingPath(full)) { error = "只支持 .dwg / .dxf 图纸（路径不能含引号）/ Only .dwg / .dxf drawings are supported (no quotes in the path)"; return null; }
            if (!File.Exists(full)) { error = "找不到图纸文件 / Drawing file not found：" + Path.GetFileName(full); return null; }
            return full;
        }

        [Description("记录指定 VS（按其解决方案保存）的 CAD 调试图纸：之后点击调试且启动程序为 acad.exe 等 CAD 时，CAD 启动会打开这张图纸并自动 NETLOAD 插件；找不到图纸时自动改为打开新图。用户发来图纸路径或 .dwg / .dxf 附件并表示用于调试时直接调用，不必反问。drawing 为空表示清除。/ Records the CAD debug drawing of a VS (stored per solution). When debugging later starts a CAD host such as acad.exe, the drawing is opened and the plug-in NETLOADed; if the drawing is missing a new drawing is used. Call directly when the user sends a drawing path or .dwg / .dxf attachment for debugging. Empty drawing clears it.")]
        private async Task<string> SetCadDebugDrawing(
            [Description("VS 编号（如 \"1\"）或名称 / VS number (such as \"1\") or name")] string vs,
            [Description("图纸完整路径（.dwg / .dxf），或用户附件编号，\"last\" 表示用户最近一条消息中的图纸附件；空字符串表示清除。/ Full drawing path (.dwg / .dxf), or a user attachment id, \"last\" for the drawing attached to the latest user message; empty clears it.")] string drawing)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            if (!(_host is IAgentCadDebugHost cad)) return "当前环境不支持记录 CAD 调试图纸 / Recording CAD debug drawings is not supported here";
            if (string.IsNullOrEmpty(v.SolutionPath)) return "「" + _host.NameOf(v) + "」尚未打开解决方案，无法记录调试图纸 / This VS has no solution open, so no drawing can be recorded";
            string path = null;
            if (!string.IsNullOrWhiteSpace(drawing))
            {
                path = ResolveCadDrawing(drawing, out string error);
                if (path == null) return error;
            }
            string title = path == null ? "清除「" + _host.NameOf(v) + "」的 CAD 调试图纸" : "记录「" + _host.NameOf(v) + "」的 CAD 调试图纸";
            if (_settings().AgentConfirm && !await ConfirmAsync(title, path ?? "调试时改为打开新图 / Debugging will open a new drawing"))
                return "用户拒绝了该操作。";
            return await cad.SetCadDrawing(v, path);
        }

        [Description("查看指定 VS 已记录的 CAD 调试图纸及文件是否存在（只读）。/ Shows the recorded CAD debug drawing of a VS and whether the file exists (read-only).")]
        private string GetCadDebugDrawing(
            [Description("VS 编号（如 \"1\"）或名称 / VS number (such as \"1\") or name")] string vs)
        {
            if (!Resolve(vs, out var v, out var err)) return err;
            string d = (_host as IAgentCadDebugHost)?.GetCadDrawing(v);
            if (string.IsNullOrWhiteSpace(d)) return "「" + _host.NameOf(v) + "」未记录调试图纸，CAD 调试时打开新图 / No debug drawing recorded; CAD debugging opens a new drawing";
            bool exists;
            try { exists = File.Exists(Environment.ExpandEnvironmentVariables(d)); } catch { exists = false; }
            return "「" + _host.NameOf(v) + "」的调试图纸 / Debug drawing: " + d
                + (exists ? "" : "（文件不存在，调试时将打开新图 / file missing, a new drawing will be opened）");
        }
    }
}
