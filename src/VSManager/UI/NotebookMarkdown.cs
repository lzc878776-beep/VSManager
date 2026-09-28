using System;
using System.Globalization;
using System.IO;
using System.Net;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace VSManager
{
    internal static class NotebookMarkdown
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .DisableHtml().UsePipeTables().UseTaskLists().UseAutoLinks().UseEmphasisExtras().Build();

        public static string Render(string text, Func<string, string> imageData = null)
        {
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                var renderer = new HtmlRenderer(writer);
                Pipeline.Setup(renderer);
                var link = renderer.ObjectRenderers.FindExact<Markdig.Renderers.Html.Inlines.LinkInlineRenderer>();
                if (link != null) renderer.ObjectRenderers.Remove(link);
                renderer.ObjectRenderers.Insert(0, new LocalLinkRenderer(imageData));
                var code = renderer.ObjectRenderers.FindExact<CodeBlockRenderer>();
                if (code != null) renderer.ObjectRenderers.Remove(code);
                renderer.ObjectRenderers.Insert(0, new CardBlockRenderer(code ?? new CodeBlockRenderer()));
                renderer.Render(Markdown.Parse(text ?? "", Pipeline));
                return writer.ToString();
            }
        }

        /// <summary>把 ```card 代码块渲染为卡片，其余代码块交给默认渲染器。/ Renders ```card blocks as cards and leaves other code blocks to the default renderer.</summary>
        private sealed class CardBlockRenderer : HtmlObjectRenderer<CodeBlock>
        {
            private readonly CodeBlockRenderer _fallback;
            public CardBlockRenderer(CodeBlockRenderer fallback) { _fallback = fallback; }
            protected override void Write(HtmlRenderer renderer, CodeBlock block)
            {
                if (block is FencedCodeBlock fenced && string.Equals((fenced.Info ?? "").Trim(), NoteCard.FenceInfo, StringComparison.OrdinalIgnoreCase))
                {
                    renderer.EnsureLine();
                    renderer.Write(CardHtml(NoteCard.Parse(fenced.Lines.ToString())));
                    return;
                }
                ((IMarkdownObjectRenderer)_fallback).Write(renderer, block);
            }
        }

        /// <summary>卡片的 HTML（所有文字均已转义）。/ HTML of a card (all text is encoded).</summary>
        internal static string CardHtml(NoteCard card)
        {
            string E(string s) => WebUtility.HtmlEncode(s ?? "");
            string Br(string s) => E(s).Replace("\n", "<br>");
            string style = NoteCard.NormalizeStyle(card.Style);
            bool hasMeta = !string.IsNullOrWhiteSpace(card.Meta), hasTitle = !string.IsNullOrWhiteSpace(card.Title);
            // 编号左列型把 meta 放到左侧列；紧凑型把标题并入首行 / Numbered moves meta to a left column; compact puts the title into the head row
            bool metaColumn = style == "numbered" && hasMeta, titleInHead = style == "compact" && hasTitle;
            var sb = new System.Text.StringBuilder();
            sb.Append("<div class=\"note-card nc-").Append(NoteCard.NormalizeStatus(card.Status).Replace('_', '-'));
            if (style != "standard") sb.Append(" ncs-").Append(style);
            sb.Append("\">");
            if (metaColumn) sb.Append("<div class=\"nc-num\">").Append(E(card.Meta)).Append("</div><div class=\"nc-body\">");
            sb.Append("<div class=\"nc-head\"><span class=\"nc-pill\"><i></i>").Append(E(card.PillText)).Append("</span>");
            if (hasMeta && !metaColumn) sb.Append("<span class=\"nc-meta\">").Append(E(card.Meta)).Append("</span>");
            if (titleInHead) sb.Append("<span class=\"nc-title\">").Append(E(card.Title)).Append("</span>");
            if (!string.IsNullOrWhiteSpace(card.Time)) sb.Append("<span class=\"nc-time\">").Append(E(card.Time)).Append("</span>");
            sb.Append("</div>");
            if (hasTitle && !titleInHead) sb.Append("<div class=\"nc-title\">").Append(E(card.Title)).Append("</div>");
            if (!string.IsNullOrWhiteSpace(card.Text)) sb.Append("<div class=\"nc-text\" title=\"").Append(E(card.Text)).Append("\">").Append(Br(card.Text)).Append("</div>");
            if (!string.IsNullOrWhiteSpace(card.Note))
            {
                // 带附注型完整显示多行附注 / Noted shows the full multi-line note
                if (style == "noted") sb.Append("<div class=\"nc-note\">").Append(Br(card.Note)).Append("</div>");
                else sb.Append("<div class=\"nc-note\" title=\"").Append(E(card.Note)).Append("\">↳ ").Append(E(card.Note.Replace("\n", " "))).Append("</div>");
            }
            if (metaColumn) sb.Append("</div>");
            return sb.Append("</div>\n").ToString();
        }

        private sealed class LocalLinkRenderer : HtmlObjectRenderer<LinkInline>
        {
            private readonly Func<string, string> _imageData;
            public LocalLinkRenderer(Func<string, string> imageData) { _imageData = imageData; }
            protected override void Write(HtmlRenderer renderer, LinkInline link)
            {
                string target = link.GetDynamicUrl != null ? link.GetDynamicUrl() : link.Url;
                if (link.IsImage)
                {
                    string data = _imageData?.Invoke(target);
                    if (data != null && data.StartsWith("data:image/", StringComparison.Ordinal))
                        renderer.Write("<img src=\"").Write(WebUtility.HtmlEncode(data)).Write("\" alt=\"Local image\" loading=\"lazy\">");
                    else
                    {
                        renderer.Write("<span class=\"image-placeholder\">[图片不可用 / Image unavailable: ");
                        renderer.WriteChildren(link);
                        renderer.Write("]</span>");
                    }
                    return;
                }
                if (IsNoteLink(target))
                {
                    // 笔记间相对链接由阅读视图脚本拦截后在笔记本内打开。/ Relative note links are intercepted by the preview script and opened in the notebook.
                    renderer.Write("<a href=\"#\" class=\"note-link\" data-note=\"").Write(WebUtility.HtmlEncode(target)).Write("\">");
                    renderer.WriteChildren(link);
                    renderer.Write("</a>");
                    return;
                }
                bool allowed = IsWebLink(target) || (!string.IsNullOrEmpty(target) && target.StartsWith("#", StringComparison.Ordinal));
                if (allowed) renderer.Write("<a href=\"").Write(WebUtility.HtmlEncode(target)).Write("\" rel=\"noreferrer noopener\">");
                renderer.WriteChildren(link);
                if (allowed) renderer.Write("</a>");
            }
        }

        /// <summary>笔记页面链接：page:编号，或笔记库内的相对 Markdown 路径。/ A page link: page:id, or a relative Markdown path inside the library.</summary>
        internal static bool IsNoteLink(string target)
        {
            if (target != null && target.StartsWith(NotebookStore.LinkPrefix, StringComparison.Ordinal))
                return NotebookStore.IsPageId(target.Substring(NotebookStore.LinkPrefix.Length).ToLowerInvariant());
            if (string.IsNullOrWhiteSpace(target) || target.Contains(":") || target.StartsWith("/", StringComparison.Ordinal)
                || target.StartsWith("\\", StringComparison.Ordinal) || target.StartsWith("#", StringComparison.Ordinal)) return false;
            string path = target.Split('#', '?')[0];
            try { path = Uri.UnescapeDataString(path); } catch (UriFormatException) { return false; }
            return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWebLink(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(uri.Host);
    }
}
