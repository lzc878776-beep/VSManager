namespace VSManager.Tests
{
    /// <summary>
    /// 测试分类（用于 <c>[TestCategory]</c>）。未标注分类的测试即「完全不弹窗」类：不显示窗口、不抢焦点、不使用剪贴板、不启动子进程，
    /// 日常只需运行这一类。新增测试若会弹窗 / 抢焦点 / 使用剪贴板，标注 <see cref="Ui"/>；若启动 git、cmd、PowerShell 等命令行进程，标注 <see cref="Console"/>。
    /// Test categories (for <c>[TestCategory]</c>). Tests without a category are the "no popup" set: they show no window, take no focus,
    /// do not touch the clipboard and start no child process; routine runs only need this set. Tag new tests that show windows, take
    /// focus or use the clipboard with <see cref="Ui"/>, and tests that start command-line processes (git, cmd, PowerShell) with <see cref="Console"/>.
    /// </summary>
    internal static class TestKind
    {
        /// <summary>界面类：显示窗口、抢占焦点或读写剪贴板。/ UI: shows windows, takes focus or uses the clipboard.</summary>
        public const string Ui = "UI";

        /// <summary>命令行类：启动 git / cmd / PowerShell 等子进程。/ Console: starts child processes such as git / cmd / PowerShell.</summary>
        public const string Console = "Console";
    }
}