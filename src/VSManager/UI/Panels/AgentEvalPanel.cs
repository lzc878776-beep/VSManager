using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 主界面「Agent 自循环」页，顶部可切换两种视图：
    /// 「汇总」把不重复的问题（按用例 + 轮次归并）列在一张大表里，每个问题一行，显示总体结果（最近一次测试通过即算通过）、测试次数与首次 / 最近测试时间；
    /// 「按批次」列出测试过的全部问题，一个批次（一次评测运行）一条分割行并记录时间，批次按时间新→旧。
    /// 两种视图选中问题后都在下方显示该问题的全部测试记录（具体提问与回答）。只读。
    /// Main-window "Agent loop" page with two views switched at the top:
    /// "Summary" puts the unique questions (merged by case + turn) in one table, one row per question, with the overall result
    /// (passed when the latest test passed), the test count and the first / latest test time;
    /// "By batch" lists every tested question with one separator row per batch (evaluation run) showing its time, newest first.
    /// In both views selecting a question shows all of its test records (exact prompt and answer) below. Read-only.
    /// </summary>
    internal sealed class AgentEvalPanel : Panel
    {
        private readonly ListView _questions = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        private readonly ListView _unique = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        private readonly RadioButton _summaryTab = ViewButton("汇总（不重复问题）/ Summary");
        private readonly RadioButton _batchTab = ViewButton("按批次 / By batch");
        private IList<AgentEvalRun> _runs;
        private string _message;
        private int _batchCount;
        private readonly TextBox _detail = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface, ForeColor = Theme.Text, Font = new Font("Consolas", 9.5F), Dock = DockStyle.Fill,
        };
        private readonly Label _summary = new Label();
        private readonly SplitContainer _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Divider };
        private readonly Font _headerFont = new Font(Theme.Regular, FontStyle.Bold);
        private bool _splitPlaced;

        public AgentEvalPanel()
        {
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Regular;

            _questions.Columns.Add("编号 / No.", Dpi.S(84));
            _questions.Columns.Add("问题概述 / Question", Dpi.S(480));
            _questions.Columns.Add("本次 / This batch", Dpi.S(110));
            _questions.Columns.Add("总体 / Overall", Dpi.S(130));
            _questions.Columns.Add("时间 / Time", Dpi.S(150));
            _questions.Columns.Add("耗时 / Duration", Dpi.S(100));
            _questions.Columns.Add("用例 / Case", Dpi.S(150));
            _questions.SelectedIndexChanged += (s, e) => ShowQuestion();

            _unique.Columns.Add("编号 / No.", Dpi.S(84));
            _unique.Columns.Add("问题概述 / Question", Dpi.S(480));
            _unique.Columns.Add("结果 / Result", Dpi.S(130));
            _unique.Columns.Add("历次测试（旧→新）· 通过/总数 / Tests", Dpi.S(230));
            _unique.Columns.Add("首次 / First", Dpi.S(150));
            _unique.Columns.Add("最近 / Latest", Dpi.S(150));
            _unique.Columns.Add("用例 / Case", Dpi.S(150));
            _unique.SelectedIndexChanged += (s, e) => ShowQuestion();

            var tabs = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = Dpi.S(34), BackColor = Theme.Sidebar, Padding = new Padding(Dpi.S(6), Dpi.S(4), 0, 0), WrapContents = false,
            };
            tabs.Controls.Add(_summaryTab);
            tabs.Controls.Add(_batchTab);
            _summaryTab.Checked = true;
            _questions.Visible = false;
            _summaryTab.CheckedChanged += (s, e) => { if (_summaryTab.Checked) ShowView(true); };
            _batchTab.CheckedChanged += (s, e) => { if (_batchTab.Checked) ShowView(false); };

            _split.Panel1.Controls.Add(_questions);
            _split.Panel1.Controls.Add(_unique);
            _split.Panel2.Controls.Add(_detail);
            // 首次有尺寸时把分隔条放在中间 / Put the splitter in the middle once the panel has a size
            _split.SizeChanged += (s, e) =>
            {
                if (_splitPlaced || _split.Height < Dpi.S(200)) return;
                _splitPlaced = true;
                try { _split.SplitterDistance = _split.Height / 2; } catch (InvalidOperationException) { }
            };

            _summary.Dock = DockStyle.Bottom;
            _summary.Height = Dpi.S(26);
            _summary.Font = Theme.Small;
            _summary.ForeColor = Theme.TextMuted;
            _summary.BackColor = Theme.Sidebar;
            _summary.UseMnemonic = false;
            _summary.AutoEllipsis = true;
            _summary.TextAlign = ContentAlignment.MiddleLeft;
            _summary.Padding = new Padding(Dpi.S(8), 0, Dpi.S(8), 0);

            Controls.Add(_split);
            Controls.Add(tabs);
            Controls.Add(_summary);
            // 隐藏的列表也要有句柄，否则选中状态读不到 / Hidden lists need a handle too, otherwise their selection cannot be read
            HandleCreated += (s, e) => { var a = _questions.Handle; var b = _unique.Handle; };
        }

        private static RadioButton ViewButton(string text)
        {
            var b = new RadioButton
            {
                Text = text, Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = Theme.Regular,
                ForeColor = Theme.Text, BackColor = Theme.Sidebar, UseMnemonic = false, Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, Dpi.S(6), 0), Padding = new Padding(Dpi.S(8), Dpi.S(1), Dpi.S(8), Dpi.S(1)),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.CheckedBackColor = Theme.AccentLight;
            b.FlatAppearance.MouseOverBackColor = Theme.RowHover;
            b.FlatAppearance.MouseDownBackColor = Theme.AccentLight;
            b.CheckedChanged += (s, e) => b.ForeColor = b.Checked ? Theme.AccentText : Theme.Text;
            return b;
        }

        /// <summary>是否显示「汇总」视图（否则为「按批次」）。/ Whether the "Summary" view is shown (otherwise "By batch").</summary>
        internal bool SummaryView
        {
            get => _summaryTab.Checked;
            set { if (value) _summaryTab.Checked = true; else _batchTab.Checked = true; }
        }

        private void ShowView(bool summary)
        {
            _unique.Visible = summary;
            _questions.Visible = !summary;
            UpdateSummary();
            ShowQuestion();
        }

        private ListView ActiveList => SummaryView ? _unique : _questions;

        internal ListView QuestionList => _questions;
        /// <summary>「汇总」大表：每个不重复的问题一行。/ The "Summary" table: one row per unique question.</summary>
        internal ListView UniqueList => _unique;
        internal string DetailText => _detail.Text;
        internal string SummaryText => _summary.Text;
        internal AgentEvalQuestion SelectedQuestion => SelectedRow?.Tag as AgentEvalQuestion ?? (SelectedRow?.Tag as AgentEvalBatchItem)?.Question;
        private ListViewItem SelectedRow => ActiveList.SelectedItems.Cast<ListViewItem>().FirstOrDefault();

        /// <summary>分割行（批次标题）的文字。/ Text of a separator (batch header) row.</summary>
        internal static string BatchHeader(AgentEvalBatch b)
        {
            var r = b.Run;
            return $"── 批次 / Batch  {r.Time:yyyy-MM-dd HH:mm:ss}  ·  {r.StateText}  ·  {b.Items.Count} 个问题 / questions"
                + (string.IsNullOrEmpty(r.Result?.Model) ? "" : "  ·  " + r.Result.Model) + $"  ({r.Name}) ──";
        }

        /// <summary>刷新数据并尽量保持当前选中的行；runs 为 null 时显示 message。/ Refreshes the data, keeping the selected row; shows message when runs is null.</summary>
        internal void SetRuns(IList<AgentEvalRun> runs, string message)
        {
            _runs = runs;
            _message = message;
            var old = _questions.SelectedItems.Cast<ListViewItem>().FirstOrDefault();
            string keepBatch = (old?.Tag as AgentEvalBatch)?.Run.Name ?? (old?.Tag is AgentEvalBatchItem ? BatchOfRow(old) : null);
            string keepQuestion = (old?.Tag as AgentEvalBatchItem)?.Question.Key;
            var batches = AgentEvalStore.Batches(runs ?? new List<AgentEvalRun>());
            _batchCount = batches.Count;
            var questions = batches.SelectMany(b => b.Items).Select(i => i.Question).Distinct().ToList();
            ListViewItem kept = null;
            _questions.BeginUpdate();
            try
            {
                _questions.Items.Clear();
                foreach (var b in batches)
                {
                    var header = new ListViewItem(new[] { "", BatchHeader(b), "", "", "", "", "" })
                    {
                        Tag = b, BackColor = Theme.Sidebar, ForeColor = b.Run.State == AgentEvalState.Passed ? Theme.Text : Theme.Warning,
                        Font = _headerFont,
                    };
                    _questions.Items.Add(header);
                    if (keepQuestion == null && b.Run.Name == keepBatch) kept = header;
                    if (b.Items.Count == 0)
                        _questions.Items.Add(new ListViewItem(new[] { "", "（本批次没有测试结果 / no test results in this batch）", "", "", "", "", "" }) { Tag = b, ForeColor = Theme.TextMuted });
                    foreach (var i in b.Items)
                    {
                        var q = i.Question;
                        var row = new ListViewItem(new[]
                        {
                            q.Number.ToString(), Brief(i.Turn.Prompt), i.Turn.Passed ? "✓ 通过" : "✗ 失败", q.ShortResult,
                            b.Run.Time.ToString("yyyy-MM-dd HH:mm:ss"), i.Turn.DurationSeconds.ToString("0.0") + " s", q.Key
                        }) { Tag = i };
                        Style(row, i.Turn.Passed ? Theme.Text : Theme.Danger, (2, ResultColor(i.Turn.Passed)), (3, ResultColor(q.Passed)));
                        _questions.Items.Add(row);
                        if (b.Run.Name == keepBatch && q.Key == keepQuestion) kept = row;
                    }
                }
            }
            finally { _questions.EndUpdate(); }

            string keepUnique = (_unique.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag as AgentEvalQuestion)?.Key;
            ListViewItem keptUnique = null;
            _unique.BeginUpdate();
            try
            {
                _unique.Items.Clear();
                foreach (var q in questions.OrderBy(q => q.Number))
                {
                    var row = new ListViewItem(new[]
                    {
                        q.Number.ToString(), Brief(q.Prompt), q.ShortResult, q.TestsText,
                        q.Attempts[0].Run.Time.ToString("yyyy-MM-dd HH:mm:ss"), q.Latest.Run.Time.ToString("yyyy-MM-dd HH:mm:ss"), q.Key
                    }) { Tag = q };
                    Style(row, q.Passed ? Theme.Text : Theme.Danger, (2, ResultColor(q.Passed)), (3, Theme.TextSecondary));
                    _unique.Items.Add(row);
                    if (q.Key == keepUnique) keptUnique = row;
                }
            }
            finally { _unique.EndUpdate(); }

            UpdateSummary();
            if (kept != null) { kept.Selected = true; kept.EnsureVisible(); }
            if (keptUnique != null) { keptUnique.Selected = true; keptUnique.EnsureVisible(); }
            if (SelectedRow == null) _detail.Text = "";
        }

        private void UpdateSummary()
        {
            if (_runs == null) { _summary.Text = _message ?? ""; return; }
            int total = _unique.Items.Count, passed = _unique.Items.Cast<ListViewItem>().Count(x => ((AgentEvalQuestion)x.Tag).Passed);
            int batches = _batchCount;
            _summary.Text = $"共 {batches} 个批次、{total} 个不重复问题：通过 {passed}，失败 {total - passed}（最近一次测试通过即算通过）· 选中查看全部测试记录"
                + $" / {batches} batches, {total} unique questions: {passed} passed, {total - passed} failing (passed = latest test passed) · select to see all tests";
        }

        private static Color ResultColor(bool passed) => passed ? Theme.Success : Theme.Danger;

        /// <summary>整行使用 <paramref name="fore"/>，指定列单独着色（结果列绿 / 红）。/ Colors the row with <paramref name="fore"/> and the given cells separately (result cells green / red).</summary>
        private static void Style(ListViewItem row, Color fore, params (int Column, Color Color)[] cells)
        {
            row.UseItemStyleForSubItems = false;
            foreach (ListViewItem.ListViewSubItem sub in row.SubItems) { sub.ForeColor = fore; sub.BackColor = Theme.Surface; }
            foreach (var c in cells) row.SubItems[c.Column].ForeColor = c.Color;
        }

        private string BatchOfRow(ListViewItem row)
        {
            for (int i = row.Index; i >= 0; i--)
                if (_questions.Items[i].Tag is AgentEvalBatch b) return b.Run.Name;
            return null;
        }

        private void ShowQuestion()
        {
            var row = SelectedRow;
            if (row?.Tag is AgentEvalQuestion q) _detail.Text = Normalize(AgentEvalStore.DescribeQuestion(q));
            else if (row?.Tag is AgentEvalBatchItem i) _detail.Text = Normalize(AgentEvalStore.DescribeQuestion(i.Question));
            else if (row?.Tag is AgentEvalBatch b) _detail.Text = Normalize(DescribeBatch(b));
            else _detail.Text = "";
        }

        /// <summary>批次概况：时间、结果与本批次各问题的结果。/ Batch overview: time, result and the result of each question in the batch.</summary>
        internal static string DescribeBatch(AgentEvalBatch b)
        {
            var r = b.Run;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"批次 / Batch {r.Name}  时间 / Time {r.Time:yyyy-MM-dd HH:mm:ss}  {r.StateText}");
            if (!string.IsNullOrEmpty(r.Result?.Model)) sb.AppendLine("模型 / Model：" + r.Result.Model);
            if (!string.IsNullOrEmpty(r.Result?.FatalError)) sb.AppendLine("整体错误 / Fatal：" + r.Result.FatalError);
            if (!string.IsNullOrEmpty(r.ReadError)) sb.AppendLine("读取失败 / Read error：" + r.ReadError);
            sb.AppendLine();
            foreach (var i in b.Items)
                sb.AppendLine($"{(i.Turn.Passed ? "✓" : "✗")} {i.Question.Number}. {Brief(i.Turn.Prompt)}" + (i.Turn.Passed ? "" : "  — " + AgentEvalStore.Reason(i.Turn)));
            return sb.ToString().TrimEnd();
        }

        /// <summary>问题概述：单行并截断。/ Question summary: one line, truncated.</summary>
        internal static string Brief(string s, int max = 80)
        {
            string line = string.Join(" ", (s ?? "").Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return line.Length <= max ? line : line.Substring(0, max) + "…";
        }

        private static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
