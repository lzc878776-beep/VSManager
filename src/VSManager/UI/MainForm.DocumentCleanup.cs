using System;
using System.Linq;

namespace VSManager
{
    public partial class MainForm
    {
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
