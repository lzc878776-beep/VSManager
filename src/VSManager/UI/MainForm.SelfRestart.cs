using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 自测重启：AI 总控助手让调试 VSManager 的 VS 重新启动调试，以加载新生成的程序并继续测试。
    /// 流程：确认本进程由某个 VS 调试 → 预编译到临时目录 → 本轮对话结束且没有发送中的任务后保存任务、配置、笔记与启动授权并写交接单 →
    /// VS 执行 Debug.Restart → 新进程读取交接单，恢复授权并把测试计划交还给助手。
    /// Self-test restart: the AI assistant has the VS debugging VSManager restart the debug session to load the new build and
    /// continue testing. Flow: confirm a VS debugs this process → pre-build into a temp folder → once the round has ended and nothing
    /// is being sent, save tasks, settings, notes and start grants and write the handoff → VS runs Debug.Restart → the new
    /// process reads the handoff, restores grants and hands the test plan back to the assistant.
    /// </summary>
    public partial class MainForm : IAgentSelfRestartHost, IAgentSelfIterationHost, IAgentChecklistHost
    {
        private SelfRestartHandoff _selfRestart;
        private VsInstance _selfRestartVs;
        private DateTime _selfRestartDeadline;
        private System.Windows.Forms.Timer _selfRestartTimer;
        private RestartUiState _restoredUi;
        private bool _restoredUiRead;

        /// <summary>重启前保存的界面状态（首次访问时读取并删除文件）。/ UI state saved before the restart (read and deleted on first access).</summary>
        private RestartUiState RestoredUi
        {
            get
            {
                if (!_restoredUiRead)
                {
                    _restoredUiRead = true;
                    _restoredUi = RestartUi.Take(DateTime.UtcNow, Process.GetCurrentProcess().Id);
                }
                return _restoredUi;
            }
        }

        /// <summary>重启前不在前台时，新窗口显示但不抢焦点。/ When the window was not in the foreground before the restart, the new one shows without taking focus.</summary>
        protected override bool ShowWithoutActivation => RestoredUi != null && !RestoredUi.Foreground;

        protected override void OnLoad(EventArgs e)
        {
            var ui = RestoredUi;
            var bounds = RestartUi.FitBounds(ui, Screen.AllScreens.Select(s => s.WorkingArea), MinimumSize);
            if (bounds != null) StartPosition = FormStartPosition.Manual;
            base.OnLoad(e);
            if (bounds == null) return;
            _restoredBounds = bounds;
            // 回到重启前的位置与大小，避免窗口跳到屏幕中央 / Back to the pre-restart bounds instead of jumping to the screen center
            Bounds = bounds.Value;
            if (ui.Hidden) WindowState = FormWindowState.Minimized;
            else if (ui.Maximized) WindowState = FormWindowState.Maximized;
        }

        /// <summary>记录当前界面状态，供重启后恢复。/ Captures the current UI state to restore after a restart.</summary>
        private RestartUiState CaptureUiState()
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var fg = Native.GetForegroundWindow();
            uint fgPid = 0;
            if (fg != IntPtr.Zero) Native.GetWindowThreadProcessId(fg, out fgPid);
            int me = Process.GetCurrentProcess().Id;
            return new RestartUiState
            {
                CreatedUtc = DateTime.UtcNow, OldPid = me,
                X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
                Maximized = WindowState == FormWindowState.Maximized,
                Hidden = !Visible || WindowState == FormWindowState.Minimized,
                Foreground = Visible && fgPid == (uint)me,
                AgentPage = _agentMode,
                SelectedVsPid = Selected?.Pid ?? 0,
                AgentDraft = _agentPanel.DraftText
            };
        }

        private const string RestoreSource = "重启恢复 / restart restore";

        /// <summary>重启后持续核对页面与选中 VS 的时长；期间被程序改动会恢复回保存值，用户手动切换则停止。/ How long page and selection are kept at the saved values after a restart; program changes are reverted, a user switch ends it.</summary>
        internal static readonly TimeSpan RestoreSettle = TimeSpan.FromSeconds(20);

        private DateTime _userNavigatedAt = DateTime.MinValue;
        private string _userNavigation;
        private string _pageChange;

        /// <summary>记录用户手动切换页面 / 选中 VS（重启恢复据此停止，不与用户抢）。/ Records a manual page / VS switch by the user (the restart restore stops instead of fighting it).</summary>
        private void NoteUserNavigation(string what)
        {
            _userNavigatedAt = DateTime.Now;
            _userNavigation = what;
        }

        /// <summary>记录页面切换及其来源，供 get_window_state 对照不一致的原因。/ Records a page switch and its source so get_window_state can explain differences.</summary>
        private void NotePageChange(bool agent, string source)
        {
            if (source == null)
                source = DateTime.Now - _userNavigatedAt < TimeSpan.FromSeconds(2) && _userNavigation != null ? _userNavigation : "程序 / program";
            _pageChange = DateTime.Now.ToString("HH:mm:ss") + " → " + (agent ? "AI 总控 / AI assistant" : "VS 对话 / VS chat") + "（" + source + "）";
            if (RestoredUi != null && DateTime.Now - _restoreStartedAt < RestoreSettle + TimeSpan.FromSeconds(10))
                AppLog.Write(ProcessWatchdog.LogFile, "重启后页面切换 / Page switched after restart: " + _pageChange);
        }

        private DateTime _restoreStartedAt = DateTime.MinValue;

        /// <summary>
        /// 界面就绪后恢复页面、选中的 VS（实例列表刷新后）与 AI 输入草稿，并关闭过渡画面。页面与选中 VS 在 <see cref="RestoreSettle"/> 内保持为保存值：
        /// 被程序切走会改回（并记日志），用户手动切换则停止恢复。
        /// Once the UI is ready, restores the page, the selected VS (after the instance list refreshes) and the AI draft, and closes the cover.
        /// Page and selection are held at the saved values for <see cref="RestoreSettle"/>: program switches are reverted (and logged), a manual user switch ends the restore.
        /// </summary>
        private void ApplyRestoredUi()
        {
            var ui = RestoredUi;
            if (ui == null) return;
            _restoreStartedAt = DateTime.Now;
            if (!string.IsNullOrEmpty(ui.AgentDraft) && string.IsNullOrEmpty(_agentPanel.DraftText)) _agentPanel.DraftText = ui.AgentDraft;
            bool wantAgent = ui.AgentPage && _settings.AgentEnabled;
            if (_agentMode != wantAgent) ShowAgent(wantAgent, RestoreSource);
            if (!ui.Hidden && ui.Foreground) Native.ForceForeground(Handle, TopMost);
            var started = _restoreStartedAt;
            var timer = new System.Windows.Forms.Timer { Interval = 300 };
            bool covered = !string.IsNullOrEmpty(ui.CoverEvent);
            bool selected = false;
            timer.Tick += (s, e) =>
            {
                // 先让新窗口完成首次绘制再撤掉过渡画面 / Let the new window paint once before removing the cover
                if (covered) { covered = false; SignalCover(ui.CoverEvent); _coverSignaled = true; }
                bool userMoved = _userNavigatedAt >= started;
                bool expired = DateTime.Now - started > RestoreSettle;
                if (!userMoved && !expired)
                {
                    if (_agentMode != wantAgent && !_notebookMode)
                    {
                        AppLog.Write(ProcessWatchdog.LogFile, "重启恢复：页面被改动，恢复为保存值 / Restart restore: page was changed, reverting to the saved value (" + _pageChange + ")");
                        ShowAgent(wantAgent, RestoreSource);
                    }
                    var v = ui.SelectedVsPid > 0 && !wantAgent ? _instances.FirstOrDefault(x => x.Pid == ui.SelectedVsPid) : null;
                    if (v != null && !_agentMode && Selected?.Pid != v.Pid)
                    {
                        int index = _list.Items.IndexOf(v);
                        if (index < 0) index = _list.Items.Cast<object>().ToList().FindIndex(x => (x as VsInstance)?.Pid == v.Pid);
                        if (index >= 0)
                        {
                            if (selected) AppLog.Write(ProcessWatchdog.LogFile, "重启恢复：选中的 VS 被改动，恢复为 PID " + v.Pid + " / Restart restore: selection was changed, reverting to PID " + v.Pid);
                            _list.SelectedIndex = index;
                        }
                    }
                    if (v != null && Selected?.Pid == v.Pid) selected = true;
                }
                if (userMoved || expired)
                {
                    if (userMoved) AppLog.Write(ProcessWatchdog.LogFile, "重启恢复：用户已手动切换，停止恢复 / Restart restore: the user switched manually; restore stopped (" + _userNavigation + ")");
                    timer.Stop();
                    timer.Dispose();
                }
            };
            timer.Start();
        }

        /// <summary>通知过渡画面关闭。/ Tells the cover window to close.</summary>
        private static void SignalCover(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            try
            {
                if (EventWaitHandle.TryOpenExisting(eventName, out var h))
                    using (h) h.Set();
            }
            catch { }
        }

        /// <summary>
        /// 窗口在前台时截取当前画面，并启动独立的过渡画面进程：旧进程退出后在原位置显示截图，直到新窗口就绪。返回命名事件名，未启动时返回 null。
        /// When the window is in the foreground, captures it and starts a separate cover process that shows the screenshot in place after
        /// the old process exits, until the new window is ready. Returns the event name, or null when no cover was started.
        /// </summary>
        private string StartRestartCover(RestartUiState ui)
        {
            if (ui == null || !ui.Foreground || ui.Hidden || !Visible) return null;
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            string dir = Path.GetTempPath();
            string image = Path.Combine(dir, "VSManager-cover-" + id + ".png");
            string script = Path.Combine(dir, "VSManager-cover-" + id + ".ps1");
            try
            {
                Update();
                var b = Bounds;
                using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
                    bmp.Save(image, System.Drawing.Imaging.ImageFormat.Png);
                }
                File.WriteAllText(script, RestartUi.CoverScript, new UTF8Encoding(true));
                string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                if (!File.Exists(ps)) throw new FileNotFoundException("powershell.exe");
                string name = RestartUi.CoverEventName(id);
                string args = "-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -Image \"" + image + "\"" +
                    " -X " + b.X + " -Y " + b.Y + " -W " + b.Width + " -H " + b.Height + " -OldPid " + ui.OldPid + " -EventName \"" + name + "\"" +
                    " -Text \"⟳ 正在重启 VSManager 以加载新程序… / Restarting VSManager to load the new build…\"";
                using (Process.Start(new ProcessStartInfo(ps, args) { UseShellExecute = false, CreateNoWindow = true })) { }
                return name;
            }
            catch (Exception ex)
            {
                AppLog.Write(ProcessWatchdog.LogFile, "过渡画面未启动 / Restart cover not started: " + ex.Message);
                try { File.Delete(image); } catch { }
                try { File.Delete(script); } catch { }
                return null;
            }
        }

        /// <summary>查找正在调试本进程的 VS；找不到时返回原因。/ Finds the VS debugging this process; returns the reason when none.</summary>
        private async Task<Tuple<VsInstance, string>> FindSelfDebuggerAsync()
        {
            if (!Debugger.IsAttached)
                return Tuple.Create<VsInstance, string>(null, "VSManager 当前不是在 VS 调试器中运行，无法由 VS 重启调试加载新程序；请在打开 VSManager 解决方案的 VS 中按 F5 启动后再试，或改用托盘菜单「重启 VSManager」（不会加载新生成的程序）。/ " +
                    "VSManager is not running under a VS debugger, so a debug restart cannot load a new build; start it with F5 from the VS that has the VSManager solution, or use the tray \"Restart VSManager\" (does not load a new build).");
            int me = Process.GetCurrentProcess().Id;
            var candidates = await OnUi(() => _instances.Where(v => v.Dte != null).ToList()).ConfigureAwait(false);
            foreach (var v in candidates)
            {
                bool debugs;
                try { debugs = await DteWorker.Run(() => VsService.DebugsProcess(v, me)).ConfigureAwait(false); }
                catch { debugs = false; }
                if (debugs) return Tuple.Create(v, (string)null);
            }
            return Tuple.Create<VsInstance, string>(null, "没有找到正在调试本进程（PID " + me + "）的 VS；可能是附加到其他调试器，或该 VS 的自动化接口不可用。/ " +
                "No VS was found debugging this process (PID " + me + "); another debugger may be attached or the VS automation interface is unavailable.");
        }

        private string SelfRestartBlocker()
        {
            if (_selfRestart != null) return "已有一次自测重启在等待执行 / A self-test restart is already pending.";
            if (_sending || _tasks.Items.Any(t => t.Status == QueueStatus.Sending))
                return "正在向 VS 发送消息，请稍后再试，避免消息半途中断 / A message is being sent to VS; try again later so it is not cut off.";
            if (_dispatcher.IsFinishingWork) return "有任务正在收尾或合并，请稍后再试 / A task is being finished or integrated; try again later.";
            return null;
        }

        /// <summary>
        /// 调试 VSManager 的 VS 中还有执行中的任务（Copilot 可能正在改代码）时返回原因；重启会中断它。
        /// Returns the reason when the VS debugging VSManager still has a running task (Copilot may be editing code); a restart would cut it off.
        /// </summary>
        private string SelfVsBusy(VsInstance vs)
        {
            if (vs == null) return null;
            var busy = _tasks.Items.FirstOrDefault(t => (t.Status == QueueStatus.Running || t.Status == QueueStatus.Sending)
                && string.Equals(t.VsKey, vs.Key, StringComparison.OrdinalIgnoreCase));
            return busy == null ? null
                : "「" + NameOf(vs) + "」中的任务 #" + busy.Id + " 仍在执行，请等它的「[任务完成通知]」后再重启 / Task #" + busy.Id + " is still running in the VS debugging VSManager; wait for its completion notice before restarting.";
        }

        async Task<string> IAgentSelfRestartHost.CheckSelfRestart()
        {
            string blocked = await OnUi(SelfRestartBlocker).ConfigureAwait(false);
            if (blocked != null) return blocked;
            var found = await FindSelfDebuggerAsync().ConfigureAwait(false);
            if (found.Item1 == null) return found.Item2;
            return await OnUi(() => SelfVsBusy(found.Item1)).ConfigureAwait(false);
        }

        async Task<Tuple<string, string>> IAgentSelfIterationHost.SelfDebuggerName()
        {
            var found = await FindSelfDebuggerAsync().ConfigureAwait(false);
            if (found.Item1 == null) return Tuple.Create<string, string>(null, found.Item2);
            return Tuple.Create(await OnUi(() => NameOf(found.Item1)).ConfigureAwait(false), (string)null);
        }

        async Task<SelfRestartBuild> IAgentSelfRestartHost.PrebuildSelf(CancellationToken cancellationToken)
        {
            var found = await FindSelfDebuggerAsync().ConfigureAwait(false);
            if (found.Item1 == null) return new SelfRestartBuild { Ok = false, Summary = found.Item2 };
            var vs = found.Item1;
            return await Task.Run(() => Prebuild(vs, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        private static SelfRestartBuild Prebuild(VsInstance vs, CancellationToken cancellationToken)
        {
            string exe = Application.ExecutablePath;
            string project = SelfRestart.FindProjectFile(exe);
            if (project == null)
                return new SelfRestartBuild { Ok = false, Summary = "在程序目录的上级目录中找不到 VSManager.csproj，无法预编译 / VSManager.csproj was not found above the program folder; cannot pre-build." };
            string msbuild = SelfRestart.MsBuildFromDevenv(VsLifecycle.FindDevenv(new[] { vs }));
            if (msbuild == null || !File.Exists(msbuild))
                return new SelfRestartBuild { Ok = false, Summary = "找不到该 VS 自带的 MSBuild.exe，无法预编译 / The MSBuild.exe of this VS was not found; cannot pre-build." };
            string configuration = SelfRestart.GuessConfiguration(exe);
            string outDir = Path.Combine(Path.GetTempPath(), "VSManager-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            // 输出到临时目录，不覆盖正在运行的程序；结尾 \\" 让命令行解析得到以 \ 结尾的路径。
            // Output goes to a temp folder so the running exe is never overwritten; the trailing \\" yields a path ending in \.
            string args = "\"" + project + "\" /nologo /restore /m /nr:false /v:m /clp:ErrorsOnly;NoSummary /p:Configuration=" + configuration +
                " /p:OutDir=\"" + outDir + "\\\\\"";
            var output = new List<string>();
            var watch = Stopwatch.StartNew();
            try
            {
                Encoding encoding;
                try { encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { encoding = Encoding.UTF8; }
                var psi = new ProcessStartInfo(msbuild, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = encoding, StandardErrorEncoding = encoding,
                    WorkingDirectory = Path.GetDirectoryName(project)
                };
                using (var p = new Process { StartInfo = psi })
                {
                    DataReceivedEventHandler collect = (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (output) output.Add(e.Data.Trim()); };
                    p.OutputDataReceived += collect;
                    p.ErrorDataReceived += collect;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    using (cancellationToken.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
                    {
                        if (!p.WaitForExit(6 * 60 * 1000))
                        {
                            try { p.Kill(); } catch { }
                            return new SelfRestartBuild { Ok = false, Summary = "预编译超过 6 分钟，已终止 / Pre-build exceeded 6 minutes and was stopped." };
                        }
                        p.WaitForExit();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    int seconds = (int)watch.Elapsed.TotalSeconds;
                    if (p.ExitCode == 0)
                        return new SelfRestartBuild { Ok = true, Summary = $"预编译通过（{configuration}，用时 {seconds} 秒）/ Pre-build passed ({configuration}, {seconds} s)" };
                    string[] lines;
                    lock (output) lines = output.ToArray();
                    var errors = lines.Where(l => l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || l.Contains("错误")).Distinct().Take(30).ToList();
                    if (errors.Count == 0) errors = lines.Skip(Math.Max(0, lines.Length - 20)).ToList();
                    return new SelfRestartBuild
                    {
                        Ok = false,
                        Summary = $"预编译失败（退出代码 {p.ExitCode}，用时 {seconds} 秒）/ Pre-build failed (exit code {p.ExitCode}, {seconds} s):\n" + string.Join("\n", errors)
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new SelfRestartBuild { Ok = false, Summary = "无法运行 MSBuild / Unable to run MSBuild: " + ex.Message };
            }
            finally
            {
                try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
            }
        }

        async Task<string> IAgentSelfRestartHost.ScheduleSelfRestart(string testPlan, int taskId, string scope, string buildSummary)
        {
            var found = await FindSelfDebuggerAsync().ConfigureAwait(false);
            if (found.Item1 == null) return found.Item2;
            return await OnUi(() =>
            {
                string blocked = SelfRestartBlocker() ?? SelfVsBusy(found.Item1);
                if (blocked != null) return blocked;
                if (taskId > 0 && _tasks.Find(taskId) == null) return "没有任务 #" + taskId + " / No task #" + taskId;
                string exe = Application.ExecutablePath;
                _selfRestart = new SelfRestartHandoff
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CreatedUtc = DateTime.UtcNow,
                    OldPid = Process.GetCurrentProcess().Id,
                    VsPid = found.Item1.Pid,
                    OldExeWriteUtc = File.GetLastWriteTimeUtc(exe),
                    BuildSummary = buildSummary,
                    TestPlan = testPlan,
                    TaskId = taskId,
                    Scope = scope
                };
                _selfRestartVs = found.Item1;
                _selfRestartDeadline = DateTime.Now + SelfRestart.MaxWaitForIdle;
                // 其他 VS 的通知先暂缓、随重启带到新进程，本轮结束即可重启 / Hold other VS notices and carry them over, so the restart happens right after this round
                _agent.HoldNotices = true;
                if (_selfRestartTimer == null)
                {
                    _selfRestartTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                    _selfRestartTimer.Tick += (s, e) => SelfRestartTick();
                }
                _selfRestartTimer.Start();
                AppLog.Write(ProcessWatchdog.LogFile, "已安排自测重启 / Self-test restart scheduled (VS PID " + found.Item1.Pid + ")");
                SetStatus("⟳ 本轮对话结束后将重启 VSManager 以测试新程序，暂停发布新任务 / VSManager restarts for testing after this round; new sends paused");
                return "已安排自测重启：本轮回复结束、且没有发送中的任务后，「" + NameOf(found.Item1) + "」会重新启动调试（先保存任务清单、对话与启动授权），" +
                    "新程序启动后你会收到「[重启完成通知]」再继续测试。期间不再发布新任务；其他 VS 中执行的任务不必等待、不受影响，重启后继续跟踪，期间到达的通知在重启后补发给你。本轮请只简短告知用户，不要再调用其他工具。/ " +
                    "Self-test restart scheduled: after this round (and any in-flight send) the debugging VS restarts the session; you will get a restart-completed notice to continue testing. " +
                    "Tasks running in other VS instances are not waited for and not affected; they are tracked again after the restart and notices arriving meanwhile are re-delivered to you.";
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 让 AI 助手验证测试清单中未勾选的 AI 项；fromToggle 为开启循环时的首次验证（没有 AI 项时不提示）。
        /// Asks the AI assistant to verify the unchecked AI items of the checklist; fromToggle is the first pass when the loop is enabled (silent without AI items).
        /// </summary>
        private void RequestAiVerify(bool fromToggle)
        {
            var notices = TestChecklistPanel.ListedTasks(_tasks.Items, _settings.TaskListClearedAt)
                .Select(t => TaskTestChecklist.SelfVerifyNotice(t)).Where(n => n != null).ToList();
            if (notices.Count == 0)
            {
                if (!fromToggle) SetStatus("测试清单中没有未勾选的 AI 可验证项 / No unchecked AI-verifiable items in the checklist");
                return;
            }
            if (!_agent.Configured) { SetStatus("AI 助手未配置模型，无法自动验证 / The AI assistant has no model configured"); return; }
            string display = "🤖 AI 验证测试清单（" + notices.Count + " 个任务）/ AI verify checklist";
            string content = string.Join("\n\n", notices);
            if (_agent.Running) _agent.Notify(display, content);
            else _ = _agent.RunAsync(content, display, new AttachmentRef[0]);
            SetStatus("已请 AI 助手验证测试清单中的 AI 项 / Asked the AI assistant to verify the AI items");
        }

        /// <summary>切换自验证循环；开启需要「任务完成自动跟进」，开启时立即验证现有的 AI 项。/ Toggles the self-verify loop; needs task auto follow-up and verifies existing AI items when enabled.</summary>
        private void SetSelfVerifyLoop(bool on)
        {
            if (on && !_settings.AgentAutoFollowUp)
            {
                SetStatus("自验证循环需要先开启「属性 → AI 助手 → 任务完成自动跟进」/ The self-verify loop needs task auto follow-up enabled in Properties");
                return;
            }
            _settings.AgentSelfVerify = on;
            _settings.Save();
            _testPanel.SetSelfVerify(on);
            SetStatus(on ? "自验证循环已开启：待验证任务中的 AI 项将自动由 AI 助手验证 / Self-verify loop on" : "自验证循环已关闭 / Self-verify loop off");
            if (on) RequestAiVerify(true);
        }

        Task<string> IAgentChecklistHost.ListTestChecklists(int taskId) => OnUi(() => TaskTestChecklist.Describe(_tasks.Items, taskId));

        Task<string> IAgentSelfRestartHost.SetTestItem(int taskId, int item, bool passed, string evidence) => OnUi(() =>
        {
            var t = _tasks.Find(taskId);
            if (t == null) return "没有任务 #" + taskId + " / No task #" + taskId;
            if (!TaskTestChecklist.Pending(t)) return $"任务 #{taskId} 当前{StatusText(t)}，不在待验证状态 / Task #{taskId} is not awaiting verification.";
            var items = TaskTestChecklist.Ensure(t);
            if (item < 1 || item > items.Length || items[item - 1] == null)
                return $"任务 #{taskId} 的测试清单共 {items.Length} 项，没有第 {item} 项 / The checklist has {items.Length} items; no item {item}.";
            string text = items[item - 1].Text;
            if (passed && !TaskTestChecklist.CanAiCheck(items[item - 1]))
                return $"任务 #{taskId} 的第 {item} 项标注为「必须人工验证」，AI 助手不能勾选；请转告用户测试 / Item {item} of task #{taskId} is marked manual; the assistant cannot check it — relay it to the user.";
            if (!_dispatcher.SetTestItem(t, item - 1, passed)) return $"任务 #{taskId} 的测试项未能更新 / The test item of task #{taskId} was not updated.";
            AppLog.Write(AppLog.TasksFile, $"AI 助手{(passed ? "勾选" : "取消勾选")}任务 #{taskId} 测试项 {item} / Assistant {(passed ? "checked" : "unchecked")} test item {item}: " +
                TextUtil.Clip(text, 200) + " | 依据 / Evidence: " + evidence);
            _taskPanel.RefreshItems();
            bool done = t.Status == QueueStatus.Done;
            SetStatus($"AI 助手{(passed ? "勾选" : "取消勾选")}了任务 #{taskId} 的测试项 {item} / The assistant {(passed ? "checked" : "unchecked")} test item {item} of task #{taskId}");
            return $"已{(passed ? "勾选" : "取消勾选")}任务 #{taskId} 的第 {item} 项「{TextUtil.Clip(text, 120)}」" +
                (done ? "；测试清单已全部勾选，任务已标记为完成" : $"；还剩 {TaskTestChecklist.Remaining(t)} 项未勾选") +
                $" / Item {item} of task #{taskId} {(passed ? "checked" : "unchecked")}" + (done ? "; all items checked, task marked done" : $"; {TaskTestChecklist.Remaining(t)} unchecked");
        });

        private void SelfRestartTick()
        {
            if (_selfRestart == null) { _selfRestartTimer?.Stop(); return; }
            if (DateTime.Now > _selfRestartDeadline)
            {
                AbortSelfRestart("等待本轮对话结束或发送完成超过 " + (int)SelfRestart.MaxWaitForIdle.TotalMinutes + " 分钟 / Waited too long for the round or sends to finish");
                return;
            }
            // 只等本轮对话、发送中的消息与收尾合并；其他 VS 的执行中任务与排队通知不等待（通知随重启带走）
            // Wait only for this round, in-flight sends and finishing / merging; running tasks and queued notices of other VS are not waited for (notices are carried over)
            if (_agent.Running || _sending || _tasks.Items.Any(t => t.Status == QueueStatus.Sending) || _dispatcher.IsFinishingWork
                || SelfVsBusy(_selfRestartVs) != null) return;
            _selfRestartTimer.Stop();
            PerformSelfRestart();
        }

        private async void PerformSelfRestart()
        {
            var handoff = _selfRestart;
            var vs = _selfRestartVs;
            RestartUiState ui = null;
            bool trayVisible = _tray.Visible;
            try
            {
                // 先再应用一次 AI 准备的场景（本轮回复可能已改变前台等），稍等窗口稳定后再保存状态
                // Re-apply the scenario the AI prepared first (this round may have changed the foreground etc.), then let the window settle before saving state
                var scenario = TakeRestartScenario();
                if (scenario != null)
                {
                    handoff.Scenario = scenario.Describe() + ApplyRestartScenario(scenario);
                    await Task.Delay(600);
                }
                string fail = null;
                if (!SaveNotebook()) fail = "笔记未保存 / The note could not be saved";
                else if (!_tasks.Save()) fail = "任务清单保存失败 / Task list save failed: " + _tasks.SaveError;
                if (fail == null)
                {
                    _settings.Save();
                    _dispatcher.CaptureGrants(handoff);
                    handoff.RunningTasks = _tasks.Items.Count(t => t.Status == QueueStatus.Running);
                    handoff.WaitingTasks = _tasks.Items.Count(t => t.Status == QueueStatus.Waiting || t.Status == QueueStatus.WaitingVs);
                    string error = SelfRestart.Save(handoff);
                    if (error != null) fail = "重启交接单保存失败 / Failed to save the restart handoff: " + error;
                    else if ((error = CarriedNotices.Save(_agent.PendingNotices(), DateTime.UtcNow)) != null)
                        fail = "待处理通知保存失败 / Failed to save the queued notices: " + error;
                    else
                    {
                        // 执行计划已随每次修改写入磁盘，这里只核对并记录，新进程启动时读回 / Plans are written on every change; just verify and log here, the new process reads them back
                        var plans = AgentPlans.Active(out string planError);
                        AppLog.Write(AgentPlans.LogFile, planError ?? "随重启保留执行计划 / Plans kept across the restart: " + plans.Count
                            + (plans.Count == 0 ? "" : "（" + string.Join("、", plans.Select(p => "#" + p.Id + " " + p.Title)) + "）"));
                    }
                }
                if (fail != null) { AbortSelfRestart(fail); return; }
                Archive.Flush(3000, true);
                ProcessWatchdog.MarkCleanExit();
                AppLog.Write(ProcessWatchdog.LogFile, "自测重启：请求 VS 重新启动调试 / Self-test restart: asking VS to restart debugging (PID " + handoff.OldPid + ")");
                SetStatus("⟳ 正在重启 VSManager… / Restarting VSManager…");
                // 无感重启：保存窗口与页面状态，前台时用过渡画面盖住重新生成的空档，并先移除托盘图标以免残留
                // Seamless restart: save window and page state, cover the rebuild gap when in the foreground, and remove the tray icon first so no ghost icon remains
                ui = CaptureUiState();
                ui.CoverEvent = StartRestartCover(ui);
                string uiError = RestartUi.Save(ui);
                if (uiError != null) AppLog.Write(ProcessWatchdog.LogFile, "界面状态未保存 / UI state not saved: " + uiError);
                _tray.Visible = false;
                string result;
                try { result = await DteWorker.Run(() => VsService.DebugAction(vs, "restart")); }
                catch (Exception ex) { result = ex.Message; }
                // 正常情况下 VS 会在这段时间内结束本进程 / Normally VS ends this process within this delay
                await Task.Delay(TimeSpan.FromSeconds(30));
                UndoRestartUi(ui, trayVisible);
                ProcessWatchdog.ClearCleanExit();
                SelfRestart.Discard();
                CarriedNotices.Discard();
                AbortSelfRestart("VS 没有重新启动调试 / VS did not restart debugging: " + result);
            }
            catch (Exception ex)
            {
                UndoRestartUi(ui, trayVisible);
                ProcessWatchdog.ClearCleanExit();
                SelfRestart.Discard();
                CarriedNotices.Discard();
                AbortSelfRestart(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>重启未发生：撤掉过渡画面与界面状态文件，恢复托盘图标。/ The restart did not happen: remove the cover and UI state file and restore the tray icon.</summary>
        private void UndoRestartUi(RestartUiState ui, bool trayVisible)
        {
            SignalCover(ui?.CoverEvent);
            RestartUi.Discard();
            _tray.Visible = trayVisible;
        }

        private void AbortSelfRestart(string reason)
        {
            var handoff = _selfRestart;
            _selfRestart = null;
            _selfRestartVs = null;
            _restartScenario = null;
            _selfRestartTimer?.Stop();
            _agent.HoldNotices = false;
            AppLog.Write(ProcessWatchdog.LogFile, "自测重启未执行 / Self-test restart not performed: " + reason);
            SetStatus("⚠ 自测重启未执行 / Self-test restart not performed: " + reason);
            _agent.Notify("⚠ VSManager 自测重启未执行 / Self-test restart not performed",
                "[自测重启未执行 / Self-test restart not performed] " + reason + "\n程序仍是旧版本，任务清单未受影响、已恢复发布；请如实告知用户，可在问题解决后再调用 restart_vsmanager_for_testing。/ " +
                "The old build is still running and the task list is unaffected; tell the user and retry after the cause is fixed.",
                handoff?.Scope);
            // 等待期间暂缓的其他 VS 完成事件照常处理 / Process the completion events of other VS deferred while waiting
            var deferred = _deferredCompletions.ToList();
            _deferredCompletions.Clear();
            foreach (var d in deferred) OnCopilotCompleted(d.Item1, d.Item2);
            _dispatcher.Pump();
        }

        /// <summary>自测重启等待期间暂缓的 Copilot 完成事件；真正重启后由新进程的执行中跟踪接手。/ Copilot completion events deferred while a self-test restart is pending; after a real restart the new process's running-task tracking takes over.</summary>
        private readonly List<Tuple<VsInstance, TimeSpan>> _deferredCompletions = new List<Tuple<VsInstance, TimeSpan>>();

        /// <summary>
        /// 自测重启已安排时暂缓其他 VS 的完成处理，避免任务在旧进程里完成、通知却随进程结束丢失；返回 true 表示已暂缓。
        /// While a self-test restart is pending, defers completion handling of other VS so a task never completes in the old process with its notice lost at exit; true when deferred.
        /// </summary>
        private bool DeferCompletionForRestart(VsInstance v, TimeSpan dur)
        {
            if (_selfRestart == null) return false;
            _deferredCompletions.Add(Tuple.Create(v, dur));
            AppLog.Write(ProcessWatchdog.LogFile, "自测重启等待中，暂缓「" + NameOf(v) + "」的完成处理，重启后继续跟踪 / Restart pending; completion of this VS deferred and tracked after the restart");
            return true;
        }

        /// <summary>
        /// 重启前带过来的通知：在续跑通知之后依次补发（助手忙时排队）。
        /// Notices carried over the restart: re-delivered after the continuation notice (queued while the assistant is busy).
        /// </summary>
        private void ReplayCarriedNotices()
        {
            var list = _carriedNotices;
            _carriedNotices = null;
            if (list == null || list.Count == 0) return;
            AppLog.Write(ProcessWatchdog.LogFile, "补发重启前的待处理通知 / Re-delivering notices carried over the restart: " + list.Count);
            foreach (var n in list) _agent.Notify(n.Display, n.Content, n.Scope);
        }

        private List<CarriedNotice> _carriedNotices;

        /// <summary>等实例列表刷新、助手空闲后再执行（最多等 8 秒实例列表）。/ Runs once instances refreshed and the assistant is idle (waits at most 8 s for instances).</summary>
        private void AfterStartupSettled(Action action)
        {
            var started = DateTime.Now;
            var timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += (s, e) =>
            {
                var waited = DateTime.Now - started;
                if (waited < TimeSpan.FromSeconds(2) || (_instances.Count == 0 && waited < TimeSpan.FromSeconds(8)) || _agent.Running) return;
                timer.Stop();
                timer.Dispose();
                action();
            };
            timer.Start();
        }

        /// <summary>
        /// 启动时调用：发现自测重启交接单则恢复启动授权，并在界面就绪后把测试计划交还给助手。
        /// Called at startup: when a self-test handoff exists, restores start grants and hands the test plan back to the assistant once the UI is ready.
        /// </summary>
        private void ResumeAfterSelfRestart()
        {
            var handoff = SelfRestart.Take(out string error);
            _resumeError = error;
            _resumedHandoff = handoff;
            if (error != null) SetStatus("⚠ " + error);
            _carriedNotices = CarriedNotices.Take(DateTime.UtcNow, out string noticeError);
            if (noticeError != null) SetStatus("⚠ " + noticeError);
            var plans = ResumablePlans();
            if (handoff == null || !SelfRestart.IsFresh(handoff, DateTime.UtcNow))
            {
                AfterStartupSettled(ReplayCarriedNotices);
                if (plans.Count > 0) AfterStartupSettled(() => NotifyPlans(plans));
            }
            if (handoff == null) return;
            if (!SelfRestart.IsFresh(handoff, DateTime.UtcNow))
            {
                _resumeExpired = true;
                _agent.ShowLocalNotice("ℹ 已忽略过期的自测重启交接单 / Ignored an expired self-test restart handoff",
                    "交接单创建于 " + handoff.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "，超过 " + (int)SelfRestart.MaxAge.TotalMinutes + " 分钟，未恢复授权也未续跑测试。/ Created too long ago; grants were not restored and no tests were resumed.");
                return;
            }
            var outcome = new SelfRestartOutcome
            {
                NewPid = Process.GetCurrentProcess().Id,
                NewExeWriteUtc = File.GetLastWriteTimeUtc(Application.ExecutablePath),
                DebuggerAttached = Debugger.IsAttached,
                RestoredGrants = _dispatcher.RestoreGrants(handoff),
                RunningTasks = _tasks.Items.Count(t => t.Status == QueueStatus.Running),
                WaitingTasks = _tasks.Items.Count(t => t.Status == QueueStatus.Waiting || t.Status == QueueStatus.WaitingVs)
            };
            _resumeOutcome = outcome;
            _taskPanel.SetWorkflowStarted(_dispatcher.IsStarted);
            string display = SelfRestart.NoticeDisplay(handoff, outcome);
            string content = SelfRestart.NoticeContent(handoff, outcome, SelfIteration.Load(DateTime.UtcNow));
            // 同一项目（或全局）的执行计划并入重启完成通知，其他项目的计划单独通知 / Plans of the same project (or global) join the restart notice; other projects' plans get their own notices
            string restartScope = handoff.Scope ?? "";
            var ownPlans = plans.Where(p => string.IsNullOrEmpty(p.Scope) || p.Scope == restartScope).ToList();
            var otherPlans = plans.Except(ownPlans).ToList();
            if (ownPlans.Count > 0)
            {
                content += "\n\n" + AgentPlans.ResumeNotice(ownPlans);
                AppLog.Write(AgentPlans.LogFile, AgentPlans.NoticeLogLine(ownPlans, true, Process.GetCurrentProcess().Id));
            }
            AppLog.Write(ProcessWatchdog.LogFile, "自测重启完成 / Self-test restart completed: PID " + handoff.OldPid + " → " + outcome.NewPid +
                ", new build " + SelfRestart.LoadedNewBuild(handoff, outcome) + ", grants " + outcome.RestoredGrants);
            SetStatus(display);
            // 等实例列表刷新、助手空闲后再续跑，工具才能看到各 VS / Continue after instances refresh and the assistant is idle so tools see every VS
            AfterStartupSettled(() =>
            {
                if (_settings.AgentEnabled && _agent.Configured)
                {
                    // 重启前在看 VS 对话时不切走页面 / Keep the VS chat page if the user was on it before the restart
                    if (RestoredUi == null) ShowAgent(true, "重启完成通知 / restart notice");
                    _ = _agent.RunAsync(content, display, null, handoff.Scope ?? "");
                }
                else _agent.ShowLocalNotice(display, content);
                ReplayCarriedNotices();
                if (otherPlans.Count > 0) NotifyPlans(otherPlans);
            });
        }

        /// <summary>
        /// 启动时可续跑的执行计划：读回磁盘上进行中的计划，跳过长时间未更新的。
        /// Plans to resume at startup: active plans read back from disk, skipping ones not updated for a long time.
        /// </summary>
        private List<AgentPlan> ResumablePlans()
        {
            var all = AgentPlans.Active(out string error);
            if (error != null) { SetStatus("⚠ " + error); AppLog.Write(AgentPlans.LogFile, error); }
            var fresh = all.Where(p => DateTime.UtcNow - p.UpdatedUtc <= AgentPlans.ResumeWindow).ToList();
            // 每次启动都记一行（含 0 个），供 read_agent_chat 列出历次启动的读回情况 / One line per startup (even 0) so read_agent_chat can list every startup
            if (error == null) AppLog.Write(AgentPlans.LogFile, AgentPlans.StartupLogLine(all.Count, fresh, Process.GetCurrentProcess().Id));
            return fresh;
        }

        /// <summary>按项目分别发出「[执行计划恢复]」通知，让 AI 接着执行。/ Sends "[Plan resumed]" notices per project so the AI continues.</summary>
        private void NotifyPlans(List<AgentPlan> plans)
        {
            foreach (var g in plans.GroupBy(p => p.Scope ?? ""))
            {
                var list = g.ToList();
                AppLog.Write(AgentPlans.LogFile, AgentPlans.NoticeLogLine(list, false, Process.GetCurrentProcess().Id));
                _agent.Notify("📋 执行计划恢复 / Plan resumed：" + string.Join("、", list.Select(p => "#" + p.Id + " " + p.Title)),
                    AgentPlans.ResumeNotice(list), g.Key);
            }
        }
    }
}
