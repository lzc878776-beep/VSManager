using System;
using System.Collections.Generic;

namespace VSManager
{
    internal static class DialogPolicy
    {
        private static readonly HashSet<string> NoticeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft Visual Studio", "Visual Studio", "提示", "信息", "通知", "操作完成",
            "Information", "Notification", "Operation Complete"
        };

        private static readonly HashSet<string> NoticeMessages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "操作已成功完成", "操作成功完成", "操作已完成", "操作完成", "保存成功", "文件已保存",
            "生成成功", "编译成功", "导出成功", "导出已完成", "没有可用的更新", "没有可用更新",
            "已是最新版本", "所有项目都是最新的",
            "The operation completed successfully", "Operation completed successfully",
            "The operation has completed successfully", "Build succeeded", "Export completed successfully",
            "The file has been saved", "No updates are available", "All projects are up-to-date"
        };

        private static readonly HashSet<string> Acknowledgements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OK", "&OK", "确定", "确定(O)", "确定(&O)", "关闭", "关闭(C)", "关闭(&C)", "Close", "&Close"
        };

        internal static bool CanAcknowledge(string title, string body, IReadOnlyList<string> buttons, bool hasInput)
        {
            if (hasInput || buttons == null || buttons.Count != 1 || !Acknowledgements.Contains(buttons[0] ?? "")) return false;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return false;
            // Match the entire message, not keywords: an appended warning or question must remain manual.
            return NoticeTitles.Contains(title.Trim())
                && NoticeMessages.Contains(body.Trim().TrimEnd('.', '!', '。', '！'));
        }
    }
}
