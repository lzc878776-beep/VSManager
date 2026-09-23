using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
	public partial class MainForm : Form, IRemoteHost, IAgentHost, ITaskDispatchHost
	{
		private readonly AppSettings _settings = AppSettings.Load();
		private volatile List<VsInstance> _instances = new List<VsInstance>();
		private readonly CopilotMonitor _monitor;
		private readonly CopilotChat _chatSvc;
		private readonly WebRemote _web;
		private bool _webWasActive;
		private bool _exiting;
		private bool _refreshing;
		private bool _sending;

		private readonly VsListBox _list = new VsListBox();
		private readonly ChatPanel _chat = new ChatPanel();
		private readonly Dictionary<string, FlatButton> _dbgButtons = new Dictionary<string, FlatButton>();
		private readonly Panel _header = new Panel();
		private readonly Panel _sidebar = new Panel();
		private readonly Label _sideCount = new Label();
		private readonly Label _status = new Label();
		private readonly FlatButton _btnSettings = new FlatButton();
		private readonly FlatButton _btnVoice = new FlatButton();
		private readonly FlatButton _btnPublish = new FlatButton();
		private readonly FlatButton _btnHistory = new FlatButton();
		private AgentHistoryForm _historyForm;
		private readonly FlatButton _btnMemory = new FlatButton();
		private MemoryForm _memoryForm;
		private readonly MemoryGuard _memGuard = new MemoryGuard();
		private string _archiveWarning;
		private readonly NotifyIcon _tray = new NotifyIcon();
		private readonly Timer _refreshTimer = new Timer { Interval = 3000 };
		private readonly ToolTip _tips = new ToolTip();
		private Icon _headerIcon;
		private readonly IVoiceService _voice;
		/// <summary>VS 进程与 DTE 操作（可替换，便于测试）。/ VS process and DTE operations (replaceable for tests).</summary>
		private readonly IVsOperations _vsOps = VsOperations.Default;
		private readonly AgentService _agent;
		/// <summary>AI 助手故障监督（自动重启与防风暴）。/ AI assistant supervisor (auto-restart and storm guard).</summary>
		private readonly AgentSupervisor _agentSupervisor;
		private readonly FlatButton _btnRestart = new FlatButton();
		/// <summary>异常退出重启后被暂停的任务数。/ Tasks paused after a restart from an abnormal exit.</summary>
		private int _pausedAfterCrash;
		private readonly AgentPanel _agentPanel = new AgentPanel();
		private readonly AgentCard _agentCard = new AgentCard();
		private bool _agentMode;

		// 对话缓存：每个 VS 最近一次读取到的对话与未发送的输入草稿，切换时立即显示
		private readonly Dictionary<int, ChatTranscript> _chatCache = new Dictionary<int, ChatTranscript>();
		private readonly Dictionary<int, string> _drafts = new Dictionary<int, string>();
		/// <summary>界面刚发送、尚未出现在 Copilot 对话中的消息（先显示在对话末尾，避免“发出去没反应”的空档）。</summary>
		private readonly Dictionary<int, PendingSend> _pendingSend = new Dictionary<int, PendingSend>();

		private sealed class PendingSend
		{
			public string Text;
			public DateTime At;
			/// <summary>发送前对话中最后一条用户消息，变化即表示新消息已出现。</summary>
			public string Before;
		}
		/// <summary>已送达、等待 Copilot 开始响应的截止时间。</summary>
		private readonly Dictionary<int, DateTime> _awaitReply = new Dictionary<int, DateTime>();
		private readonly Dictionary<int, IReadOnlyList<ChatImage>> _imageDrafts = new Dictionary<int, IReadOnlyList<ChatImage>>();

		// 任务清单：目标 VS 忙碌时排队，空闲后自动发布，完成后通知 AI 助手
		private readonly TaskQueue _tasks;
		/// <summary>任务调度（发布、跟踪、完成 / 失败通知）。/ Task dispatching (publish, track, completion / failure notices).</summary>
		private readonly TaskDispatcher _dispatcher;
		private readonly TaskPanel _taskPanel = new TaskPanel();
		private readonly Timer _taskTimer = new Timer { Interval = 2000 };
		/// <summary>启动后的一段时间内 VS 列表与 Copilot 状态尚未就绪，不据此判定执行中的任务失败或完成。</summary>
		private DateTime _tasksReadyAt = DateTime.MaxValue;

		// VS 手动对话：监听到的、不是由本工具发送的 Copilot 提问（仅内存，与任务队列互不影响）
		private readonly List<ExternalChat> _externals = new List<ExternalChat>();
		/// <summary>每个 VS 最近看到的提问，用于判断是否出现新的一轮对话。</summary>
		private readonly Dictionary<int, string> _lastQuestion = new Dictionary<int, string>();
		/// <summary>经 VSManager 发送的消息（任务清单 / 内置对话 / 网页 / AI 助手），这些不算“手动对话”。</summary>
		private readonly List<(int Pid, string Text, DateTime At)> _recentSends = new List<(int, string, DateTime)>();
		private int _nextExternalId = 1;

		// 启动配置（launchSettings.json）
		private readonly DarkCombo _profileCombo = new DarkCombo();
		private readonly Dictionary<int, VsService.LaunchProfiles> _profiles = new Dictionary<int, VsService.LaunchProfiles>();
		private bool _profileUpdating;
		private bool _profileShown;

		public MainForm()
		{
			Text = "多 VS 管理工具";
			Font = Theme.Regular;
			BackColor = Theme.Background;
			ForeColor = Theme.Text;
			var wa = Screen.PrimaryScreen.WorkingArea;
			Size = new Size(Math.Min(Dpi.S(1360), wa.Width), Math.Min(Dpi.S(880), wa.Height));
			MinimumSize = new Size(Math.Min(Dpi.S(960), wa.Width), Math.Min(Dpi.S(620), wa.Height));
			StartPosition = FormStartPosition.CenterScreen;
			TopMost = _settings.TopMost;
			try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

			// 归档目录需在任务清单 / 发送日志开始写入前确定；保存一次以确保 settings.json 中有 ArchiveRoot
			_archiveWarning = Archive.Configure(_settings);
			_settings.Save();

			_voice = new DoubaoVoice(() => _settings);
			_voice.Failed += err => SafeInvoke(() => SetStatus("豆包语音播报失败：" + err));
			_voice.Notice += msg => SafeInvoke(() => SetStatus("🔊 " + msg));
			_agent = new AgentService(this, () => _settings);
			_agentSupervisor = new AgentSupervisor(_agent, () => _settings);
			_agentSupervisor.Notice += msg => SafeInvoke(() =>
			{
				SetStatus(msg);
				if (msg.StartsWith("⚠", StringComparison.Ordinal)) ShowBalloon("AI 助手 / AI assistant", msg, ToolTipIcon.Warning);
			});
			AgentChatLog.Limits = () => Tuple.Create(_settings.AgentChatKeepDays, _settings.AgentChatMaxRecords);
			_tasks = new TaskQueue(_settings);
			// 异常退出后由看门狗重启：上次正在发送的任务可能已送达 VS，改为失败待手动重新排队，避免重复发布
			// Restarted by the watchdog after an abnormal exit: tasks that were being sent may already have reached VS, so they
			// are marked failed for a manual requeue instead of being published twice
			if (ProcessWatchdog.RestartedAfterCrash)
				_pausedAfterCrash = _tasks.PauseInterruptedSends("VSManager 异常退出时正在发送，可能已送达；为避免重复发布已暂停，确认后请手动重新排队 / " +
					"Was being sent when VSManager exited abnormally and may have been delivered; paused to avoid a duplicate, requeue manually if needed");
			AppDomain.CurrentDomain.UnhandledException += (s, e) => EmergencySave();
			_dispatcher = new TaskDispatcher(_tasks, this);

			_chatSvc = new CopilotChat(() => _settings);
			_chatSvc.Updated += (vs, t) => SafeInvoke(() => OnChatUpdated(vs, t));
			_chatSvc.Instances = () => _instances;
			_chatSvc.Opening += vs => SafeInvoke(() =>
			{
				if (vs == Selected) _chat.SetEmpty("正在「" + NameOf(vs) + "」中打开 Copilot 对话助手…");
			});

			BuildUi();

			_web = new WebRemote(this, () => _settings);
			_web.StatusChanged += () => SafeInvoke(() => _header.Invalidate());
			_chatSvc.RemoteActive = () => _web.Active;

			_monitor = new CopilotMonitor(() => _instances, () => _settings);
			_monitor.StateChanged += vs => SafeInvoke(() => UpdateRow(vs));
			_monitor.Completed += (vs, dur) => SafeInvoke(() => OnCopilotCompleted(vs, dur));
			_monitor.ReadTail = (vs, skip) => _chatSvc.ReadTail(vs, 2, skip);
			_monitor.Conversation += (vs, t, busy) => SafeInvoke(() => OnConversation(vs, t, busy));
			_monitor.PaneRestored += (vs, ok) =>
			{
				if (ok) _chatSvc?.PaneRestored(vs.Pid);
				string msg = ok ? "Copilot 对话窗格被切走，已自动切回" : "未能切回 Copilot 对话窗格";
				SafeInvoke(() => SetStatus(NameOf(vs) + "：" + msg));
			};
			Activated += (s, e) => UpdateChatHeader();

			_refreshTimer.Tick += (s, e) =>
			{
				RefreshInstances();
				bool webActive = _web.Active;
				if (webActive != _webWasActive) { _webWasActive = webActive; _header.Invalidate(); }
			};
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			Theme.DarkTitleBar(this);
			// 全局快捷键 Ctrl+Alt+0：随时唤出主窗口 / Global hotkey Ctrl+Alt+0 shows the main window at any time
			_showHotkeyOk = Native.RegisterHotKey(Handle, ShowHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x30);
		}

		private const int ShowHotkeyId = 100;
		private bool _showHotkeyOk;

		#region UI 构建

		private void BuildUi()
		{
			SuspendLayout();

			// ---- 顶部标题栏 ----
			_header.Dock = DockStyle.Top;
			_header.Height = Dpi.S(58);
			SetDoubleBuffered(_header);
			_header.Paint += Header_Paint;
			_header.Resize += (s, e) => { LayoutHeader(); _header.Invalidate(); };
			_btnSettings.Text = "⚙  属性";
			_btnSettings.Size = new Size(Dpi.S(96), Dpi.S(34));
			_btnSettings.Click += (s, e) => OpenSettings();
			_tips.SetToolTip(_btnSettings, "屏幕布局、快捷操作与选项");
			_header.Controls.Add(_btnSettings);
			_btnVoice.Size = new Size(Dpi.S(118), Dpi.S(34));
			_btnVoice.Click += (s, e) => ToggleVoice();
			_header.Controls.Add(_btnVoice);
			UpdateVoiceButton();
			_btnPublish.Text = "🚀  发布";
			_btnPublish.Size = new Size(Dpi.S(96), Dpi.S(34));
			_btnPublish.Click += (s, e) => OpenPublish();
			_tips.SetToolTip(_btnPublish, "发布到 GitHub（含敏感信息自检）\nPublish to GitHub (with a sensitive-content scan)");
			_header.Controls.Add(_btnPublish);
			_btnHistory.Text = "📜  对话记录";
			_btnHistory.Size = new Size(Dpi.S(118), Dpi.S(34));
			_btnHistory.Click += (s, e) => OpenAgentHistory();
			_tips.SetToolTip(_btnHistory, "查看 AI 助手全部历史对话（支持搜索、按日期筛选）\nView the full AI assistant chat history (search, filter by date)");
			_header.Controls.Add(_btnHistory);
			_btnMemory.Text = "🧠  内存";
			_btnMemory.Size = new Size(Dpi.S(92), Dpi.S(34));
			_btnMemory.Click += (s, e) => OpenMemory();
			_tips.SetToolTip(_btnMemory, "查看 VSManager 与各 VS（含子进程）的内存占用，并温和清理\nView memory of VSManager and each VS (with child processes) and clean gently");
			_header.Controls.Add(_btnMemory);
			_btnRestart.Text = "⟳  重启";
			_btnRestart.Size = new Size(Dpi.S(92), Dpi.S(34));
			_btnRestart.ContextMenuStrip = BuildRestartMenu();
			_btnRestart.Click += (s, e) => _btnRestart.ContextMenuStrip.Show(_btnRestart, new Point(0, _btnRestart.Height));
			_tips.SetToolTip(_btnRestart, "重启 AI 助手 / 重启 VSManager，以及自动重启与看门狗开关\nRestart the AI assistant / VSManager, and the auto-restart and watchdog switches");
			_header.Controls.Add(_btnRestart);
			_memGuard.Notify += msg => BeginInvoke(new Action(() =>
			{
				SetStatus("🧠 " + msg);
				_tray.ShowBalloonTip(6000, "内存 / Memory", msg, ToolTipIcon.Warning);
			}));

			// ---- 侧边栏 ----
			_sidebar.Dock = DockStyle.Left;
			_sidebar.Width = _settings.SidebarWidth > 0 ? Math.Max(Dpi.S(240), _settings.SidebarWidth) : Dpi.S(320);
			_sidebar.BackColor = Theme.Sidebar;
			_sidebar.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height); };
			_sidebar.Padding = new Padding(0, 0, 1, 0);

			var sideTop = new Panel { Dock = DockStyle.Top, Height = Dpi.S(52), BackColor = Theme.Sidebar };
			var sideTitle = new Label
			{
				Text = "VS 实例", Font = Theme.SemiBold, ForeColor = Theme.Text, AutoSize = true, BackColor = Theme.Sidebar,
				Location = new Point(Dpi.S(20), Dpi.S(17))
			};
			_sideCount.AutoSize = true;
			_sideCount.Font = Theme.Small;
			_sideCount.ForeColor = Theme.TextMuted;
			_sideCount.BackColor = Theme.Sidebar;
			_sideCount.Location = new Point(Dpi.S(84), Dpi.S(19));
			var btnRefresh = new FlatButton { Text = "↻", Ghost = true, Size = new Size(Dpi.S(34), Dpi.S(30)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
			btnRefresh.Font = new Font(Theme.FontName, 11F);
			btnRefresh.Click += (s, e) => { SetStatus("正在刷新 VS 实例…"); RefreshInstances(); UpdateProfiles(true); };
			_tips.SetToolTip(btnRefresh, "刷新列表");
			sideTop.Controls.Add(sideTitle);
			sideTop.Controls.Add(_sideCount);
			sideTop.Controls.Add(btnRefresh);
			sideTop.Resize += (s, e) => btnRefresh.Location = new Point(sideTop.Width - btnRefresh.Width - Dpi.S(12), Dpi.S(11));

			_list.Dock = DockStyle.Fill;
			_list.ItemHeight = Dpi.S(88);
			_list.EmptyText = "未发现正在运行的 Visual Studio\r\n启动 VS 后会自动出现在这里";
			_list.DrawItem += List_DrawItem;
			_list.SelectedIndexChanged += (s, e) => BeginInvoke(new Action(OnSelectionChanged));
			_list.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && _settings.ClickToActivate && ItemAt(e.Location) is VsInstance v) ActivateVs(v); };
			_list.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left && ItemAt(e.Location) is VsInstance v) ActivateVs(v); };
			_list.KeyDown += (s, e) =>
			{
				if (e.KeyCode == Keys.F2) RenameSelected();
				else if (e.KeyCode == Keys.Enter && Selected != null) ActivateVs(Selected);
			};
			_list.Resize += (s, e) => _list.Invalidate();
			_list.ContextMenuStrip = BuildListMenu();

			var sideHint = new Label
			{
				Dock = DockStyle.Bottom, Height = Dpi.S(52), ForeColor = Theme.TextMuted, Font = Theme.Small, BackColor = Theme.Sidebar,
				Padding = new Padding(Dpi.S(20), 0, Dpi.S(12), Dpi.S(6)), TextAlign = ContentAlignment.MiddleLeft,
				Text = "双击激活 · F2 重命名 · 右键更多\r\nCtrl+Alt+数字 快速切换"
			};
			sideHint.Paint += (s, e) => { using (var pen = new Pen(Theme.Divider)) e.Graphics.DrawLine(pen, 0, 0, sideHint.Width, 0); };

			// AI 总控助手：侧边栏卡片 + 主区域对话
			_agentCard.Dock = DockStyle.Top;
			_agentCard.Bind(_agent);
			_agentCard.Click += (s, e) => ShowAgent(true);
			_tips.SetToolTip(_agentCard, "AI 总控助手：统一管理所有 VS、发布任务");
			_agentPanel.Dock = DockStyle.Fill;
			_agentPanel.Visible = false;
			_agentPanel.Bind(_agent);
			_agentPanel.RefreshConfig();
			_agentPanel.SettingsRequested += OpenSettings;

			_sidebar.Controls.Add(_list);
			_sidebar.Controls.Add(sideHint);
			_sidebar.Controls.Add(sideTop);
			_sidebar.Controls.Add(_agentCard);
			UpdateAgentVisibility();

			var splitter = new Splitter { Dock = DockStyle.Left, Width = Dpi.S(4), BackColor = Theme.Background, MinSize = Dpi.S(240), MinExtra = Dpi.S(560) };
			splitter.SplitterMoved += (s, e) => { _settings.SidebarWidth = _sidebar.Width; _settings.Save(); _list.Invalidate(); };

			// ---- 主区域：对话 ----
			_chat.Dock = DockStyle.Fill;
			_chat.SendRequested += OnChatSend;
			_chat.VoiceBegin += OnVoiceBegin;
			_chat.VoiceEnd += () => OnVoiceEnd(false);
			_chat.VoiceCancel += () => OnVoiceEnd(true);
			_chat.StopRequested += () => InvokeChatButton("CancelButton", "停止 Copilot");
			_chat.NewThreadRequested += () => InvokeChatButton("createNewThread", "新建对话线程");
			_chat.OpenInVsRequested += () => { if (Selected != null) ActivateVs(Selected); };
			_chat.OpenPaneRequested += OpenPane;
			_chat.DockPaneRequested += () => DockAllPanes();
			_chat.ShowSteps.Checked = _settings.ShowChatSteps;
			_chat.ShowSteps.CheckedChanged += (s, e) => { _settings.ShowChatSteps = _chat.ShowSteps.Checked; _settings.Save(); _chatSvc.Poke(); };
			BuildDebugBar(_chat.Toolbar);
			UpdateChatHint();

			// ---- 状态栏 ----
			var statusBar = new Panel { Dock = DockStyle.Bottom, Height = Dpi.S(28), BackColor = Theme.Sidebar };
			statusBar.Paint += (s, e) =>
			{
				using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, statusBar.Width, 0);
				e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
				Theme.FillCircle(e.Graphics, Theme.IdleDot, Dpi.S(18), statusBar.Height / 2f, Dpi.S(3));
			};
			_status.Dock = DockStyle.Fill;
			_status.AutoEllipsis = true;
			_status.TextAlign = ContentAlignment.MiddleLeft;
			_status.ForeColor = Theme.TextSecondary;
			_status.BackColor = Theme.Sidebar;
			_status.Font = Theme.Small;
			_status.Padding = new Padding(Dpi.S(28), 0, Dpi.S(12), 0);
			_status.Text = "就绪";
			var version = new Label
			{
				Dock = DockStyle.Right, AutoSize = false, Width = Dpi.S(90), TextAlign = ContentAlignment.MiddleRight, BackColor = Theme.Sidebar,
				ForeColor = Theme.TextMuted, Font = Theme.Small, Padding = new Padding(0, 0, Dpi.S(14), 0),
				Text = "v" + typeof(MainForm).Assembly.GetName().Version.ToString(3)
			};
			statusBar.Controls.Add(_status);
			statusBar.Controls.Add(version);

			_taskPanel.Bind(_tasks, () => _externals, () => _settings.TaskListClearedAt, () => _settings.HiddenResentTasks);
			_taskPanel.ExternalActionRequested += OnExternalAction;
			_taskPanel.SetCollapsed(_settings.TaskPanelCollapsed);
			_taskPanel.CollapsedChanged += c => { _settings.TaskPanelCollapsed = c; _settings.Save(); };
			_taskPanel.ActionRequested += OnTaskAction;
			_taskTimer.Tick += (s, e) => PumpTasks();

			Controls.Add(_chat);
			Controls.Add(_agentPanel);
			Controls.Add(_taskPanel);
			Controls.Add(splitter);
			Controls.Add(_sidebar);
			Controls.Add(_header);
			Controls.Add(statusBar);

			// ---- 托盘 ----
			_tray.Icon = Icon ?? SystemIcons.Application;
			_tray.Text = "多 VS 管理工具";
			_tray.Visible = true;
			var trayMenu = new ContextMenuStrip();
			Theme.Apply(trayMenu);
			trayMenu.Opening += (s, e) => BuildTrayMenu(trayMenu);
			_tray.ContextMenuStrip = trayMenu;
			_tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMe(); };
			_tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMe(); };

			ResumeLayout(true);
		}

		private static void SetDoubleBuffered(Control c) =>
			typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
				?.SetValue(c, true, null);

		private ContextMenuStrip BuildListMenu()
		{
			var ctx = new ContextMenuStrip();
			Theme.Apply(ctx);
			ctx.Items.Add("激活到前台", null, (s, e) => { if (Selected != null) ActivateVs(Selected); });
			ctx.Items.Add("重命名 (F2)", null, (s, e) => RenameSelected());
			ctx.Items.Add("职责描述（AI 自动分派）…", null, (s, e) => EditNote());
			ctx.Items.Add("登记此解决方案 / Register this solution", null, (s, e) => RegisterSelectedSolution());
			ctx.Items.Add("解决方案登记… / Solution registry…", null, (s, e) => OpenSolutionRegistry(this));
			ctx.Items.Add(new ToolStripSeparator());
			ctx.Items.Add("打开 Copilot 对话助手", null, (s, e) => OpenPane());
			ctx.Items.Add("Copilot 切换为工具窗模式（全部 VS）", null, (s, e) => DockAllPanes());
			ctx.Items.Add("一键布局（主界面 + 工具窗）", null, (s, e) => QuickLayout());
			ctx.Items.Add("主界面 → 主屏幕", null, (s, e) => { if (Check()) MoveMain(Selected, true); });
			ctx.Items.Add("输出/错误栏 → 副屏幕", null, (s, e) => { if (Check()) MoveTools(Selected); });
			ctx.Items.Add(new ToolStripSeparator());
			ctx.Items.Add("启动调试 / 继续 (F5)", null, (s, e) => DoDebug("go"));
			ctx.Items.Add("停止调试", null, (s, e) => DoDebug("stop"));
			ctx.Items.Add("生成解决方案", null, (s, e) => DoDebug("build"));
			ctx.Items.Add(new ToolStripSeparator());
			ctx.Items.Add("清除名称", null, (s, e) =>
			{
				if (Selected == null) return;
				_settings.SetAlias(Selected.Key, null);
				_settings.Save();
				UpdateRow(Selected);
				UpdateChatHeader();
			});
			ctx.Items.Add("查看发送日志", null, (s, e) => OpenSendLog());
			ctx.Items.Add("打开配置目录", null, (s, e) =>
			{
				try { System.Diagnostics.Process.Start("explorer.exe", "\"" + System.IO.Path.GetDirectoryName(AppSettings.FilePath) + "\""); }
				catch (Exception ex) { SetStatus("无法打开配置目录：" + ex.Message); }
			});
			ctx.Items.Add("属性…", null, (s, e) => OpenSettings());
			return ctx;
		}

		private void OpenSendLog()
		{
			try
			{
				string f = SendLog.TodayFile;
				if (File.Exists(f)) System.Diagnostics.Process.Start("notepad.exe", "\"" + f + "\"");
				else
				{
					Directory.CreateDirectory(SendLog.Folder);
					System.Diagnostics.Process.Start("explorer.exe", "\"" + SendLog.Folder + "\"");
				}
			}
			catch (Exception ex) { SetStatus("打开发送日志失败：" + ex.Message); }
		}
		private void OpenPane()
		{
			if (!Check()) return;
			_chat.SetEmpty("正在「" + NameOf(Selected) + "」中打开 Copilot 对话助手…");
			_chatSvc.RequestOpen();
		}

		private bool _docking;

		/// <summary>一键把所有 VS 的 Copilot 对话窗格切换为停靠的工具窗口。</summary>
		private async Task<string> DockAllPanes()
		{
			if (_docking) return "正在切换中";
			var list = _instances.ToList();
			if (list.Count == 0) { SetStatus("没有正在运行的 VS"); return "没有正在运行的 VS"; }
			_docking = true;
			_chat.SetDocking(true);
			SetStatus("正在把 Copilot 对话助手切换为工具窗口…");
			var fg = Native.GetForegroundWindow();
			var lines = new List<string>();
			int changed = 0, failed = 0;
			try
			{
				foreach (var v in list)
				{
					string r = await DteWorker.Run(() => _vsOps.DockCopilotAsToolWindow(v, _settings.CopilotPaneKeyword));
					if (r.StartsWith("已切换")) changed++;
					else if (!r.StartsWith("已是")) failed++;
					lines.Add(NameOf(v) + "：" + r);
					_chatSvc.PaneRestored(v.Pid);
				}
			}
			finally
			{
				_docking = false;
				_chat.SetDocking(false);
			}
			if (fg != IntPtr.Zero && Native.GetForegroundWindow() != fg) Native.Activate(fg);
			string summary = $"工具窗模式：{list.Count} 个 VS，切换 {changed} 个" + (failed > 0 ? $"，失败 {failed} 个" : "，其余已是工具窗口");
			SetStatus(summary);
			return summary + "\n" + string.Join("\n", lines);
		}

		private void LayoutHeader()
		{
			_btnSettings.Location = new Point(_header.Width - _btnSettings.Width - Dpi.S(16), (_header.Height - _btnSettings.Height) / 2);
			_btnVoice.Location = new Point(_btnSettings.Left - _btnVoice.Width - Dpi.S(8), _btnSettings.Top);
			_btnPublish.Location = new Point(_btnVoice.Left - _btnPublish.Width - Dpi.S(8), _btnSettings.Top);
			_btnHistory.Location = new Point(_btnPublish.Left - _btnHistory.Width - Dpi.S(8), _btnSettings.Top);
			_btnMemory.Location = new Point(_btnHistory.Left - _btnMemory.Width - Dpi.S(8), _btnSettings.Top);
			_btnRestart.Location = new Point(_btnMemory.Left - _btnRestart.Width - Dpi.S(8), _btnSettings.Top);
		}

		/// <summary>当前 VS 列表的快照（编号与名称与主界面一致）。/ Snapshot of the VS list (numbers and names as in the main window).</summary>
		private IList<VsRef> BuildVsRefs()
		{
			var list = _instances;
			var refs = new List<VsRef>(list.Count);
			for (int i = 0; i < list.Count; i++)
			{
				string name;
				try { name = NameOf(list[i]); } catch { name = list[i].DisplaySolution; }
				refs.Add(new VsRef { Vs = list[i], Number = i + 1, Name = name });
			}
			return refs;
		}

		/// <summary>打开「内存」面板（非模态，已打开时切到前台并刷新）。/ Opens the memory panel (modeless; brings it to front when already open).</summary>
		private void OpenMemory()
		{
			try
			{
				if (_memoryForm == null || _memoryForm.IsDisposed)
				{
					_memoryForm = new MemoryForm(_settings, BuildVsRefs, SetStatus);
					_memoryForm.FormClosed += (s, e) => _memoryForm = null;
					_memoryForm.Show(this);
				}
				else
				{
					if (_memoryForm.WindowState == FormWindowState.Minimized) _memoryForm.WindowState = FormWindowState.Normal;
					_memoryForm.Activate();
					var _ = _memoryForm.RefreshAsync();
				}
			}
			catch (Exception ex) { SetStatus("内存面板出错 / Memory panel error：" + ex.Message); }
		}

		/// <summary>
		/// 打开「对话记录」窗口（非模态，已打开时切到前台并刷新）。
		/// Opens the chat history window (modeless; brings it to front and refreshes when already open).
		/// </summary>
		private void OpenAgentHistory()
		{
			try
			{
				if (_historyForm == null || _historyForm.IsDisposed)
				{
					_historyForm = new AgentHistoryForm();
					_historyForm.FormClosed += (s, e) => _historyForm = null;
					_historyForm.Show(this);
				}
				else
				{
					_historyForm.Reload();
					if (_historyForm.WindowState == FormWindowState.Minimized) _historyForm.WindowState = FormWindowState.Normal;
					_historyForm.Activate();
				}
			}
			catch (Exception ex) { SetStatus("对话记录窗口出错：" + ex.Message); }
		}

		/// <summary>打开「发布到 GitHub」窗口；异常只提示，不影响主程序。/ Opens the publish window; errors are reported without affecting the app.</summary>
		private void OpenPublish()
		{
			try
			{
				using (var f = new PublishForm(_settings)) f.ShowDialog(this);
			}
			catch (Exception ex) { SetStatus("发布窗口出错：" + ex.Message); }
		}

		/// <summary>顶部「语音播报」开关：与设置项 VoiceEnabled 双向同步，关闭时立即停止正在播报的语音。</summary>
		private void ToggleVoice()
		{
			_settings.VoiceEnabled = !_settings.VoiceEnabled;
			if (!_settings.VoiceEnabled) _voice.StopAll();
			bool saved = _settings.Save();
			UpdateVoiceButton();
			string msg = _settings.VoiceEnabled ? "🔊 已开启任务完成语音播报" : "🔇 已关闭任务完成语音播报";
			if (_settings.VoiceEnabled && !_settings.HasVoiceKey) msg += "（尚未填写豆包语音 API Key，请在「属性」中配置）";
			SetStatus(saved ? msg : msg + "（保存设置失败，重启后可能不生效）");
		}

		private void UpdateVoiceButton()
		{
			bool on = _settings.VoiceEnabled;
			_btnVoice.Text = on ? "🔊  语音" : "🔇  语音已关";
			_btnVoice.Tint = on ? Theme.IdleDot : Theme.TextMuted;
			string tip = on ? "语音播报已开启，点击关闭任务完成语音播报" : "语音播报已关闭，点击开启任务完成语音播报";
			if (on && !_settings.HasVoiceKey) tip += "\n（尚未填写豆包语音 API Key，暂不会播报）";
			_tips.SetToolTip(_btnVoice, tip);
			_btnVoice.Invalidate();
		}

		private void Header_Paint(object sender, PaintEventArgs e)
		{
			var g = e.Graphics;
			var r = _header.ClientRectangle;
			if (r.Width <= 0 || r.Height <= 0) return;
			using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(r, Theme.HeaderStart, Theme.HeaderEnd, 0F))
				g.FillRectangle(br, r);
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
			using (var glow = new SolidBrush(Color.FromArgb(14, 139, 92, 246)))
				g.FillEllipse(glow, r.Width - Dpi.S(520), -Dpi.S(90), Dpi.S(420), Dpi.S(200));
			using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, r.Height - 1, r.Width, r.Height - 1);

			int iconSize = Dpi.S(34);
			var iconBox = new RectangleF(Dpi.S(16), (r.Height - iconSize) / 2f, iconSize, iconSize);
			Theme.FillRound(g, Color.FromArgb(40, 139, 92, 246), iconBox, Dpi.S(9));
			Theme.DrawRound(g, Color.FromArgb(70, 139, 92, 246), iconBox, Dpi.S(9));
			if (Icon != null)
			{
				if (_headerIcon == null) try { _headerIcon = new Icon(Icon, Dpi.S(22), Dpi.S(22)); } catch { }
				if (_headerIcon != null)
					g.DrawIcon(_headerIcon, new Rectangle((int)(iconBox.X + (iconSize - Dpi.S(22)) / 2f), (int)(iconBox.Y + (iconSize - Dpi.S(22)) / 2f), Dpi.S(22), Dpi.S(22)));
			}

			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
			int tx = (int)iconBox.Right + Dpi.S(12);
			TextRenderer.DrawText(g, "多 VS 管理工具", Theme.AppTitle, new Point(tx, Dpi.S(9)), Theme.Text, TextFormatFlags.NoPadding);
			TextRenderer.DrawText(g, "Copilot 多实例控制台 · 对话 · 调试 · 布局", Theme.Small, new Point(tx + 1, Dpi.S(33)), Theme.TextMuted, TextFormatFlags.NoPadding);

			var list = _instances;
			int busy = list.Count(v => v.Copilot == CopilotState.Busy);
			int debugging = list.Count(v => v.DebugMode == 2 || v.DebugMode == 3);
			int x = _btnRestart.Left - Dpi.S(14);
			// 窗口较窄时跳过放不下的状态标签，避免压在标题上 / Skip chips that don't fit so they never cover the title
			int minLeft = tx + Math.Max(TextRenderer.MeasureText(g, "多 VS 管理工具", Theme.AppTitle).Width,
				TextRenderer.MeasureText(g, "Copilot 多实例控制台 · 对话 · 调试 · 布局", Theme.Small).Width) + Dpi.S(12);
			x = DrawChip(g, x, r.Height, $"调试中  {debugging}", debugging > 0 ? Theme.IdleDot : Theme.NoneDot, minLeft);
			x = DrawChip(g, x - Dpi.S(8), r.Height, $"Copilot 运行中  {busy}", busy > 0 ? Theme.BusyDot : Theme.NoneDot, minLeft);
			x = DrawChip(g, x - Dpi.S(8), r.Height, $"VS 实例  {list.Count}", Theme.Accent, minLeft);
			if (_web != null && _settings.WebEnabled)
				DrawChip(g, x - Dpi.S(8), r.Height, !_web.Running ? "手机遥控 未启动" : _web.Active ? "手机遥控 使用中" : "手机遥控 已开启",
					!_web.Running ? Theme.Danger : _web.Active ? Theme.IdleDot : Theme.Accent, minLeft);
		}

		private static int DrawChip(Graphics g, int right, int headerHeight, string text, Color dot, int minLeft)
		{
			var sz = TextRenderer.MeasureText(g, text, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
			int h = Dpi.S(28), w = sz.Width + Dpi.S(34);
			if (right - w < minLeft) return int.MinValue / 2;
			var rect = new RectangleF(right - w, (headerHeight - h) / 2f, w, h);
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
			Theme.FillRound(g, Color.FromArgb(160, 30, 30, 38), rect, h / 2f);
			Theme.DrawRound(g, Theme.Border, rect, h / 2f);
			Theme.FillCircle(g, dot, rect.X + Dpi.S(14), rect.Y + h / 2f, Dpi.S(3));
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
			TextRenderer.DrawText(g, text, Theme.Small, new Rectangle((int)rect.X + Dpi.S(24), (int)rect.Y, sz.Width + Dpi.S(4), h), Theme.TextSecondary,
				TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			return (int)rect.X;
		}

		private object ItemAt(Point p)
		{
			int i = _list.IndexFromPoint(p);
			return i >= 0 && _list.GetItemRectangle(i).Contains(p) ? _list.Items[i] : null;
		}

		private void List_DrawItem(object sender, DrawItemEventArgs e)
		{
			var g = e.Graphics;
			using (var b = new SolidBrush(Theme.Sidebar)) g.FillRectangle(b, e.Bounds);
			if (e.Index < 0 || e.Index >= _list.Items.Count) return;
			var v = (VsInstance)_list.Items[e.Index];
			bool selected = (e.State & DrawItemState.Selected) != 0;
			bool hover = e.Index == _list.HoverIndex;

			var card = new RectangleF(e.Bounds.X + Dpi.S(10), e.Bounds.Y + Dpi.S(3), e.Bounds.Width - Dpi.S(20), e.Bounds.Height - Dpi.S(6));
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
			if (selected)
			{
				Theme.FillRound(g, Theme.RowSelected, card, Dpi.S(10));
				Theme.DrawRound(g, Color.FromArgb(70, 139, 92, 246), card, Dpi.S(10));
				Theme.FillRound(g, Theme.Accent, new RectangleF(card.X + Dpi.S(1), card.Y + Dpi.S(14), Dpi.S(3), card.Height - Dpi.S(28)), Dpi.S(2));
			}
			else if (hover) Theme.FillRound(g, Theme.RowHover, card, Dpi.S(10));
			if (v.CompletionUnseen && !selected)
				Theme.DrawRound(g, Color.FromArgb(170, Theme.IdleDot), card, Dpi.S(10));

			GetStateColors(v, out var fg, out var pill, out var dot);
			int x0 = (int)card.X + Dpi.S(16), y0 = (int)card.Y + Dpi.S(10);
			int right = (int)card.Right - Dpi.S(12);
			if (IsCompleted(v))
			{
				float cx = x0 + Dpi.S(4), cy = y0 + Dpi.S(10), r = Dpi.S(7);
				Theme.FillCircle(g, dot, (int)cx, (int)cy, (int)r);
				using (var pen = new Pen(Color.FromArgb(20, 24, 28), Dpi.S(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
					g.DrawLines(pen, new[] { new PointF(cx - r * 0.45f, cy + r * 0.02f), new PointF(cx - r * 0.1f, cy + r * 0.38f), new PointF(cx + r * 0.48f, cy - r * 0.35f) });
			}
			else
				Theme.FillCircle(g, dot, x0 + Dpi.S(4), y0 + Dpi.S(10), Dpi.S(4));
			if (v.Copilot == CopilotState.Busy && _settings.MonitorCopilot)
				using (var pen = new Pen(Color.FromArgb(90, dot), Dpi.S(2))) g.DrawEllipse(pen, x0 + Dpi.S(4) - Dpi.S(7), y0 + Dpi.S(10) - Dpi.S(7), Dpi.S(14), Dpi.S(14));
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

			var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
			string hot = e.Index < 9 ? $"Ctrl+Alt+{e.Index + 1}" : "";
			int hotW = hot.Length > 0 ? TextRenderer.MeasureText(g, hot, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
			int nameX = x0 + Dpi.S(18);
			TextRenderer.DrawText(g, NameOf(v), Theme.SemiBold, new Rectangle(nameX, y0, Math.Max(0, right - nameX - hotW - Dpi.S(8)), Dpi.S(20)),
				selected ? Color.White : Theme.Text, flags);
			if (hot.Length > 0)
				TextRenderer.DrawText(g, hot, Theme.Small, new Rectangle(right - hotW, y0, hotW + 2, Dpi.S(20)), Theme.TextMuted, flags);

			string path = string.IsNullOrEmpty(v.SolutionPath) ? "未获取到解决方案路径" : v.SolutionPath;
			TextRenderer.DrawText(g, path, Theme.Small, new Rectangle(nameX, y0 + Dpi.S(22), Math.Max(0, right - nameX), Dpi.S(18)), Theme.TextMuted,
				(flags & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.PathEllipsis);

			int px = nameX, py = y0 + Dpi.S(46);
			int w = Theme.DrawPill(g, px, py, right - px, StateText(v), pill, fg, null);
			if (v.Dte != null && w > 0 && px + w + Dpi.S(6) < right)
			{
				GetDebugColors(v, out var dfg, out var dbg, out var ddot);
				Theme.DrawPill(g, px + w + Dpi.S(6), py, right - px - w - Dpi.S(6), DebugText(v), dbg, dfg, ddot);
			}
		}

		private void GetStateColors(VsInstance v, out Color fg, out Color bg, out Color dot)
		{
			if (v != null && _settings.MonitorCopilot && v.Copilot == CopilotState.Busy) { fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; }
			else if (v != null && _settings.MonitorCopilot && v.Copilot == CopilotState.Idle) { fg = Theme.IdleFg; bg = Theme.IdleBg; dot = Theme.IdleDot; }
			else { fg = Theme.NoneFg; bg = Theme.NoneBg; dot = Theme.NoneDot; }
		}

		private static void GetDebugColors(VsInstance v, out Color fg, out Color bg, out Color dot)
		{
			if (v != null && v.Building) { fg = Theme.AccentText; bg = Theme.AccentLight; dot = Theme.Accent; }
			else if (v != null && v.DebugMode == 3) { fg = Theme.IdleFg; bg = Theme.IdleBg; dot = Theme.IdleDot; }
			else if (v != null && v.DebugMode == 2) { fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; }
			else { fg = Theme.NoneFg; bg = Theme.NoneBg; dot = Theme.NoneDot; }
		}

		private static string DebugText(VsInstance v)
		{
			if (v.Dte == null) return "—";
			if (v.Building) return "生成中";
			switch (v.DebugMode)
			{
				case 3: return "调试中";
				case 2: return "已中断";
				case 1: return "未调试";
				default: return "—";
			}
		}

		#endregion

		#region 调试工具栏

		private void BuildDebugBar(FlowLayoutPanel bar)
		{
			_profileCombo.Width = Dpi.S(136);
			_profileCombo.Margin = new Padding(Dpi.S(6), Dpi.S(1), Dpi.S(8), 0);
			_profileCombo.Visible = false;
			_profileCombo.SelectionChangeCommitted += (s, e) => SetProfile(_profileCombo.SelectedItem as string);
			_profileCombo.DropDown += (s, e) => UpdateProfiles(true);
			bar.Controls.Add(_profileCombo);
			AddDbg(bar, "go", "▶  调试", Theme.Success, "启动调试 / 继续 (F5)");
			AddDbg(bar, "run", "▷  运行", null, "开始执行（不调试）(Ctrl+F5)");
			AddDbg(bar, "break", "❚❚  中断", Theme.Warning, "全部中断 (Ctrl+Alt+Break)");
			AddDbg(bar, "stop", "■  停止", Theme.Danger, "停止调试 (Shift+F5)");
			AddDbg(bar, "restart", "↻  重启", null, "重新启动 (Ctrl+Shift+F5)");
			AddSep(bar);
			AddDbg(bar, "stepover", "逐过程", null, "逐过程 (F10)");
			AddDbg(bar, "stepinto", "逐语句", null, "逐语句 (F11)");
			AddDbg(bar, "stepout", "跳出", null, "跳出 (Shift+F11)");
			AddSep(bar);
			AddDbg(bar, "build", "生成", null, "生成解决方案 (Ctrl+Shift+B)");
			AddDbg(bar, "rebuild", "重新生成", null, "重新生成解决方案");
			bar.Resize += (s, e) => FitToolbar();
		}

		// 空间不足时按此顺序隐藏次要按钮
		private static readonly string[] ToolbarDropOrder = { "rebuild", "restart", "run", "stepout", "stepinto" };

		private void FitToolbar()
		{
			var bar = _chat.Toolbar;
			if (bar.ClientSize.Width <= 0) return;
			int need = bar.Padding.Horizontal;
			foreach (Control c in bar.Controls)
			{
				if (c == _profileCombo && !_profileShown) continue;
				need += c.Width + c.Margin.Horizontal;
			}
			var hide = new HashSet<string>();
			foreach (var k in ToolbarDropOrder)
			{
				if (need <= bar.ClientSize.Width) break;
				var b = _dbgButtons[k];
				hide.Add(k);
				need -= b.Width + b.Margin.Horizontal;
			}
			foreach (var k in ToolbarDropOrder)
			{
				bool vis = !hide.Contains(k);
				if (_dbgButtons[k].Visible != vis) _dbgButtons[k].Visible = vis;
			}
		}

		private static void AddSep(FlowLayoutPanel bar)
		{
			var sep = new Panel { Width = Dpi.S(1), Height = Dpi.S(18), BackColor = Theme.Border, Margin = new Padding(Dpi.S(6), Dpi.S(7), Dpi.S(8), 0) };
			bar.Controls.Add(sep);
		}

		private void AddDbg(FlowLayoutPanel bar, string action, string text, Color? tint, string tip)
		{
			var b = new FlatButton { Text = text, Tint = tint, Ghost = true, Height = Dpi.S(30), Margin = new Padding(0, 0, Dpi.S(2), 0) };
			b.Width = TextRenderer.MeasureText(text, b.Font).Width + Dpi.S(20);
			b.Click += (s, e) => DoDebug(action);
			_tips.SetToolTip(b, tip);
			_dbgButtons[action] = b;
			bar.Controls.Add(b);
		}

		/// <summary>读取选中 VS 启动项目的 launchSettings 配置并刷新下拉框。</summary>
		private async void UpdateProfiles(bool quiet)
		{
			var v = Selected;
			if (v == null) { ShowProfiles(null); return; }
			if (!quiet) ShowProfiles(_profiles.TryGetValue(v.Pid, out var c) ? c : null);
			VsService.LaunchProfiles r;
			try { r = await DteWorker.Run(() => _vsOps.GetLaunchProfiles(v)); }
			catch { return; }
			_profiles[v.Pid] = r;
			if (v == Selected && !_profileCombo.DroppedDown) ShowProfiles(r);
			else if (v == Selected && _profileCombo.DroppedDown && !SameProfiles(r)) ShowProfiles(r);
		}

		private bool SameProfiles(VsService.LaunchProfiles r) =>
			_profileCombo.Items.Cast<string>().SequenceEqual(r.Names) && (_profileCombo.SelectedItem as string) == r.Active;

		private void ShowProfiles(VsService.LaunchProfiles r)
		{
			bool show = r != null && r.Names.Count > 0;
			if (show && !SameProfiles(r))
			{
				_profileUpdating = true;
				_profileCombo.BeginUpdate();
				_profileCombo.Items.Clear();
				foreach (var n in r.Names) _profileCombo.Items.Add(n);
				_profileCombo.SelectedItem = r.Active;
				_profileCombo.EndUpdate();
				_profileUpdating = false;
				int w = r.Names.Select(n => TextRenderer.MeasureText(n, _profileCombo.Font).Width).DefaultIfEmpty(0).Max();
				_profileCombo.DropDownWidth = Math.Max(_profileCombo.Width, w + Dpi.S(24));
			}
			if (show) _tips.SetToolTip(_profileCombo, "启动配置 · " + r.Project + (r.CanSet ? "" : "（只读）"));
			_profileCombo.Enabled = show && r.CanSet;
			if (_profileCombo.Visible != show || _profileShown != show)
			{
				_profileShown = show;
				_profileCombo.Visible = show;
				FitToolbar();
			}
		}

		private async void SetProfile(string name)
		{
			var v = Selected;
			if (_profileUpdating || v == null || string.IsNullOrEmpty(name)) return;
			string r;
			try { r = await DteWorker.Run(() => _vsOps.SetLaunchProfile(v, name)); }
			catch (Exception ex) { r = "切换启动配置失败：" + ex.Message; }
			SetStatus($"「{NameOf(v)}」{r}");
			UpdateProfiles(true);
		}

		private async void DoDebug(string action)
		{
			if (!Check()) return;
			var v = Selected;
			if (action == "build" && v.Building) action = "cancelbuild";
			bool reclaim = action == "stop" && Form.ActiveForm == this;
			await RunDebug(v, action);
			if (reclaim) ReclaimFocusFrom(v);
		}

		private async Task<string> RunDebug(VsInstance v, string action)
		{
			SetStatus($"「{NameOf(v)}」正在执行…");
			string r;
			try { r = await DteWorker.Run(() => _vsOps.DebugAction(v, action)); }
			catch (Exception ex) { r = "失败：" + ex.Message; }
			SetStatus($"「{NameOf(v)}」{r}");
			UpdateRow(v);
			// 调试状态切换有延迟，稍后再刷新一次
			var t = new Timer { Interval = 1200 };
			t.Tick += (s, e) => { t.Stop(); t.Dispose(); RefreshInstances(); };
			t.Start();
			return r;
		}

		private void UpdateDebugBar()
		{
			var v = Selected;
			bool has = v?.Dte != null;
			int mode = has ? v.DebugMode : 0;
			bool building = has && v.Building;
			bool running = mode == 3, broken = mode == 2, design = mode == 1;
			void Set(string k, bool en)
			{
				var b = _dbgButtons[k];
				if (b.Enabled != en) b.Enabled = en;
			}
			var go = _dbgButtons["go"];
			string goText = broken ? "▶  继续" : "▶  调试";
			if (go.Text != goText) go.Text = goText;
			Set("go", has && !running && !building);
			Set("run", has && design && !building);
			Set("break", has && running);
			Set("stop", has && (running || broken));
			Set("restart", has && (running || broken));
			Set("stepover", has && !running && !building);
			Set("stepinto", has && !running && !building);
			Set("stepout", has && broken);
			var build = _dbgButtons["build"];
			string buildText = building ? "■  取消生成" : "生成";
			if (build.Text != buildText) { build.Text = buildText; build.Width = TextRenderer.MeasureText(buildText, build.Font).Width + Dpi.S(20); FitToolbar(); }
			build.Tint = building ? Theme.Danger : (Color?)null;
			Set("build", has && (design || building));
			Set("rebuild", has && design && !building);
		}

		#endregion

		#region Copilot 对话

		/// <summary>在主区域显示 AI 总控助手（true）或 VS 对话（false）。</summary>
		private void ShowAgent(bool on)
		{
			on &= _settings.AgentEnabled;
			_agentMode = on;
			_agentCard.Selected = on;
			if (on && _list.SelectedIndex >= 0) { _list.ClearSelected(); OnSelectionChanged(); }
			_agentPanel.Visible = on;
			_chat.Visible = !on;
			if (on) { _agentPanel.BringToFront(); _agentPanel.FocusInput(); }
			else if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
		}

		private void OnSelectionChanged()
		{
			var v = Selected;
			if (v != null && _agentMode) ShowAgent(false);
			var old = _chatSvc.Target;
			if (old != v)
			{
				if (old != null)
				{
					_drafts[old.Pid] = _chat.InputText;
					_imageDrafts[old.Pid] = _chat.Images;
				}
				_chatSvc.Target = v;
				_chat.SetPaneMissing(false);
				_chat.ResetView();
				if (v == null) _chat.SetEmpty(_list.Items.Count == 0 ? "未发现正在运行的 Visual Studio" : "请在左侧选择一个 VS");
				else if (_chatCache.TryGetValue(v.Pid, out var cached)) _chat.Render(cached);
				else _chat.SetEmpty("正在读取「" + NameOf(v) + "」的 Copilot 对话…");
				_chat.InputText = v != null && _drafts.TryGetValue(v.Pid, out var draft) ? draft : "";
				_chat.Images = v != null && _imageDrafts.TryGetValue(v.Pid, out var images) ? images : Array.Empty<ChatImage>();
				_chatSvc.Poke();
				UpdateProfiles(false);
			}
			UpdateChatHeader();
			UpdateDebugBar();
		}

		private void UpdateChatHeader()
		{
			var v = Selected;
			_chat.SetTarget(v == null ? null : NameOf(v), v == null ? "" : (string.IsNullOrEmpty(v.SolutionPath) ? "未获取到解决方案路径" : v.SolutionPath));
			if (v == null) { _chat.SetState("未选择 VS", Theme.NoneFg, Theme.NoneBg, Theme.NoneDot, false); _chat.SetCompletion(null); return; }
			if (v.CompletionUnseen && ContainsFocus) { v.CompletionUnseen = false; _list.InvalidateItem(v); }
			GetStateColors(v, out var fg, out var bg, out var dot);
			bool busy = v.Copilot == CopilotState.Busy;
			if (busy) _awaitReply.Remove(v.Pid);
			bool awaiting = !busy && _awaitReply.TryGetValue(v.Pid, out var until) && DateTime.Now < until;
			if (!busy && !awaiting) _awaitReply.Remove(v.Pid);
			if (_pendingSend.TryGetValue(v.Pid, out var pend) && !_sending && DateTime.Now - pend.At > TimeSpan.FromSeconds(20)) _pendingSend.Remove(v.Pid);
			string text, activity;
			if (_sending) { text = "发送中…"; activity = "正在发送到 Copilot…"; }
			else if (busy)
			{
				string sec = v.BusySince == default(DateTime) ? "" : " · " + FormatDuration(DateTime.Now - v.BusySince);
				text = "接收中" + sec;
				activity = "Copilot 正在回复" + sec;
			}
			else if (awaiting) { text = "已送达 · 等待响应"; activity = "已送达，等待 Copilot 响应…"; }
			else { text = "Copilot " + StateText(v); activity = null; }
			if (_sending || awaiting) { fg = Theme.BusyFg; bg = Theme.BusyBg; dot = Theme.BusyDot; }
			_chat.SetState(text, fg, bg, dot, busy);
			_chat.SetActivity(_settings.MonitorCopilot || _sending || awaiting ? activity : null,
				_pendingSend.TryGetValue(v.Pid, out pend) ? pend.Text : null);
			_chat.SetCompletion(IsCompleted(v) && !_sending && !awaiting
				? $"任务已完成 · {v.CompletedAt.Value:HH:mm:ss} · 用时 {FormatDuration(v.LastDuration)}"
				: null);
		}

		/// <summary>对话中已出现刚发送的消息时，移除“发送中”的占位消息。</summary>
		private void ResolvePending(VsInstance v, ChatTranscript t)
		{
			if (!_pendingSend.TryGetValue(v.Pid, out var pend)) return;
			if (_sending) return;
			string want = Squash(pend.Text);
			if (want.Length > 24) want = want.Substring(0, 24);
			string got = LastUserText(t);
			if (want.Length == 0 || got.Contains(want) || (pend.Before != null && got != pend.Before))
			{
				_pendingSend.Remove(v.Pid);
				UpdateChatHeader();
			}
		}

		private static string LastUserText(ChatTranscript t)
		{
			var last = t?.Messages?.LastOrDefault(m => m.Role == ChatRole.User);
			return last == null ? "" : Squash(string.Join("", last.Parts.Where(p => !p.IsStep).Select(p => p.Text)));
		}

		private static string Squash(string s) => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

		private bool IsCompleted(VsInstance v) =>
			v != null && _settings.MonitorCopilot && v.Copilot == CopilotState.Idle && v.CompletedAt.HasValue;

		private void UpdateChatHint()
		{
			_chat.VoiceInputEnabled = _settings.AsrEnabled;
			string voice = _settings.AsrEnabled ? " · 按住空格说话" : "";
			_chat.Hint = _settings.BackgroundSend
				? "Enter 发送 · Shift+Enter 换行" + voice + " · 后台发送，不切换窗口"
				: "Enter 发送 · Shift+Enter 换行" + voice + " · 发送时会短暂切换到 VS";
		}

		#region 语音输入

		private DoubaoAsr _asr;
		private Task _asrConnect;
		private Timer _asrLimit;
		private static readonly Color VoiceRed = Color.FromArgb(248, 113, 113);

		private async void OnVoiceBegin()
		{
			CleanupVoice();
			string key = _settings.EffectiveVoiceApiKey;
			if (string.IsNullOrWhiteSpace(key))
			{
				_chat.ForceEndVoice();
				ShowVoiceResult("⚠ 请先在「属性 → 豆包语音」中填写 API Key", Theme.Danger);
				return;
			}
			var asr = new DoubaoAsr();
			_asr = asr;
			asr.Partial += text => SafeInvoke(() => { if (_asr == asr) _chat.SetVoiceStatus("🎙 " + text, Theme.Text); });
			asr.Level += level => SafeInvoke(() => { if (_asr == asr) _chat.SetVoiceLevel(level); });
			try { asr.StartMicrophone(); }
			catch (Exception ex)
			{
				CleanupVoice();
				_chat.ForceEndVoice();
				ShowVoiceResult("⚠ 无法打开麦克风：" + ex.Message + "（请检查 Windows 隐私设置中的麦克风权限）", Theme.Danger);
				return;
			}
			_chat.SetVoiceStatus("🎙 正在聆听… 松开空格结束 · Esc 取消", VoiceRed);
			_asrLimit = new Timer { Interval = (int)DoubaoAsr.MaxDuration.TotalMilliseconds };
			_asrLimit.Tick += (s, e) => { if (_asr == asr) _chat.ForceEndVoice(); };
			_asrLimit.Start();
			var connect = asr.ConnectAsync(key, _settings.AsrResource);
			_asrConnect = connect;
			try { await connect; }
			catch (Exception ex)
			{
				if (_asr != asr) return;
				CleanupVoice();
				_chat.ForceEndVoice();
				ShowVoiceResult("⚠ " + ex.Message, Theme.Danger);
			}
		}

		private async void OnVoiceEnd(bool cancel)
		{
			var asr = _asr;
			var connect = _asrConnect;
			if (asr == null) return;
			_asr = null;
			_asrLimit?.Dispose();
			_asrLimit = null;
			if (cancel)
			{
				asr.Dispose();
				ShowVoiceResult("已取消语音输入", Theme.TextMuted);
				return;
			}
			_chat.SetVoiceStatus("⏳ 正在识别…" + (asr.Text.Length > 0 ? " " + asr.Text : ""), Theme.BusyFg);
			try
			{
				if (connect != null) await connect;
				string text = (await asr.StopAsync()).Trim();
				if (text.Length == 0) { ShowVoiceResult("未识别到语音，请靠近麦克风再试", Theme.TextMuted); return; }
				_chat.InsertVoiceText(text);
				ShowVoiceResult("✔ 已识别 " + text.Length + " 字，可修改后按 Enter 发送", Theme.IdleFg);
			}
			catch (Exception ex) { ShowVoiceResult("⚠ " + ex.Message, Theme.Danger); }
			finally { asr.Dispose(); }
		}

		/// <summary>在输入状态栏显示识别结果几秒后恢复。</summary>
		private void ShowVoiceResult(string text, Color color)
		{
			_chat.SetVoiceStatus(text, color);
			var t = new Timer { Interval = 3500 };
			t.Tick += (s, e) =>
			{
				t.Dispose();
				if (_asr == null) _chat.SetVoiceStatus(null, Theme.TextSecondary);
			};
			t.Start();
		}

		private void CleanupVoice()
		{
			_asrLimit?.Dispose();
			_asrLimit = null;
			_asr?.Dispose();
			_asr = null;
			_asrConnect = null;
		}

		#endregion

		private void OnChatUpdated(VsInstance v, ChatTranscript t)
		{
			Archive.VsChat(v.Key, NameOf(v), t, v.Copilot == CopilotState.Busy);
			if (v != Selected)
			{
				// 后台同步的其他 VS：只更新缓存
				if (t.PaneFound) _chatCache[v.Pid] = t;
				else _chatCache.Remove(v.Pid);
				return;
			}
			if (!t.PaneFound)
			{
				_chatCache.Remove(v.Pid);
				_chat.SetPaneMissing(true);
				_chat.SetEmpty("未在「" + NameOf(v) + "」中找到 Copilot 对话窗格\n点击右上角「打开对话助手」即可在该 VS 中打开");
				return;
			}
			_chatCache[v.Pid] = t;
			_chat.SetPaneMissing(false);
			_chat.Render(t);
			ResolvePending(v, t);
		}

		private async void OnChatSend(string text)
		{
			var v = Selected;
			if (v == null) return;
			if (_sending) { SendLog.Event(NameOf(v), "界面发送被忽略：上一条消息仍在发送中"); SetStatus("上一条消息仍在发送中，请稍候"); return; }
			if (v.Copilot == CopilotState.Busy)
			{
				if (string.IsNullOrWhiteSpace(text) || _chat.Images.Count > 0)
				{
					SendLog.Event(NameOf(v), "界面发送被拒绝：Copilot 状态为运行中");
					SetStatus("Copilot 正在运行，请等待完成或先点击「停止」（带图片的消息不能排队）");
					return;
				}
				// 正忙时不再拒绝：记入任务清单，空闲后自动发布
				var q = _tasks.Add(v.Key, NameOf(v), text.Trim(), "用户");
				string hidden = HideResentFailed(q);
				_drafts.Remove(v.Pid);
				_chat.ClearInput();
				if (_taskPanel.Collapsed) { _taskPanel.SetCollapsed(false); _settings.TaskPanelCollapsed = false; _settings.Save(); }
				SetStatus($"「{NameOf(v)}」正忙，已加入任务清单（#{q.Id}，前面 {_tasks.Ahead(q)} 个），空闲后自动发布" + (hidden == null ? "" : "；" + hidden));
				return;
			}
			if (!string.IsNullOrWhiteSpace(text))
				_pendingSend[v.Pid] = new PendingSend
				{
					Text = text.Trim(), At = DateTime.Now,
					Before = _chatCache.TryGetValue(v.Pid, out var before) ? LastUserText(before) : null
				};
			string r = await SendChatCore(v, text, _chat.Images);
			if (!SendRetryPolicy.IsDelivered(r)) { _pendingSend.Remove(v.Pid); if (v == Selected) { UpdateChatHeader(); _chat.FocusInput(); } }
			else
			{
				if (_pendingSend.TryGetValue(v.Pid, out var sent)) sent.At = DateTime.Now;
				_drafts.Remove(v.Pid);
				_imageDrafts.Remove(v.Pid);
				if (v == Selected) _chat.ClearInput();
			}
		}

		/// <summary>发送到指定 VS 的 Copilot（界面与网页远程共用）。必须在界面线程调用。</summary>
		private async Task<string> SendChatCore(VsInstance v, string text, IReadOnlyList<ChatImage> images = null)
		{
			if (_sending) { SendLog.Event(NameOf(v), "发送被拒绝：另一条消息正在发送"); return "另一条消息正在发送，请稍后再试"; }
			if (v.Copilot == CopilotState.Busy) { SendLog.Event(NameOf(v), "发送被拒绝：Copilot 状态为运行中"); return "Copilot 正在运行，请等待完成或先停止"; }
			_sending = true;
			if (!string.IsNullOrWhiteSpace(text))
			{
				_recentSends.RemoveAll(x => (DateTime.Now - x.At).TotalMinutes > 30);
				_recentSends.Add((v.Pid, Squash(text), DateTime.Now));
			}
			_chat.SetSending(true);
			UpdateChatHeader();
			SetStatus($"正在发送到「{NameOf(v)}」…");
			bool background = _settings.BackgroundSend;
			IntPtr me = Handle;
			string r;
			_chatSvc.Paused = true;
			try { r = await DteWorker.RunSta(() => _chatSvc.Send(v, text, me, background, images)); }
			catch (Exception ex) { r = "发送失败：" + ex.Message; SendLog.Event(NameOf(v), "发送线程异常：" + ex); }
			finally
			{
				_chatSvc.Paused = false;
				_sending = false;
				_chat.SetSending(false);
			}
			if (SendRetryPolicy.IsDelivered(r)) _awaitReply[v.Pid] = DateTime.Now.AddSeconds(20);
			SetStatus($"「{NameOf(v)}」{r}" + (SendRetryPolicy.IsDelivered(r) ? "" : "　·　右键 VS →「查看发送日志」"));
			UpdateChatHeader();
			_chatSvc.Poke();
			return r;
		}

		#region IRemoteHost（网页远程控制，均可在后台线程调用）

		IList<VsInstance> IRemoteHost.Instances => _instances;

		string IRemoteHost.NameOf(VsInstance v) => NameOf(v);

		ChatTranscript IRemoteHost.CachedChat(VsInstance v)
		{
			if (IsDisposed || !IsHandleCreated) return null;
			return (ChatTranscript)Invoke((Func<ChatTranscript>)(() => _chatCache.TryGetValue(v.Pid, out var t) ? t : null));
		}

		Task<string> IRemoteHost.SendChat(VsInstance v, string text)
		{
			var tcs = new TaskCompletionSource<string>();
			SafeInvoke(async () =>
			{
				try { tcs.SetResult(await SendChatCore(v, text)); }
				catch (Exception ex) { tcs.SetResult("发送失败：" + ex.Message); }
			});
			return tcs.Task;
		}

		async Task<string> IRemoteHost.InvokeChatButton(VsInstance v, string automationId, string name)
		{
			try { return await DteWorker.RunSta(() => _chatSvc.InvokeButton(v, automationId, name)); }
			catch (Exception ex) { return name + "失败：" + ex.Message; }
		}

		void IRemoteHost.Log(string s) => SafeInvoke(() => SetStatus(s));

		void IRemoteHost.FocusChat(int pid) => _chatSvc.Focus(pid);

		Task<string> IRemoteHost.DockPanes() => OnUiAsync(DockAllPanes);

		Task<string> IRemoteHost.ErrorList(VsInstance v, int max) => ((IAgentHost)this).ErrorList(v, max);

		#endregion

		#region IAgentHost（AI 总控助手，均可在后台线程调用）

		IList<VsInstance> IAgentHost.Instances => _instances;

		string IAgentHost.NameOf(VsInstance v) => NameOf(v);

		string IAgentHost.NoteOf(VsInstance v) => NoteOf(v);

		string IRemoteHost.NoteOf(VsInstance v) => NoteOf(v);

		Task<string> IRemoteHost.SetNote(VsInstance v, string note) => ((IAgentHost)this).SetNote(v, note);

		/// <summary>职责描述：先按解决方案路径查找，再兼容解决方案路径暂不可用时按名称记录的描述。</summary>
		private string NoteOf(VsInstance v) =>
			_settings.GetNote(v.Key) ?? (string.IsNullOrEmpty(v.SolutionPath) ? null : _settings.GetNote("title:" + v.DisplaySolution));

		Task<string> IAgentHost.SetNote(VsInstance v, string note) => OnUi(() =>
		{
			_settings.SetNote(v.Key, note);
			_settings.Save();
			return string.IsNullOrWhiteSpace(note) ? "已清除「" + NameOf(v) + "」的职责描述" : "已记录「" + NameOf(v) + "」的职责：" + note.Trim();
		});

		async Task<ChatTranscript> IAgentHost.ReadChat(VsInstance v, int maxMessages)
		{
			try
			{
				var t = await DteWorker.RunSta(() => _chatSvc.Read(v, maxMessages)).ConfigureAwait(false);
				if (t != null && t.PaneFound) return t;
			}
			catch { }
			return await OnUi(() => _chatCache.TryGetValue(v.Pid, out var c) ? c : null).ConfigureAwait(false);
		}

		Task<string> IAgentHost.SendTask(VsInstance v, string text) => ((IRemoteHost)this).SendChat(v, text);

		Task<string> IAgentHost.QueueTask(VsInstance v, string text) => OnUiAsync(async () =>
		{
			string name = NameOf(v);
			var dup = TaskStateMachine.FindActiveDuplicate(_tasks.Items, v.Key, text);
			if (dup != null) return $"「{name}」的任务清单中已有相同任务 #{dup.Id}（{StatusText(dup)}），未重复添加。";
			var q = _tasks.Add(v.Key, name, text, "AI");
			string hidden = HideResentFailed(q);
			string note = hidden == null ? "" : "\n" + hidden;
			await PumpTasksAsync();
			switch (q.Status)
			{
				case QueueStatus.Running:
					return $"已发送（任务 #{q.Id}）到「{name}」。完成后会自动通知你，无需调用 wait_for_vs。" + note;
				case QueueStatus.Waiting:
					return (q.Attempts > 0
						? $"发送暂未成功（{q.Error}），任务 #{q.Id} 已留在任务清单，30 秒后自动重试。"
						: $"「{name}」正忙，任务已加入任务清单（#{q.Id}，前面 {_tasks.Ahead(q)} 个），空闲后自动发布，完成后通知你。") + note;
				case QueueStatus.Failed:
					return $"任务 #{q.Id} 发布失败：{q.Error}" + note;
				default:
					return $"任务 #{q.Id}：{StatusText(q)}" + note;
			}
		});

		Task<string> IAgentHost.ListTasks() => OnUi(() =>
		{
			int recent = Math.Max(8, AppSettings.ClampQuota(nameof(AppSettings.AgentMaxReadCount), _settings.AgentMaxReadCount) * 2 / 5);
			int taskText = AppSettings.ClampQuota(nameof(AppSettings.AgentMaxTaskText), _settings.AgentMaxTaskText);
			var items = _tasks.Items.Where(t => QueueStatus.Active(t.Status))
				.Concat(_tasks.Items.Where(t => !QueueStatus.Active(t.Status)).OrderByDescending(t => t.Finished ?? t.Created).Take(recent)).ToList();
			if (items.Count == 0) return "任务清单为空。";
			var sb = new System.Text.StringBuilder();
			foreach (var t in items)
			{
				sb.Append('#').Append(t.Id).Append(" → ").Append(t.VsName).Append(" | ").Append(StatusText(t)).Append(" | ").Append(Clip(t.Text, Math.Max(120, taskText / 5)));
				if (!string.IsNullOrEmpty(t.Result) && t.Status == QueueStatus.Done) sb.Append(" | 结果：").Append(Clip(t.Result, Math.Max(200, taskText / 3)));
				if (!string.IsNullOrEmpty(t.Error) && t.Status != QueueStatus.Done) sb.Append(" | 错误：").Append(t.Error);
				if (TaskHideList.IsHidden(_settings.HiddenResentTasks, t))
					sb.Append(" | 已被 #").Append(TaskHideList.ReplacedBy(_settings.HiddenResentTasks, t.Id)).Append(" 重新排队取代（界面已隐藏）/ superseded by #")
						.Append(TaskHideList.ReplacedBy(_settings.HiddenResentTasks, t.Id)).Append(" (hidden in the UI)");
				sb.AppendLine();
			}
			return sb.ToString().TrimEnd();
		});

		Task<string> IAgentHost.CancelTask(int id) => OnUi(() =>
		{
			var t = _tasks.Find(id);
			if (t == null) return "没有任务 #" + id;
			if (!CancelQueued(t)) return $"任务 #{id} 当前{StatusText(t)}，无法取消";
			return $"已取消任务 #{id}";
		});

		Task<string> IAgentHost.DebugAction(VsInstance v, string action) => OnUiAsync(() => RunDebug(v, action));

		Task<string> IAgentHost.InvokeChatButton(VsInstance v, string automationId, string name) =>
			((IRemoteHost)this).InvokeChatButton(v, automationId, name);

		Task<string> IAgentHost.Activate(VsInstance v) => OnUi(() => { ActivateVs(v); return "已切换到「" + NameOf(v) + "」"; });

		async Task<string> IAgentHost.ErrorList(VsInstance v, int max)
		{
			try { return await DteWorker.Run(() => _vsOps.ReadErrorList(v, max)).ConfigureAwait(false); }
			catch (Exception ex) { return "读取错误列表失败：" + ex.Message; }
		}

		Task<bool> IAgentHost.Confirm(string title, string detail) =>
			OnUi(() => MessageBox.Show(this, detail, "AI 助手 · " + title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK);

		Task<string> IAgentHost.DockPanes() => OnUiAsync(DockAllPanes);

		private Task<T> OnUi<T>(Func<T> f) => OnUiAsync(() => Task.FromResult(f()));

		private Task<T> OnUiAsync<T>(Func<Task<T>> f)
		{
			var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (IsDisposed || !IsHandleCreated) { tcs.SetException(new InvalidOperationException("主窗口已关闭")); return tcs.Task; }
			try
			{
				BeginInvoke(new Action(async () =>
				{
					try { tcs.TrySetResult(await f()); }
					catch (Exception ex) { tcs.TrySetException(ex); }
				}));
			}
			catch (Exception ex) { tcs.TrySetException(ex); }
			return tcs.Task;
		}

		private void UpdateAgentVisibility()
		{
			bool on = _settings.AgentEnabled;
			_agentCard.Visible = on;
			if (!on) { _agent.Stop(); if (_agentMode) ShowAgent(false); }
		}

		#endregion

		private async void InvokeChatButton(string id, string name)
		{
			var v = Selected;
			if (v == null) return;
			string r;
			try { r = await DteWorker.RunSta(() => _chatSvc.InvokeButton(v, id, name)); }
			catch (Exception ex) { r = name + "失败：" + ex.Message; }
			SetStatus($"「{NameOf(v)}」{r}");
		}

		#endregion

		private void BuildTrayMenu(ContextMenuStrip m)
		{
			m.Items.Clear();
			m.Items.Add("显示主窗口" + (_showHotkeyOk ? "（Ctrl+Alt+0）" : ""), null, (s, e) => ShowMe());
			m.Items.Add(new ToolStripSeparator());
			foreach (var vs in _instances)
			{
				var v = vs;
				m.Items.Add($"{NameOf(v)}   [{StateText(v)}]", null, (s, e) => ActivateVs(v));
			}
			m.Items.Add(new ToolStripSeparator());
			m.Items.Add("重启 AI 助手 / Restart AI assistant", null, (s, e) => RestartAgent());
			m.Items.Add("重启 VSManager… / Restart VSManager…", null, (s, e) => { ShowMe(); RestartApp(); });
			m.Items.Add(new ToolStripSeparator());
			m.Items.Add("退出", null, (s, e) => { _exiting = true; Close(); });
		}

		#region 重启 / Restart

		/// <summary>「⟳ 重启」按钮的菜单。/ Menu of the "⟳ Restart" button.</summary>
		private ContextMenuStrip BuildRestartMenu()
		{
			var m = new ContextMenuStrip();
			var auto = new ToolStripMenuItem("AI 助手自动重启 / Auto-restart AI assistant") { CheckOnClick = true };
			auto.Click += (s, e) => { _settings.AgentAutoRestart = auto.Checked; ApplySettings(); SetStatus(auto.Checked ? "已开启 AI 助手自动重启 / Auto-restart on" : "已关闭 AI 助手自动重启 / Auto-restart off"); };
			var dog = new ToolStripMenuItem("进程看门狗（异常退出后自动拉起）/ Process watchdog") { CheckOnClick = true };
			dog.Click += (s, e) => { _settings.ProcessWatchdogEnabled = dog.Checked; ApplySettings(); SetStatus(dog.Checked ? "已开启进程看门狗 / Watchdog on" : "已关闭进程看门狗 / Watchdog off"); };
			var info = new ToolStripMenuItem { Enabled = false };
			m.Items.Add("重启 AI 助手 / Restart AI assistant", null, (s, e) => RestartAgent());
			m.Items.Add("重启 VSManager… / Restart VSManager…", null, (s, e) => RestartApp());
			m.Items.Add(new ToolStripSeparator());
			m.Items.Add(auto);
			m.Items.Add(dog);
			m.Items.Add(info);
			m.Items.Add(new ToolStripSeparator());
			m.Items.Add("打开日志目录 / Open log folder", null, (s, e) =>
			{
				try { Directory.CreateDirectory(AppPaths.LogFolder); System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.LogFolder + "\""); }
				catch (Exception ex) { SetStatus("打开日志目录失败 / Failed to open the log folder：" + ex.Message); }
			});
			m.Opening += (s, e) =>
			{
				auto.Checked = _settings.AgentAutoRestart;
				dog.Checked = _settings.ProcessWatchdogEnabled;
				var lim = _agentSupervisor.Limiter;
				info.Text = "本次启动自动重启 AI 助手 / Auto restarts: " + _agentSupervisor.AutoRestarts +
					"（上限 / limit " + _settings.AutoRestartMaxCount + " / " + _settings.AutoRestartWindowMinutes + " min" + (lim.Tripped ? "，已暂停 / paused" : "") + "）" +
					(ProcessWatchdog.WatchdogRunning ? "  · 看门狗运行中 / watchdog running" : "");
			};
			return m;
		}

		/// <summary>手动重启 AI 助手（不影响任务清单）。/ Restarts the AI assistant manually (the task list is not affected).</summary>
		private void RestartAgent()
		{
			if (_agent.Running && MessageBox.Show(this, "AI 助手正在处理当前对话，重启会中断这一轮（已发布的任务不受影响）。确定重启吗？\n\n" +
				"The assistant is working on the current round; restarting interrupts it (published tasks are not affected). Restart now?",
				"重启 AI 助手 / Restart AI assistant", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
			_agentSupervisor.RestartNow();
		}

		/// <summary>
		/// 手动重启 VSManager：确认后保存任务清单与配置，启动新实例（等待本进程退出），再正常退出。
		/// 发送中拒绝重启，避免消息半途中断；执行中的任务保持原状态，重启后继续跟踪，不会重新发布。
		/// Manual restart: after confirmation, saves the task list and settings, starts a new instance (which waits for this
		/// process to exit) and exits normally. Refused while sending; running tasks keep their state and are tracked again,
		/// not republished.
		/// </summary>
		private void RestartApp()
		{
			if (_sending || _tasks.Items.Any(t => t.Status == QueueStatus.Sending))
			{
				MessageBox.Show(this, "正在向 VS 发送消息，请稍后再重启，避免消息半途中断。\n\nA message is being sent to VS; please restart later so it is not cut off.",
					"重启 VSManager / Restart VSManager", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			int running = _tasks.Items.Count(t => t.Status == QueueStatus.Running);
			int waiting = _tasks.Items.Count(t => t.Status == QueueStatus.Waiting);
			string msg = "确定重启 VSManager 吗？\n\n• 任务清单与配置会先保存；执行中 " + running + " 条、排队 " + waiting + " 条，重启后继续跟踪 / 发布，执行中的不会重复发布。\n" +
				"• AI 助手正在进行的一轮会中断。\n\n" +
				"Restart VSManager?\n\n• The task list and settings are saved first; " + running + " running and " + waiting + " waiting tasks are tracked / published" +
				" again after the restart, running ones are not republished.\n• The assistant's current round is interrupted.";
			if (MessageBox.Show(this, msg, "重启 VSManager / Restart VSManager", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
			if (_sending || _tasks.Items.Any(t => t.Status == QueueStatus.Sending)) { SetStatus("正在发送，已取消重启 / Sending in progress; restart cancelled"); return; }
			if (!_tasks.Save())
			{
				MessageBox.Show(this, "任务清单保存失败，已取消重启：" + _tasks.SaveError + "\n\nFailed to save the task list; restart cancelled.",
					"重启 VSManager / Restart VSManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}
			_settings.Save();
			string err = ProcessWatchdog.LaunchReplacement();
			if (err != null) { SetStatus("重启失败 / Restart failed：" + err); return; }
			ProcessWatchdog.MarkCleanExit();
			_exiting = true;
			Close();
		}

		/// <summary>未处理异常导致进程即将终止时：尽力保存任务清单与配置。/ The process is about to die from an unhandled exception: best-effort save of tasks and settings.</summary>
		private void EmergencySave()
		{
			try { _tasks?.Save(); } catch { }
			try { _settings.Save(); } catch { }
			try { AppLog.Write(ProcessWatchdog.LogFile, "未处理异常，已尝试保存任务清单与配置 / Unhandled exception; tried to save tasks and settings"); } catch { }
		}

		private void ShowBalloon(string title, string text, ToolTipIcon icon)
		{
			try { if (_tray.Visible) _tray.ShowBalloonTip(6000, title, text, icon); } catch { }
		}

		#endregion

		#region 设置

		private void OpenSettings()
		{
			var v = Selected;
			var acts = new SettingsForm.Actions
			{
				Layout = QuickLayout,
				Activate = () => { if (Check()) ActivateVs(Selected); },
				Rename = RenameSelected,
				MoveMain = () => { if (Check()) MoveMain(Selected, true); },
				MoveTools = () => { if (Check()) MoveTools(Selected); },
				AllMain = () => { foreach (var x in _instances) MoveMain(x, false); SetStatus("已将所有 VS 主界面移到主屏幕"); },
				WebStatus = () => _web.Status,
				WebUrls = () => _web.Urls(),
				WebResetToken = () => { _settings.WebToken = AppSettings.NewToken(); _settings.Save(); },
				VoiceTest = (key, res, spk, text, en) => _voice.TestAsync(key, res, spk, text, en),
				AgentTest = AgentService.TestAsync,
				OpenSolutions = () => OpenSolutionRegistry(Form.ActiveForm ?? this)
			};
			using (var f = new SettingsForm(_settings, v == null ? null : NameOf(v), acts))
			{
				f.Changed += ApplySettings;
				f.ShowDialog(this);
			}
			// 容量设置可能已修改：后台裁剪对话记录 / Capacity may have changed: trim the chat history in the background
			AgentChatLog.TrimAsync();
		}

		private void ApplySettings()
		{
			_settings.Save();
			TopMost = _settings.TopMost;
			if (!_settings.VoiceEnabled) _voice.StopAll();
			UpdateVoiceButton();
			string archiveWarning = Archive.Configure(_settings);
			if (archiveWarning != null && archiveWarning != _archiveWarning) SetStatus("⚠ " + archiveWarning);
			_archiveWarning = archiveWarning;
			if (!_settings.MonitorCopilot)
				foreach (var v in _instances) v.Copilot = CopilotState.Unknown;
			UpdateChatHint();
			UpdateChatHeader();
			UpdateAgentVisibility();
			_agentPanel.RefreshConfig();
			_web.Apply();
			string dogErr = ProcessWatchdog.Apply(_settings);
			if (dogErr != null) SetStatus("⚠ 看门狗启动失败 / Watchdog failed to start：" + dogErr);
			_list.Invalidate();
			_header.Invalidate();
		}

		private Screen MainScreen => ScreenHelper.Find(_settings.MainScreen) ?? ScreenHelper.Ordered().FirstOrDefault() ?? Screen.PrimaryScreen;

		private Screen ToolScreen
		{
			get
			{
				var s = ScreenHelper.Find(_settings.ToolScreen);
				if (s != null) return s;
				var all = ScreenHelper.Ordered();
				return all.Count >= 3 ? all[2] : all.LastOrDefault() ?? Screen.PrimaryScreen;
			}
		}

		#endregion

		#region 实例列表

		private VsInstance Selected => _list.SelectedItem as VsInstance;

		private bool Check()
		{
			if (Selected != null) return true;
			SetStatus("请先在左侧选择一个 VS");
			return false;
		}

		private string NameOf(VsInstance v) =>
			_settings.GetAlias(v.Key) ?? (string.IsNullOrEmpty(v.SolutionPath) ? null : _settings.GetAlias("title:" + v.DisplaySolution)) ?? v.DisplaySolution;

		private string StateText(VsInstance v)
		{
			if (!_settings.MonitorCopilot) return "未监听";
			switch (v.Copilot)
			{
				case CopilotState.Busy: return "运行中 · " + FormatDuration(DateTime.Now - v.BusySince);
				case CopilotState.Idle: return v.CompletedAt.HasValue ? "✔ 已完成 · " + FormatDuration(v.LastDuration) : "空闲";
				default: return "对话窗格未打开";
			}
		}

		private static string FormatDuration(TimeSpan t) => TextUtil.FormatDuration(t);

		/// <summary>在后台线程枚举 VS（含 DTE 调用），完成后回到界面线程更新列表。</summary>
		private async void RefreshInstances()
		{
			if (_refreshing) return;
			_refreshing = true;
			try
			{
				var existing = _instances.ToDictionary(v => v.Pid);
				var list = await DteWorker.Run(() => _vsOps.Enumerate(existing));
				if (IsDisposed) return;
				ApplyInstances(list);
			}
			catch (Exception ex) { SetStatus("刷新失败: " + ex.Message); }
			finally { _refreshing = false; }
		}

		private void ApplyInstances(List<VsInstance> list)
		{
			_instances = list;
			var alive = new HashSet<int>(list.Select(v => v.Pid));
			foreach (var pid in _chatCache.Keys.Where(k => !alive.Contains(k)).ToList()) _chatCache.Remove(pid);
			foreach (var pid in _drafts.Keys.Where(k => !alive.Contains(k)).ToList()) _drafts.Remove(pid);
			foreach (var pid in _imageDrafts.Keys.Where(k => !alive.Contains(k)).ToList()) _imageDrafts.Remove(pid);
			foreach (var pid in _profiles.Keys.Where(k => !alive.Contains(k)).ToList()) _profiles.Remove(pid);
			var selPid = Selected?.Pid;
			bool same = _list.Items.Count == list.Count &&
						_list.Items.Cast<VsInstance>().Select(i => i.Pid).SequenceEqual(list.Select(v => v.Pid));
			if (!same)
			{
				_list.BeginUpdate();
				_list.Items.Clear();
				foreach (var v in list) _list.Items.Add(v);
				_list.EndUpdate();
				int idx = list.FindIndex(v => v.Pid == selPid);
				if (idx >= 0) _list.SelectedIndex = idx;
				RegisterHotkeys();
			}
			if (!_agentMode && _list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
			if (_list.Items.Count == 0 && _chatSvc.Target != null) OnSelectionChanged();
			_sideCount.Text = list.Count.ToString();
			_list.Invalidate();
			_header.Invalidate();
			UpdateDebugBar();
			UpdateChatHeader();
			if (_status.Text.StartsWith("正在")) SetStatus($"共发现 {list.Count} 个 VS 实例");
		}

		private void UpdateRow(VsInstance v)
		{
			_list.InvalidateItem(v);
			if (v == Selected) { UpdateChatHeader(); UpdateDebugBar(); }
			_header.Invalidate();
		}

		private void RenameSelected()
		{
			var v = Selected;
			if (v == null) { SetStatus("请先选择一个 VS"); return; }
			var name = Prompt.Show(this, "重命名", "显示名称（留空恢复为解决方案名）：", _settings.GetAlias(v.Key) ?? v.DisplaySolution);
			if (name == null) return;
			_settings.SetAlias(v.Key, string.IsNullOrWhiteSpace(name) || name == v.DisplaySolution ? null : name);
			_settings.Save();
			SetStatus($"已命名为「{NameOf(v)}」");
			UpdateRow(v);
			UpdateChatHeader();
		}

		private void EditNote()
		{
			var v = Selected;
			if (v == null) { SetStatus("请先选择一个 VS"); return; }
			var note = Prompt.Show(this, "职责描述", "「" + NameOf(v) + "」负责什么（AI 总控助手据此自动选择发布任务的目标，留空清除）：", NoteOf(v) ?? "");
			if (note == null) return;
			_settings.SetNote(v.Key, note);
			_settings.Save();
			SetStatus(string.IsNullOrWhiteSpace(note) ? $"已清除「{NameOf(v)}」的职责描述" : $"「{NameOf(v)}」职责：{note.Trim()}");
		}

		#endregion

		#region 操作

		private void QuickLayout()
		{
			if (!Check()) return;
			var v = Selected;
			MoveMain(v, true);
			MoveTools(v);
			Native.Activate(v.MainHwnd);
		}

		private void ActivateVs(VsInstance v)
		{
			if (v == null || !Native.IsWindow(v.MainHwnd)) { RefreshInstances(); return; }
			if (_settings.ActivateMoveMain) MoveMain(v, false);
			Native.Activate(v.MainHwnd);
			if (_settings.ActivateMoveTools) MoveTools(v);
			SetStatus($"已激活「{NameOf(v)}」");
		}

		private void MoveMain(VsInstance v, bool force)
		{
			var target = MainScreen;
			if (!force && Native.IsZoomed(v.MainHwnd) && Screen.FromHandle(v.MainHwnd).DeviceName == target.DeviceName) return;
			Native.MoveAndMaximize(v.MainHwnd, target.WorkingArea);
			if (force) SetStatus($"「{NameOf(v)}」主界面已移到 {target.DeviceName}");
		}

		private async void MoveTools(VsInstance v)
		{
			var screen = ToolScreen;
			var area = screen.WorkingArea;
			string layout = _settings.Layout;
			SetStatus($"「{NameOf(v)}」正在移动输出/错误栏…");
			string msg;
			try { msg = await DteWorker.Run(() => _vsOps.MoveToolWindows(v, area, layout)); }
			catch (Exception ex) { msg = ex.Message; }
			SetStatus($"「{NameOf(v)}」 → {screen.DeviceName}：{msg}");
		}

		#region 任务清单

		private VsInstance FindVs(string key) => _instances.FirstOrDefault(i => i.Key == key);

		private static string Clip(string s, int max) => TextUtil.Clip(s, max);

		private static string StatusText(QueuedTask t) => TaskStateMachine.StatusText(t, DateTime.Now);

		/// <summary>该 VS 当前能否接收新任务：Copilot 空闲、没有正在发送或刚送达等待响应的消息、不是刚刚完成。</summary>
		private bool CanDispatch(VsInstance v) =>
			v.Copilot != CopilotState.Busy && !v.Building
			&& !(_awaitReply.TryGetValue(v.Pid, out var until) && DateTime.Now < until)
			&& !_pendingSend.ContainsKey(v.Pid)
			&& !(v.CompletedAt.HasValue && (DateTime.Now - v.CompletedAt.Value).TotalSeconds < 3);

		private void PumpTasks() => _dispatcher.Pump();

		/// <summary>调度任务清单（委托给 <see cref="TaskDispatcher"/>）。只在界面线程调用。/ Dispatches the task list (delegates to <see cref="TaskDispatcher"/>). UI thread only.</summary>
		private Task PumpTasksAsync() => _dispatcher.PumpAsync();

		/// <summary>任务完成：读取 Copilot 最新回复作为结果，并通知 AI 助手。/ Completes a task and notifies the AI assistant.</summary>
		private void FinishTask(QueuedTask t, VsInstance v, TimeSpan? dur) => _dispatcher.Finish(t, v, dur);

		private bool CancelQueued(QueuedTask t) => _dispatcher.Cancel(t);

		private void OnTaskAction(QueuedTask t, string action)
		{
			switch (action)
			{
				case "clear":
					// 只在界面隐藏已完成的条目：记录清除时间点（settings.json），不修改 tasks.json 与归档
					// Hide completed items in the UI only: remember the clear time (settings.json); tasks.json and the archive stay untouched
					_settings.TaskListClearedAt = DateTime.Now;
					_settings.Save();
					_taskPanel.RefreshItems();
					return;
				case "unclear":
					// 撤销清除：同时恢复「已重新排队」而隐藏的失败条目 / Undo clear: also restores failed entries hidden as "requeued"
					_settings.TaskListClearedAt = null;
					if (_settings.HiddenResentTasks.Count > 0)
						AppLog.Write(AppLog.TasksFile, $"撤销清除：恢复显示 {_settings.HiddenResentTasks.Count} 条已重新排队的失败条目 / undo clear: {_settings.HiddenResentTasks.Count} requeued failed entries shown again");
					_settings.HiddenResentTasks.Clear();
					_settings.Save();
					_taskPanel.RefreshItems();
					return;
				case "unhide":
					if (TaskHideList.Remove(_settings.HiddenResentTasks, t.Id))
					{
						_settings.Save();
						AppLog.Write(AppLog.TasksFile, $"恢复显示失败条目 #{t.Id} / failed entry #{t.Id} shown again");
						_taskPanel.RefreshItems();
						SetStatus($"已恢复显示失败任务 #{t.Id} / Failed task #{t.Id} is shown again");
					}
					return;
				case "open":
					var v = FindVs(t.VsKey);
					if (v == null && t.Status == QueueStatus.WaitingVs)
					{
						// 暂存任务：打开登记的解决方案，打开后自动推送 / Parked task: open the registered solution; the task is pushed once it opens
						var entry = _solutions.FindByPath(t.VsKey);
						if (entry != null) { OpenSolutionFromUi(entry); return; }
					}
					if (v == null) { SetStatus($"「{t.VsName}」当前未打开"); return; }
					_list.SelectedItem = v;
					return;
				case "cancel": CancelQueued(t); return;
				case "remove":
					if (_tasks.Remove(t.Id) && TaskHideList.Remove(_settings.HiddenResentTasks, t.Id)) _settings.Save();
					return;
				case "retry":
					// 手动重新排队复用原条目，不再隐藏 / A manual requeue reuses the entry, which is no longer hidden
					if (TaskHideList.Remove(_settings.HiddenResentTasks, t.Id)) _settings.Save();
					_dispatcher.Retry(t);
					return;
				case "dispatch": _dispatcher.DispatchNow(t); return;
			}
		}

		/// <summary>
		/// 新任务列入排队后调用：若它是某条失败任务的重新发布，则把原失败条目从界面隐藏（记入 settings.json，不改 tasks.json 与归档）。
		/// 返回附加到提示中的说明；未隐藏时返回 null。无法可靠判定的条目保留不动，并写入任务日志。
		/// Call after a new task is queued: if it republishes a failed task, the original failed entry is hidden in the UI
		/// (recorded in settings.json; tasks.json and the archive stay untouched). Returns a note for the status text, or null
		/// when nothing was hidden. Entries that cannot be identified reliably are kept and explained in the task log.
		/// </summary>
		private string HideResentFailed(QueuedTask q)
		{
			if (q == null) return null;
			ResendMatch m;
			try { m = ResentTaskMatcher.Find(_tasks.Items, q); }
			catch (Exception ex) { AppLog.Write(AppLog.TasksFile, $"重新发布判定失败，保留原条目 / resend check failed, entries kept: {ex.Message}"); return null; }
			foreach (var k in m.Kept)
				AppLog.Write(AppLog.TasksFile, $"新任务 #{q.Id} 与失败任务 #{k.Task.Id} 内容相同，但未隐藏：{k.Reason} / new task #{q.Id} matches failed task #{k.Task.Id} but it was kept");
			if (m.ReferencedId.HasValue && !m.Hide.Any(x => x.Id == m.ReferencedId.Value) && !m.Kept.Any(x => x.Task.Id == m.ReferencedId.Value))
				AppLog.Write(AppLog.TasksFile, $"新任务 #{q.Id} 引用了 #{m.ReferencedId}，但它不是内容相同的失败任务，未隐藏 / new task #{q.Id} references #{m.ReferencedId}, which is not a failed task with the same content; nothing hidden");
			if (m.Hide.Count == 0) return null;
			string ids = string.Join(", ", m.Hide.Select(x => "#" + x.Id));
			if (!_settings.AutoHideResentFailedTasks)
			{
				AppLog.Write(AppLog.TasksFile, $"新任务 #{q.Id} 重新发布了失败任务 {ids}，自动隐藏已关闭，保留原条目 / new task #{q.Id} republishes failed {ids}; auto-hide is off, entries kept");
				return null;
			}
			var now = DateTime.Now;
			foreach (var x in m.Hide) TaskHideList.Add(_settings.HiddenResentTasks, x.Id, q.Id, now);
			_settings.Save();
			AppLog.Write(AppLog.TasksFile, $"新任务 #{q.Id} 重新发布了失败任务 {ids}（指纹 {ResentTaskMatcher.Fingerprint(ResentTaskMatcher.StripMarker(q.Text, out _))}），原条目已在界面隐藏 / new task #{q.Id} republishes failed {ids}; the original entries are hidden in the UI");
			SendLog.Event(q.VsName, $"任务清单：#{q.Id} 已重新排队，原失败条目 {ids} 已隐藏 / task #{q.Id} requeued, original failed entry {ids} hidden");
			_taskPanel.RefreshItems();
			string zh = $"已重新排队，原失败条目 {ids} 已隐藏";
			string en = $"Requeued; the original failed entry {ids} is hidden";
			if (_settings.AutoHideResentFailedNotify) NotifyTask(q, zh, en);
			return $"{zh}（可在任务清单「历史」中查看或撤销）/ {en} (view or undo via History in the task list)";
		}

		#endregion

		#region ITaskDispatchHost（任务调度宿主，界面线程调用）/ Task dispatch host (UI thread)

		VsInstance ITaskDispatchHost.FindVs(string vsKey) => FindVs(vsKey);
		bool ITaskDispatchHost.CanDispatch(VsInstance v) => CanDispatch(v);
		string ITaskDispatchHost.NameOf(VsInstance v) => NameOf(v);
		bool ITaskDispatchHost.IsSending => _sending;
		DateTime ITaskDispatchHost.TrackingReadyAt => _tasksReadyAt;
		Task<string> ITaskDispatchHost.SendAsync(VsInstance v, string text) => SendChatCore(v, text);

		async Task<string> ITaskDispatchHost.ReadAnswerAsync(VsInstance v)
		{
			ChatTranscript chat = null;
			try { chat = await DteWorker.RunSta(() => _chatSvc.Read(v, 4)); } catch { }
			if ((chat == null || !chat.PaneFound) && !_chatCache.TryGetValue(v.Pid, out chat)) chat = null;
			return TaskSummary.AnswerText(chat);
		}

		void ITaskDispatchHost.SetStatus(string text) => SetStatus(text);
		void ITaskDispatchHost.LogEvent(string vsName, string text) => SendLog.Event(vsName, text);
		void ITaskDispatchHost.NotifyAgent(string title, string body) => _agent.Notify(title, body);
		void ITaskDispatchHost.QueueActivityChanged(bool anyActive) => _taskTimer.Enabled = anyActive;

		#endregion

		#region VS 手动对话监听

		private static string MessageText(ChatMessage m) =>
			m == null ? "" : string.Join("\n\n", m.Parts.Where(p => !p.IsStep && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text.Trim()));

		/// <summary>该提问是否由 VSManager 发送（文本前缀互相包含即视为同一条）。</summary>
		private bool SentByUs(int pid, string key)
		{
			foreach (var s in _recentSends)
			{
				if (s.Pid != pid || (DateTime.Now - s.At).TotalMinutes > 30 || s.Text.Length == 0) continue;
				string a = s.Text.Substring(0, Math.Min(40, s.Text.Length)), b = key.Substring(0, Math.Min(40, key.Length));
				if (key.Contains(a) || s.Text.Contains(b)) return true;
			}
			return false;
		}

		/// <summary>监听到某个 VS 的最近一轮对话：新的手动提问加入任务清单，已有条目更新回答与状态。</summary>
		private void OnConversation(VsInstance v, ChatTranscript t, bool busy)
		{
			Archive.VsChat(v.Key, NameOf(v), t, busy);
			if (!_settings.WatchConversations || t?.Messages == null) return;
			int ui = t.Messages.FindLastIndex(m => m.Role == ChatRole.User);
			if (ui < 0) return;
			string question = MessageText(t.Messages[ui]);
			string key = Squash(question);
			if (key.Length == 0) return;
			string answer = MessageText(t.Messages.Skip(ui + 1).FirstOrDefault(m => m.Role == ChatRole.Assistant));

			var entry = _externals.LastOrDefault(c => c.Pid == v.Pid);
			if (entry == null || entry.Key != key)
			{
				// 重启后从归档恢复的同一轮对话：关联到当前 VS 进程，不再重复新增
				var restored = _externals.LastOrDefault(c => c.Restored && c.Pid == 0 && c.Key == key && string.Equals(c.VsKey, v.Key, StringComparison.OrdinalIgnoreCase));
				if (restored != null)
				{
					restored.Pid = v.Pid;
					_lastQuestion[v.Pid] = key;
					if (busy && !restored.Stopped) { restored.Generating = true; restored.Interrupted = false; restored.Finished = null; }
					else if (!busy && restored.Interrupted && answer.Length > 0) restored.Interrupted = false;  // VS 中已生成完毕
					entry = restored;
				}
			}
			if (entry != null && entry.Key == key)
			{
				bool changed = false;
				if (answer.Length > 0 && answer != entry.Answer) { entry.Answer = answer; changed = true; }
				if (entry.Generating && !busy) { entry.Generating = false; entry.Interrupted = false; entry.Finished = DateTime.Now; changed = true; }
				Archive.ManualChat(entry);
				if (changed) _taskPanel.RefreshItems();
				return;
			}

			bool first = !_lastQuestion.TryGetValue(v.Pid, out var prev);
			_lastQuestion[v.Pid] = key;
			if (prev == key) return;           // 已见过（如条目已被移除）
			if (first && !busy) return;        // 启动 / 新发现的 VS：已结束的旧对话只作为基准
			if (SentByUs(v.Pid, key)) return;  // 由本工具发送，不是手动对话

			foreach (var old in _externals.Where(c => c.Pid == v.Pid && c.Generating))
			{
				old.Generating = false;
				old.Finished = DateTime.Now;
				Archive.ManualChat(old);
			}
			var added = new ExternalChat
			{
				Id = _nextExternalId++, Pid = v.Pid, VsKey = v.Key, VsName = NameOf(v), Key = key,
				Question = question, Answer = answer, Generating = busy, Started = DateTime.Now,
				Finished = busy ? (DateTime?)null : DateTime.Now
			};
			_externals.Add(added);
			Archive.ManualChat(added);
			// 只在内存中保留最近 50 条已结束的手动对话（归档中保留全部）
			foreach (var stale in _externals.Where(c => !c.Generating).OrderByDescending(c => c.Finished ?? c.Started)
				.Skip(Math.Max(50, _settings.ExternalRestoreLimit)).ToList())
				_externals.Remove(stale);
			SendLog.Event(NameOf(v), "监听到手动对话：" + Clip(question, 60) + (busy ? "（生成中）" : "（已完成）"));
			_taskPanel.RefreshItems();
		}

		/// <summary>启动时从归档恢复最近的手动对话（显示为已结束）；未启用归档时不恢复。</summary>
		private void RestoreExternals()
		{
			if (!Archive.Enabled) return;
			try
			{
				var list = Archive.RestoreManual(_settings.ExternalRestoreHours, _settings.ExternalRestoreLimit);
				foreach (var c in list.OrderBy(c => c.Started))
				{
					if (_externals.Any(x => x.ArchiveId == c.ArchiveId)) continue;
					c.Id = _nextExternalId++;
					_externals.Add(c);
				}
				if (list.Count > 0) _taskPanel.RefreshItems();
			}
			catch (Exception ex) { SetStatus("恢复手动对话失败：" + ex.Message); }
		}

		/// <summary>对话监听漏读完成状态时兜底：VS 已关闭或 Copilot 已空闲的“生成中”条目标记为完成。</summary>
		private void SettleExternals(VsInstance completed = null)
		{
			bool changed = false;
			foreach (var c in _externals.Where(x => x.Generating))
			{
				var v = _instances.FirstOrDefault(i => i.Pid == c.Pid);
				if (v == completed || v == null || (v.Copilot == CopilotState.Idle && (DateTime.Now - c.Started).TotalSeconds > 8))
				{
					c.Generating = false;
					c.Finished = DateTime.Now;
					Archive.ManualChat(c);
					changed = true;
				}
			}
			if (changed) _taskPanel.RefreshItems();
		}

		private async void OnExternalAction(ExternalChat c, string action)
		{
			var v = _instances.FirstOrDefault(i => i.Pid == c.Pid) ?? FindVs(c.VsKey);
			switch (action)
			{
				case "copy":
					try { Clipboard.SetText(c.CopyText); SetStatus("已复制「" + c.VsName + "」的提问与回答"); }
					catch (Exception ex) { SetStatus("复制失败：" + ex.Message); }
					return;
				case "remove":
					_externals.Remove(c);
					Archive.ManualChat(c, true);
					_taskPanel.RefreshItems();
					return;
			}
			if (v == null) { SetStatus("「" + c.VsName + "」当前未打开"); return; }
			if (action == "stop")
			{
				string r;
				try { r = await DteWorker.RunSta(() => _chatSvc.InvokeButton(v, "CancelButton", "停止生成")); }
				catch (Exception ex) { r = "停止生成失败：" + ex.Message; }
				if (r.StartsWith("已"))
				{
					c.Generating = false;
					c.Stopped = true;
					c.Finished = DateTime.Now;
					Archive.ManualChat(c);
					_taskPanel.RefreshItems();
				}
				SetStatus($"「{NameOf(v)}」{r}");
			}
			else if (action == "open")
			{
				ActivateVs(v);
				// 把 Copilot 对话窗格切到前面，该轮对话即为当前会话的最后一条
				try { await DteWorker.Run(() => _vsOps.OpenCopilotChat(v)); } catch { }
				SetStatus($"已打开「{NameOf(v)}」并定位到 Copilot 对话");
			}
		}

		#endregion

		private void OnCopilotCompleted(VsInstance v, TimeSpan dur)
		{
			SettleExternals(v);
			var queued = _tasks.Items.FirstOrDefault(x => x.Status == QueueStatus.Running && x.VsKey == v.Key);
			if (queued != null) FinishTask(queued, v, dur);
			else PumpTasks();
			v.CompletionUnseen = !(v == Selected && ContainsFocus);
			UpdateRow(v);
			string name = NameOf(v);
			string msg = $"用时 {FormatDuration(dur)}，点击切换到该 VS";
			if (queued != null)
			{
				// 该任务是失败任务的重新发布：在完成通知中注明 / The task republished failed ones: say so in the completion notice
				var replaced = TaskHideList.Replaced(_settings.HiddenResentTasks, queued.Id);
				if (replaced.Count > 0)
				{
					string ids = string.Join(", ", replaced.Select(x => "#" + x));
					msg += $"\n任务 #{queued.Id} 是失败任务 {ids} 的重新排队（原条目已隐藏）/ Task #{queued.Id} requeued failed {ids} (original entry hidden)";
				}
			}
			SetStatus($"[{DateTime.Now:HH:mm:ss}] 「{name}」Copilot 已完成任务（{FormatDuration(dur)}）");
			if (v == Selected) _chatSvc.Poke();
			if (_settings.Sound) PlaySound();
			Native.Flash(v.MainHwnd);
			if (_settings.Popup)
				new ToastForm($"✔ {name}  Copilot 已完成", msg, () => ActivateVs(v)).Show();
			else
				_tray.ShowBalloonTip(5000, $"{name}  Copilot 已完成", msg, ToolTipIcon.Info);
			if (_settings.VoiceEnabled && _settings.HasVoiceKey) AnnounceCompletion(v);
		}

		/// <summary>读取最新对话，提炼 30 字概述并用豆包语音播报。</summary>
		private async void AnnounceCompletion(VsInstance v)
		{
			ChatTranscript t = null;
			try { t = await DteWorker.RunSta(() => _chatSvc.Read(v, 4)); } catch { }
			if ((t == null || !t.PaneFound) && !_chatCache.TryGetValue(v.Pid, out t)) t = null;
			var (summary, how) = await SummarizeForVoice(t);
			bool en = _settings.IsEnglishVoice;
			string text = _settings.VoiceIncludeName ? NameOf(v) + Prompts.NameSeparator(en) + summary : summary;
			if (!_settings.VoiceEnabled) return;
			SetStatus($"[{DateTime.Now:HH:mm:ss}] 🔊 播报（{how}）：{text}");
			_voice.Speak(text);
		}

		/// <summary>
		/// AI 总结（已配置时）→ 规则提取首句 →（与语音语言不一致时）豆包翻译，均失败时使用默认语句；全程按语音语言选择中文或英文。
		/// AI summary (if configured) → first sentence → Doubao translation (when it differs from the voice language) → default sentence; all in the voice language.
		/// </summary>
		private async Task<(string Text, string How)> SummarizeForVoice(ChatTranscript t)
		{
			bool en = _settings.IsEnglishVoice;
			string answer = TaskSummary.AnswerText(t);
			if (answer != null && _settings.VoiceAiSummary && _agent.Configured)
			{
				try
				{
					using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12)))
					{
						string s = TaskSummary.Clean(await Task.Run(() => _agent.SummarizeAsync(answer, en, cts.Token)));
						if (s != null) return (s, "AI 总结");
					}
				}
				catch (Exception ex) { SetStatus("AI 总结失败，改用规则提取：" + ex.Message); }
			}
			string summary = TaskSummary.From(t, en);
			if (summary == null) return (Prompts.DefaultCompletion(en), "默认");
			// 概述语言与语音语言不一致时才翻译 / Translate only when the summary language differs from the voice language
			if (_settings.VoiceTranslate && TaskSummary.IsChinese(summary) == en && !string.IsNullOrWhiteSpace(_settings.EffectiveVoiceApiKey))
			{
				string full = TaskSummary.FirstSentence(answer ?? "", false) ?? summary;
				try
				{
					using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)))
					{
						string key = _settings.EffectiveVoiceApiKey;
						string zh = TaskSummary.Clean(await Task.Run(() => DoubaoVoice.Translate(key, full, en ? VoiceLanguages.English : VoiceLanguages.Chinese, cts.Token)));
						if (zh != null) return (zh, "翻译");
					}
				}
				catch (Exception ex) { SetStatus("翻译失败，播报原文：" + ex.Message); }
			}
			return (summary, "首句");
		}

		private static void PlaySound()
		{
			try
			{
				var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Windows Notify System Generic.wav");
				if (File.Exists(wav)) new SoundPlayer(wav).Play();
				else SystemSounds.Asterisk.Play();
			}
			catch { }
		}

		private void SetStatus(string s) => _status.Text = s;

		private void SafeInvoke(Action a)
		{
			if (IsDisposed || !IsHandleCreated) return;
			try { BeginInvoke(a); } catch { }
		}

		/// <summary>
		/// 显示并激活主窗口：从托盘隐藏/最小化恢复、拉回可见屏幕，再绕过前台锁切到前台。
		/// Show and activate the main window: restore from tray/minimized, pull it back on screen, then bring it to the foreground.
		/// </summary>
		private void ShowMe()
		{
			if (IsDisposed) return;
			if (!Visible) Show();
			if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
			EnsureOnScreen();
			Native.ForceForeground(Handle, TopMost);
			Activate();
		}

		/// <summary>窗口落在已断开的显示器上时移回主屏。/ Move the window back if it sits on a disconnected monitor.</summary>
		private void EnsureOnScreen()
		{
			if (WindowState != FormWindowState.Normal) return;
			var b = Bounds;
			var title = new Rectangle(b.X, b.Y, b.Width, Math.Min(b.Height, 40));
			if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(title))) return;
			var wa = Screen.PrimaryScreen.WorkingArea;
			int w = Math.Min(b.Width, wa.Width), h = Math.Min(b.Height, wa.Height);
			Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
		}

		/// <summary>
		/// 用户在本窗口点击「停止调试」后，VS 会在调试结束时把自己提到前台；若前台确实被该 VS 抢走，则把焦点还给本窗口。
		/// 只收回因我们自己的操作被抢走的焦点，不影响用户主动切换的其他窗口。
		/// After the user clicks "stop debugging" here, VS raises itself when the session ends; if VS really took the foreground, give it back.
		/// Only reclaims focus taken as a result of our own action; windows the user switches to are left alone.
		/// </summary>
		private void ReclaimFocusFrom(VsInstance v)
		{
			int pid = v.Pid;
			int left = 2;
			var t = new Timer { Interval = 600 };
			t.Tick += (s, e) =>
			{
				if (--left <= 0) { t.Stop(); t.Dispose(); }
				if (IsDisposed || !Visible) return;
				var fg = Native.GetForegroundWindow();
				if (fg == Handle) { t.Stop(); t.Dispose(); return; }
				Native.GetWindowThreadProcessId(fg, out uint fgPid);
				if (fgPid != (uint)pid) return;
				Native.ForceForeground(Handle, TopMost);
			};
			t.Start();
		}

		#endregion

		#region 热键 / 生命周期

		private void RegisterHotkeys()
		{
			if (!IsHandleCreated) return;
			for (int i = 1; i <= 9; i++) Native.UnregisterHotKey(Handle, i);
			for (int i = 1; i <= Math.Min(9, _list.Items.Count); i++)
				Native.RegisterHotKey(Handle, i, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)(0x30 + i));
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == Native.WM_HOTKEY)
			{
				int id = m.WParam.ToInt32();
				if (id == ShowHotkeyId) ShowMe();
				else if (id >= 1 && id <= _list.Items.Count && _list.Items[id - 1] is VsInstance v)
					ActivateVs(v);
			}
			else if (Program.ShowMainMessage != 0 && m.Msg == Program.ShowMainMessage)
			{
				ShowMe();
				return;
			}
			base.WndProc(ref m);
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			SetStatus("正在扫描 VS 实例…");
			RefreshInstances();
			_refreshTimer.Start();
			_monitor.Start();
			_chatSvc.Start();
			_web.Apply();
			// 已安装的 AI Skill 随程序版本自动更新
			if (SkillInstaller.Installed) System.Threading.Tasks.Task.Run(() => SkillInstaller.Install(out _));
			var uiTimer = new Timer { Interval = 1000 };
			uiTimer.Tick += (s, a) =>
			{
				foreach (var v in _instances) if (v.Copilot == CopilotState.Busy) UpdateRow(v);
				if (Selected != null && (_awaitReply.ContainsKey(Selected.Pid) || _pendingSend.ContainsKey(Selected.Pid))) UpdateChatHeader();
				_tasks.RetrySaveIfNeeded();
				if (_externals.Count > 0) SettleExternals();
				MemoryTrim.Tick();
				_memGuard.Tick(_settings, BuildVsRefs);
				_agentSupervisor.Tick();
			};
			uiTimer.Start();
			_tasksReadyAt = DateTime.Now.AddSeconds(45);
			if (_tasks.LoadWarning != null) SetStatus("任务清单：" + _tasks.LoadWarning);
			RestoreExternals();
			AgentChatLog.TrimAsync();
			if (_archiveWarning != null)
			{
				SetStatus("⚠ " + _archiveWarning);
				_tray.ShowBalloonTip(8000, "历史归档", _archiveWarning, ToolTipIcon.Warning);
			}
			string watchdogErr = ProcessWatchdog.Apply(_settings);
			if (watchdogErr != null) SetStatus("⚠ 看门狗启动失败 / Watchdog failed to start：" + watchdogErr);
			if (ProcessWatchdog.RestartedAfterCrash)
			{
				string note = "VSManager 已由看门狗自动重启（上次退出代码 " + ProcessWatchdog.FormatExitCode(ProcessWatchdog.PreviousExitCode) + "）" +
					(_pausedAfterCrash > 0 ? "，" + _pausedAfterCrash + " 条发送中的任务已暂停待确认" : "") +
					" / Restarted by the watchdog" + (_pausedAfterCrash > 0 ? "; " + _pausedAfterCrash + " interrupted send(s) paused" : "");
				SetStatus("⟳ " + note);
				ShowBalloon("多 VS 管理工具 / VSManager", note, ToolTipIcon.Warning);
				AppLog.Write(ProcessWatchdog.LogFile, "重启后恢复 / Recovered after restart：" + _tasks.Items.Count + " 条任务 / tasks，暂停 / paused " + _pausedAfterCrash);
			}
			_tasks.Changed += () => { if (_tasks.Items.Any(x => QueueStatus.Active(x.Status))) _taskTimer.Start(); };
			if (_tasks.Items.Any(x => QueueStatus.Active(x.Status))) _taskTimer.Start();
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray) Hide();
			UpdateChatVisibility();
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			UpdateChatVisibility();
		}

		private void UpdateChatVisibility()
		{
			if (_chatSvc == null) return;
			bool wasHidden = _chatSvc.Hidden;
			_chatSvc.Hidden = !Visible || WindowState == FormWindowState.Minimized;
			if (_chatSvc.Hidden && !wasHidden) MemoryTrim.Tick(true);
			if (!_chatSvc.Hidden) _chatSvc.Poke();
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (!_exiting && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
			{
				e.Cancel = true;
				Hide();
				_tray.ShowBalloonTip(2000, "多 VS 管理工具", "已最小化到托盘，继续监听 Copilot。右键托盘图标可退出。", ToolTipIcon.Info);
				return;
			}
			// 真正退出：告诉看门狗这是正常退出 / Real exit: tell the watchdog this is a clean exit
			ProcessWatchdog.MarkCleanExit();
			_taskTimer.Stop();
			if (!_tasks.Save() && e.CloseReason != CloseReason.WindowsShutDown)
				MessageBox.Show(this, "任务清单保存失败：" + _tasks.SaveError + "\n\n最近的改动可能未写入 " + TaskQueue.FilePath + "，详情见 " + TaskQueue.LogPath,
					"多 VS 管理工具", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			_monitor.Stop();
			_chatSvc.Stop();
			Archive.Flush(3000, true);
			_web.Dispose();
			_voice.Dispose();
			_agent.Dispose();
			CleanupVoice();
			for (int i = 1; i <= 9; i++) Native.UnregisterHotKey(Handle, i);
			Native.UnregisterHotKey(Handle, ShowHotkeyId);
			_tray.Visible = false;
			_tray.Dispose();
			base.OnFormClosing(e);
		}

		#endregion
	}
}
