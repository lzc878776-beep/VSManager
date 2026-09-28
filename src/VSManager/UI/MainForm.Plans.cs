using System;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>Notion 计划编排与执行层的衔接。/ Wiring between Notion plan orchestration and the execution layer.</summary>
    public partial class MainForm : IAgentPlanHost, IPlanHost
    {
        private NotionPlanService _plans;

        NotionPlanService IAgentPlanHost.Plans => _plans ?? (_plans = CreatePlans());

        private NotionPlanService CreatePlans()
        {
            var plans = new NotionPlanService(this, new NotionClient(() => _settings.EffectiveNotionToken), () => _settings.NotionStatusProperty);
            _tasks.Changed += SyncPlans;
            return plans;
        }

        private async void SyncPlans()
        {
            try { if (_plans != null) await _plans.SyncAsync(); }
            catch (Exception ex) { AppLog.Error(AppLog.TasksFile, "Notion 计划同步失败 / Notion plan sync failed", ex); }
        }

        SolutionLookup IPlanHost.ResolveSolution(string query) => _solutions.Resolve(query);

        Task<int?> IPlanHost.EnqueuePlanTask(SolutionEntry entry, string text) => OnUi<int?>(() =>
        {
            var open = FindOpenSolution(entry, _instances);
            string result = open != null ? EnqueueTextTask(open, text, "AI") : ParkTaskCore(entry, text, null);
            var m = System.Text.RegularExpressions.Regex.Match(result ?? "", @"@(\d+)");
            return m.Success && _tasks.Find(int.Parse(m.Groups[1].Value)) != null ? int.Parse(m.Groups[1].Value) : (int?)null;
        });

        QueuedTask IPlanHost.FindTask(int id) => _tasks.Find(id);

        bool IPlanHost.SkipFailedPredecessors => _settings.SkipFailedPredecessors;

        int IPlanHost.MaxTaskText => AppSettings.ClampQuota(nameof(AppSettings.AgentMaxTaskText), _settings.AgentMaxTaskText);
    }
}
