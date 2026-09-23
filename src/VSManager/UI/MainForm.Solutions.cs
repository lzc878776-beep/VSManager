using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>
    /// 主窗口：解决方案登记、打开 / 关闭 VS、任务暂存与自动推送。
    /// Main window: solution registry, opening / closing VS, parking tasks and pushing them automatically.
    /// </summary>
    public partial class MainForm
    {
        /// <summary>解决方案登记表（%APPDATA%\VSManager\solutions.json）。/ Solution registry (%APPDATA%\VSManager\solutions.json).</summary>
        private readonly SolutionRegistry _solutions = new SolutionRegistry().Load();

        /// <summary>
        /// 查找已打开该解决方案的 VS：先按路径；读不到路径的 VS 再按窗口标题中的解决方案名；最后按登记的默认 VS 编号（同样只用于读不到路径的实例）。
        /// Finds the VS that has the solution open: by path first; for a VS whose path cannot be read, by the solution name in
        /// the window title; finally by the registered default VS number (also only for instances without a path).
        /// </summary>
        internal VsInstance FindOpenSolution(SolutionEntry e, IList<VsInstance> list = null)
        {
            if (e == null) return null;
            list = list ?? _instances;
            var v = list.FirstOrDefault(i => SolutionMatcher.SamePath(i.SolutionPath, e.Path) || SolutionMatcher.SamePath(i.LaunchPath, e.Path));
            if (v != null) return v;
            string file = e.FileName;
            var unknown = list.Where(i => string.IsNullOrEmpty(i.SolutionPath)).ToList();
            v = unknown.FirstOrDefault(i => file.Length > 0 && string.Equals(VsService.TitleName(i.Title), file, StringComparison.OrdinalIgnoreCase));
            if (v != null) return v;
            if (e.DefaultVs > 0 && e.DefaultVs <= list.Count && string.IsNullOrEmpty(list[e.DefaultVs - 1].SolutionPath)) return list[e.DefaultVs - 1];
            return null;
        }

        /// <summary>登记条目当前是否已打开的简短文字。/ Short text telling whether the entry is open.</summary>
        private string SolutionStateText(SolutionEntry e)
        {
            var list = _instances;
            var v = FindOpenSolution(e, list);
            return v == null ? "○ 未打开 / not open" : "● #" + (list.IndexOf(v) + 1) + " 已打开 / open";
        }

        private void OpenSolutionRegistry(IWin32Window owner)
        {
            using (var f = new SolutionsForm(_solutions,
                () => _instances.Select((v, i) => new SolutionsForm.OpenVs { Number = i + 1, Name = NameOf(v), SolutionPath = v.SolutionPath }).ToList(),
                SolutionStateText, e => OpenSolutionFromUi(e)))
                f.ShowDialog(owner);
        }

        /// <summary>把选中 VS 的解决方案登记到登记表（已登记时提示别名）。/ Registers the selected VS's solution (shows the alias if already registered).</summary>
        private void RegisterSelectedSolution()
        {
            var v = Selected;
            if (v == null) { SetStatus("请先选择一个 VS / Select a VS first"); return; }
            if (string.IsNullOrEmpty(v.SolutionPath)) { SetStatus("无法读取该 VS 的解决方案路径，无法登记 / Cannot read this VS's solution path"); return; }
            var existing = _solutions.FindByPath(v.SolutionPath);
            if (existing != null) { SetStatus($"该解决方案已登记为「{existing.Alias}」/ Already registered as \"{existing.Alias}\""); return; }
            var alias = Prompt.Show(this, "登记解决方案 / Register solution", "别名（口语名称）/ Alias (spoken name)：", NameOf(v));
            if (string.IsNullOrWhiteSpace(alias)) return;
            var syn = Prompt.Show(this, "登记解决方案 / Register solution", "同义词（逗号或顿号分隔，可留空）/ Synonyms (comma separated, optional)：", "");
            var entry = new SolutionEntry { Alias = alias.Trim(), Path = v.SolutionPath, Synonyms = SolutionEntry.ParseSynonyms(syn) };
            if (_solutions.Items.Any(x => string.Equals(x.Alias, entry.Alias, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus($"别名「{entry.Alias}」已存在 / Alias already exists");
                return;
            }
            string err = _solutions.Upsert(entry);
            SetStatus(err ?? $"已登记「{entry.Alias}」→ {System.IO.Path.GetFileName(entry.Path)} / Registered");
            PumpTasks();
        }

        /// <summary>界面上打开登记的解决方案：已打开则激活，否则启动 VS。/ Opens a registered solution from the UI: activates it if open, otherwise starts VS.</summary>
        private async void OpenSolutionFromUi(SolutionEntry e)
        {
            var v = FindOpenSolution(e);
            if (v != null) { ActivateVs(v); return; }
            string err = await LaunchSolutionAsync(e.Path);
            SetStatus(err ?? $"正在打开「{e.Alias}」，VS 窗口出现后自动识别 / Opening \"{e.Alias}\"; detected automatically once the window appears");
        }

        private async Task<string> LaunchSolutionAsync(string path)
        {
            var running = _instances.ToList();
            string full = Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
            string devenv = await Task.Run(() => VsLifecycle.FindDevenv(running)).ConfigureAwait(false);
            string err = VsLifecycle.Launch(full, devenv);
            if (err == null)
                SendLog.Event(System.IO.Path.GetFileNameWithoutExtension(full), "打开解决方案 / Open solution：" + System.IO.Path.GetFileName(full) +
                    (devenv == null ? "（按文件关联 / via file association）" : ""));
            return err;
        }

        #region ITaskDispatchHost：暂存任务 / Parked tasks

        VsInstance ITaskDispatchHost.FindTargetVs(QueuedTask t)
        {
            var v = FindVs(t.VsKey) ?? _instances.FirstOrDefault(i => SolutionMatcher.SamePath(i.SolutionPath, t.VsKey));
            if (v != null) return v;
            var e = _solutions.FindByPath(t.VsKey) ??
                    (string.IsNullOrEmpty(t.Target) ? null : _solutions.Items.FirstOrDefault(x => string.Equals(x.Alias, t.Target, StringComparison.OrdinalIgnoreCase)));
            return FindOpenSolution(e);
        }

        TimeSpan ITaskDispatchHost.TargetSettleDelay => TimeSpan.FromSeconds(Math.Max(0, _settings.PendingVsSettleSeconds));

        void ITaskDispatchHost.AnnounceTask(QueuedTask t, string zh, string en) => AnnounceTask(t, zh, en);

        /// <summary>暂存 / 推送通知：弹窗或托盘气泡（中英双语），开启语音时按语音语言播报。/ Parked / pushed notice: popup or tray balloon (bilingual), spoken in the voice language when voice is on.</summary>
        private void AnnounceTask(QueuedTask t, string zh, string en)
        {
            if (_settings.PendingVsNotify) NotifyTask(t, zh, en);
        }

        /// <summary>任务通知（不检查开关）：弹窗或托盘气泡（中英双语），开启语音时按语音语言播报。/ Task notice (no switch check): popup or tray balloon (bilingual), spoken in the voice language when voice is on.</summary>
        private void NotifyTask(QueuedTask t, string zh, string en)
        {
            string title = $"📋 任务 #{t.Id} / Task #{t.Id}";
            string body = zh + "\n" + en;
            try
            {
                if (_settings.Popup) new ToastForm(title, body, () => { ShowMe(); }).Show();
                else ShowBalloon(title, body, ToolTipIcon.Info);
            }
            catch { }
            if (_settings.VoiceEnabled && _settings.HasVoiceKey)
            {
                string text = _settings.IsEnglishVoice ? en : zh;
                SetStatus($"[{DateTime.Now:HH:mm:ss}] 🔊 播报 / Announce：{text}");
                _voice.Speak(text);
            }
        }

        #endregion

        #region IAgentHost：解决方案 / Solutions

        SolutionRegistry IAgentHost.Solutions => _solutions;

        VsInstance IAgentHost.FindOpenSolution(SolutionEntry e) => FindOpenSolution(e, _instances);

        Task<string> IAgentHost.ParkTask(SolutionEntry e, string text) => OnUiAsync(async () =>
        {
            var dup = TaskStateMachine.FindActiveDuplicate(_tasks.Items, e.Path, text);
            if (dup != null) return $"「{e.Alias}」的任务清单中已有相同任务 #{dup.Id}（{StatusText(dup)}），未重复添加。";
            var q = _tasks.AddParked(e.Path, e.Alias, text, "AI");
            string hidden = HideResentFailed(q);
            string note = hidden == null ? "" : "\n" + hidden;
            SendLog.Event(e.Alias, $"任务清单：任务 #{q.Id} 已暂存，等待打开「{e.Alias}」/ task #{q.Id} parked, waiting for \"{e.Alias}\" to open");
            SetStatus($"任务清单：#{q.Id} 已暂存，等待打开「{e.Alias}」/ Task #{q.Id} parked, waiting for \"{e.Alias}\"");
            AnnounceTask(q, $"任务已暂存，等待打开{e.Alias}", $"Task parked, waiting for {e.Alias} to open");
            await PumpTasksAsync();
            if (q.Status != QueueStatus.WaitingVs) return $"「{e.Alias}」已打开，任务 #{q.Id} 已转入任务清单：{StatusText(q)}。" + note;
            return $"目标「{e.Alias}」尚未打开，任务 #{q.Id} 已暂存（状态：等待目标 VS）。该解决方案被打开后（open_solution 或用户手动打开）会自动推送，完成后通知你；需要立即执行请调用 open_solution。" + note;
        });

        Task<string> IAgentHost.LaunchSolution(string path) => LaunchSolutionAsync(path);

        /// <summary>
        /// 关闭前检查：Copilot 正在运行、有发送中 / 执行中的任务、正在调试或生成、存在未保存修改（或无法检查）时返回拒绝原因；可以关闭时返回 null。
        /// Pre-close check: returns the refusal reason when Copilot is running, a task is being sent / running, debugging or a
        /// build is in progress, or there are unsaved changes (or they cannot be checked); null when it can be closed.
        /// </summary>
        async Task<string> IAgentHost.CheckCanClose(VsInstance v)
        {
            string name = NameOf(v);
            if (v.Copilot == CopilotState.Busy) return $"拒绝关闭「{name}」：Copilot 正在运行，关闭会中断其任务。/ Refused: Copilot is running.";
            bool busyTask = await OnUi(() => _tasks.Items.Any(t => t.VsKey == v.Key && (t.Status == QueueStatus.Sending || t.Status == QueueStatus.Running))).ConfigureAwait(false);
            if (busyTask) return $"拒绝关闭「{name}」：任务清单中有发送中或执行中的任务。/ Refused: a task is being sent or running there.";
            if (v.Building) return $"拒绝关闭「{name}」：正在生成。/ Refused: a build is in progress.";
            if (v.DebugMode == 2 || v.DebugMode == 3) return $"拒绝关闭「{name}」：正在调试，请先停止调试。/ Refused: debugging is in progress.";
            var (items, reason) = await DteWorker.Run(() => { var l = VsLifecycle.UnsavedItems(v, out string r); return (l, r); }).ConfigureAwait(false);
            if (items == null) return $"拒绝关闭「{name}」：{reason}。/ Refused: unsaved changes cannot be checked.";
            if (items.Count > 0)
                return $"拒绝关闭「{name}」：有 {items.Count} 处未保存的修改，请先在 VS 中保存或放弃：\n" + string.Join("\n", items.Take(12).Select(x => "- " + x)) +
                       (items.Count > 12 ? $"\n- …（共 {items.Count} 处 / {items.Count} in total）" : "") + $"\nRefused: {items.Count} unsaved change(s).";
            return null;
        }

        async Task<string> IAgentHost.CloseVs(VsInstance v)
        {
            string name = NameOf(v);
            // 确认对话框期间状态可能变化：关闭前再检查一次 / State may change during the confirmation: check again right before closing
            string refuse = await ((IAgentHost)this).CheckCanClose(v).ConfigureAwait(false);
            if (refuse != null) return refuse;
            bool posted = await OnUi(() => VsLifecycle.RequestClose(v)).ConfigureAwait(false);
            if (!posted) return $"无法向「{name}」发送关闭请求（窗口可能已关闭）。/ Could not send the close request.";
            SendLog.Event(name, "已请求关闭 VS（WM_CLOSE，不强制结束进程）/ Close requested (WM_CLOSE, no force kill)");
            var deadline = DateTime.Now.AddSeconds(30);
            while (DateTime.Now < deadline)
            {
                await Task.Delay(500).ConfigureAwait(false);
                if (!VsLifecycle.IsRunning(v.Pid))
                {
                    await OnUi(() => { RefreshInstances(); return true; }).ConfigureAwait(false);
                    SendLog.Event(name, "VS 已关闭 / VS closed");
                    return $"已关闭「{name}」。/ \"{name}\" has been closed.";
                }
            }
            return $"已向「{name}」发送关闭请求，但 30 秒内未退出（VS 可能弹出了提示框等待处理），请到该 VS 中查看；本工具不会强制结束进程。/ Close requested but VS did not exit within 30 s (it may be showing a prompt); VSManager never kills the process.";
        }

        #endregion
    }
}
