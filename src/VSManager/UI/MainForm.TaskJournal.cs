using System;
using System.IO;

namespace VSManager
{
    public partial class MainForm
    {
        /// <summary>把成功完成的任务写入笔记本当天的「任务记录」；失败只记录日志，不影响任务。/ Records a completed task in today's notebook folder; failures are logged only.</summary>
        private void RecordTaskInNotebook(QueuedTask t)
        {
            if (!_settings.RecordCompletedTasksInNotebook || t == null) return;
            try
            {
                string path = new NotebookTaskJournal(new NotebookStore()).Record(t, t.FullResult);
                AppLog.Write(AppLog.TasksFile, "任务 #" + t.Id + " 已写入笔记本 / Recorded in notebook: " + path);
                _notebook?.ReloadIfClean();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                AppLog.Write(AppLog.TasksFile, "任务 #" + t.Id + " 写入笔记本失败 / Notebook record failed: " + ex.Message);
                SetStatus("任务记录写入笔记本失败 / Notebook record failed: " + ex.Message);
            }
            finally { t.FullResult = null; }
        }
    }
}