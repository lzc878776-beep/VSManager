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
        [DataMember] public int SelectedVsPid;
        [DataMember] public string AgentDraft;
        /// <summary>过渡画面的命名事件；为空表示没有过渡画面。/ Named event of the cover window; empty when there is none.</summary>
        [DataMember] public string CoverEvent;

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

        /// <summary>草稿最大长度。/ Maximum draft length.</summary>
        public const int MaxDraftChars = 20000;

        public static string FilePath => Path.Combine(AppPaths.DataFolder, "restart-ui.json");

        /// <summary>过渡画面命名事件名（本机会话内）。/ Name of the cover window's event (local session).</summary>
        public static string CoverEventName(string id) => @"Local\VSManager-restart-cover-" + id;

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

        /// <summary>
        /// 过渡画面脚本（PowerShell）：等旧进程退出后在原位置显示旧窗口截图，新窗口就绪（命名事件被触发）或超时后关闭并删除截图。
        /// Cover script (PowerShell): after the old process exits it shows the old window's screenshot in place, and closes and deletes
        /// the screenshot once the new window is ready (the named event is set) or on timeout.
        /// </summary>
        public const string CoverScript = @"param([string]$Image, [int]$X, [int]$Y, [int]$W, [int]$H, [int]$OldPid, [string]$EventName, [string]$Text)
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
try { Add-Type -Namespace VsmCover -Name Dpi -MemberDefinition '[DllImport(""user32.dll"")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr v); [DllImport(""user32.dll"")] public static extern bool SetProcessDPIAware();'
  if (-not [VsmCover.Dpi]::SetProcessDpiAwarenessContext([IntPtr](-4))) { [void][VsmCover.Dpi]::SetProcessDPIAware() } } catch { }
$created = $false
$ev = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $EventName, [ref]$created)
$start = Get-Date
try {
  while ($true) {
    if ($ev.WaitOne(200)) { return }
    if (-not (Get-Process -Id $OldPid -ErrorAction SilentlyContinue)) { break }
    if (((Get-Date) - $start).TotalSeconds -gt 90) { return }
  }
  $bmp = [System.Drawing.Image]::FromFile($Image)
  $f = New-Object System.Windows.Forms.Form
  $f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.ShowInTaskbar = $false
  $f.Bounds = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
  $f.BackgroundImage = $bmp; $f.BackgroundImageLayout = 'Stretch'
  $l = New-Object System.Windows.Forms.Label
  $l.AutoSize = $true; $l.Text = $Text; $l.Padding = New-Object System.Windows.Forms.Padding(14, 8, 14, 8)
  $l.BackColor = [System.Drawing.Color]::FromArgb(62, 52, 96); $l.ForeColor = [System.Drawing.Color]::White
  $l.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
  $f.Controls.Add($l)
  $f.Add_Shown({ $l.Left = [Math]::Max(0, [int](($f.ClientSize.Width - $l.Width) / 2)); $l.Top = [Math]::Max(0, $f.ClientSize.Height - $l.Height - 48) })
  $t = New-Object System.Windows.Forms.Timer
  $t.Interval = 150
  $t.Add_Tick({ if ($ev.WaitOne(0) -or ((Get-Date) - $start).TotalSeconds -gt 240) { $t.Stop(); $f.Close() } })
  $t.Start()
  [System.Windows.Forms.Application]::Run($f)
  $bmp.Dispose()
} finally {
  $ev.Dispose()
  Remove-Item -LiteralPath $Image -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
}
";
    }
}
