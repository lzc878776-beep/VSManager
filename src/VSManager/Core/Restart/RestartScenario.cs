using System;
using System.Collections.Generic;

namespace VSManager
{
    /// <summary>
    /// 自测重启前要造出的界面场景（页面、选中的 VS、AI 输入框草稿、窗口状态、所在屏幕、前台应用），
    /// 让 AI 总控助手能自己补齐「在某个 VS 对话页重启」「先切到其他应用再重启」这类测试项的前置条件。
    /// A UI scenario to set up before a self-test restart (page, selected VS, AI input draft, window state, screen, foreground app),
    /// so the AI assistant can create the preconditions of items such as "restart on a VS chat page" or "switch to another app first".
    /// </summary>
    public sealed class RestartScenario
    {
        public const string PageAgent = "agent", PageVs = "vs";
        public const string WindowMaximize = "maximize", WindowNormal = "normal", WindowMinimize = "minimize";
        public const string ForeVsManager = "vsmanager", ForeOther = "other";
        /// <summary>场景保留时长：超时未重启则作废 / How long a prepared scenario stays valid without a restart.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        public string Page;
        public string Vs;
        public string Draft;
        public string Window;
        public int Screen;
        public string Foreground;

        /// <summary>
        /// 由工具参数建立并规范化（接受常见同义词）；参数无效时返回 null 并给出中英错误。
        /// Builds and normalizes from tool arguments (common synonyms accepted); returns null with a bilingual error when invalid.
        /// </summary>
        public static RestartScenario Create(string page, string vs, string draft, string window, int screen, string foreground, out string error)
        {
            error = null;
            var s = new RestartScenario
            {
                Page = Norm(page, new Dictionary<string, string>
                {
                    ["agent"] = PageAgent, ["ai"] = PageAgent, ["assistant"] = PageAgent, ["总控"] = PageAgent, ["ai总控"] = PageAgent,
                    ["vs"] = PageVs, ["chat"] = PageVs, ["对话"] = PageVs, ["vs对话"] = PageVs,
                }, "page", ref error),
                Vs = (vs ?? "").Trim(),
                Draft = draft ?? "",
                Window = Norm(window, new Dictionary<string, string>
                {
                    ["maximize"] = WindowMaximize, ["maximized"] = WindowMaximize, ["max"] = WindowMaximize, ["最大化"] = WindowMaximize,
                    ["normal"] = WindowNormal, ["restore"] = WindowNormal, ["还原"] = WindowNormal, ["普通"] = WindowNormal,
                    ["minimize"] = WindowMinimize, ["minimized"] = WindowMinimize, ["min"] = WindowMinimize, ["最小化"] = WindowMinimize,
                }, "window", ref error),
                Screen = screen,
                Foreground = Norm(foreground, new Dictionary<string, string>
                {
                    ["vsmanager"] = ForeVsManager, ["self"] = ForeVsManager, ["本程序"] = ForeVsManager,
                    ["other"] = ForeOther, ["others"] = ForeOther, ["其他"] = ForeOther, ["其他应用"] = ForeOther,
                }, "foreground", ref error),
            };
            if (error != null) return null;
            if (s.Vs.Length > 0 && s.Page == null) s.Page = PageVs;
            if (s.Page == PageAgent && s.Vs.Length > 0) error = "page=agent 时不能同时指定 vs / vs cannot be combined with page=agent.";
            else if (s.Window == WindowMinimize && s.Foreground == ForeVsManager) error = "最小化与前台为 VSManager 互相矛盾 / minimize contradicts foreground=vsmanager.";
            else if (s.Screen < 0) error = "screen 从 1 开始 / screen is 1-based.";
            else if (s.Draft.Length > RestartUi.MaxDraftChars) error = $"草稿超过 {RestartUi.MaxDraftChars} 字 / draft exceeds {RestartUi.MaxDraftChars} chars.";
            else if (s.IsEmpty) error = "至少指定一项场景 / specify at least one scenario item.";
            return error == null ? s : null;
        }

        /// <summary>
        /// 合并到之前仍有效的场景上：本次未指定的项沿用之前的值；本次改页面时丢弃冲突的 VS，本次最小化时丢弃之前的「前台=VSManager」（反之亦然）。
        /// Merges onto a still-valid earlier scenario: items not given now keep the earlier values; a new page drops a conflicting VS,
        /// and a new minimize drops an earlier foreground=vsmanager (and vice versa).
        /// </summary>
        public RestartScenario MergeOnto(RestartScenario earlier)
        {
            if (earlier == null) return this;
            var m = new RestartScenario
            {
                Page = Page ?? earlier.Page,
                Vs = Vs.Length > 0 ? Vs : Page == null || Page == earlier.Page ? earlier.Vs : "",
                Draft = Draft.Length > 0 ? Draft : earlier.Draft,
                Window = Window ?? earlier.Window,
                Screen = Screen > 0 ? Screen : earlier.Screen,
                Foreground = Foreground ?? earlier.Foreground
            };
            if (m.Page == PageAgent) m.Vs = "";
            if (m.Window == WindowMinimize && m.Foreground == ForeVsManager)
            {
                if (Window != null) m.Foreground = null;
                else m.Window = null;
            }
            return m;
        }

        public bool IsEmpty => Page == null && Vs.Length == 0 && Draft.Length == 0 && Window == null && Screen == 0 && Foreground == null;

        /// <summary>中英场景说明（草稿只报字数，不回显内容）/ Bilingual description (the draft is reported by length only).</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Page == PageAgent) parts.Add("页面=AI 总控 / page=agent");
            else if (Page == PageVs) parts.Add("页面=VS 对话 / page=vs" + (Vs.Length > 0 ? "（" + Vs + "）" : ""));
            if (Window != null) parts.Add("窗口=" + (Window == WindowMaximize ? "最大化" : Window == WindowMinimize ? "最小化" : "还原") + " / window=" + Window);
            if (Screen > 0) parts.Add("屏幕=" + Screen + " / screen=" + Screen);
            if (Draft.Length > 0) parts.Add("草稿=" + Draft.Length + " 字 / draft=" + Draft.Length + " chars");
            if (Foreground != null) parts.Add("前台=" + (Foreground == ForeOther ? "其他应用" : "VSManager") + " / foreground=" + Foreground);
            return string.Join("；", parts);
        }

        private static string Norm(string value, Dictionary<string, string> map, string name, ref string error)
        {
            string key = (value ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (key.Length == 0) return null;
            if (map.TryGetValue(key, out string v)) return v;
            if (error == null) error = $"{name} 取值无效：{value} / invalid {name}: {value}";
            return null;
        }
    }
}
