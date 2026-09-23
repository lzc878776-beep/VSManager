using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VSManager
{
    /// <summary>
    /// 打开 / 关闭 Visual Studio 的辅助方法。关闭只发送温和的关闭请求（WM_CLOSE），绝不强制结束进程；
    /// 关闭前通过 DTE 检查未保存的修改。DTE 调用需在 <see cref="DteWorker"/> 线程上执行。
    /// Helpers to open / close Visual Studio. Closing only sends a gentle close request (WM_CLOSE) and never kills the
    /// process; unsaved changes are checked through DTE first. DTE calls must run on the <see cref="DteWorker"/> thread.
    /// </summary>
    public static class VsLifecycle
    {
        private const int WM_CLOSE = 0x0010;

        /// <summary>
        /// 查找 devenv.exe：优先使用正在运行的 VS 的可执行文件，其次用 vswhere 查找最新安装；都找不到时返回 null（改用文件关联打开）。
        /// Finds devenv.exe: prefers the executable of a running VS, then the latest installation via vswhere; null when
        /// neither is found (the file association is used instead).
        /// </summary>
        public static string FindDevenv(IEnumerable<VsInstance> running)
        {
            foreach (var v in running ?? Enumerable.Empty<VsInstance>())
            {
                string p = ExecutablePath(v.Pid);
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string vswhere = Path.Combine(pf, "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (!File.Exists(vswhere)) return null;
                var psi = new ProcessStartInfo(vswhere, "-latest -prerelease -property productPath")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    string path = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).FirstOrDefault(File.Exists);
                    return path;
                }
            }
            catch { return null; }
        }

        private static string ExecutablePath(int pid)
        {
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = " + pid))
                    foreach (System.Management.ManagementObject o in q.Get())
                        return o["ExecutablePath"] as string;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 启动 VS 打开解决方案，返回错误信息（null 表示已启动）。<paramref name="devenv"/> 为空时按文件关联打开。
        /// Starts VS with the solution; returns the error (null = started). Uses the file association when <paramref name="devenv"/> is empty.
        /// </summary>
        public static string Launch(string solutionPath, string devenv)
        {
            try
            {
                if (!File.Exists(solutionPath)) return "解决方案文件不存在 / Solution file not found：" + solutionPath;
                var psi = !string.IsNullOrEmpty(devenv)
                    ? new ProcessStartInfo(devenv, "\"" + solutionPath + "\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(solutionPath) }
                    : new ProcessStartInfo(solutionPath) { UseShellExecute = true };
                using (Process.Start(psi)) { }
                return null;
            }
            catch (Exception ex) { return "启动 Visual Studio 失败 / Failed to start Visual Studio：" + ex.Message; }
        }

        /// <summary>
        /// 列出未保存的文档、项目与解决方案（通过 DTE）。无法检查时返回 null 并给出原因。
        /// Lists unsaved documents, projects and the solution (through DTE). Returns null with a reason when it cannot check.
        /// </summary>
        public static List<string> UnsavedItems(VsInstance vs, out string reason)
        {
            reason = null;
            if (vs?.Dte == null) { reason = "无法连接到该 VS 的自动化接口（DTE），无法检查未保存的修改 / Cannot reach the VS automation interface (DTE) to check for unsaved changes"; return null; }
            var list = new List<string>();
            try
            {
                dynamic dte = vs.Dte;
                foreach (dynamic d in dte.Documents)
                {
                    try { if (!(bool)d.Saved) list.Add("文档 / Document：" + (string)d.Name); } catch { }
                }
                try
                {
                    foreach (dynamic p in dte.Solution.Projects)
                        try { if (!(bool)p.Saved) list.Add("项目 / Project：" + (string)p.Name); } catch { }
                }
                catch { }
                try { if (!string.IsNullOrEmpty((string)dte.Solution.FullName) && !(bool)dte.Solution.Saved) list.Add("解决方案 / Solution：" + Path.GetFileName((string)dte.Solution.FullName)); } catch { }
                return list;
            }
            catch (Exception ex)
            {
                reason = "检查未保存修改失败 / Failed to check unsaved changes：" + ex.Message;
                return null;
            }
        }

        /// <summary>向 VS 主窗口发送温和的关闭请求（等同点击右上角关闭，不会强制结束进程）。/ Sends a gentle close request to the VS main window (like clicking its close button; never kills the process).</summary>
        public static bool RequestClose(VsInstance vs) =>
            vs != null && vs.MainHwnd != IntPtr.Zero && Native.IsWindow(vs.MainHwnd) && Native.PostMessage(vs.MainHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        /// <summary>进程是否仍在运行。/ Whether the process is still running.</summary>
        public static bool IsRunning(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch { return false; }
        }
    }
}
