using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// VS 会话：「记录当前 VS」把打开的解决方案与窗口布局写入 %APPDATA%\VSManager\vs-session.json，
    /// 「一键打开 VS」打开其中尚未打开的解决方案，等它们就绪后按记录恢复主窗口与浮动窗格的位置。
    /// VS session: "Record current VS" writes the open solutions and their window layout to %APPDATA%\VSManager\vs-session.json;
    /// "Open recorded VS" opens the solutions not yet open, waits until they are ready and restores the recorded main-window and floating-pane positions.
    /// </summary>
    public partial class MainForm : IAgentVsSessionHost
    {
        /// <summary>等待新打开的 VS 被识别的最长时间。/ Longest wait for newly opened VS instances to be detected.</summary>
        private static readonly TimeSpan SessionOpenTimeout = TimeSpan.FromMinutes(4);
        private bool _sessionBusy;

        /// <summary>侧边栏 VS 列表上方的「记录 / 一键打开」按钮条。/ The "record / open" button bar above the sidebar VS list.</summary>
        private void BuildVsSessionBar(Control vsBody)
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Top, Height = Dpi.S(38), BackColor = Theme.Sidebar, ColumnCount = 2, RowCount = 1,
                Padding = new Padding(Dpi.S(12), Dpi.S(2), Dpi.S(12), Dpi.S(4)),
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var record = new FlatButton { Text = "📌 记录 VS", Ghost = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, Dpi.S(4), 0) };
            var open = new FlatButton { Text = "▶ 一键打开 VS", Ghost = true, Dock = DockStyle.Fill, Margin = new Padding(Dpi.S(4), 0, 0, 0) };
            record.Click += async (s, e) => await RecordVsSessionAsync();
            open.Click += async (s, e) => await OpenVsSessionAsync();
            _tips.SetToolTip(record, "记录当前打开的 VS（解决方案）以及主窗口与浮动窗格的位置，覆盖上一次记录"
                + "\nRecord the open VS (solutions) with their main-window and floating-pane positions, replacing the previous record");
            _tips.SetToolTip(open, "打开记录中尚未打开的 VS，就绪后按记录恢复窗口布局；已打开的 VS 只恢复布局"
                + "\nOpen the recorded VS that are not open yet and restore the recorded window layout once ready; already open VS only get their layout restored");
            bar.Controls.Add(record, 0, 0);
            bar.Controls.Add(open, 1, 0);
            vsBody.Controls.Add(bar);
        }

        Task<string> IAgentVsSessionHost.RecordVsSession() => OnUiAsync(RecordVsSessionAsync);

        Task<string> IAgentVsSessionHost.OpenVsSession() => OnUiAsync(OpenVsSessionAsync);

        Task<string> IAgentVsSessionHost.DescribeVsSession() => Task.FromResult(VsSessionStore.Describe(VsSessionStore.Load()));

        /// <summary>记录当前全部 VS 与布局（只读，不移动窗口）；需在界面线程调用。/ Records every open VS and its layout (read-only); call on the UI thread.</summary>
        private async Task<string> RecordVsSessionAsync()
        {
            if (_sessionBusy) return Report("正在处理 VS 记录，请稍候 / A VS session operation is in progress");
            var live = _instances.ToList();
            if (live.Count == 0) return Report("当前没有正在运行的 Visual Studio，未覆盖原记录 / No running Visual Studio; the previous record was kept");
            var names = live.Select(NameOf).ToList();
            string keyword = _settings.CopilotPaneKeyword;
            string signature = ReadWorkspaceDisplays().Signature;
            _sessionBusy = true;
            try
            {
                SetStatus("正在记录当前 VS 与布局… / Recording the current VS and layout…");
                var items = await DteWorker.Run(() => live.Select((v, i) => VsWorkspaceLayout.Capture(v, names[i], keyword)).ToList());
                var record = new VsSessionRecord { SavedUtc = DateTime.UtcNow, DisplaySignature = signature, Items = items };
                string err = VsSessionStore.Save(record);
                if (err != null) return Report("VS 记录保存失败 / Failed to save the VS record：" + err);
                int noSolution = items.Count(x => !x.HasSolution);
                int panes = items.Sum(x => new[] { x.Copilot, x.Output, x.ErrorList, x.SolutionExplorer }.Count(r => r != null));
                AppLog.Write(LayoutLogFile, "记录 VS 会话 / Recorded VS session:\n" + VsSessionStore.Describe(record));
                return Report($"已记录 {items.Count} 个 VS 与布局（{panes} 个浮动窗格）/ Recorded {items.Count} VS with layout ({panes} floating panes)"
                    + (noSolution > 0 ? $"；其中 {noSolution} 个未打开解决方案，一键打开时跳过 / {noSolution} without a solution will be skipped when opening" : ""));
            }
            catch (Exception ex) { return Report("记录 VS 失败 / Failed to record VS：" + ex.Message); }
            finally { _sessionBusy = false; }
        }

        /// <summary>打开记录中尚未打开的 VS 并恢复布局；需在界面线程调用。/ Opens recorded VS that are not open and restores the layout; call on the UI thread.</summary>
        private async Task<string> OpenVsSessionAsync()
        {
            if (_sessionBusy) return Report("正在处理 VS 记录，请稍候 / A VS session operation is in progress");
            var record = VsSessionStore.Load();
            if (record == null || record.Items.Count == 0)
                return Report("还没有 VS 记录，请先点「📌 记录 VS」/ No VS record yet; click \"📌 记录 VS\" first");
            _sessionBusy = true;
            var lines = new List<string>();
            try
            {
                var entries = record.Items;
                var launch = VsSessionStore.ToLaunch(entries, _instances.Select(v => v.SolutionPath).ToList());
                var wanted = new HashSet<int>(Enumerable.Range(0, entries.Count).Where(i => entries[i].HasSolution));
                foreach (int i in Enumerable.Range(0, entries.Count).Where(i => !entries[i].HasSolution))
                    lines.Add($"· {entries[i].Name}：记录时未打开解决方案，跳过 / no solution was open when recorded; skipped");
                int launched = 0;
                foreach (int i in launch)
                {
                    string path = Environment.ExpandEnvironmentVariables(entries[i].SolutionPath);
                    if (!File.Exists(path))
                    {
                        wanted.Remove(i);
                        lines.Add($"· {entries[i].Name}：解决方案文件不存在，跳过 / solution file not found; skipped");
                        continue;
                    }
                    SetStatus($"正在打开 {launched + 1}/{launch.Count}：{entries[i].Name} / Opening");
                    string err = await LaunchSolutionAsync(path);
                    if (err != null) { wanted.Remove(i); lines.Add($"· {entries[i].Name}：打开失败 / failed to open：{err}"); continue; }
                    launched++;
                    lines.Add($"· {entries[i].Name}：已启动 / launched");
                    // 错开启动，避免多个 devenv 同时初始化抢占资源 / Stagger launches so several devenv processes do not initialize at once
                    await Task.Delay(1500);
                }
                if (launch.Count == 0) lines.Add("· 记录中的 VS 都已打开，只恢复布局 / Every recorded VS is already open; restoring the layout only");

                // 等待新启动的 VS 被识别并可用 / Wait until the launched VS are detected and usable
                var started = DateTime.UtcNow;
                while (true)
                {
                    var list = _instances;
                    var match = VsSessionStore.MatchOpen(entries, list.Select(v => v.SolutionPath).ToList());
                    var pending = wanted.Where(i => match[i] < 0 || !Ready(list[match[i]])).ToList();
                    if (pending.Count == 0) break;
                    if (DateTime.UtcNow - started > SessionOpenTimeout)
                    {
                        foreach (int i in pending) lines.Add($"· {entries[i].Name}：{SessionOpenTimeout.TotalMinutes:0} 分钟内未就绪，未布局 / not ready within {SessionOpenTimeout.TotalMinutes:0} min; layout skipped");
                        break;
                    }
                    SetStatus($"等待 VS 就绪（{wanted.Count - pending.Count}/{wanted.Count}）… / Waiting for VS to be ready");
                    await Task.Delay(2000);
                    if (IsDisposed) return "";
                }
                if (launched > 0) await Task.Delay(3000);
                lines.Insert(0, await ApplySessionLayoutAsync(record));
                return Report(string.Join("\n", lines));
            }
            catch (Exception ex) { return Report("一键打开 VS 失败 / Failed to open recorded VS：" + ex.Message + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "")); }
            finally { _sessionBusy = false; }
        }

        private static bool Ready(VsInstance v) =>
            v != null && v.Dte != null && v.MainHwnd != IntPtr.Zero && Native.IsWindow(v.MainHwnd) && Native.IsWindowVisible(v.MainHwnd);

        /// <summary>按记录摆放已打开的 VS；屏幕配置改变时不移动窗口。/ Places the open VS as recorded; moves nothing when the displays changed.</summary>
        private async Task<string> ApplySessionLayoutAsync(VsSessionRecord record)
        {
            if (_arranging || _docking) return "正在调整窗格布局，未恢复布局 / Layout change in progress; layout not restored";
            if (CopilotLayout.SavedCount > 0) return "请先还原 Copilot 对话布局，未恢复布局 / Restore the Copilot layout first; layout not restored";
            string signature = ReadWorkspaceDisplays().Signature;
            if (!string.IsNullOrEmpty(record.DisplaySignature) && record.DisplaySignature != signature)
                return "屏幕配置与记录时不同，只打开 VS、未移动窗口 / Displays differ from the record; VS opened but no windows moved";
            var list = _instances.ToList();
            var match = VsSessionStore.MatchOpen(record.Items, list.Select(v => v.SolutionPath).ToList());
            var placements = new List<VsWorkspacePlacement>();
            var maximize = new List<VsInstance>();
            for (int i = 0; i < match.Length; i++)
            {
                var e = record.Items[i];
                if (match[i] < 0 || e.Main == null || !Ready(list[match[i]])) continue;
                var vs = list[match[i]];
                placements.Add(new VsWorkspacePlacement
                {
                    Vs = vs, Name = NameOf(vs), MainBounds = e.Main.ToRectangle(),
                    CopilotBounds = e.Copilot?.ToRectangle() ?? Rectangle.Empty,
                    OutputBounds = e.Output?.ToRectangle() ?? Rectangle.Empty,
                    ErrorListBounds = e.ErrorList?.ToRectangle() ?? Rectangle.Empty,
                    SolutionExplorerBounds = e.SolutionExplorer?.ToRectangle() ?? Rectangle.Empty,
                });
                if (e.Maximized) maximize.Add(vs);
            }
            if (placements.Count == 0) return "没有可恢复布局的 VS / No VS to restore the layout for";
            string keyword = _settings.CopilotPaneKeyword;
            var foreground = Native.GetForegroundWindow();
            _arranging = true;
            SetStatus("正在按记录恢复 VS 布局… / Restoring the recorded VS layout…");
            try
            {
                string result = await DteWorker.Run(() =>
                {
                    string r = VsWorkspaceLayout.Arrange(placements, true, true, keyword, true);
                    foreach (var v in maximize)
                        if (!VsWorkspaceLayout.Maximize(v)) r += "\n· " + v.Pid + " / Main: 最大化失败 / maximize failed";
                    return r;
                });
                foreach (var p in placements) _chatSvc.PaneRestored(p.Vs.Pid);
                AppLog.Write(LayoutLogFile, "恢复 VS 会话布局 / Restored VS session layout:\n" + result);
                return $"已按记录恢复 {placements.Count} 个 VS 的布局（可用「还原工作区布局」撤销）/ Restored the recorded layout of {placements.Count} VS (undo with restore workspace layout)\n" + result;
            }
            finally
            {
                _arranging = false;
                if (foreground != IntPtr.Zero && Native.IsWindow(foreground) && !Native.IsIconic(foreground) && Native.GetForegroundWindow() != foreground) Native.Activate(foreground);
            }
        }

        private const string LayoutLogFile = "vs-session.log";

        private string Report(string text)
        {
            SetStatus(text.Split('\n')[0]);
            return text;
        }
    }
}
