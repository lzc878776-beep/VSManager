using System;

namespace VSManager
{
    /// <summary>
    /// 任务清单中的“VS 手动对话”条目：用户直接在某个 VS 中向 Copilot 提问（不是由本工具发布的任务）。
    /// 启用历史归档时追加写入 chat\vs-名称-日期.jsonl（kind=manual），启动时从归档恢复最近的条目；未启用时仅保存在内存中。
    /// 与 TaskQueue 的排队 / 自动发布逻辑互不影响。
    /// </summary>
    public sealed class ExternalChat
    {
        public int Id;
        public int Pid;
        public string VsKey;
        public string VsName;
        /// <summary>提问去空白后的文本，用于识别同一轮对话。</summary>
        public string Key;
        public string Question;
        public string Answer;
        public bool Generating;
        /// <summary>在 VSManager 中点击了“停止生成”。</summary>
        public bool Stopped;
        /// <summary>从归档恢复时仍处于生成中（VSManager 退出前未观察到完成）。</summary>
        public bool Interrupted;
        /// <summary>启动时从归档恢复的条目。</summary>
        public bool Restored;
        /// <summary>归档中的条目标识（轮次 + 开始时间），用于去重与恢复。</summary>
        public string ArchiveId;
        public DateTime Started;
        public DateTime? Finished;

        public string StatusText => Generating ? "生成中" : Stopped ? "已停止" : Interrupted ? "已中断" : "已完成";

        public string CopyText =>
            "【提问】" + Environment.NewLine + (Question ?? "") + Environment.NewLine + Environment.NewLine +
            "【回答】" + Environment.NewLine + (string.IsNullOrWhiteSpace(Answer) ? "（暂无）" : Answer);
    }
}
