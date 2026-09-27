using System;
using System.Globalization;
using System.IO;
using System.Net;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
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
                renderer.Render(Markdown.Parse(text ?? "", Pipeline));
                return writer.ToString();
            }
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
