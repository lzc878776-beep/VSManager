using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>有界、只读探测缓存；慢 UIA 不阻塞其他目标，不保留输入文本。/ Bounded read-only probe cache; slow UIA never blocks other targets and input text is never retained.</summary>
    internal sealed class ManualChatProbeCache
    {
        private sealed class Entry
        {
            internal VsInstance Target;
            internal string Path;
            internal int Pid;
            internal long StartTicks;
            internal DateTime At;
            internal int IdlePasses;
            internal bool Observed;
            internal Task<ManualChatObservation> Work;
            internal bool Matches(VsInstance target) => ReferenceEquals(Target, target)
                && Path == target.SolutionPath && Pid == target.Pid && StartTicks == target.StartTicks;
        }
        private readonly Dictionary<VsInstance, Entry> _entries = new Dictionary<VsInstance, Entry>();
        // 缓存失效不忘记在途身份；旧结果丢弃后才允许同目标新探测。/ Invalidating cached observations retains in-flight identities; discard stale results before probing the same target again.
        private readonly List<Entry> _running = new List<Entry>();
        private readonly Func<VsInstance, Task<ManualChatObservation>> _probe;
        private readonly Func<DateTime> _clock;
        internal ManualChatProbeCache(Func<VsInstance, Task<ManualChatObservation>> probe, Func<DateTime> clock = null)
        { _probe = probe; _clock = clock ?? (() => DateTime.UtcNow); }
        internal void Clear() => _entries.Clear();
        internal void Invalidate(VsInstance target)
        {
            if (target != null) _entries.Remove(target);
        }
        internal ManualChatObservation Read(VsInstance target, IEnumerable<VsInstance> alive)
        {
            var retained = new HashSet<VsInstance>(alive);
            foreach (var old in _entries.Keys.Where(v => !retained.Contains(v)).ToArray()) _entries.Remove(old);
            _running.RemoveAll(work => work.Work.IsCompleted);
            if (!retained.Contains(target)) return ManualChatObservation.Unknown;
            _entries.TryGetValue(target, out var entry);
            bool same = entry != null && entry.Matches(target);
            if (!same)
            {
                _entries.Remove(target);
                entry = null;
            }
            if (entry != null && entry.Work.IsCompleted && !entry.Observed)
            {
                entry.Observed = true;
                entry.At = _clock();
                entry.IdlePasses = entry.Work.Status == TaskStatus.RanToCompletion && entry.Work.Result == ManualChatObservation.Idle ? entry.IdlePasses + 1 : 0;
            }
            if (entry != null && !entry.Work.IsCompleted) return ManualChatObservation.Unknown;
            if (!same || _clock() - entry.At >= TimeSpan.FromSeconds(2))
            {
                if (_running.Count >= 4 || _running.Any(work => work.Matches(target))) return ManualChatObservation.Unknown;
                var next = new Entry { Target = target, Path = target.SolutionPath, Pid = target.Pid, StartTicks = target.StartTicks, IdlePasses = same ? entry.IdlePasses : 0 };
                try { next.Work = _probe(target) ?? Task.FromResult(ManualChatObservation.Unknown); }
                catch { next.Work = Task.FromResult(ManualChatObservation.Unknown); }
                _entries[target] = next;
                _running.Add(next);
                return ManualChatObservation.Unknown;
            }
            var result = entry.Work.Status == TaskStatus.RanToCompletion ? entry.Work.Result : ManualChatObservation.Unknown;
            return result == ManualChatObservation.Idle && entry.IdlePasses < 2 ? ManualChatObservation.Unknown : result;
        }
    }
}
