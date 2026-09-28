using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using VSManager.CadAgent;

namespace VSManager
{
    /// <summary>
    /// CAD 动作结果窗口：概要 + 每张截图 / 每份日志一个选项卡。内容只在内存中，关闭即释放，不写磁盘。
    /// CAD action result window: a summary plus one tab per screenshot / log. Content lives in memory only, is released on close and never written to disk.
    /// </summary>
    public sealed class CadArtifactsForm : Form
    {
        private readonly List<Image> _images = new List<Image>();

        public CadArtifactsForm(string title, CadSequenceResult result)
        {
            Text = title;
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(960), Dpi.S(640));
            MinimumSize = new Size(Dpi.S(520), Dpi.S(360));
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(TextPage("概要 / Summary", Summary(result)));
            int shot = 0, log = 0;
            foreach (var r in result.Items)
                foreach (var a in r.Items)
                {
                    if (a.Kind == CadArtifact.Image && !string.IsNullOrEmpty(a.Data))
                    {
                        Image img;
                        try { using (var ms = new MemoryStream(Convert.FromBase64String(a.Data))) img = new Bitmap(Image.FromStream(ms)); }
                        catch { continue; }
                        _images.Add(img);
                        var page = new TabPage("🖼 " + (++shot)) { BackColor = Theme.Surface };
                        page.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = img, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black });
                        tabs.TabPages.Add(page);
                    }
                    else if (a.Kind == CadArtifact.Log)
                        tabs.TabPages.Add(TextPage("📄 " + (++log) + " " + a.Name, a.Content ?? ""));
                }
            if (shot > 0) tabs.SelectedIndex = 1;
            Controls.Add(tabs);
            FormClosed += (s, e) => { foreach (var i in _images) i.Dispose(); _images.Clear(); };
        }

        private static TabPage TextPage(string title, string text)
        {
            var page = new TabPage(title) { BackColor = Theme.Surface };
            page.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
                Font = new Font("Consolas", Theme.Regular.Size), Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
            });
            return page;
        }

        internal static string Summary(CadSequenceResult result)
        {
            var lines = new List<string> { (result.Ok ? "✅ " : "❌ ") + (result.Message ?? "") + "  (" + result.DurationMs + " ms)", "" };
            int i = 0;
            foreach (var r in result.Items)
            {
                i++;
                string mark = r.Ok ? "✓" : r.ErrorCode == CadErrors.Skipped ? "–" : r.ErrorCode == CadErrors.Timeout ? "⏱" : "✗";
                lines.Add(i + ". " + mark + " " + r.Action + (r.Ok || r.ErrorCode == null ? "" : " [" + r.ErrorCode + "]") + "  " + (r.Message ?? "")
                    + (r.DurationMs > 0 ? "  (" + r.DurationMs + " ms)" : ""));
                foreach (var a in r.Items.Where(x => x.Kind == CadArtifact.Text && !string.IsNullOrEmpty(x.Content)))
                    lines.Add("     " + a.Name + ": " + a.Content.Replace("\n", "; "));
            }
            return string.Join("\n", lines);
        }
    }
}
