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

        /// <summary>
        /// 检测到的手动对话生成完成时写入当天的「任务记录」（每轮只写一次；停止或中断的不记录）。
        /// Records a detected manual chat in today's task records once it finishes (once per round; stopped or interrupted chats are skipped).
        /// </summary>
        private void RecordManualChatInNotebook(ExternalChat c)
        {
            if (!_settings.RecordCompletedTasksInNotebook || c == null || c.NotebookRecorded || c.Generating || c.Stopped || c.Interrupted) return;
            c.NotebookRecorded = true;
            try
            {
                string path = new NotebookTaskJournal(new NotebookStore()).RecordManual(c);
                AppLog.Write(AppLog.TasksFile, "手动对话已写入笔记本 / Manual chat recorded in notebook: " + path);
                _notebook?.ReloadIfClean();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                AppLog.Write(AppLog.TasksFile, "手动对话写入笔记本失败 / Manual chat notebook record failed: " + ex.Message);
                SetStatus("任务记录写入笔记本失败 / Notebook record failed: " + ex.Message);
            }
        }
    }
}