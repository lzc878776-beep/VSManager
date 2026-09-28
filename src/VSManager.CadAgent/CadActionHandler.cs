using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace VSManager.CadAgent
{
    /// <summary>CAD 侧动作接口。/ CAD-side action interface.</summary>
    public interface ICadActionHandler
    {
        CadActionResult Execute(CadActionRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 适配包 actions.cs 可实现的动作扩展：返回 true 表示已处理（可新增或覆盖动作）。
    /// Action extension an adapter's actions.cs may implement: return true when handled (adds or overrides actions).
    /// </summary>
    public interface ICadActionOverride
    {
        bool TryExecute(CadActionRequest request, CadActionContext context, out CadActionResult result);
    }

    /// <summary>传给动作扩展的上下文。/ Context passed to action extensions.</summary>
    public sealed class CadActionContext
    {
        internal CadActionHandlerBase Handler;
        public CadAdapter Adapter => Handler.Adapter;
        /// <summary>在 CAD 主线程执行并等待结果。/ Runs on the CAD main thread and waits for the result.</summary>
        public T OnMainThread<T>(Func<T> f, int timeoutMs) => Handler.OnUi(f, timeoutMs);
    }

    /// <summary>
    /// 动作执行基类：参数校验、截图 / 日志（后台线程）与通用规则；CAD API 调用由引导 DLL 在主线程实现。
    /// Action execution base: argument checks, screenshots / logs (background thread) and shared rules; CAD API calls are implemented by the boot DLL on the main thread.
    /// </summary>
    public abstract class CadActionHandlerBase : ICadActionHandler
    {
        private readonly ISynchronizeInvoke _ui;
        private readonly List<ICadActionOverride> _overrides = new List<ICadActionOverride>();

        protected CadActionHandlerBase(ISynchronizeInvoke ui) { _ui = ui ?? throw new ArgumentNullException(nameof(ui)); }

        public CadAdapter Adapter { get; set; }

        public void AddOverride(ICadActionOverride o) { if (o != null) _overrides.Add(o); }

        /// <summary>在主线程执行；超时抛出 TimeoutException（已排队的调用仍可能稍后执行）。/ Runs on the main thread; throws TimeoutException on timeout (the queued call may still run later).</summary>
        public T OnUi<T>(Func<T> f, int timeoutMs)
        {
            if (!_ui.InvokeRequired) return f();
            var r = _ui.BeginInvoke(f, null);
            if (!r.AsyncWaitHandle.WaitOne(Math.Max(100, timeoutMs)))
                throw new TimeoutException("CAD 主线程忙，未在时限内响应 / The CAD main thread did not respond in time");
            return (T)_ui.EndInvoke(r);
        }

        // ---- 由引导 DLL 实现的 CAD API（均在主线程调用）/ CAD API implemented by the boot DLL (always called on the main thread) ----
        /// <summary>已打开时切换过去并返回 true。/ Switches to the drawing and returns true when it is already open.</summary>
        protected abstract bool ActivateOpen(string path);
        protected abstract string OpenDrawing(string path, bool readOnly);
        protected abstract string NewDrawing(string template);
        /// <summary>按名称、完整路径或序号（从 1 开始）切换；找不到返回 null。/ Switches by name, full path or 1-based index; null when not found.</summary>
        protected abstract string SwitchDrawing(string name);
        protected abstract int CloseAllDrawings();
        protected abstract string ListDrawings();
        protected abstract bool HasActiveDrawing();
        protected abstract void SendCommand(string command);
        protected abstract bool CommandActive();
        protected abstract string GetSystemVariable(string name);
        /// <summary>模型空间实体数：返回总数，breakdown 为按类型统计。/ Model-space entity count: returns the total, breakdown is per type.</summary>
        protected abstract int CountEntities(string dxfType, string layer, out string breakdown);
        protected virtual string DefaultTemplate => "";

        public CadActionResult Execute(CadActionRequest request, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            CadActionResult result;
            try { result = Dispatch(request, cancellationToken); }
            catch (TimeoutException ex) { result = CadActionResult.Fail(CadErrors.Timeout, ex.Message); }
            catch (OperationCanceledException) { result = CadActionResult.Fail(CadErrors.Cancelled, "已取消 / Cancelled"); }
            catch (Exception ex)
            {
                var e = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                result = CadActionResult.Fail(CadErrors.ActionFailed, e.GetType().Name + ": " + e.Message);
            }
            result = result ?? CadActionResult.Fail(CadErrors.ActionFailed, "动作没有返回结果 / No result");
            result.Id = request?.Id;
            result.Action = request?.Action;
            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }

        private CadActionResult Dispatch(CadActionRequest req, CancellationToken ct)
        {
            if (req == null) return CadActionResult.Fail(CadErrors.InvalidArgs, "空请求 / Empty request");
            var context = new CadActionContext { Handler = this };
            foreach (var o in _overrides)
                if (o.TryExecute(req, context, out var custom)) return custom;
            string action = CadActions.Normalize(req.Action);
            if (action == null) return CadActionResult.Fail(CadErrors.UnknownAction, "未知动作 / Unknown action: " + req.Action);
            int timeout = req.EffectiveTimeoutMs;
            switch (action)
            {
                case CadActions.OpenDrawing: return Open(req, timeout);
                case CadActions.SwitchDrawing:
                {
                    string name = req.Arg("name") ?? req.Arg("path");
                    if (name == null) return CadActionResult.Fail(CadErrors.InvalidArgs, "缺少 args.name / args.name is required");
                    string hit = OnUi(() => SwitchDrawing(name), timeout);
                    return hit == null
                        ? CadActionResult.Fail(CadErrors.NotFound, "没有打开名为「" + name + "」的图纸 / Drawing not open. " + OnUi(ListDrawings, timeout))
                        : CadActionResult.Success("已切换到 / Switched to " + hit);
                }
                case CadActions.CloseAllDrawings:
                {
                    if (!req.Flag("discard"))
                        return CadActionResult.Fail(CadErrors.InvalidArgs, "关闭全部图纸会丢弃未保存修改，需传 args.discard=true / Closing all drawings discards unsaved changes; pass args.discard=true");
                    int n = OnUi(CloseAllDrawings, timeout);
                    return CadActionResult.Success("已关闭 " + n + " 张图纸（未保存修改已丢弃）/ Closed " + n + " drawing(s), unsaved changes discarded");
                }
                case CadActions.RunCommand: return Run(req, timeout, ct);
                case CadActions.GetParam:
                {
                    string names = req.Arg("name");
                    if (names == null) return CadActionResult.Fail(CadErrors.InvalidArgs, "缺少 args.name（系统变量，逗号分隔）/ args.name is required (system variables, comma-separated)");
                    var lines = new List<string>();
                    foreach (var n in names.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Take(20))
                    {
                        string v;
                        try { v = OnUi(() => GetSystemVariable(n), timeout); }
                        catch (TimeoutException) { throw; }
                        catch (Exception ex) { v = "<错误 / error: " + ex.Message + ">"; }
                        lines.Add(n + " = " + v);
                    }
                    return CadActionResult.Success(string.Join("; ", lines), new CadArtifact { Kind = CadArtifact.Text, Name = "params", Content = string.Join("\n", lines) });
                }
                case CadActions.Screenshot:
                {
                    var shot = CadCapture.CaptureMainWindow(Math.Max(320, Math.Min(3840, req.Number("maxWidth", 1920))));
                    return CadActionResult.Success("已截取 CAD 窗口 / Captured the CAD window (" + shot.Width + "×" + shot.Height + ")", shot);
                }
                case CadActions.GetLog: return Log(req);
                case CadActions.GetEntityCount:
                {
                    if (!OnUi(HasActiveDrawing, timeout)) return CadActionResult.Fail(CadErrors.NotFound, "没有打开的图纸 / No drawing is open");
                    string type = req.Arg("type"), layer = req.Arg("layer");
                    string breakdown = null;
                    int count = OnUi(() => CountEntities(type, layer, out breakdown), timeout);
                    string filter = (type != null ? " type=" + type : "") + (layer != null ? " layer=" + layer : "");
                    return CadActionResult.Success("模型空间实体数 / Model-space entities" + filter + ": " + count,
                        new CadArtifact { Kind = CadArtifact.Text, Name = "entities", Content = "count=" + count + (string.IsNullOrEmpty(breakdown) ? "" : "\n" + breakdown) });
                }
            }
            return CadActionResult.Fail(CadErrors.UnknownAction, "未实现的动作 / Action not implemented: " + action);
        }

        /// <summary>
        /// 打开图纸：已打开则切换；path 为空或文件不存在时按规则打开新图，并在 message 中说明。
        /// Opens a drawing: switches when already open; with no path or a missing file a new drawing is opened by rule and the message says so.
        /// </summary>
        private CadActionResult Open(CadActionRequest req, int timeout)
        {
            string path = req.Arg("path");
            string template = req.Arg("template") ?? DefaultTemplate;
            string full = null;
            if (path != null)
            {
                try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"'))); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { full = null; }
            }
            if (full != null && File.Exists(full))
            {
                string ext = Path.GetExtension(full);
                if (!ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dxf", StringComparison.OrdinalIgnoreCase))
                    return CadActionResult.Fail(CadErrors.InvalidArgs, "只能打开 .dwg / .dxf 图纸 / Only .dwg / .dxf drawings can be opened");
                if (OnUi(() => ActivateOpen(full), timeout)) return CadActionResult.Success("图纸已打开，已切换过去 / Drawing already open; switched to " + Path.GetFileName(full));
                bool readOnly = req.Flag("readOnly");
                string opened = OnUi(() => OpenDrawing(full, readOnly), timeout);
                return CadActionResult.Success("已打开图纸 / Opened " + Path.GetFileName(opened ?? full));
            }
            string created = OnUi(() => NewDrawing(template), timeout);
            string why = path == null
                ? "未指定且未记录调试图纸，已按规则打开新图 / No drawing given or recorded; opened a new drawing by rule"
                : "未找到图纸「" + SafeName(path) + "」，已按规则打开新图 / Drawing not found; opened a new drawing by rule";
            return new CadActionResult
            {
                Ok = true, Message = why + "（" + (created ?? "new") + "）",
                Artifacts = new List<CadArtifact> { new CadArtifact { Kind = CadArtifact.Text, Name = "newDrawing", Content = "newDrawing=true" + (path == null ? "" : "\nmissing=" + SafeName(path)) } },
            };
        }

        private static string SafeName(string path)
        {
            try { return Path.GetFileName(path.Trim('"')); } catch (ArgumentException) { return path; }
        }

        private CadActionResult Run(CadActionRequest req, int timeout, CancellationToken ct)
        {
            string input = req.Arg("command");
            string command;
            if (Adapter != null)
            {
                command = Adapter.ResolveCommand(input, out string error);
                if (command == null) return CadActionResult.Fail(CadErrors.InvalidArgs, error);
            }
            else
            {
                command = input;
                if (string.IsNullOrWhiteSpace(command)) return CadActionResult.Fail(CadErrors.InvalidArgs, "缺少 args.command / args.command is required");
            }
            if (!OnUi(HasActiveDrawing, timeout)) return CadActionResult.Fail(CadErrors.NotFound, "没有打开的图纸，无法执行命令 / No drawing is open, cannot run a command");
            if (OnUi(CommandActive, timeout)) return CadActionResult.Fail(CadErrors.Busy, "CAD 正在执行其他命令 / CAD is running another command");
            var sw = Stopwatch.StartNew();
            OnUi(() => { SendCommand(command); return true; }, timeout);
            if (!req.Flag("wait", true)) return CadActionResult.Success("已发送命令（不等待结束）/ Command sent without waiting: " + command);
            int idle = 0;
            Thread.Sleep(Math.Max(0, Math.Min(5000, req.Number("settleMs", 500))));
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int left = timeout - (int)sw.ElapsedMilliseconds;
                if (left <= 0) return CadActionResult.Fail(CadErrors.Timeout, "命令在时限内未结束（可能停在对话框或提示处）/ Command did not finish in time (it may wait at a dialog or prompt): " + command);
                bool active;
                try { active = OnUi(CommandActive, Math.Min(left, 5000)); }
                catch (TimeoutException) { active = true; }
                idle = active ? 0 : idle + 1;
                if (idle >= 2) return CadActionResult.Success("命令已执行完成 / Command finished: " + command);
                Thread.Sleep(250);
            }
        }

        private CadActionResult Log(CadActionRequest req)
        {
            if (Adapter == null) return CadActionResult.Fail(CadErrors.NotFound, "未关联适配包，没有可读取的日志 / No adapter, so no log is available");
            var files = Adapter.ResolveLogs();
            if (files.Count == 0) return CadActionResult.Fail(CadErrors.NotFound, "适配包声明的日志文件不存在 / The adapter's log files do not exist yet");
            string name = req.Arg("name");
            if (name != null)
            {
                files = int.TryParse(name, out int idx) && idx >= 1 && idx <= files.Count
                    ? new List<string> { files[idx - 1] }
                    : files.Where(f => Path.GetFileName(f).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (files.Count == 0) return CadActionResult.Fail(CadErrors.NotFound, "没有匹配「" + name + "」的日志 / No log matches the name");
            }
            DateTime? since = DateTime.TryParse(req.Arg("since") ?? "", out var s) ? s : (DateTime?)null;
            int lines = req.Number("lines", 200);
            var arts = files.Select(f => CadLogReader.ReadTail(f, lines, since)).ToArray();
            return CadActionResult.Success("已读取日志 / Read log: " + string.Join(", ", arts.Select(a => a.Name)), arts);
        }
    }
}
