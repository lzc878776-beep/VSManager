using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace VSManager
{
    /// <summary>
    /// 点击调试启动 CAD 前检查系统变量 FILEDIA：为 0 时启动脚本无法自动 NETLOAD，这里在启动前改回 1。
    /// AutoCAD 把 FILEDIA 保存在 HKCU\Software\Autodesk\AutoCAD\&lt;版本&gt;\&lt;产品&gt;\FixedProfile\General Configuration 的 FileDialog 值中，
    /// 只修改与启动程序安装目录匹配的产品（无法匹配时修改全部为 0 的产品）；其他 CAD 宿主由启动脚本首行在启动后改回 1。
    /// Checks the FILEDIA system variable before debugging starts CAD: when it is 0 the startup script cannot NETLOAD automatically, so it is set back to 1 before launch.
    /// AutoCAD stores FILEDIA as the FileDialog value under HKCU\Software\Autodesk\AutoCAD\&lt;release&gt;\&lt;product&gt;\FixedProfile\General Configuration;
    /// only products whose install folder matches the start program are changed (all products at 0 when none matches). Other CAD hosts are fixed by the first line of the startup script after launch.
    /// </summary>
    public static class CadFileDia
    {
        internal const string AutoCadKey = @"Autodesk\AutoCAD";
        internal const string ConfigKey = @"FixedProfile\General Configuration";
        internal const string ValueName = "FileDialog";

        /// <summary>
        /// 启动 CAD 前调用：把 FILEDIA=0 改为 1，返回被修改的产品（如 "R24.3\ACAD-7101:804"）；无需修改或失败时返回空列表。
        /// Call before launching CAD: changes FILEDIA=0 to 1 and returns the changed products (e.g. "R24.3\ACAD-7101:804"); an empty list when nothing needed changing or on failure.
        /// </summary>
        public static List<string> EnsureEnabled(string host, string program)
        {
            if (!string.Equals(host, "AutoCAD", StringComparison.OrdinalIgnoreCase)) return new List<string>();
            try
            {
                using (var user = Registry.CurrentUser.OpenSubKey("Software"))
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var machine = baseKey.OpenSubKey("SOFTWARE"))
                    return EnsureAutoCad(user, machine, program);
            }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>
        /// 在给定的 Software 根下修正 AutoCAD 的 FileDialog（测试可传入临时键）。
        /// Fixes AutoCAD's FileDialog under the given Software roots (tests may pass temporary keys).
        /// </summary>
        internal static List<string> EnsureAutoCad(RegistryKey userSoftware, RegistryKey machineSoftware, string program)
        {
            var changed = new List<string>();
            if (userSoftware == null) return changed;
            var products = new List<string>();
            using (var acad = userSoftware.OpenSubKey(AutoCadKey))
            {
                if (acad == null) return changed;
                foreach (string release in acad.GetSubKeyNames())
                    using (var r = acad.OpenSubKey(release))
                        if (r != null)
                            foreach (string product in r.GetSubKeyNames())
                                using (var cfg = r.OpenSubKey(product + "\\" + ConfigKey))
                                    if (cfg != null) products.Add(release + "\\" + product);
            }

            // 启动程序对应的安装能确定时只处理它；否则处理全部产品。/ Only the install matching the start program when it can be identified; otherwise every product.
            string dir = InstallDir(program);
            if (dir != null && machineSoftware != null)
            {
                var matching = products.Where(p => SameDir(Location(machineSoftware, p), dir)).ToList();
                if (matching.Count > 0) products = matching;
            }
            foreach (string p in products)
            {
                try
                {
                    using (var cfg = userSoftware.OpenSubKey(AutoCadKey + "\\" + p + "\\" + ConfigKey, true))
                    {
                        if (cfg == null || !IsZero(cfg.GetValue(ValueName))) continue;
                        cfg.SetValue(ValueName, 1, RegistryValueKind.DWord);
                        changed.Add(p);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException) { }
            }
            return changed;
        }

        private static bool IsZero(object value)
        {
            if (value == null) return false;
            try { return Convert.ToInt64(value) == 0; } catch (Exception) { return false; }
        }

        private static string Location(RegistryKey machineSoftware, string product)
        {
            try
            {
                using (var k = machineSoftware.OpenSubKey(AutoCadKey + "\\" + product))
                    return k?.GetValue("AcadLocation") as string;
            }
            catch (Exception) { return null; }
        }

        private static string InstallDir(string program)
        {
            if (string.IsNullOrWhiteSpace(program)) return null;
            try
            {
                string path = Environment.ExpandEnvironmentVariables(program.Trim().Trim('"').Trim());
                return Path.IsPathRooted(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
            }
            catch (Exception) { return null; }
        }

        private static bool SameDir(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a.Trim()).TrimEnd('\\'), Path.GetFullPath(b.Trim()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
    }
}
