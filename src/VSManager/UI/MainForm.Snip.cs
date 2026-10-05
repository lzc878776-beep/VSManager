using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 主窗口的截图器：顶部「✂ 截图」按钮、托盘菜单与全局快捷键 Ctrl+Alt+Shift+S，框选结果复制到剪贴板。
    /// Screenshot tool of the main window: the "✂ 截图" header button, the tray menu and the global hotkey Ctrl+Alt+Shift+S;
    /// the selected area is copied to the clipboard.
    /// </summary>
    public partial class MainForm
    {
        private const int SnipHotkeyId = 101;
        private const string SnipLogFile = "snip.log";
        private const uint SnipHotkeyKey = 0x53; // S
        private const uint ModShift = 4;
        private readonly FlatButton _btnSnip = new FlatButton();
        private bool _snipHotkeyOk;
        private bool _snipping;

        /// <summary>快捷键说明文字（注册失败时为空）。/ Hotkey caption (empty when registration failed).</summary>
        private string SnipHotkeyText => _snipHotkeyOk ? "Ctrl+Alt+Shift+S" : "";

        private void BuildSnipButton()
        {
            _btnSnip.Text = "✂  截图";
            _btnSnip.Size = new Size(Dpi.S(92), Dpi.S(34));
            _btnSnip.Click += (s, e) => StartSnip(true);
            _header.Controls.Add(_btnSnip);
            UpdateSnipTooltip();
        }

        private void UpdateSnipTooltip()
        {
            string key = SnipHotkeyText;
            _tips.SetToolTip(_btnSnip, "框选屏幕区域截图（截图时暂时隐藏本窗口），结果复制到剪贴板，可粘贴到 AI 助手、对话输入框或笔记"
                + (key.Length > 0 ? "；全局快捷键 " + key + "（不隐藏本窗口）" : "")
                + "\nSelect a screen area to capture (this window is hidden while capturing); the image is copied to the clipboard for the AI assistant, chat input or notebook"
                + (key.Length > 0 ? "; global hotkey " + key + " (keeps this window visible)" : ""));
        }

        private void RegisterSnipHotkey()
        {
            _snipHotkeyOk = Native.RegisterHotKey(Handle, SnipHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | ModShift | Native.MOD_NOREPEAT, SnipHotkeyKey);
            if (!_snipHotkeyOk) AppLog.Write(SnipLogFile, "截图快捷键 Ctrl+Alt+Shift+S 注册失败（可能被其他程序占用）/ Screenshot hotkey Ctrl+Alt+Shift+S could not be registered (possibly taken by another program)");
            UpdateSnipTooltip();
        }

        private void UnregisterSnipHotkey() => Native.UnregisterHotKey(Handle, SnipHotkeyId);

        /// <summary>
        /// 开始一次框选截图。<paramref name="hideSelf"/> 为 true 时截图前暂时隐藏主窗口，不把自己拍进去；<paramref name="delayMs"/> 用于等菜单收起。
        /// Starts a region screenshot. When <paramref name="hideSelf"/> is true the main window is hidden during the capture;
        /// <paramref name="delayMs"/> lets a menu close first.
        /// </summary>
        private async void StartSnip(bool hideSelf, int delayMs = 0)
        {
            if (_snipping || IsDisposed) return;
            _snipping = true;
            List<ScreenSnipCapture> captures = null;
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs);
                bool cloaked = false;
                if (hideSelf && Visible && WindowState != FormWindowState.Minimized && IsHandleCreated)
                {
                    cloaked = ScreenSnipper.Cloak(Handle, true);
                    if (cloaked) await Task.Delay(150);
                }
                try { captures = ScreenSnipper.CaptureScreens(); }
                finally { if (cloaked) ScreenSnipper.Cloak(Handle, false); }

                using (var shot = await ScreenSnipper.SelectAsync(captures))
                {
                    if (shot == null) { SetStatus("已取消截图 / Screenshot cancelled"); AppLog.Write(SnipLogFile, "截图已取消 / Screenshot cancelled"); return; }
                    int w = shot.Width, h = shot.Height;
                    await Task.Run(() => DirectClipboard.SetImage(shot));
                    string zh = $"截图 {w}×{h} 已复制到剪贴板，可粘贴到 AI 助手、对话输入框或笔记";
                    string en = $"Screenshot {w}×{h} copied to the clipboard; paste it into the AI assistant, chat input or notebook";
                    SetStatus("✂ " + zh + " / " + en);
                    AppLog.Write(SnipLogFile, $"截图 {w}×{h} 已复制到剪贴板 / Screenshot {w}×{h} copied to the clipboard");
                    if (!Visible || WindowState == FormWindowState.Minimized)
                        _tray.ShowBalloonTip(4000, "截图 / Screenshot", zh + "\n" + en, ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(SnipLogFile, "截图失败 / Screenshot failed", ex);
                SetStatus("截图失败 / Screenshot failed：" + ex.Message);
            }
            finally
            {
                if (captures != null) foreach (var c in captures) c.Dispose();
                _snipping = false;
            }
        }
    }
}
