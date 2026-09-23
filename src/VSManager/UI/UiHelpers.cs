using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace VSManager
{
    public static class Dpi
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForSystem();

        public static readonly float Scale = GetScale();

        private static float GetScale()
        {
            try { return GetDpiForSystem() / 96f; } catch { return 1f; }
        }

        public static int S(int v) => (int)Math.Round(v * Scale);
    }

    public static class ScreenHelper
    {
        /// <summary>按 Windows 显示器编号 (DISPLAY1, DISPLAY2...) 排序。</summary>
        public static List<Screen> Ordered() =>
            Screen.AllScreens.OrderBy(s => Number(s)).ThenBy(s => s.Bounds.X).ToList();

        public static int Number(Screen s)
        {
            var m = Regex.Match(s.DeviceName ?? "", @"(\d+)$");
            return m.Success ? int.Parse(m.Groups[1].Value) : 999;
        }

        public static string Label(Screen s, int index) =>
            $"屏幕{index + 1}  ({s.DeviceName.TrimStart('\\', '.')}  {s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? "  主显示器" : "")})";

        public static Screen Find(string deviceName) =>
            Screen.AllScreens.FirstOrDefault(s => s.DeviceName == deviceName);

        public static void Identify()
        {
            var screens = Ordered();
            var forms = new List<Form>();
            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i];
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    TopMost = true,
                    ShowInTaskbar = false,
                    BackColor = Color.FromArgb(0, 122, 204),
                    Opacity = 0.9,
                    Bounds = new Rectangle(s.WorkingArea.X + s.WorkingArea.Width / 2 - Dpi.S(150), s.WorkingArea.Y + s.WorkingArea.Height / 2 - Dpi.S(150), Dpi.S(300), Dpi.S(300)),
                    Text = ""
                };
                f.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    Text = (i + 1).ToString(),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", Dpi.S(120), FontStyle.Bold, GraphicsUnit.Pixel),
                    TextAlign = ContentAlignment.MiddleCenter
                });
                f.Show();
                forms.Add(f);
            }
            var t = new Timer { Interval = 2000 };
            t.Tick += (a, b) => { t.Stop(); t.Dispose(); forms.ForEach(x => x.Close()); };
            t.Start();
        }
    }

    /// <summary>右下角弹出通知，点击可激活对应 VS。</summary>
    public class ToastForm : Form
    {
        private static readonly List<ToastForm> Open = new List<ToastForm>();
        private readonly Timer _timer = new Timer { Interval = 15000 };

        public ToastForm(string title, string message, Action onClick)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(45, 45, 48);
            Size = new Size(Dpi.S(360), Dpi.S(96));
            Padding = new Padding(1);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(37, 37, 38), Cursor = Cursors.Hand };
            var bar = new Panel { Dock = DockStyle.Left, Width = Dpi.S(6), BackColor = Color.FromArgb(16, 185, 129) };
            var lblTitle = new Label
            {
                Text = title, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
                Location = new Point(Dpi.S(16), Dpi.S(10)), AutoSize = false, Size = new Size(Dpi.S(310), Dpi.S(24)), Cursor = Cursors.Hand
            };
            var lblMsg = new Label
            {
                Text = message, ForeColor = Color.Gainsboro, Font = new Font("Microsoft YaHei UI", 9),
                Location = new Point(Dpi.S(16), Dpi.S(38)), AutoSize = false, Size = new Size(Dpi.S(330), Dpi.S(50)), Cursor = Cursors.Hand
            };
            var close = new Label
            {
                Text = "✕", ForeColor = Color.Silver, Location = new Point(Dpi.S(334), Dpi.S(6)), AutoSize = true, Cursor = Cursors.Hand
            };
            close.Click += (s, e) => Close();
            body.Controls.AddRange(new Control[] { close, lblTitle, lblMsg });
            Controls.Add(body);
            Controls.Add(bar);

            EventHandler click = (s, e) => { onClick?.Invoke(); Close(); };
            body.Click += click; lblTitle.Click += click; lblMsg.Click += click;

            _timer.Tick += (s, e) => Close();
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Open.Add(this);
            Relayout();
            _timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Dispose();
            Open.Remove(this);
            Relayout();
            base.OnFormClosed(e);
        }

        private static void Relayout()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int y = wa.Bottom - 10;
            foreach (var t in Open)
            {
                y -= t.Height;
                t.Location = new Point(wa.Right - t.Width - 10, y);
                y -= 8;
            }
        }
    }
}
