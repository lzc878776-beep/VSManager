using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>Agent 自循环反馈：运行目录读取、状态判定、问题汇总与侧边栏卡片。/ Agent self-loop feedback: run loading, states, issue summary and the sidebar card.</summary>
    [TestClass]
    public class AgentEvalTests
    {
        private string _root;

        [TestInitialize]
        public void Setup() => _root = Path.Combine(Path.GetTempPath(), "vsm-eval-" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup() { try { Directory.Delete(_root, true); } catch { } }

        private static string Result(int passed, int failed, string fatal = null, string turns = "") =>
            "{\"runId\":\"r\",\"model\":\"m1\",\"passedTurns\":" + passed + ",\"failedTurns\":" + failed
            + ",\"fatalError\":" + (fatal == null ? "null" : "\"" + fatal + "\"") + ",\"unknownField\":{\"x\":1},\"conversations\":[" + turns + "]}";

        private static string Conv(string name, int index, string prompt, bool passed, string error = null) =>
            "{\"name\":\"" + name + "\",\"turns\":[{\"index\":" + index + ",\"prompt\":\"" + prompt + "\",\"passed\":" + (passed ? "true" : "false")
            + ",\"error\":" + (error == null ? "null" : "\"" + error + "\"")
            + ",\"toolCalls\":[{\"toolName\":\"query_a\",\"succeeded\":true}],\"checks\":[{\"kind\":\"contains\",\"value\":\"41.76\",\"passed\":" + (passed ? "true" : "false") + "}]}]}";

        private string Run(string name, string result, string cases = "{ // 注释\n \"conversations\": [ { \"name\": \"转角\", }, ] }")
        {
            string dir = Path.Combine(_root, "runs", name);
            Directory.CreateDirectory(dir);
            if (cases != null) File.WriteAllText(Path.Combine(dir, "cases.json"), cases);
            if (result != null) File.WriteAllText(Path.Combine(dir, "result.json"), result);
            return dir;
        }

        [TestMethod]
        public void Load_ReadsRunsNewestFirst_AndClassifiesStates()
        {
            Run("20261008-100000", Result(19, 0));
            Run("20261008-110000", Result(2, 1, null, Conv("转角", 1, "有几个转角", false)));
            Run("20261008-120000", Result(0, 0, "JsonException: bad"));
            string noResult = Run("20261008-130000", null);
            Run("20261008-140000", null);
            Directory.CreateDirectory(Path.Combine(_root, "runs", "not-a-run"));
            Directory.SetLastWriteTime(noResult, DateTime.Now.AddHours(-3));
            File.SetLastWriteTime(Path.Combine(noResult, "cases.json"), DateTime.Now.AddHours(-3));

            // 既可配置上一级（含 runs\），也可直接配置运行目录 / Either the parent (holding runs\) or the run folder itself
            Assert.AreEqual(Path.Combine(_root, "runs"), AgentEvalStore.ResolveRunsDir(_root));
            Assert.AreEqual(Path.Combine(_root, "runs"), AgentEvalStore.ResolveRunsDir(Path.Combine(_root, "runs")));
            Assert.IsNull(AgentEvalStore.ResolveRunsDir(""));
            Assert.IsNull(AgentEvalStore.ResolveRunsDir(Path.Combine(_root, "missing")));

            var runs = AgentEvalStore.Load(_root);
            CollectionAssert.AreEqual(new[] { "20261008-140000", "20261008-130000", "20261008-120000", "20261008-110000", "20261008-100000" }, runs.Select(r => r.Name).ToArray());
            CollectionAssert.AreEqual(new[] { AgentEvalState.Running, AgentEvalState.NoResult, AgentEvalState.Fatal, AgentEvalState.Failed, AgentEvalState.Passed },
                runs.Select(r => r.State).ToArray());
            Assert.AreEqual("m1", runs[4].Result.Model);
            Assert.AreEqual(new DateTime(2026, 10, 8, 14, 0, 0), runs[0].Time);
            CollectionAssert.AreEqual(new[] { "转角" }, runs[0].CaseNames);
            var failed = runs[3].FailedTurns().Single();
            Assert.AreEqual("转角", failed.Conversation);
            Assert.AreEqual("query_a", failed.Turn.Tools);
            StringAssert.Contains(AgentEvalStore.Feedback(runs[3]), "contains 41.76");
            StringAssert.Contains(AgentEvalStore.Summary(runs), "近 3 次通过 1 次");
        }

        [TestMethod]
        public void Load_UnreadableResult_IsNoResultWithReason()
        {
            Run("20261008-100000", "{ not json");
            var run = AgentEvalStore.Load(_root).Single();
            Assert.AreEqual(AgentEvalState.NoResult, run.State);
            Assert.IsNotNull(run.ReadError);
        }

        [TestMethod]
        public void Issues_MergeFailuresAcrossRuns_AndMarkResolved()
        {
            Run("20261008-100000", Result(0, 2, null, Conv("转角", 1, "有几个转角", false) + "," + Conv("阀井", 1, "详细参数", false, "InvalidOperationException: x")));
            Run("20261008-110000", Result(1, 1, null, Conv("转角", 1, "有几个转角", false) + "," + Conv("阀井", 1, "详细参数", true)));
            Run("20261008-120000", Result(1, 0, null, Conv("阀井", 1, "改写后的详细参数提问", true)));
            var issues = AgentEvalStore.Issues(AgentEvalStore.Load(_root));
            Assert.AreEqual(2, issues.Count);
            var corner = issues[0];
            Assert.AreEqual("转角", corner.Conversation);
            Assert.AreEqual(2, corner.Failures);
            Assert.IsFalse(corner.LastPassed);
            Assert.IsTrue(corner.NotRerun, "later run did not include the corner case");
            StringAssert.StartsWith(corner.State, "未再运行");
            Assert.AreEqual("20261008-110000", corner.LastFailure.Name);
            StringAssert.Contains(corner.LastReason, "contains 41.76");
            var valve = issues[1];
            Assert.AreEqual(1, valve.Failures);
            Assert.IsTrue(valve.LastPassed);
            Assert.AreEqual("20261008-120000", valve.LastSeen.Name);
            Assert.AreEqual("改写后的详细参数提问", valve.Prompt);
            StringAssert.StartsWith(valve.LastReason, "InvalidOperationException");
        }

        [TestMethod]
        public void Questions_IncludeEveryPrompt_OrderedByCase_PassedWhenLatestPassed()
        {
            Run("20261008-100000", Result(1, 2, null, Conv("转角", 1, "有几个转角", false) + "," + Conv("阀井", 1, "详细参数", false) + "," + Conv("管长", 1, "总长", true)));
            Run("20261008-110000", Result(2, 1, null, Conv("管长", 1, "总长", false) + "," + Conv("转角", 1, "有几个转角", true) + "," + Conv("阀井", 1, "改写后的详细参数", true)));
            Run("20261008-120000", null);
            var questions = AgentEvalStore.Questions(AgentEvalStore.Load(_root));
            // 按最新运行的用例顺序编号，而不是按时间 / Numbered in the newest run's case order, not by time
            CollectionAssert.AreEqual(new[] { "管长#1", "转角#1", "阀井#1" }, questions.Select(q => q.Key).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, questions.Select(q => q.Number).ToArray());
            Assert.IsFalse(questions[0].Passed, "latest test failed");
            StringAssert.StartsWith(questions[0].ResultText, "✗ 失败（曾通过）");
            Assert.IsTrue(questions[1].Passed, "failed first, passed later");
            Assert.AreEqual(2, questions[1].Attempts.Count);
            Assert.AreEqual("改写后的详细参数", questions[2].Prompt);
            string detail = AgentEvalStore.DescribeQuestion(questions[2]);
            StringAssert.Contains(detail, "详细参数");
            StringAssert.Contains(detail, "改写后的详细参数");
            Assert.IsTrue(detail.IndexOf("20261008-100000") < detail.IndexOf("20261008-110000"), "attempts oldest first");
            StringAssert.Contains(detail, "contains 41.76");
        }

        [TestMethod]
        public void CompactEnvPath_UsesVariablesForProfileFolders()
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            Assert.AreEqual(@"%LOCALAPPDATA%\Tool\Eval", MainForm.CompactEnvPath(local + @"\Tool\Eval"));
            Assert.AreEqual(@"X:\Other\Eval", MainForm.CompactEnvPath(@"X:\Other\Eval"));
            Assert.AreEqual(local + @"\Tool", Environment.ExpandEnvironmentVariables(MainForm.CompactEnvPath(local + @"\Tool")));
        }

        [TestMethod]
        public void Card_BarsRunOldestToNewest_AndClickOpensRun()
        {
            Run("20261008-100000", Result(19, 0));
            Run("20261008-110000", Result(2, 1, null, Conv("转角", 1, "有几个转角", false)));
            var runs = AgentEvalStore.Load(_root);
            using (var card = new AgentEvalCard { Width = Dpi.S(300) })
            {
                card.SetRuns(null, "未设置");
                Assert.AreEqual("未设置", card.Message);
                Assert.AreEqual(-1, card.BarAt(new System.Drawing.Point(Dpi.S(25), card.Height - Dpi.S(12))));
                card.SetRuns(runs);
                CollectionAssert.AreEqual(new[] { "20261008-100000", "20261008-110000" }, card.BarRuns().Select(r => r.Name).ToArray());
                Assert.AreEqual(0, card.BarAt(new System.Drawing.Point(Dpi.S(25), card.Height - Dpi.S(12))));
                Assert.AreEqual(Theme.Danger, AgentEvalCard.ColorOf(AgentEvalState.Failed));
            }
        }

        [TestMethod]
        public void Question_ShortResultAndHistory()
        {
            var q = new AgentEvalQuestion();
            foreach (bool passed in new[] { false, false, false, true })
                q.Attempts.Add(new AgentEvalAttempt { Run = new AgentEvalRun(), Turn = new AgentEvalTurn { Passed = passed } });
            Assert.AreEqual("✓ 通过 · 曾失败", q.ShortResult);
            Assert.AreEqual("✗✗✗✓  1/4", q.TestsText);
            Assert.AreEqual("…✗✓", q.History(2));
            q.Attempts.Add(new AgentEvalAttempt { Run = new AgentEvalRun(), Turn = new AgentEvalTurn { Passed = false } });
            Assert.AreEqual("✗ 失败 · 曾通过", q.ShortResult);
            q.Attempts.RemoveRange(1, 4);
            Assert.AreEqual("✗ 失败", q.ShortResult);
            q.Attempts[0].Turn.Passed = true;
            Assert.AreEqual("✓ 通过", q.ShortResult);
        }

        [TestMethod]
        public void Panel_ListsQuestionsWithAllTests()
        {
            Run("20261008-100000", Result(0, 1, null, Conv("转角", 1, "有几个转角", false)));
            Run("20261008-110000", Result(1, 0, null, Conv("转角", 1, "有几个转角", true)));
            var runs = AgentEvalStore.Load(_root);
            OnUi(() =>
            {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-20000, -20000), ShowInTaskbar = false, Size = new System.Drawing.Size(900, 600) })
            {
                var panel = new AgentEvalPanel { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                form.Show();
                panel.SetRuns(runs, null);
                // 默认「汇总」：不重复的问题各一行 / Default "Summary": one row per unique question
                Assert.IsTrue(panel.SummaryView);
                Assert.AreEqual(1, panel.UniqueList.Items.Count);
                var u = panel.UniqueList.Items[0];
                Assert.AreEqual("1", u.SubItems[0].Text);
                Assert.AreEqual("✓ 通过 · 曾失败", u.SubItems[2].Text);
                Assert.AreEqual(Theme.Success, u.SubItems[2].ForeColor);
                Assert.AreEqual("✗✓  1/2", u.SubItems[3].Text);
                Assert.AreEqual("2026-10-08 10:00:00", u.SubItems[4].Text);
                Assert.AreEqual("2026-10-08 11:00:00", u.SubItems[5].Text);
                StringAssert.Contains(panel.SummaryText, "1 个不重复问题");
                u.Selected = true;
                StringAssert.Contains(panel.DetailText, "第 2 次");
                panel.SetRuns(runs, null);
                Assert.AreEqual("转角#1", panel.SelectedQuestion?.Key, "refresh keeps the summary selection");
                panel.SummaryView = false;
                Assert.IsTrue(panel.QuestionList.Visible && !panel.UniqueList.Visible);
                Assert.IsNull(panel.SelectedQuestion);
                // 每个批次一条分割行 + 本批次问题，批次新→旧 / One separator per batch plus its questions, newest batch first
                Assert.AreEqual(4, panel.QuestionList.Items.Count);
                var items = panel.QuestionList.Items;
                Assert.IsInstanceOfType(items[0].Tag, typeof(AgentEvalBatch));
                StringAssert.Contains(items[0].SubItems[1].Text, "2026-10-08 11:00:00");
                Assert.IsInstanceOfType(items[2].Tag, typeof(AgentEvalBatch));
                StringAssert.Contains(items[2].SubItems[1].Text, "2026-10-08 10:00:00");
                Assert.AreEqual("1", items[1].SubItems[0].Text);
                StringAssert.Contains(items[1].SubItems[1].Text, "有几个转角");
                StringAssert.StartsWith(items[1].SubItems[2].Text, "✓");
                StringAssert.StartsWith(items[3].SubItems[2].Text, "✗");
                Assert.AreEqual("✓ 通过 · 曾失败", items[1].SubItems[3].Text);
                Assert.AreEqual(Theme.Danger, items[3].SubItems[2].ForeColor);
                Assert.AreEqual("2026-10-08 10:00:00", items[3].SubItems[4].Text);
                StringAssert.Contains(panel.SummaryText, "2 个批次");
                StringAssert.Contains(panel.SummaryText, "通过 1");
                items[3].Selected = true;
                StringAssert.Contains(panel.DetailText, "第 2 次");
                StringAssert.Contains(panel.DetailText, "回答 / Answer");
                panel.SetRuns(runs, null);
                Assert.IsNotNull(panel.SelectedQuestion, "refresh keeps the selected question");
                Assert.AreEqual(3, panel.QuestionList.SelectedItems[0].Index);
                panel.QuestionList.Items[0].Selected = true;
                StringAssert.Contains(panel.DetailText, "批次 / Batch 20261008-110000");
                panel.SetRuns(null, "未设置");
                Assert.AreEqual(0, panel.QuestionList.Items.Count);
                Assert.AreEqual(0, panel.UniqueList.Items.Count);
                Assert.AreEqual("未设置", panel.SummaryText);
                Assert.AreEqual("", panel.DetailText);
            }
            });
        }

        [TestMethod]
        public void Navigation_SwitchesToEvalPage()
        {
            OnUi(() =>
            {
                var vs = new Panel(); var agent = new Panel(); var notes = new Panel(); var eval = new Panel();
                var nav = new WorkspaceNavigation(vs, agent, notes, () => true, eval);
                Assert.IsTrue(nav.Select(WorkspacePage.AgentEval));
                Assert.AreEqual(WorkspacePage.AgentEval, nav.Current);
                Assert.IsTrue(eval.Visible);
                Assert.IsFalse(vs.Visible || agent.Visible || notes.Visible);
                Assert.IsTrue(nav.Select(WorkspacePage.VisualStudio));
                Assert.IsFalse(eval.Visible);
                Assert.IsFalse(new WorkspaceNavigation(vs, agent, notes, () => true).Select(WorkspacePage.AgentEval));
            });
        }

        [TestMethod]
        public void Sidebar_EvalSectionFollowsNotes_AndCollapses()
        {
            OnUi(() =>
            {
            var vs = new Panel();
            var ai = new Button { Height = Dpi.S(84) };
            var notes = new Panel();
            var card = new AgentEvalCard();
            using (var sidebar = new WorkspaceSidebar(vs, ai, notes, new Label(), new Button(), () => Dpi.S(200), card) { Size = new System.Drawing.Size(Dpi.S(300), Dpi.S(900)) })
            {
                sidebar.PerformLayout();
                Assert.IsTrue(card.Visible);
                Assert.AreEqual(Dpi.S(84), card.Height);
                Assert.IsTrue(notes.Bottom <= card.Top, $"notes {notes.Bounds} card {card.Bounds}");
                bool? saved = null;
                int opened = 0;
                sidebar.EvalCollapsedChanged += c => saved = c;
                sidebar.EvalRequested += () => opened++;
                sidebar.SetEvalCollapsed(true);
                Assert.IsFalse(card.Visible);
                Assert.AreEqual(true, saved);
                sidebar.SetNotionCollapsed(true);
                sidebar.SetEvalCollapsed(false);
                sidebar.PerformLayout();
                Assert.IsTrue(card.Visible && card.Top < sidebar.Height / 2, $"card {card.Bounds}");
            }
            });
        }

        private static void OnUi(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Agent eval UI test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
