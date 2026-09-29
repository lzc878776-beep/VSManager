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
    /// <summary>
    /// 重启前的界面状态（窗口位置 / 大小 / 最大化、是否在前台、当前页面、选中的 VS、AI 输入框草稿），
    /// 写入 %APPDATA%\VSManager\restart-ui.json，新进程启动时读取一次即删除，让重启前后界面保持一致。
    /// UI state before a restart (window bounds / maximized, foreground, current page, selected VS, AI input draft), written to
    /// %APPDATA%\VSManager\restart-ui.json; the new process reads it once and deletes it so the UI looks the same after the restart.
    /// </summary>
    [DataContract]
    public sealed class RestartUiState
    {
        [DataMember] public DateTime CreatedUtc;
        [DataMember] public int OldPid;
        [DataMember] public int X;
        [DataMember] public int Y;
        [DataMember] public int Width;
        [DataMember] public int Height;
        [DataMember] public bool Maximized;
        /// <summary>重启前窗口已最小化或隐藏到托盘。/ The window was minimized or hidden to the tray.</summary>
        [DataMember] public bool Hidden;
        /// <summary>重启前窗口在前台（用户正在看）。/ The window was in the foreground (the user was looking at it).</summary>
        [DataMember] public bool Foreground;
        [DataMember] public bool AgentPage;
        /// <summary>自测重启固定回到 AI 总控页；普通重启仍恢复原页面。/ Self-test restarts open the AI page; ordinary restarts keep the saved page.</summary>
        [DataMember(EmitDefaultValue = false)] public bool SelfTest;
        [DataMember] public int SelectedVsPid;
        [DataMember] public string AgentDraft;
        /// <summary>过渡画面的命名事件；为空表示没有过渡画面。/ Named event of the cover window; empty when there is none.</summary>
        [DataMember] public string CoverEvent;
        /// <summary>旧进程等待截图就绪的毫秒数；零表示没有确认。/ Milliseconds the old process waited for snapshot readiness; zero means no acknowledgement.</summary>
        [DataMember(EmitDefaultValue = false)] public int CoverReadyMilliseconds;

        public Rectangle Bounds => new Rectangle(X, Y, Width, Height);
    }

    /// <summary>
    /// 重启界面状态的读写与校验（纯逻辑，便于测试）。
    /// Persistence and validation of the restart UI state (pure logic, easy to test).
    /// </summary>
    public static class RestartUi
    {
        /// <summary>有效期：超过即忽略（例如重启失败后很久才手动启动）。/ Lifetime: older states are ignored (e.g. a manual start long after a failed restart).</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

        /// <summary>自测重启优先 AI 总控页，但不改变 AI 功能的启用设置。/ Self-test restarts prefer the AI page without changing whether the AI feature is enabled.</summary>
        public static bool WantsAgentPage(RestartUiState state, bool agentEnabled) =>
            state != null && agentEnabled && (state.SelfTest || state.AgentPage);

        /// <summary>草稿最大长度。/ Maximum draft length.</summary>
        public const int MaxDraftChars = 20000;

        public static string FilePath => Path.Combine(AppPaths.DataFolder, "restart-ui.json");

        /// <summary>过渡画面命名事件名（本机会话内）。/ Name of the cover window's event (local session).</summary>
        public static string CoverEventName(string id) => @"Local\VSManager-restart-cover-" + id;

        private static string _activeCoverEvent;

        public static void ReleaseActiveCover() => SignalCover(_activeCoverEvent);

        /// <summary>仅在事件确实存在并已触发时返回真。/ Returns true only when the event exists and was actually signaled.</summary>
        public static bool SignalCover(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            try
            {
                if (System.Threading.EventWaitHandle.TryOpenExisting(eventName, out var handle))
                    using (handle) return handle.Set();
            }
            catch { }
            return false;
        }

        public static string Save(RestartUiState state)
        {
            if (state == null) return "状态为空 / Empty state";
            state.CreatedUtc = state.CreatedUtc.Kind == DateTimeKind.Utc ? state.CreatedUtc : DateTime.SpecifyKind(state.CreatedUtc, DateTimeKind.Utc);
            if (state.AgentDraft != null && state.AgentDraft.Length > MaxDraftChars) state.AgentDraft = state.AgentDraft.Substring(0, MaxDraftChars);
            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                var r = AtomicFile.Write(FilePath, stream =>
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                        new DataContractJsonSerializer(typeof(RestartUiState)).WriteObject(w, state);
                }, backupBeforeOverwrite: false, skipFallbackOnSerializationError: true);
                return r.Ok ? null : r.Error.GetType().Name + "：" + r.Error.Message;
            }
            catch (Exception ex) { return ex.GetType().Name + "：" + ex.Message; }
        }

        /// <summary>
        /// 读取并删除界面状态；没有、过期、损坏或属于当前进程时返回 null。
        /// Reads and deletes the UI state; null when absent, expired, corrupt or written by the current process.
        /// </summary>
        public static RestartUiState Take(DateTime nowUtc, int currentPid)
        {
            string path = FilePath;
            if (!File.Exists(path)) return null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Skip(3).ToArray();
                RestartUiState s;
                using (var r = JsonReaderWriterFactory.CreateJsonReader(data, XmlDictionaryReaderQuotas.Max))
                    s = (RestartUiState)new DataContractJsonSerializer(typeof(RestartUiState)).ReadObject(r);
                if (s == null || s.OldPid == currentPid) return null;
                if (s.CreatedUtc > nowUtc.AddMinutes(1) || nowUtc - s.CreatedUtc > MaxAge) return null;
                _activeCoverEvent = s.CoverEvent;
                return s;
            }
            catch { return null; }
            finally { Discard(); }
        }

        public static void Discard()
        {
            try { File.Delete(FilePath); } catch { }
            try { File.Delete(FilePath + ".tmp"); } catch { }
        }

        /// <summary>
        /// 要恢复的窗口位置：尺寸不小于最小尺寸，且标题栏仍落在某个屏幕的工作区内；否则返回 null（按默认位置显示）。
        /// Bounds to restore: at least the minimum size and with the title bar on some screen's working area; null otherwise (default placement).
        /// </summary>
        public static Rectangle? FitBounds(RestartUiState s, IEnumerable<Rectangle> workingAreas, Size minimum)
        {
            if (s == null || s.Width <= 0 || s.Height <= 0) return null;
            var b = new Rectangle(s.X, s.Y, Math.Max(s.Width, minimum.Width), Math.Max(s.Height, minimum.Height));
            var title = new Rectangle(b.X, b.Y, b.Width, Math.Min(b.Height, 40));
            return (workingAreas ?? Enumerable.Empty<Rectangle>()).Any(a => a.IntersectsWith(title)) ? b : (Rectangle?)null;
        }

        /// <summary>首次绘制并选中页面后撤除截图；找不到 VS 时最多额外等三秒。/ Release after painting and selection, waiting at most three extra seconds for a missing VS.</summary>
        public static bool CanReleaseCover(bool painted, bool selected, bool hidden, bool userMoved, TimeSpan elapsed) =>
            hidden || userMoved || painted && (selected || elapsed >= TimeSpan.FromSeconds(3));

        /// <summary>实例刷新结束即可续跑；空列表最多等八秒，助手忙时仍等待。/ Resume once discovery completes; wait at most eight seconds for an empty list, but always wait for the assistant.</summary>
        public static bool CanResumeStartup(bool refreshing, int instanceCount, bool agentBusy, TimeSpan elapsed) =>
            !agentBusy && ((!refreshing && instanceCount > 0) || elapsed >= TimeSpan.FromSeconds(8));

        /// <summary>
        /// 先在旧窗口正后方准备不激活的截图并确认就绪；不置顶、不占任务栏，关闭事件或超时后清理。
        /// Prepares a nonactivating snapshot directly behind the old window and acknowledges readiness; never topmost or on the taskbar, and cleans up on cancellation or timeout.
        /// </summary>
        public const string CoverScript = @"param([string]$Image, [int]$X, [int]$Y, [int]$W, [int]$H, [int]$OldPid, [long]$OldWindow, [string]$EventName, [string]$Text)
