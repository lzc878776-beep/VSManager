using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 与深色主题一致的提示框：悬停一段时间后显示；按固定宽高比自动换行；渲染标题、「标签：内容」和中英双语（英文以次要颜色另起一行）。
    /// Tooltip matching the dark theme: appears after a hover delay, wraps text to a fixed aspect ratio and renders a title,
    /// "label: value" pairs and bilingual text (English on its own line in a secondary color).
    /// </summary>
    public class ThemedToolTip : ToolTip
    {
        internal const int HoverDelay = 700;
        internal const double AspectRatio = 2.4;
        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        private const TextFormatFlags SingleLine = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        private static readonly Regex LabelPattern = new Regex(@"^(?<label>[^：:\s/]{1,10})[：:]\s*(?<value>.+)$", RegexOptions.Compiled);

        public ThemedToolTip()
        {
            OwnerDraw = true;
            InitialDelay = HoverDelay;
            ReshowDelay = 200;
            AutoPopDelay = 30000;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Popup += OnPopup;
            Draw += OnDraw;
        }

        /// <summary>一段排版单元：可选标签、中文（主）与英文（次）。/ One layout block: optional label, primary text and secondary (English) text.</summary>
        internal sealed class Block
        {
            public string Label;
            public string Primary;
            public string Secondary;
            public bool Title;
            public bool GapBefore;
        }

        internal static List<Block> Parse(string text)
        {
            var blocks = new List<Block>();
            bool gap = false;
            foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Replace("**", "").Trim();
                if (line.Length == 0) { gap = blocks.Count > 0; continue; }
                var b = new Block { GapBefore = gap };
                gap = false;
                SplitBilingual(line, out b.Primary, out b.Secondary);
                var m = LabelPattern.Match(b.Primary);
                if (m.Success && HasCjk(m.Groups["label"].Value)) { b.Label = m.Groups["label"].Value + "："; b.Primary = m.Groups["value"].Value; }
                // 英文单独成行且紧跟中文行时并入上一段 / An English-only line right after a Chinese line joins that block
                var last = blocks.LastOrDefault();
                if (!b.GapBefore && b.Label == null && b.Secondary == null && !HasCjk(b.Primary) && last != null && last.Secondary == null && HasCjk(last.Primary))
                {
                    last.Secondary = b.Primary;
                    continue;
                }
                blocks.Add(b);
            }
            if (blocks.Count > 1 && blocks[0].Label == null && (blocks[0].Primary.Length <= 40)) blocks[0].Title = true;
            return blocks;
        }

        /// <summary>在第一个右侧不含中文的「 / 」处拆分中英文。/ Splits at the first " / " whose right side contains no CJK text.</summary>
        internal static void SplitBilingual(string line, out string primary, out string secondary)
        {
            primary = line;
            secondary = null;
            if (!HasCjk(line)) return;
            for (int i = line.IndexOf(" / ", StringComparison.Ordinal); i > 0; i = line.IndexOf(" / ", i + 3, StringComparison.Ordinal))
            {
                string right = line.Substring(i + 3).Trim();
                string left = line.Substring(0, i).Trim();
                if (right.Length > 0 && !HasCjk(right) && HasCjk(left)) { primary = left; secondary = right; return; }
            }
        }

        internal static bool HasCjk(string s) => s != null && s.Any(c => c >= 0x3000 && c <= 0x9FFF || c >= 0xFF00 && c <= 0xFFEF);

        private static Font PrimaryFont(Block b) => b.Title ? Theme.SemiBold : Theme.Regular;
        private static int Pad => Dpi.S(10);
        private static int LineGap => Dpi.S(4);

        /// <summary>按宽度排版，返回内容高度。/ Lays out at a width and returns the content height.</summary>
        private static int Measure(List<Block> blocks, int width, Action<Block, Rectangle, Rectangle, Rectangle> place = null)
        {
            int y = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (i > 0) y += b.GapBefore ? Dpi.S(10) : LineGap;
                int labelW = b.Label == null ? 0 : TextRenderer.MeasureText(b.Label, Theme.SemiBold, Size.Empty, SingleLine).Width;
                int textW = Math.Max(Dpi.S(40), width - labelW);
                var ps = TextRenderer.MeasureText(b.Primary, PrimaryFont(b), new Size(textW, int.MaxValue), Flags);
                var labelRect = new Rectangle(0, y, labelW, ps.Height);
                var primaryRect = new Rectangle(labelW, y, textW, ps.Height);
                y += ps.Height;
                var secondaryRect = Rectangle.Empty;
                if (b.Secondary != null)
                {
                    y += Dpi.S(1);
                    var ss = TextRenderer.MeasureText(b.Secondary, Theme.Small, new Size(textW, int.MaxValue), Flags);
                    secondaryRect = new Rectangle(labelW, y, textW, ss.Height);
                    y += ss.Height;
                }
                place?.Invoke(b, labelRect, primaryRect, secondaryRect);
            }
            return y;
        }

        /// <summary>选择最接近固定宽高比的内容宽度。/ Picks the content width closest to the fixed aspect ratio.</summary>
        internal static Size Layout(List<Block> blocks)
        {
            // 以字号为单位，与 DPI 感知方式无关 / Measured in font units so it does not depend on DPI awareness
            int em = Math.Max(8, TextRenderer.MeasureText("中", Theme.Regular, Size.Empty, SingleLine).Height);
            int min = em * 11, max = em * 34, step = Math.Max(4, em);
            int natural = 0;
            foreach (var b in blocks)
            {
                int w = (b.Label == null ? 0 : TextRenderer.MeasureText(b.Label, Theme.SemiBold, Size.Empty, SingleLine).Width)
                    + Math.Max(TextRenderer.MeasureText(b.Primary, PrimaryFont(b), Size.Empty, SingleLine).Width,
                               b.Secondary == null ? 0 : TextRenderer.MeasureText(b.Secondary, Theme.Small, Size.Empty, SingleLine).Width);
                natural = Math.Max(natural, w);
            }
            max = Math.Max(em * 4, Math.Min(max, natural + 1));
            min = Math.Min(min, max);
            Size best = Size.Empty;
            double bestScore = double.MaxValue;
            for (int w = min; ; w = Math.Min(max, w + step))
            {
                int h = Measure(blocks, w);
                double score = Math.Abs(Math.Log((w + 2.0 * Pad) / Math.Max(1, h + 2.0 * Pad) / AspectRatio));
                if (score < bestScore - 0.0001) { bestScore = score; best = new Size(w, h); }
                if (w >= max) break;
            }
            return new Size(best.Width + Pad * 2, best.Height + Pad * 2);
        }

        private string _shown;

        /// <summary>在指定位置显示一段文字（由调用方自行控制延迟）。/ Shows text at a position; the caller controls the delay.</summary>
        internal void ShowText(string text, Control control, Point location)
        {
            _shown = text;
            Show(text, control, location, AutoPopDelay);
        }

        internal void HideText(Control control)
        {
            if (_shown == null) return;
            _shown = null;
            Hide(control);
        }

        private void OnPopup(object sender, PopupEventArgs e)
        {
            string text = _shown ?? (e.AssociatedControl == null ? "" : GetToolTip(e.AssociatedControl));
            var blocks = Parse(text);
            if (blocks.Count == 0) { e.Cancel = true; return; }
            e.ToolTipSize = Layout(blocks);
        }

        private void OnDraw(object sender, DrawToolTipEventArgs e)
        {
            var g = e.Graphics;
            var bounds = e.Bounds;
            using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, bounds);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            using (var accent = new SolidBrush(Theme.Accent)) g.FillRectangle(accent, bounds.X + 1, bounds.Y + 1, Dpi.S(3), bounds.Height - 2);
            var blocks = Parse(e.ToolTipText);
            int width = bounds.Width - Pad * 2;
            var origin = new Point(bounds.X + Pad, bounds.Y + Pad);
            Measure(blocks, width, (b, label, primary, secondary) =>
            {
                if (b.Label != null) TextRenderer.DrawText(g, b.Label, Theme.SemiBold, Offset(label, origin), Theme.AccentText, Flags);
                TextRenderer.DrawText(g, b.Primary, PrimaryFont(b), Offset(primary, origin), b.Title ? Theme.Text : (b.Label != null ? Theme.Text : Theme.TextSecondary.Blend(Theme.Text)), Flags);
                if (b.Secondary != null) TextRenderer.DrawText(g, b.Secondary, Theme.Small, Offset(secondary, origin), Theme.TextMuted, Flags);
            });
        }

        private static Rectangle Offset(Rectangle r, Point origin) => new Rectangle(r.X + origin.X, r.Y + origin.Y, r.Width, r.Height);
    }

    internal static class ThemedToolTipColors
    {
        /// <summary>两色取中。/ Midpoint between two colors.</summary>
        internal static Color Blend(this Color a, Color b) => Color.FromArgb((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2);
    }
}