using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 侧边栏「Agent 自循环」区：读取设置中的评测运行目录（<see cref="Settings.AgentEvalDir"/>），在侧边栏画出最近运行的通过率，
    /// 点击后主区域切换到问题汇总页。目录有新结果时自动刷新。
    /// Sidebar "Agent self-loop" section: reads the evaluation run folder from settings (<see cref="Settings.AgentEvalDir"/>), draws the recent
    /// pass rates in the sidebar and switches the main area to the question page on click. Refreshes automatically when new results appear.
    /// </summary>
    public partial class MainForm : IAgentEvalHost
    {
        private readonly AgentEvalCard _evalCard = new AgentEvalCard();
        private readonly AgentEvalPanel _evalPanel = new AgentEvalPanel { Dock = DockStyle.Fill, Visible = false };
        private FileSystemWatcher _evalWatcher;
        private string _evalWatched;
        private Timer _evalDebounce, _evalPoll;
        private int _evalVersion;
        private bool _evalDiscovering;

        /// <summary>主区域正在显示问题汇总页。/ The main area is showing the question page.</summary>
        private bool _evalMode => _workspaceNavigation?.Current == WorkspacePage.AgentEval;

        private void InitAgentEval()
        {
            _evalCard.OpenRequested += run => OpenAgentEval();
            _workspaceSidebar.EvalRequested += OpenAgentEval;
            _workspaceSidebar.SetEvalCollapsed(_settings.AgentEvalCollapsed);
            _workspaceSidebar.EvalCollapsedChanged += collapsed => { _settings.AgentEvalCollapsed = collapsed; _settings.Save(); };
            _tips.SetToolTip(_evalCard, "Agent 自循环：点击在主界面查看问题汇总\nAgent self-loop: click to show the question table in the main window");
            // 文件变化成批到达，合并后再读 / File changes arrive in bursts; coalesce before reading
            _evalDebounce = new Timer { Interval = 1500 };
            _evalDebounce.Tick += (s, e) => { _evalDebounce.Stop(); RefreshAgentEval(); };
            // 有运行中的评测时定期刷新，以便及时转为「无结果」/ Poll while a run is in progress so it turns into "no result" in time
            _evalPoll = new Timer { Interval = 30000 };
            _evalPoll.Tick += (s, e) => { if (_evalCard.Runs.Any(r => r.State == AgentEvalState.Running)) RefreshAgentEval(); };
            _evalPoll.Start();
            Shown += async (s, e) =>
            {
                RefreshAgentEval();
                // 未设置目录时读取代码自动选择 / Without a folder, read code to select one automatically
                if (string.IsNullOrWhiteSpace(_settings.AgentEvalDir)) await DiscoverAgentEvalAsync("", true, false);
            };
            FormClosed += (s, e) => { _evalWatcher?.Dispose(); _evalDebounce.Dispose(); _evalPoll.Dispose(); };
        }

        /// <summary>重新读取评测目录并更新侧边栏与问题汇总页；可在界面线程任意调用。/ Re-reads the evaluation folder and updates the sidebar and the question page; call on the UI thread.</summary>
        private void RefreshAgentEval()
        {
            string configured = _settings.AgentEvalDir;
            string dir = AgentEvalStore.ResolveRunsDir(configured);
            WatchAgentEval(dir);
            if (dir == null)
            {
                string message = string.IsNullOrWhiteSpace(configured)
                    ? "未设置评测目录，点击从代码中自动查找 / No evaluation folder set; click to detect it from code"
                    : "评测目录不存在 / Evaluation folder not found：" + configured;
                _evalCard.SetRuns(null, message);
                _evalPanel.SetRuns(null, message);
                return;
            }
            int version = ++_evalVersion;
            Task.Run(() => AgentEvalStore.Load(dir)).ContinueWith(t =>
            {
                if (IsDisposed || version != _evalVersion) return;
                if (t.IsFaulted)
                {
                    string message = "读取评测目录失败 / Failed to read the evaluation folder：" + t.Exception?.GetBaseException().Message;
                    _evalCard.SetRuns(null, message);
                    _evalPanel.SetRuns(null, message);
                    return;
                }
                _evalCard.SetRuns(t.Result);
                _evalPanel.SetRuns(t.Result, null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void WatchAgentEval(string dir)
        {
            if (string.Equals(dir, _evalWatched, StringComparison.OrdinalIgnoreCase)) return;
            _evalWatcher?.Dispose();
            _evalWatcher = null;
            _evalWatched = dir;
            if (dir == null) return;
            try
            {
                _evalWatcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                FileSystemEventHandler changed = (s, e) =>
                {
                    // 只关心运行结果与用例，忽略 DWG / 日志等大量写入 / Only results and cases matter; ignore DWG / log churn
                    string name = Path.GetFileName(e.FullPath);
                    bool relevant = e.ChangeType != WatcherChangeTypes.Changed
                        ? name.IndexOf('.') < 0 || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        : name.Equals("result.json", StringComparison.OrdinalIgnoreCase);
                    if (relevant && !IsDisposed) BeginInvoke(new Action(() => { _evalDebounce.Stop(); _evalDebounce.Start(); }));
                };
                _evalWatcher.Created += changed;
                _evalWatcher.Changed += changed;
                _evalWatcher.Deleted += changed;
                _evalWatcher.Renamed += (s, e) => changed(s, e);
                _evalWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                AppLog.Error(AgentService.LogFile, "监视评测目录失败 / Failed to watch the evaluation folder", ex);
                _evalWatcher?.Dispose();
                _evalWatcher = null;
            }
        }

        /// <summary>主区域切换到「Agent 自循环」问题汇总页。/ Switches the main area to the "Agent loop" question page.</summary>
        private async void OpenAgentEval()
        {
            if (_workspaceNavigation == null) return;
            if (!_workspaceNavigation.Select(WorkspacePage.AgentEval))
            {
                SetStatus("笔记未保存，无法切换，请查看笔记本底部的错误。 / Note not saved; check the notebook status before switching.");
                return;
            }
            NoteUserNavigation("用户打开 Agent 自循环问题汇总 / user opened the Agent loop questions");
            _agentMode = false;
            _agentCard.Selected = false;
            _workspaceSidebar.SetEvalCollapsed(false);
            if (_list.SelectedIndex >= 0) { _list.ClearSelected(); OnSelectionChanged(); }
            if (AgentEvalStore.ResolveRunsDir(_settings.AgentEvalDir) == null)
            {
                // 先读取代码自动选择，找不到再让用户选文件夹 / Read code first; let the user pick a folder only when nothing is found
                await DiscoverAgentEvalAsync("", true, true);
                if (IsDisposed) return;
                if (AgentEvalStore.ResolveRunsDir(_settings.AgentEvalDir) == null && !ChooseAgentEvalFolder()) return;
            }
            RefreshAgentEval();
        }

        /// <summary>选择评测目录并保存（尽量写成环境变量形式，避免设置里出现本机用户路径）。/ Picks and saves the evaluation folder (stored with environment variables where possible).</summary>
        private bool ChooseAgentEvalFolder()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "选择 Agent 评测运行目录（含各次运行子目录，或含 runs 子目录）/ Choose the agent evaluation run folder (holding run sub-folders, or a runs sub-folder)",
                ShowNewFolderButton = false,
                SelectedPath = Environment.ExpandEnvironmentVariables(_settings.AgentEvalDir ?? ""),
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                _settings.AgentEvalDir = CompactEnvPath(dialog.SelectedPath);
                _settings.Save();
            }
            RefreshAgentEval();
            return true;
        }

        Task<string> IAgentEvalHost.FindAgentEvalFolder(string path, bool apply) => OnUiAsync(() => DiscoverAgentEvalAsync(path, apply, true));

        Task<string> IAgentEvalHost.DescribeAgentEvalIssues(string state) => OnUiAsync(async () =>
        {
            string dir = AgentEvalStore.ResolveRunsDir(_settings.AgentEvalDir);
            if (dir == null)
                return string.IsNullOrWhiteSpace(_settings.AgentEvalDir)
                    ? "未设置评测目录，请先调用 find_agent_eval_folder / No evaluation folder set; call find_agent_eval_folder first"
                    : "评测目录不存在 / Evaluation folder not found：" + _settings.AgentEvalDir;
            var runs = await Task.Run(() => AgentEvalStore.Load(dir));
            return "评测目录 / Evaluation folder：" + _settings.AgentEvalDir + "\n" + AgentEvalStore.DescribeIssues(runs, state);
        });

        /// <summary>代码根：path 指定的文件夹，或已打开 / 已登记解决方案与文件根所在的仓库。/ Code roots: the given folder, or the repositories of open / registered solutions and file roots.</summary>
        private List<string> AgentEvalCodeRoots(string path) => AgentEvalDiscovery.CodeRoots(!string.IsNullOrWhiteSpace(path)
            ? new[] { path }
            : _instances.Select(v => v.SolutionPath).Concat(_solutions.Items.Select(e => e.Path)).Concat(_settings.AgentFileRoots ?? new List<string>()).ToArray());

        /// <summary>
        /// 读取代码查找评测运行目录；apply 时选择最近有运行的文件夹并保存（尽量保留代码中的环境变量形式）。返回候选说明。
        /// Reads code to find evaluation run folders; with apply, selects the folder with the latest runs and saves it (keeping the
        /// environment-variable form from code where possible). Returns the candidate description.
        /// </summary>
        private async Task<string> DiscoverAgentEvalAsync(string path, bool apply, bool report)
        {
            if (_evalDiscovering) return "正在读取代码查找评测目录，请稍候 / Already reading code for the evaluation folder";
            _evalDiscovering = true;
            try
            {
                var roots = AgentEvalCodeRoots(path);
                if (roots.Count == 0)
                    return "没有可读取的代码文件夹（未打开或登记解决方案）/ No code folder to read (no open or registered solution)"
                        + (string.IsNullOrWhiteSpace(path) ? "" : "：" + path);
                bool unset = AgentEvalStore.ResolveRunsDir(_settings.AgentEvalDir) == null;
                if (unset) _evalCard.SetRuns(null, "正在读取代码查找评测目录… / Reading code to find the evaluation folder…");
                var found = await Task.Run(() => AgentEvalDiscovery.Discover(roots));
                string text = AgentEvalDiscovery.Describe(found, roots);
                var best = AgentEvalDiscovery.Best(found);
                if (apply && best != null)
                {
                    string value = best.Display.StartsWith("%", StringComparison.Ordinal) ? best.Display : CompactEnvPath(best.Folder);
                    bool changed = !string.Equals(value, _settings.AgentEvalDir, StringComparison.OrdinalIgnoreCase);
                    _settings.AgentEvalDir = value;
                    if (changed) _settings.Save();
                    text += "\n已选择 / Selected：" + value;
                    if (report) SetStatus("Agent 自循环：已从代码中选择评测目录 / Agent loop: evaluation folder selected from code：" + value);
                }
                else if (apply)
                {
                    text += "\n未找到含运行的评测目录，保持原设置 / No folder with runs found; the setting is unchanged";
                    if (report) SetStatus("Agent 自循环：代码中没有找到含运行的评测目录 / Agent loop: no evaluation folder with runs found in code");
                }
                RefreshAgentEval();
                return text;
            }
            catch (Exception ex)
            {
                AppLog.Error(AgentService.LogFile, "查找评测目录失败 / Failed to find the evaluation folder", ex);
                RefreshAgentEval();
                return "查找评测目录失败 / Failed to find the evaluation folder：" + ex.Message;
            }
            finally { _evalDiscovering = false; }
        }

        /// <summary>把路径开头的 %LOCALAPPDATA% / %APPDATA% / %USERPROFILE% 还原成变量。/ Rewrites a leading %LOCALAPPDATA% / %APPDATA% / %USERPROFILE% back to the variable.</summary>
        internal static string CompactEnvPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            foreach (string name in new[] { "LOCALAPPDATA", "APPDATA", "USERPROFILE" })
            {
                string value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(value)) continue;
                value = value.TrimEnd('\\');
                if (path.Equals(value, StringComparison.OrdinalIgnoreCase)) return "%" + name + "%";
                if (path.StartsWith(value + "\\", StringComparison.OrdinalIgnoreCase)) return "%" + name + "%" + path.Substring(value.Length);
            }
            return path;
        }
    }
}
