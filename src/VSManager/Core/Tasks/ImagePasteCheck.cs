using System;

namespace VSManager
{
    /// <summary>单张图片粘贴后的附件状态。/ Attachment state after pasting one image.</summary>
    public enum ImagePasteState
    {
        /// <summary>还没出现新附件。/ No new attachment yet.</summary>
        Pending,
        /// <summary>恰好多出一张。/ Exactly one more attachment.</summary>
        Confirmed,
        /// <summary>数量不符（已确认的附件消失，或多出不止一张）：可能有人改动了草稿。/ Count mismatch (confirmed attachments vanished, or more than one appeared): the draft may have been changed.</summary>
        Unexpected
    }

    /// <summary>
    /// 逐张粘贴图片时按附件数量判断结果。只比较数量：附件列表重新渲染时 UI 自动化的 RuntimeId 会变化，
    /// 按 ID 比对会把本程序刚粘贴的图片当成别人改动的内容。
    /// Judges each image paste by attachment count. Only counts are compared: UI Automation RuntimeIds change when the chip
    /// list re-renders, so an ID comparison would treat images this program just pasted as someone else's changes.
    /// </summary>
    public static class ImagePasteCheck
    {
        /// <param name="baseline">粘贴第一张前已有的附件数。/ Attachments present before the first paste.</param>
        /// <param name="confirmed">本次已确认加入的图片数。/ Images this send has already confirmed.</param>
        /// <param name="current">当前附件数。/ Current attachment count.</param>
        public static ImagePasteState Evaluate(int baseline, int confirmed, int current)
        {
            int expected = Math.Max(0, baseline) + Math.Max(0, confirmed) + 1;
            if (current == expected) return ImagePasteState.Confirmed;
            if (current < expected - 1) return ImagePasteState.Unexpected;
            if (current > expected) return ImagePasteState.Unexpected;
            return ImagePasteState.Pending;
        }

        /// <summary>
        /// 计算附件区的用户附件数。Copilot 把所有粘贴的图片都命名为「图像」且彼此相等，WPF 只为它们生成一个 UI 自动化元素
        /// （同一个 RuntimeId），所以第 2 张起的图片在 UI 自动化树中不可见；列表项的 SizeOfSet（列表实际项数）不受影响，
        /// 用它减去可见的非用户项（「添加引用」按钮、活动文档）即为真实附件数。读不到 SizeOfSet 时退回可见数量。
        /// Counts user attachments in the attachment list. Copilot names every pasted image "图像"/"Image" and treats them as
        /// equal, so WPF exposes a single UI Automation element (one RuntimeId) for all of them and images from the 2nd one on
        /// are invisible in the UIA tree. The list items' SizeOfSet (the real item count) is unaffected: subtracting the visible
        /// non-user items (the "add reference" button, the active document) gives the real attachment count. Falls back to the
        /// visible count when SizeOfSet is unavailable.
        /// </summary>
        /// <param name="visibleUser">UI 自动化树中可见的用户附件数。/ User attachments visible in the UIA tree.</param>
        /// <param name="visibleOther">可见的非用户项数。/ Visible non-user items.</param>
        /// <param name="setSize">列表项报告的 SizeOfSet，读不到时为 null。/ SizeOfSet reported by a list item; null when unavailable.</param>
        public static int CountAttachments(int visibleUser, int visibleOther, int? setSize)
        {
            int user = Math.Max(0, visibleUser), other = Math.Max(0, visibleOther);
            if (setSize == null || setSize.Value < user + other) return user;
            return setSize.Value - other;
        }
    }
}
