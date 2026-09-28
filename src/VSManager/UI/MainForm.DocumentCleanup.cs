using System;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    public partial class MainForm : ITaskCompletionHost
    {
        /// <summary>AI 任务完成后只关闭目标 VS 已保存的 .cs 标签页，保留未保存文件并提示。/ After an AI task completes, closes only saved .cs tabs in its VS and reports unsaved files kept open.</summary>
        async Task ITaskCompletionHost.AfterTaskCompletedAsync(QueuedTask t, VsInstance v)
        {
            if (!_settings.SaveAndCloseDocumentsAfterTask || t == null || !t.FromAgent
                || (t.Status != QueueStatus.Done && t.Status != QueueStatus.Unverified)) return;
            string name = v == null ? t.VsName : NameOf(v);
            var result = await DteWorker.RunSta(() => VsDocumentCleanup.RunAfterTask(v, message => SendLog.Event(name, message)));
            string kept = string.Join("、", result.UnsavedNames.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => (n ?? "").Replace("\r", " ").Replace("\n", " ")));
            string zh = $"AI 任务 #{t.Id}「{name}」.cs 标签页检查：已关闭 {result.Closed}，未保存保留 {result.SkippedUnsaved}，关闭失败 {result.Failed}，状态未知 {result.Unknown}；未自动保存任何文件"
                + (kept.Length > 0 ? "；保留文件：" + kept : "")
                + (result.DebuggingOrUnknown ? "；调试中或调试状态未知，未关闭" : "");
            string en = $"AI task #{t.Id} \"{name}\" .cs tab check: closed {result.Closed}, unsaved kept {result.SkippedUnsaved}, failed {result.Failed}, unknown {result.Unknown}; no files were auto-saved"
                + (kept.Length > 0 ? "; kept files: " + kept : "")
                + (result.DebuggingOrUnknown ? "; debugging or unknown debugger state, tabs kept open" : "");
            string text = zh + "\n" + en;
            SendLog.Event(name, text);
            AppLog.Write(AppLog.TasksFile, text);
            SetStatus(text.Replace("\n", " · "));
            if (result.InitialTabCount > 0 || result.Unknown > 0 || result.Failed > 0 || result.DebuggingOrUnknown)
                _agent?.ShowLocalNotice("AI 任务后 .cs 标签页清理 / Post-task C# tab cleanup", text);
            if (result.SkippedUnsaved > 0 || result.Unknown > 0 || result.Failed > 0 || result.DebuggingOrUnknown)
                NotifyWithVoice(".cs 文件已保留 / C# files kept open", zh, en);
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
