using System;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    public partial class MainForm : ITaskCompletionHost
    {
        /// <summary>任务成功后保存并关闭目标 VS 的文档；未保存成功的文档保持打开。/ After success, save and close the target VS's documents; documents that could not be saved stay open.</summary>
        async Task ITaskCompletionHost.AfterTaskCompletedAsync(QueuedTask t, VsInstance v)
        {
            if (!_settings.SaveAndCloseDocumentsAfterTask || v?.Dte == null) return;
            string name = NameOf(v);
            var result = await DteWorker.RunSta(() => VsDocumentCleanup.RunAfterTask(v, message => SendLog.Event(name, message)));
            var kept = result.SaveFailedNames.Concat(result.UnsavedNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string text = $"任务 #{t.Id} 完成：「{name}」已保存 {result.SavedCount} 个、关闭 {result.Closed} 个文档"
                + (kept.Length > 0 ? "，保留未保存：" + string.Join("、", kept) : "")
                + (result.DebuggingOrUnknown ? "（调试中未关闭）" : "")
                + $" / Saved {result.SavedCount}, closed {result.Closed} documents";
            SendLog.Event(name, text);
            AppLog.Write(AppLog.TasksFile, text);
            if (result.SavedCount > 0 || result.Closed > 0 || kept.Length > 0 || result.DebuggingOrUnknown) SetStatus(text);
        }

        private void ReportDocumentCleanup(VsInstance vs, VsDocumentCleanupResult result)
        {
            if (!result.ThresholdExceeded && !result.DebuggingOrUnknown && result.Unknown == 0 && result.Failed == 0) return;
            string name = NameOf(vs);
            string names = string.Join("、", result.UnsavedNames.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => (n ?? "").Replace("\r", " ").Replace("\n", " ")));
            string zh = "「" + name + "」" + result.SummaryZh;
            string en = "\"" + name + "\" " + result.SummaryEn;
            if (result.DebuggingOrUnknown)
            {
                zh += "；已跳过调试中或状态未知的文档";
                en += "; debugging or unknown-state documents were skipped";
            }
            string detail = zh + "\n" + en;
            if (names.Length > 0) detail += "\n未保存，已跳过 / Unsaved, skipped: " + names;
            if ((result.Unknown > 0 || result.Failed > 0 || result.DebuggingOrUnknown) && result.Diagnostics.Count > 0)
                detail += "\n" + result.Diagnostics.Last();
            SendLog.Event(name, detail);
            _agent.ShowLocalNotice("文档标签页清理 / Document tab cleanup", detail);
            NotifyWithVoice("文档标签页清理 / Document tab cleanup", zh, en);
            SetStatus(detail.Replace("\n", " · "));
        }
    }
}
