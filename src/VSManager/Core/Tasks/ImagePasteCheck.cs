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
    }
}
