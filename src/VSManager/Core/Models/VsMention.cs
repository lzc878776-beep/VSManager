using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSManager
{
    /// <summary>不可变实例快照，不按显示编号重新解析。/ Immutable instance snapshot; never resolved by display number.</summary>
    public sealed class VsMentionTarget
    {
        public string InstanceKey { get; }
        public string SolutionPath { get; }
        public string Name { get; }
        public int Number { get; }
        public string Note { get; }
        /// <summary>AI 总控助手的昵称，用于 @小维 提及。/ Nickname of the AI assistant, used by the @小维 mention.</summary>
        public const string AssistantName = "小维";
        /// <summary>是否为 @小维（AI 总控助手）而非某个 VS。/ Whether this is @小维 (the AI assistant) rather than a VS.</summary>
        public bool IsAssistant { get; }

        private VsMentionTarget()
        {
            InstanceKey = "assistant"; SolutionPath = ""; Number = 0; Name = AssistantName;
            Note = "AI 总控助手，消息交给 AI 处理 / AI assistant (ai.agent)";
            IsAssistant = true;
        }

        /// <summary>@ 候选列表中的 AI 总控助手项。/ The AI assistant entry of the @ candidate list.</summary>
        public static VsMentionTarget Assistant() => new VsMentionTarget();

        public VsMentionTarget(VsInstance instance, int number, string name, string note = null)
        {
            InstanceKey = instance.InstanceKey;
            SolutionPath = instance.SolutionPath ?? "";
            Number = number;
            Name = name ?? "";
            Note = note ?? "";
        }
        public bool Matches(VsInstance instance) => !IsAssistant && instance != null && instance.InstanceKey == InstanceKey
            && string.Equals(instance.SolutionPath ?? "", SolutionPath, StringComparison.OrdinalIgnoreCase);
        public override string ToString() => IsAssistant ? "@" + Name + " — " + Note : "#" + Number + " " + Name + (Note.Length == 0 ? "" : " — " + Note);
    }

    public sealed class MentionResolution
    {
        public bool HasMention { get; internal set; }
        public VsMentionTarget Target { get; internal set; }
        public string Body { get; internal set; }
        public string Error { get; internal set; }
        public bool Valid => HasMention && Target != null && Error == null;
    }

    public sealed class MentionSubmission
    {
        public QueuedTask Task { get; }
        public string Message { get; }
        public bool Accepted => Task != null;
        public MentionSubmission(QueuedTask task, string message) { Task = task; Message = message; }
    }

    /// <summary>会话内令牌表与纯路由校验；跨草稿共用，未知令牌拒绝。/ Session token registry and pure routing validation; shared across drafts, unknown tokens rejected.</summary>
    public sealed class VsMentionSession
    {
        public const string ChooseError = "请从 @ 列表确认目标；未发送 / Confirm a target from the @ list; not sent";
        public const string MissingError = "提及的 VS 已关闭、更换解决方案或无法核验；请重新选择，未发送 / Mentioned VS closed, changed solution or could not be verified; select again, not sent";
        private readonly Dictionary<string, VsMentionTarget> _tokens = new Dictionary<string, VsMentionTarget>(StringComparer.Ordinal);
        private static readonly Regex Literal = new Regex(@"`[^`]*`|(?<!\S)[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|(?<!\S)@[A-Za-z0-9._\-]+/[A-Za-z0-9._/\-]+|(?<!\S)(?:var|string|int|bool|object|class|namespace)\s+@\w+", RegexOptions.Compiled);

        public string Select(VsMentionTarget target)
        {
            string label = Regex.Replace(target.Name, @"[\r\n\[\]|@]", " ");
            string token;
            do token = (target.IsAssistant ? "@[" + VsMentionTarget.AssistantName : "@[#" + target.Number + " " + label)
                    + "|" + Guid.NewGuid().ToString("N").Substring(0, 6) + "]";
            while (_tokens.ContainsKey(token));
            _tokens.Add(token, target);
            return token;
        }

        public bool TryGetTarget(string token, out VsMentionTarget target) => _tokens.TryGetValue(token ?? "", out target);

        private static readonly Regex TokenPattern = new Regex(@"@\[((?:#\d+ [^\]\|\r\n]*)|小维)\|[0-9a-f]{6,12}\]", RegexOptions.Compiled);
        private static readonly Regex AssistantPattern = new Regex(@"\G@(?:\[小维\|[0-9a-f]{6,12}\]|小维)", RegexOptions.Compiled);

        /// <summary>已确认令牌在输入框中的位置及显示标签（不含内部标识）。/ Positions and display labels (without the internal id) of confirmed tokens in the input.</summary>
        public IEnumerable<(int Start, int Length, string Label)> Chips(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("@[", StringComparison.Ordinal) < 0) yield break;
            foreach (Match m in TokenPattern.Matches(text))
                if (_tokens.ContainsKey(m.Value)) yield return (m.Index, m.Length, "@" + m.Groups[1].Value.TrimEnd());
        }

        public static IEnumerable<int> Starts(string text)
        {
            text = text ?? "";
            if (text.IndexOf('@') < 0) yield break;
            var literals = Literal.Matches(text).Cast<Match>().ToArray();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '@') continue;
                if (i + 1 < text.Length && text[i + 1] == '@') { i++; continue; }
                if (literals.Any(m => i >= m.Index && i < m.Index + m.Length)) continue;
                // 拉丁词中间的 @ 保持普通文字；中文句内允许提及。/ Keep @ inside Latin words literal; allow mentions within Chinese sentences.
                if (i > 0 && !(i + 1 < text.Length && text[i + 1] == '[')
                    && (text[i - 1] < 128 && (char.IsLetterOrDigit(text[i - 1]) || "_./\\:".IndexOf(text[i - 1]) >= 0))) continue;
                yield return i;
                if (i + 1 < text.Length && text[i + 1] == '[')
                {
                    int end = text.IndexOf(']', i + 2);
                    if (end >= 0) i = end;
                }
            }
        }

        /// <summary>只有从列表确认的 @[…] 令牌才算提及；裸 @（包括后面为空或普通文字）按普通文字发送。
        /// / Only @[…] tokens confirmed from the list are mentions; a bare @ (empty or followed by plain text) is sent as plain text.</summary>
        public static IEnumerable<int> TokenStarts(string text) =>
            Starts(text).Where(i => i + 1 < text.Length && text[i + 1] == '[' && !AssistantPattern.Match(text, i).Success);

        /// <summary>是否含指向 VS 的提及（@小维 不算）。/ Whether the text mentions a VS (@小维 does not count).</summary>
        public static bool HasIntent(string text) => TokenStarts(text).Any();

        /// <summary>
        /// @小维 提及的位置：手动输入的 @小维 或从列表选择的 @[小维|标识]；反引号代码、@@ 等普通文字规则同 VS 提及。
        /// Positions of @小维 mentions: typed @小维 or the @[小维|id] token chosen from the list; backtick code, @@ and other literal rules match VS mentions.
        /// </summary>
        public static IEnumerable<(int Start, int Length)> AssistantMentions(string text)
        {
            text = text ?? "";
            if (text.IndexOf(VsMentionTarget.AssistantName, StringComparison.Ordinal) < 0) yield break;
            foreach (int i in Starts(text))
            {
                var m = AssistantPattern.Match(text, i);
                if (m.Success) yield return (i, m.Length);
            }
        }

        /// <summary>是否用 @小维 明确交给 AI 总控助手。/ Whether the text is explicitly addressed to the AI assistant via @小维.</summary>
        public static bool AddressesAssistant(string text) => AssistantMentions(text).Any();

        /// <summary>去掉所有 @小维 提及并整理空白。/ Removes every @小维 mention and tidies whitespace.</summary>
        public static string StripAssistant(string text)
        {
            var sb = new StringBuilder(text ?? "");
            foreach (var m in AssistantMentions(text).Reverse())
            {
                int start = m.Start, length = m.Length;
                if (start + length < sb.Length && (sb[start + length] == ' ' || sb[start + length] == '\u3000')) length++;
                sb.Remove(start, length);
            }
            return sb.ToString().Trim();
        }

        public MentionResolution Resolve(string text, IEnumerable<VsInstance> instances, bool hasAttachments = false)
        {
            var result = new MentionResolution { Body = text };
            var starts = TokenStarts(text ?? "").ToArray();
            if (starts.Length == 0) return result;
            result.HasMention = true;
            var body = new StringBuilder(text);
            foreach (int start in starts.AsEnumerable().Reverse())
            {
                int end = text.IndexOf(']', start);
                string token = end >= start ? text.Substring(start, end - start + 1) : "";
                if (!_tokens.TryGetValue(token, out var target)) { result.Error = ChooseError; return result; }
                if (!instances.Any(target.Matches)) { result.Error = MissingError; return result; }
                if (result.Target != null && (result.Target.InstanceKey != target.InstanceKey
                    || !string.Equals(result.Target.SolutionPath, target.SolutionPath, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Error = "一次只能指定一个不同的 VS；整条未发送 / Only one distinct VS per message; nothing sent";
                    return result;
                }
                result.Target = target;
                body.Remove(start, token.Length);
            }
            result.Body = body.ToString().Trim();
            if (result.Body.Length == 0)
            {
                if (hasAttachments) result.Body = "请查看附件并处理 / Please review and handle the attachments";
                else result.Error = "请在提及后输入任务内容；未发送 / Enter task text after the mention; not sent";
            }
            return result;
        }

        public static VsMentionTarget[] Filter(IEnumerable<VsMentionTarget> targets, string query) =>
            targets.Where(t => string.IsNullOrEmpty(query) || t.Number.ToString() == query.TrimStart('#')
                || t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || t.Note.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
    }
}
