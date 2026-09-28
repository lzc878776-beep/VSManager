using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace VSManager
{
    public partial class CopilotChat
    {
        /// <summary>最多展开读取的步骤数（从本轮末尾往前数）。/ Maximum steps expanded and read (counting back from the end of the turn).</summary>
        public const int MaxLogSteps = 20;
        private const int ExpandWaitMs = 600;
        private const int LeafHeadLines = 8, LeafHeadChars = 600;

        /// <summary>
        /// 读取最后一条 Copilot 回复的步骤日志开头与界面提示：折叠的步骤（运行命令、Autopilot 重试等）先临时展开、读取日志最顶层内容后再折叠回去，
        /// 思考过程不展开；只读取可见的提示（如「此响应被截断」），隐藏的模板提示忽略。仅在任务失败时调用，必须在 STA 线程调用。
        /// Reads the step log heads and UI notices of the last Copilot reply: collapsed steps (run command, Autopilot retries,
        /// etc.) are expanded temporarily, the top of their log is read, then they are collapsed again; thoughts are not expanded.
        /// Only visible notices (such as "the response was truncated") are read; hidden template notices are ignored. Called only
        /// on task failure, on an STA thread.
        /// </summary>
        public TurnLog ReadTurnLog(VsInstance vs, int maxSteps = MaxLogSteps)
        {
            var log = new TurnLog();
            var pane = FindPane(vs);
            var list = pane == null ? null : FindList(pane);
            if (list == null) return log;
            var walker = TreeWalker.RawViewWalker;
            AutomationElement item = null;
            for (var c = walker.GetLastChild(list); c != null && item == null; c = walker.GetPreviousSibling(c))
                if (Safe(() => c.Current.ClassName) == "ChatMessageItem") item = c;
            var chat = item == null ? null : Children(walker, item).FirstOrDefault(e => Safe(() => e.Current.AutomationId) == "Chat");
            if (chat == null) return log;
            var children = Children(walker, chat);
            if (!children.Any(e => Safe(() => e.Current.Name) == "GitHub Copilot" || Safe(() => e.Current.ClassName) == "Image")) return log;

            foreach (var e in children)
            {
                if (Safe(() => e.Current.ControlType) != ControlType.Custom || Safe(() => e.Current.AutomationId) != "") continue;
                string name = Safe(() => e.Current.Name);
                if (!string.IsNullOrWhiteSpace(name) && !IsBidiWarning(name) && Visible(e)) log.Notices.Add(name.Trim());
            }

            var box = children.FirstOrDefault(e => Safe(() => e.Current.ControlType) == ControlType.List);
            if (box == null) return log;
            var steps = new List<AutomationElement>();
            foreach (var lbi in Children(walker, box))
            {
                var exp = Children(walker, lbi).FirstOrDefault(e => Safe(() => e.Current.ClassName) == "Expander");
                if (exp == null || IsThought(lbi, exp)) continue;
                steps.Add(exp);
            }
            foreach (var exp in steps.Skip(Math.Max(0, steps.Count - maxSteps)))
            {
                string header = Safe(() => exp.Current.Name)?.Trim();
                if (string.IsNullOrEmpty(header)) continue;
                log.Steps.Add(new StepLog { Header = header, Detail = ReadExpanded(walker, exp, header) });
            }
            return log;
        }

        private static bool IsBidiWarning(string name) =>
            name.IndexOf("双向 unicode", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("bidirectional Unicode", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsThought(AutomationElement lbi, AutomationElement exp)
        {
            string a = Safe(() => lbi.Current.Name) ?? "", b = Safe(() => exp.Current.Name) ?? "";
            return a.StartsWith("正在思考", StringComparison.Ordinal) || b.StartsWith("Thought:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>展开（必要时）并读取步骤内容的文字，读取后恢复原来的折叠状态。/ Expands the step if needed, reads its content text, then restores the collapsed state.</summary>
        private static string ReadExpanded(TreeWalker walker, AutomationElement exp, string header)
        {
            ExpandCollapsePattern ec = null;
            bool expanded = false;
            try
            {
                if (exp.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object p)) ec = (ExpandCollapsePattern)p;
                if (ec != null && ec.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    ec.Expand();
                    expanded = true;
                }
                string text = null;
                for (int waited = 0; ; waited += 100)
                {
                    text = ContentText(walker, exp, header);
                    if (!string.IsNullOrEmpty(text) || !expanded || waited >= ExpandWaitMs) break;
                    Thread.Sleep(100);
                }
                return text;
            }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            finally
            {
                if (expanded) { try { ec.Collapse(); } catch { } }
            }
        }

        /// <summary>
        /// 步骤内容的文字：按树的顺序收集文档与文字块（嵌套的子步骤只取标题，即日志最顶层），跳过标题与缩放等控件。
        /// Text of a step's content: documents and text blocks in tree order (nested child steps contribute only their headers,
        /// i.e. the top level of the log), skipping the header and zoom controls.
        /// </summary>
        private static string ContentText(TreeWalker walker, AutomationElement exp, string header)
        {
            var lines = new List<string>();
            var seen = new HashSet<string>();
            int chars = 0;
            void Visit(AutomationElement e, int depth)
            {
                if (depth > 8 || chars > TaskReply.LogHeadChars * 2) return;
                string id = Safe(() => e.Current.AutomationId), cls = Safe(() => e.Current.ClassName);
                if (id == "HeaderSite" || cls == "ScrollBar" || cls == "RepeatButton" || cls == "Slider") return;
                string name = Safe(() => e.Current.Name)?.Trim();
                bool leaf = cls == "FlowDocumentScrollViewer" || cls == "TextBlock" || cls == "Expander";
                if (leaf && !string.IsNullOrEmpty(name) && name != header && seen.Add(name))
                {
                    // 每段只取开头，让后面的段落（如命令输出）也能进入摘要 / Keep only the head of each block so later blocks (such as command output) still fit
                    string head = TaskReply.LogHead(name, LeafHeadLines, LeafHeadChars);
                    lines.Add(head);
                    chars += head.Length;
                }
                // 文档与子步骤只取自身文字，不再深入 / Documents and child steps contribute their own text only
                if (cls == "FlowDocumentScrollViewer" || cls == "Expander" && depth > 0) return;
                foreach (var c in Children(walker, e)) Visit(c, depth + 1);
            }
            foreach (var c in Children(walker, exp)) Visit(c, 1);
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        private static List<AutomationElement> Children(TreeWalker walker, AutomationElement e)
        {
            var result = new List<AutomationElement>();
            try
            {
                for (var c = walker.GetFirstChild(e); c != null && result.Count < 500; c = walker.GetNextSibling(c)) result.Add(c);
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            return result;
        }

        private static bool Visible(AutomationElement e)
        {
            try
            {
                // 隐藏的模板提示没有布局区域；滚出视野的真实提示仍有区域 / Hidden template notices have no layout box; real ones scrolled out of view still do
                var r = e.Current.BoundingRectangle;
                return !r.IsEmpty && r.Height > 0 && r.Width > 0;
            }
            catch { return false; }
        }

        private static T Safe<T>(Func<T> read)
        {
            try { return read(); } catch { return default(T); }
        }
    }
}