$ErrorActionPreference = 'Stop'
$ev = $null; $ready = $null; $bmp = $null; $f = $null; $t = $null
try {
  $ev = [System.Threading.EventWaitHandle]::OpenExisting($EventName)
  $ready = [System.Threading.EventWaitHandle]::OpenExisting($EventName + '-ready')
  if ($ev.WaitOne(0)) { return }
  Add-Type -AssemblyName System.Windows.Forms, System.Drawing
  Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;
namespace VsmCover {
  public sealed class CoverForm : Form {
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; }
    }
    [DllImport(""user32.dll"")] public static extern IntPtr GetForegroundWindow();
    [DllImport(""user32.dll"")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport(""user32.dll"")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport(""user32.dll"")] public static extern bool SetProcessDPIAware();
  }
}
'@
  try { if (-not [VsmCover.CoverForm]::SetProcessDpiAwarenessContext([IntPtr](-4))) { [void][VsmCover.CoverForm]::SetProcessDPIAware() } } catch { }
  if ($ev.WaitOne(0)) { return }
  $bmp = [System.Drawing.Image]::FromFile($Image)
  $f = New-Object VsmCover.CoverForm
  $f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.ShowInTaskbar = $false; $f.Opacity = 0
  $f.Bounds = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
  $f.BackgroundImage = $bmp; $f.BackgroundImageLayout = 'Stretch'
  $l = New-Object System.Windows.Forms.Label
  $l.AutoSize = $true; $l.Text = $Text; $l.Visible = $false; $l.Padding = New-Object System.Windows.Forms.Padding(14, 8, 14, 8)
  $l.BackColor = [System.Drawing.Color]::FromArgb(62, 52, 96); $l.ForeColor = [System.Drawing.Color]::White
  $l.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
  $f.Controls.Add($l)
  $clock = [System.Diagnostics.Stopwatch]::StartNew()
  $script:exitedAt = -1
  $f.Add_Shown({
    if ($ev.WaitOne(0) -or [VsmCover.CoverForm]::GetForegroundWindow() -ne [IntPtr]$OldWindow) { $f.Close(); return }
    if (-not [VsmCover.CoverForm]::SetWindowPos($f.Handle, [IntPtr]$OldWindow, 0, 0, 0, 0, 0x13)) { $f.Close(); return }
    # 旧窗口置顶也不让截图继承置顶 / Do not inherit topmost status from the old window.
    if (-not [VsmCover.CoverForm]::SetWindowPos($f.Handle, [IntPtr](-2), 0, 0, 0, 0, 0x13)) { $f.Close(); return }
    $l.Left = [Math]::Max(0, [int](($f.ClientSize.Width - $l.Width) / 2)); $l.Top = [Math]::Max(0, $f.ClientSize.Height - $l.Height - 48)
    $f.Opacity = 1; $f.Update(); [void]$ready.Set()
  })
  $f.Add_MouseDown({ $f.Close() }); $l.Add_MouseDown({ $f.Close() })
  $t = New-Object System.Windows.Forms.Timer
  $t.Interval = 75
  $t.Add_Tick({
    if ($ev.WaitOne(0) -or $clock.Elapsed.TotalSeconds -gt 240) { $f.Close(); return }
    if ($script:exitedAt -lt 0) {
      if (-not (Get-Process -Id $OldPid -ErrorAction SilentlyContinue)) { $script:exitedAt = $clock.ElapsedMilliseconds }
      elseif ([VsmCover.CoverForm]::GetForegroundWindow() -ne [IntPtr]$OldWindow -or $clock.Elapsed.TotalSeconds -gt 90) { $f.Close(); return }
    }
    if ($script:exitedAt -ge 0 -and $clock.ElapsedMilliseconds - $script:exitedAt -ge 1500) { $l.Visible = $true }
  })
  $t.Start()
  [System.Windows.Forms.Application]::Run($f)
} finally {
  if ($t) { $t.Dispose() }
  if ($f) { $f.Dispose() }
  if ($bmp) { $bmp.Dispose() }
  if ($ready) { $ready.Dispose() }
  if ($ev) { $ev.Dispose() }
  Remove-Item -LiteralPath $Image -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
}
";
    }
}
