using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>笔记卡片的解析、生成与渲染测试。/ Tests for parsing, formatting and rendering note cards.</summary>
    [TestClass]
    public class NoteCardTests
    {
        [TestMethod]
        public void ToMarkdown_ParsesBack_WithMultiLineTextAndKeyLikeLines()
        {
            var card = new NoteCard { Status = "已完成", Duration = "16m57s", Meta = "#108 · AI", Time = "09:48", Title = "→ Demo", Text = "第一行\nnote: 不是键\n第三行", Note = "Now the edits" };
            string md = card.ToMarkdown();
            StringAssert.StartsWith(md, "```card\n");
            StringAssert.EndsWith(md, "\n```");
            var back = NoteCard.Parse(md.Substring(8, md.Length - 12));
            Assert.AreEqual("done", back.Status);
            Assert.AreEqual("✓ 已完成 · 16m57s", back.PillText);
            Assert.AreEqual("#108 · AI", back.Meta);
            Assert.AreEqual("09:48", back.Time);
            Assert.AreEqual("→ Demo", back.Title);
            Assert.AreEqual("第一行\nnote： 不是键\n第三行", back.Text);
            Assert.AreEqual("Now the edits", back.Note);
        }

        [TestMethod]
        public void ToMarkdown_UsesLongerFence_WhenContentHasBackticks()
        {
            string md = new NoteCard { Title = "x", Text = "```code```" }.ToMarkdown();
            StringAssert.StartsWith(md, "````card\n");
            StringAssert.EndsWith(md, "\n````");
            StringAssert.Contains(NotebookMarkdown.Render(md), "```code```");
        }

        [DataTestMethod]
        [DataRow("done", "done")]
        [DataRow("成功", "done")]
        [DataRow("待验证", "needs_user")]
        [DataRow("Needs-User", "needs_user")]
        [DataRow("未验证", "unverified")]
        [DataRow("执行中", "running")]
        [DataRow("queued", "waiting")]
        [DataRow("失败", "failed")]
        [DataRow("canceled", "cancelled")]
        [DataRow("", "info")]
        [DataRow(null, "info")]
        [DataRow("whatever", "info")]
        public void NormalizeStatus_AcceptsAliases(string input, string expected) => Assert.AreEqual(expected, NoteCard.NormalizeStatus(input));

        [TestMethod]
        public void PillText_PrefersCustomLabel()
        {
            Assert.AreEqual("记录", new NoteCard().PillText);
            Assert.AreEqual("◐ 未验证", new NoteCard { Status = "unverified" }.PillText);
            Assert.AreEqual("自定义 · 3m", new NoteCard { Status = "done", Label = "自定义", Duration = "3m" }.PillText);
        }

        [TestMethod]
        public void Render_CardBlock_ProducesEncodedCard()
        {
            string html = NotebookMarkdown.Render("# T\n\n```card\nstatus: failed\nmeta: #7 · AI\ntime: 10:01\ntitle: → <Demo>\ntext: <script>alert(1)</script>\n  继续一行\nnote: 编译失败\n```\n");
            StringAssert.Contains(html, "<div class=\"note-card nc-failed\">");
            StringAssert.Contains(html, "<span class=\"nc-pill\"><i></i>失败</span>");
            StringAssert.Contains(html, "<span class=\"nc-meta\">#7 &#183; AI</span>");
            StringAssert.Contains(html, "<span class=\"nc-time\">10:01</span>");
            StringAssert.Contains(html, "→ &lt;Demo&gt;");
            StringAssert.Contains(html, "&lt;script&gt;alert(1)&lt;/script&gt;<br>继续一行");
            StringAssert.Contains(html, "↳ 编译失败");
            Assert.IsFalse(html.Contains("<script>"));
            Assert.IsFalse(html.Contains("<pre>"));
        }

        [DataTestMethod]
        [DataRow("compact", "compact")]
        [DataRow("紧凑型", "compact")]
        [DataRow("编号左列", "numbered")]
        [DataRow("带附注型", "noted")]
        [DataRow("Accent", "accent")]
        [DataRow("", "standard")]
        [DataRow(null, "standard")]
        [DataRow("whatever", "standard")]
        public void NormalizeStyle_AcceptsAliases(string input, string expected) => Assert.AreEqual(expected, NoteCard.NormalizeStyle(input));

        [TestMethod]
        public void Style_RoundTrips_AndStandardIsOmitted()
        {
            Assert.IsFalse(new NoteCard { Title = "x" }.ToMarkdown().Contains("style:"));
            string md = new NoteCard { Title = "x", Style = "编号左列型" }.ToMarkdown();
            StringAssert.StartsWith(md, "```card\nstatus: info\nstyle: numbered\n");
            Assert.AreEqual("numbered", NoteCard.Parse(md.Substring(8, md.Length - 12)).Style);
            Assert.AreEqual("standard", NoteCard.Parse("title: x").Style);
        }

        [TestMethod]
        public void Render_Styles_ChangeLayoutOnly()
        {
            string Card(string style) => NotebookMarkdown.Render("```card\nstatus: waiting\nstyle: " + style + "\nmeta: 01\ntitle: 标题\ntext: 正文\nnote: 附注\n  第二行\n```");
            string standard = NotebookMarkdown.Render("```card\nstatus: waiting\nmeta: 01\ntitle: 标题\n```");
            StringAssert.Contains(standard, "<div class=\"note-card nc-waiting\">");
            Assert.IsFalse(standard.Contains("ncs-"));

            string compact = Card("compact");
            StringAssert.Contains(compact, "note-card nc-waiting ncs-compact");
            StringAssert.Contains(compact, "<span class=\"nc-meta\">01</span><span class=\"nc-title\">标题</span></div>");

            string numbered = Card("numbered");
            StringAssert.Contains(numbered, "<div class=\"nc-num\">01</div><div class=\"nc-body\"><div class=\"nc-head\">");
            Assert.IsFalse(numbered.Contains("nc-meta"));
            StringAssert.Contains(numbered, "<div class=\"nc-title\">标题</div>");

            string noted = Card("noted");
            StringAssert.Contains(noted, "<div class=\"nc-note\">附注<br>第二行</div>");
            Assert.IsFalse(noted.Contains("↳"));

            StringAssert.Contains(Card("accent"), "ncs-accent");
            // 编号左列型没有编号时退回普通首行 / Numbered without meta falls back to the normal head
            Assert.IsFalse(NotebookMarkdown.Render("```card\nstyle: numbered\ntitle: x\n```").Contains("nc-num"));
        }

        [TestMethod]
        public void StyleSamples_WriteOneCardPerStyle()
        {
            CollectionAssert.AreEqual(NoteCard.Styles, AgentService.ParseStyleList(null));
            CollectionAssert.AreEqual(NoteCard.Styles, AgentService.ParseStyleList("all"));
            CollectionAssert.AreEqual(new[] { "compact", "numbered" }, AgentService.ParseStyleList("紧凑型、numbered, compact"));
            string md = AgentService.StyleSamplesMarkdown(new NoteCard { Title = "示例", Meta = "01", Status = "waiting" }, new[] { "standard", "noted" }, "对比");
            StringAssert.StartsWith(md, "### 对比\n\n**标准型** `style: standard`\n\n```card\nstatus: waiting\n");
            StringAssert.Contains(md, "**带附注型** `style: noted`\n\n```card\nstatus: waiting\nstyle: noted\n");
            string html = NotebookMarkdown.Render(md);
            Assert.AreEqual(2, html.Split(new[] { "class=\"note-card " }, StringSplitOptions.None).Length - 1);
        }

        [TestMethod]
        public void Render_OtherCodeBlocks_StayCode()
        {
            string html = NotebookMarkdown.Render("```cs\nvar x = 1;\n```\n\n```\nplain\n```");
            StringAssert.Contains(html, "<pre><code class=\"language-cs\">var x = 1;");
            StringAssert.Contains(html, "<pre><code>plain");
            Assert.IsFalse(html.Contains("note-card"));
        }

        [TestMethod]
        public void NoteAgent_CanFormatCards_ButNotWriteThem()
        {
            var settings = new AppSettings { AgentEndpoint = "http://localhost:11434/v1", AgentModel = "test", AgentConfirm = false };
            using (var agent = new AgentService(new AgentDesktopTests.DesktopHost(), () => settings, null, AgentProfile.Notes))
            {
                var field = typeof(AgentService).GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var names = ((System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.AITool>)field.GetValue(agent)).Select(t => t.Name).ToList();
                CollectionAssert.Contains(names, "format_note_card");
                CollectionAssert.DoesNotContain(names, "add_note_card");
            }
            foreach (bool en in new[] { false, true })
                StringAssert.Contains(Prompts.NoteAgentSystem(en, DateTime.Now, null), "format_note_card");
        }
    }
}
