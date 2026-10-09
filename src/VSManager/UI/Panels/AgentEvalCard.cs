using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 侧边栏「Agent 自循环」卡片：一行最近状态摘要 + 最近若干次运行的通过率柱状条（绿 = 全部通过，红 = 有失败，灰 = 无结果，黄 = 运行中）。
    /// 点击柱子打开该次运行，点击其他位置打开反馈窗口。
    /// Sidebar "Agent self-loop" card: a one-line summary of the latest state plus pass-rate bars for the recent runs
    /// (green = all passed, red = failures, grey = no result, amber = running). Clicking a bar opens that run; clicking elsewhere opens the feedback window.
    /// </summary>
    internal sealed class AgentEvalCard : Control
    {
        private const int MaxBars = 24;
        private IList<AgentEvalRun> _runs = new List<AgentEvalRun>();
        private string _message = "未设置评测目录，点击选择 / No evaluation folder set; click to choose";
        private int _hover = -1;
        private readonly ToolTip _tip = new ToolTip { InitialDelay = 200, ReshowDelay = 100 };

        /// <summary>打开反馈窗口；参数为点中的运行，可为 null。/ Opens the feedback window; the argument is the clicked run or null.</summary>
        internal event Action<AgentEvalRun> OpenRequested;

        public AgentEvalCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
            ForeColor = Theme.TextSecondary;
            Font = Theme.Small;
            Height = Dpi.S(84);
            Cursor = Cursors.Hand;
            AccessibleName = "Agent 自循环反馈 / Agent self-loop feedback";
        }

        internal IList<AgentEvalRun> Runs => _runs;
        internal string Message => _message;

        /// <summary>显示最新运行；runs 为 null 时显示 message（未配置或目录不存在）。/ Shows the latest runs; when runs is null, shows message (not configured or missing folder).</summary>
        internal void SetRuns(IList<AgentEvalRun> runs, string message = null)
        {
            _runs = runs ?? new List<AgentEvalRun>();
            _message = runs == null ? message : AgentEvalStore.Summary(_runs);
            AccessibleDescription = _message;
            _hover = -1;
            Invalidate();
        }

        /// <summary>柱状条按时间从左（旧）到右（新）。/ Bars run from oldest (left) to newest (right).</summary>
        internal List<AgentEvalRun> BarRuns() => _runs.Take(MaxBars).Reverse().ToList();

        private Rectangle ChartBounds() =>
            new Rectangle(Dpi.S(20), Dpi.S(30), Math.Max(0, Width - Dpi.S(36)), Math.Max(0, Height - Dpi.S(40)));

        internal int BarAt(Point p)
        {
            var bars = BarRuns();
            var chart = ChartBounds();
            if (bars.Count == 0 || !chart.Contains(p)) return -1;
            float slot = (float)chart.Width / MaxBars;
            int index = (int)((p.X - chart.X) / slot);
            return index < bars.Count ? index : -1;
        }

        internal static Color ColorOf(AgentEvalState state)
        {
            switch (state)
            {
                case AgentEvalState.Passed: return Theme.Success;
                case AgentEvalState.Failed:
                case AgentEvalState.Fatal: return Theme.Danger;
                case AgentEvalState.Running: return Theme.Warning;
                default: return Theme.NoneDot;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var text = new Rectangle(Dpi.S(20), Dpi.S(6), Math.Max(0, Width - Dpi.S(32)), Dpi.S(20));
            TextRenderer.DrawText(g, _message ?? "", Font, text, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var chart = ChartBounds();
            if (chart.Width <= 0 || chart.Height <= 0) return;
            using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, chart.Left, chart.Bottom, chart.Right, chart.Bottom);
            var bars = BarRuns();
            float slot = (float)chart.Width / MaxBars;
            for (int i = 0; i < bars.Count; i++)
            {
                var run = bars[i];
                // 有结果按通过率定高度，最少留一截便于看见；无结果画短灰条
                // Bars with results scale by pass rate with a visible minimum; runs without results get a short grey bar
                float ratio = run.Total > 0 ? (float)run.Passed / run.Total : 0f;
                float h = run.Result != null ? Math.Max(Dpi.S(4), chart.Height * Math.Max(ratio, 0.12f)) : Dpi.S(4);
                if (run.State == AgentEvalState.Running) h = chart.Height * 0.5f;
                var r = new RectangleF(chart.X + i * slot + slot * 0.15f, chart.Bottom - h, Math.Max(1f, slot * 0.7f), h);
                var color = ColorOf(run.State);
                if (i == _hover) color = ControlPaint.Light(color);
                using (var b = new SolidBrush(color)) g.FillRectangle(b, r);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = BarAt(e.Location);
            if (index == _hover) return;
            _hover = index;
            Invalidate();
            var bars = BarRuns();
            _tip.SetToolTip(this, index >= 0
                ? $"{bars[index].Time:yyyy-MM-dd HH:mm:ss}  {bars[index].StateText}\n{bars[index].Result?.Model}\n点击查看详情 / Click for details"
                : "点击打开 Agent 自循环反馈 / Click to open Agent self-loop feedback");
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover < 0) return;
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int index = BarAt(e.Location);
            OpenRequested?.Invoke(index >= 0 ? BarRuns()[index] : null);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
