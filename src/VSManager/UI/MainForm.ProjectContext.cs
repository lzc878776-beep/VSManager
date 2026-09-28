using System;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// 项目摘要：为任务通知与任务发布结果提供该 VS 的项目名、职责描述与最近任务结果。
    /// Project summary: supplies the project name, responsibility and recent task outcomes of a VS for task notifications and
    /// task submission results.
    /// </summary>
    public partial class MainForm : ITaskProjectContextHost, IAgentProjectContextHost
    {
        /// <summary>调度器在界面线程调用。/ Called by the dispatcher on the UI thread.</summary>
        string ITaskProjectContextHost.ProjectContext(QueuedTask t) =>
            t == null ? null : ProjectContextFor(t.VsKey, t.VsName, t.Id);

        Task<string> IAgentProjectContextHost.ProjectContext(string vsKey, string vsName) =>
            OnUi(() => ProjectContextFor(vsKey, vsName, 0));

        private string ProjectContextFor(string vsKey, string vsName, int excludeId)
        {
            string project = TaskProjectContext.ProjectName(vsKey, vsName);
            return TaskProjectContext.Summary(project, NoteForKey(vsKey, project), _tasks.Items, vsKey, excludeId);
        }

        /// <summary>职责描述：优先取已打开实例的描述，否则按解决方案键或名称查找。/ Responsibility: the open instance's note first, otherwise by solution key or name.</summary>
        private string NoteForKey(string vsKey, string project)
        {
            var open = _instances.FirstOrDefault(i => string.Equals(i.Key, vsKey, StringComparison.OrdinalIgnoreCase));
            if (open != null && NoteOf(open) is string note) return note;
            if (!string.IsNullOrEmpty(vsKey) && _settings.GetNote(vsKey) is string byKey) return byKey;
            return string.IsNullOrEmpty(project) ? null : _settings.GetNote("title:" + project);
        }
    }
}
