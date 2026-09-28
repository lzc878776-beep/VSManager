using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VSManager
{
    /// <summary>把 VSManager 的控制 API 打包成 AI Agent Skill（SKILL.md + vsm.ps1），安装到 Copilot CLI / Claude 等的技能目录。</summary>
    public static class SkillInstaller
    {
        public const string SkillName = "vsmanager";
        private static readonly string[] Files = { "SKILL.md", "vsm.ps1" };

        /// <summary>技能安装目录（用户级）。</summary>
        public static IEnumerable<string> TargetDirs()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".copilot", "skills", SkillName);
            yield return Path.Combine(home, ".claude", "skills", SkillName);
            yield return Path.Combine(home, ".agents", "skills", SkillName);
        }

        public static bool Installed => TargetDirs().Any(d => File.Exists(Path.Combine(d, "SKILL.md")));

        /// <summary>
        /// 只更新已安装过的技能目录中内容不同的文件，不新建目录；返回更新的目录数。启动时调用，让新增命令（如 screenshot）自动生效。
        /// Updates only differing files in already-installed skill directories without creating new ones; returns the number updated. Called at startup so new commands (such as screenshot) take effect.
        /// </summary>
        public static int RefreshInstalled(IEnumerable<string> dirs = null)
        {
            var data = Resources(out _);
            if (data == null) return 0;
            int updated = 0;
            foreach (var dir in dirs ?? TargetDirs())
            {
                try
                {
                    if (!File.Exists(Path.Combine(dir, "SKILL.md"))) continue;
                    bool changed = false;
                    foreach (var kv in data)
                    {
                        string file = Path.Combine(dir, kv.Key);
                        if (File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(kv.Value)) continue;
                        File.WriteAllBytes(file, kv.Value);
                        changed = true;
                    }
                    if (changed) updated++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return updated;
        }

        private static Dictionary<string, byte[]> Resources(out string error)
        {
            error = null;
            var asm = typeof(SkillInstaller).Assembly;
            var data = new Dictionary<string, byte[]>();
            foreach (var f in Files)
            {
                var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("Skill." + f, StringComparison.OrdinalIgnoreCase));
                if (res == null) { error = "技能资源缺失：" + f; return null; }
                using (var s = asm.GetManifestResourceStream(res))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    data[f] = ms.ToArray();
                }
            }
            return data;
        }

        /// <summary>安装 / 更新技能文件，返回成功写入的目录。</summary>
        public static List<string> Install(out string error)
        {
            error = null;
            var done = new List<string>();
            var data = Resources(out error);
            if (data == null) return done;
            foreach (var dir in TargetDirs())
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    foreach (var kv in data) File.WriteAllBytes(Path.Combine(dir, kv.Key), kv.Value);
                    done.Add(dir);
                }
                catch (Exception ex) { error = dir + "：" + ex.Message; }
            }
            return done;
        }
    }
}
