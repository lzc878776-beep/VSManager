using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class CopilotMonitorTests
    {
        [TestMethod]
        public void Monitor_UsesInjectedLightweightProbe_InsteadOfFullTreeSearch()
        {
            var vs = new VsInstance { Pid = -12345, Key = "A", Title = "A" };
            var settings = new AppSettings { MonitorCopilot = true, WatchConversations = false, RestoreCopilotPane = false, PollMs = 500 };
            var monitor = new CopilotMonitor(() => new List<VsInstance> { vs }, () => settings);
            int probes = 0;
            bool fresh = true;
            using (var changed = new ManualResetEventSlim())
            {
                monitor.ProbeState = (v, s, f) => { Interlocked.Increment(ref probes); fresh = f; return CopilotState.Busy; };
                monitor.StateChanged += v => changed.Set();
                monitor.Start();
                try { Assert.IsTrue(changed.Wait(5000)); }
                finally { monitor.Stop(); }
            }
            Assert.IsTrue(probes >= 1);
            Assert.IsFalse(fresh, "常规轮询应节流未找到窗格的完整搜索 / Regular polling throttles full pane searches");
            Assert.AreEqual(CopilotState.Busy, vs.Copilot);
        }
    }
}
