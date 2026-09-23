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

        /// <summary>安装 / 更新技能文件，返回成功写入的目录。</summary>
        public static List<string> Install(out string error)
        {
            error = null;
            var done = new List<string>();
            var asm = typeof(SkillInstaller).Assembly;
            var data = new Dictionary<string, byte[]>();
            foreach (var f in Files)
            {
                var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("Skill." + f, StringComparison.OrdinalIgnoreCase));
                if (res == null) { error = "技能资源缺失：" + f; return done; }
                using (var s = asm.GetManifestResourceStream(res))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    data[f] = ms.ToArray();
                }
            }
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
