using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 发送诊断面板：列出最近的发送，逐步显示「排队 → 定位输入框 → 填入 → 提交 → 确认送达 → 恢复焦点」，
    /// 高亮出错的步骤并给出处理建议；送达不确定时明确提示禁止盲目重发。内容只在内存中，随发送实时刷新。
    /// Send diagnostics panel: lists recent sends and shows each step "queue → locate input → fill → submit → confirm delivery → restore focus",
    /// highlights the failing step with advice, and clearly warns against blind resending when delivery is uncertain. Kept in memory only and
    /// refreshed live as sends happen.
    /// </summary>
    public sealed class SendDiagnosticsForm : Form
    {
        private readonly ListView _list;
        private readonly Label[] _stages = new Label[SendDiagnosis.StageCount];
        private readonly Label _verdict;
        private readonly TextBox _detail;
        private readonly CheckBox _problemsOnly;
        private readonly Action _changed;
        private int? _pid;
        private long _selectedSeq = -1;

        public SendDiagnosticsForm(int? pid, Action openSendLog)
        {
            _pid = pid;
            Text = "发送诊断 / Send diagnostics";
            Font = Theme.Regular;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Dpi.S(1000), Dpi.S(660));
            MinimumSize = new Size(Dpi.S(640), Dpi.S(420));
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
                BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
            };
            _list.Columns.Add("时间 / Time", Dpi.S(90));
            _list.Columns.Add("VS", Dpi.S(170));
            _list.Columns.Add("任务 / Task", Dpi.S(80));
            _list.Columns.Add("步骤 / Steps", Dpi.S(260));
            _list.Columns.Add("结论 / Verdict", Dpi.S(380));
            _list.SelectedIndexChanged += (s, e) => ShowSelected();

            var strip = new TableLayoutPanel { Dock = DockStyle.Top, Height = Dpi.S(52), ColumnCount = SendDiagnosis.StageCount, BackColor = Theme.Background, Padding = new Padding(Dpi.S(4)) };
            for (int i = 0; i < SendDiagnosis.StageCount; i++)
            {
                strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / SendDiagnosis.StageCount));
                _stages[i] = new Label
                {
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(Dpi.S(3)),
                    BackColor = Theme.NoneBg, ForeColor = Theme.NoneFg, Text = SendDiagnosis.Name((SendStage)i),
                };
                strip.Controls.Add(_stages[i], i, 0);
            }
            _verdict = new Label { Dock = DockStyle.Top, Height = Dpi.S(28), TextAlign = ContentAlignment.MiddleLeft, Font = Theme.SemiBold, Padding = new Padding(Dpi.S(8), 0, 0, 0) };
            _detail = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = new Font("Consolas", Theme.Regular.Size),
            };

            var bottom = new Panel { Dock = DockStyle.Fill };
            bottom.Controls.Add(_detail);
            bottom.Controls.Add(_verdict);
            bottom.Controls.Add(strip);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Divider };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Controls.Add(bottom);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Dpi.S(40), FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(Dpi.S(6)) };
            _problemsOnly = new CheckBox { Text = "只看有问题的 / Problems only", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(Dpi.S(4), Dpi.S(6), Dpi.S(12), 0) };
            _problemsOnly.CheckedChanged += (s, e) => Reload();
            var all = new CheckBox { Text = "全部 VS / All VS", AutoSize = true, ForeColor = Theme.Text, Checked = pid == null, Enabled = pid != null, Margin = new Padding(Dpi.S(4), Dpi.S(6), Dpi.S(12), 0) };
            all.CheckedChanged += (s, e) => { _pid = all.Checked ? null : pid; Reload(); };
            var copy = new Button { Text = "复制诊断 / Copy", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text };
            copy.Click += (s, e) =>
            {
                var r = Selected();
                if (r == null) return;
                try { Clipboard.SetText(r.Describe()); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
            };
            var log = new Button { Text = "打开发送日志 / Open send log", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text };
            log.Click += (s, e) => openSendLog?.Invoke();
            bar.Controls.AddRange(new Control[] { _problemsOnly, all, copy, log });

            Controls.Add(split);
            Controls.Add(bar);
            Load += (s, e) => { split.SplitterDistance = Math.Max(Dpi.S(120), ClientSize.Height * 2 / 5); Reload(); };

            _changed = () =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)Reload); } catch (InvalidOperationException) { }
            };
            SendDiagnosis.Changed += _changed;
            FormClosed += (s, e) => SendDiagnosis.Changed -= _changed;
        }

        private SendDiagnosisRecord Selected() => _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as SendDiagnosisRecord;

        private void Reload()
        {
            if (IsDisposed) return;
            var records = SendDiagnosis.Recent()
                .Where(r => _pid == null || r.Pid == _pid.Value)
                .Where(r => !_problemsOnly.Checked || r.ProblemStage != null)
                .ToList();
            long keep = Selected()?.Seq ?? _selectedSeq;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var r in records)
                {
                    var item = new ListViewItem(new[]
                    {
                        r.Time.ToString("HH:mm:ss"),
                        r.VsName ?? ("pid " + r.Pid),
                        r.TaskId.HasValue ? "#" + r.TaskId.Value : "",
                        r.StageLine,
                        SendDiagnosis.Verdict(r),
                    }) { Tag = r, ForeColor = VerdictColor(r) };
                    _list.Items.Add(item);
                }
                var target = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((SendDiagnosisRecord)i.Tag).Seq == keep)
                    ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
                if (target != null) { target.Selected = true; target.EnsureVisible(); }
            }
            finally { _list.EndUpdate(); }
            ShowSelected();
        }

        private void ShowSelected()
        {
            var r = Selected();
            _selectedSeq = r?.Seq ?? -1;
            for (int i = 0; i < SendDiagnosis.StageCount; i++)
            {
                var state = r?.States[i] ?? SendStageState.NotReached;
                _stages[i].Text = SendDiagnosis.Mark(state) + " " + SendDiagnosis.Name((SendStage)i) + "\n" + SendDiagnosis.StateName(state);
                StageColors(state, out var back, out var fore);
                _stages[i].BackColor = back;
                _stages[i].ForeColor = fore;
            }
            _verdict.Text = r == null ? "暂无发送记录 / No sends yet" : SendDiagnosis.Verdict(r);
            _verdict.ForeColor = r == null ? Theme.TextMuted : VerdictColor(r);
            _detail.Text = r == null
                ? "发送一条消息后，这里会显示每一步的结果。/ Send a message and each step's result appears here."
                : r.Describe().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        private static Color VerdictColor(SendDiagnosisRecord r)
        {
            if (r.Uncertain) return Theme.Warning;
            var p = r.ProblemStage;
            if (p == null) return Theme.Success;
            var state = r.States[(int)p.Value];
            if (state == SendStageState.Waiting) return Theme.BusyFg;
            return r.Delivered ? Theme.Warning : Theme.Danger;
        }

        private static void StageColors(SendStageState state, out Color back, out Color fore)
        {
            switch (state)
            {
                case SendStageState.Ok: back = Theme.IdleBg; fore = Theme.IdleFg; break;
                case SendStageState.Waiting: back = Theme.BusyBg; fore = Theme.BusyFg; break;
                case SendStageState.Failed: back = Color.FromArgb(70, 24, 28); fore = Theme.Danger; break;
                case SendStageState.Uncertain: back = Color.FromArgb(72, 52, 10); fore = Theme.Warning; break;
                default: back = Theme.NoneBg; fore = Theme.NoneFg; break;
            }
        }
    }
}
