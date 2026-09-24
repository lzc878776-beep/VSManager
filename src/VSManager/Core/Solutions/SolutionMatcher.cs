using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VSManager
{
    /// <summary>一个候选及其匹配得分与原因。/ A candidate with its score and reason.</summary>
    public sealed class SolutionCandidate
    {
        public SolutionEntry Entry;
        public int Score;
        public string Reason;
    }

    /// <summary>别名解析结果：唯一命中、多个候选或未命中（附已登记别名）。/ Alias lookup result: a unique hit, several candidates or no match (with the registered aliases).</summary>
    public sealed class SolutionLookup
    {
        public SolutionEntry Hit;
        public List<SolutionCandidate> Candidates = new List<SolutionCandidate>();
        public string Query;
        public bool Found => Hit != null;
        public bool Ambiguous => Hit == null && Candidates.Count > 1;
    }

    /// <summary>
    /// 解决方案别名解析（纯逻辑，便于单元测试）。规则按得分从高到低：
    /// 路径完全一致 100；别名完全一致 100；同义词完全一致 95；解决方案文件名一致 90；
    /// 去掉通用后缀（项目 / 解决方案 / 工程 / project / solution …）后一致 88；
    /// 互相包含 70；按顺序包含全部字符 50；说明中包含 35；字符二元组相似度 ≥ 0.5 时 30–50。
    /// 比较前统一做 NFKC 规范化（全角转半角）、转小写、去掉空白与标点。最高分唯一时命中；并列（或模糊匹配相差不到 10 分）时返回候选。
    /// Solution alias resolution (pure logic, unit-testable). Scores from high to low:
    /// same path 100; same alias 100; same synonym 95; same solution file name 90; same after removing generic suffixes
    /// (项目 / 解决方案 / 工程 / project / solution …) 88; one contains the other 70; all characters in order 50; found in the
    /// description 35; character-bigram similarity ≥ 0.5 gives 30–50.
    /// Text is NFKC-normalized (full-width to half-width), lower-cased and stripped of whitespace and punctuation first.
    /// A unique top score is a hit; ties (or fuzzy scores within 10 points) return candidates.
    /// </summary>
    public static class SolutionMatcher
    {
        private static readonly string[] GenericSuffixes =
        {
            "解决方案", "项目", "工程", "方案", "代码", "solution", "project", "slnx", "sln", "repo", "仓库"
        };

        /// <summary>规范化：NFKC、小写、去掉空白 / 标点 / 符号。/ Normalizes: NFKC, lower case, no whitespace / punctuation / symbols.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string n;
            try { n = s.Normalize(NormalizationForm.FormKC); } catch { n = s; }
            var sb = new StringBuilder(n.Length);
            foreach (char c in n.ToLowerInvariant())
            {
                var cat = char.GetUnicodeCategory(c);
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c) || char.IsControl(c) ||
                    cat == UnicodeCategory.Format || cat == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>规范化后再去掉通用后缀（至少保留 1 个字符）。/ Normalizes and strips generic suffixes (keeping at least one character).</summary>
        public static string Core(string s)
        {
            string n = Normalize(s);
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var suf in GenericSuffixes)
                    if (n.Length > suf.Length && n.EndsWith(suf, StringComparison.Ordinal))
                    {
                        n = n.Substring(0, n.Length - suf.Length);
                        changed = true;
                    }
            }
            return n;
        }

        /// <summary>看起来像文件路径（含目录分隔符或以 .sln / .slnx 结尾）。/ Looks like a file path (has a separator or ends with .sln / .slnx).</summary>
        public static bool LooksLikePath(string s)
        {
            s = (s ?? "").Trim().Trim('"');
            return s.IndexOf('\\') >= 0 || s.IndexOf('/') >= 0 ||
                   s.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把路径规范为可比较的形式（完整路径、统一分隔符、去掉末尾分隔符、忽略大小写）。/ Canonical comparable path (full path, unified separators, no trailing separator, case-insensitive).</summary>
        public static string CanonicalPath(string p)
        {
            p = (p ?? "").Trim().Trim('"');
            if (p.Length == 0) return "";
            try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
            try { p = System.IO.Path.GetFullPath(p); } catch { }
            return p.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
        }

        /// <summary>两个路径是否指向同一文件（忽略大小写与分隔符差异）。/ Whether two paths point to the same file (ignoring case and separators).</summary>
        public static bool SamePath(string a, string b)
        {
            string x = CanonicalPath(a), y = CanonicalPath(b);
            return x.Length > 0 && x == y;
        }

        /// <summary>解析别名、同义词或路径。/ Resolves an alias, a synonym or a path.</summary>
        public static SolutionLookup Resolve(IEnumerable<SolutionEntry> entries, string query)
        {
            var list = (entries ?? Enumerable.Empty<SolutionEntry>()).Where(e => e != null).ToList();
            var r = new SolutionLookup { Query = (query ?? "").Trim() };
            if (r.Query.Length == 0 || list.Count == 0) return r;

            if (LooksLikePath(r.Query))
            {
                var byPath = list.Where(e => SamePath(e.Path, r.Query)).ToList();
                if (byPath.Count > 0)
                {
                    r.Candidates = byPath.Select(e => new SolutionCandidate { Entry = e, Score = 100, Reason = "路径一致 / same path" }).ToList();
                    if (byPath.Count == 1) r.Hit = byPath[0];
                    return r;
                }
                if (r.Query.IndexOf('\\') >= 0 || r.Query.IndexOf('/') >= 0) return r;
            }

            var scored = list.Select(e => Score(e, r.Query)).Where(c => c.Score > 0).OrderByDescending(c => c.Score).ToList();
            if (scored.Count == 0) return r;
            int top = scored[0].Score;
            // 精确类命中（≥ 88）只取并列最高分；模糊匹配时把相差不到 10 分的也列为候选
            // Exact-class hits (≥ 88) keep only the tied top score; fuzzy matches also list candidates within 10 points
            int floor = top >= 88 ? top : top - 9;
            r.Candidates = scored.Where(c => c.Score >= floor).ToList();
            if (r.Candidates.Count == 1) r.Hit = r.Candidates[0].Entry;
            return r;
        }

        /// <summary>优先按已知路径查找；标题必须唯一且登记文件名无歧义，默认编号仅用于路径未知的实例。/ Prefers known paths; titles require a unique instance and unambiguous registered filename, defaults require unknown paths.</summary>
        public static VsInstance FindOpenSolution(SolutionEntry entry, IList<VsInstance> instances, IEnumerable<SolutionEntry> registry)
        {
            if (entry == null || instances == null) return null;
            var match = instances.FirstOrDefault(v => SamePath(v.SolutionPath, entry.Path));
            if (match != null) return match;
            match = instances.FirstOrDefault(v => string.IsNullOrWhiteSpace(v.SolutionPath) && SamePath(v.LaunchPath, entry.Path));
            if (match != null) return match;

            var unknown = instances.Where(v => string.IsNullOrWhiteSpace(v.SolutionPath) && string.IsNullOrWhiteSpace(v.LaunchPath)).ToList();
            string file = entry.FileName;
            bool uniqueFile = !(registry ?? Enumerable.Empty<SolutionEntry>()).Any(e => e != null &&
                string.Equals(e.FileName, file, StringComparison.OrdinalIgnoreCase) && !SamePath(e.Path, entry.Path));
            if (file.Length > 0 && uniqueFile)
            {
                var titled = instances.Where(v => string.Equals(VsService.TitleName(v.Title), file, StringComparison.OrdinalIgnoreCase)).ToList();
                if (titled.Count == 1 && unknown.Contains(titled[0])) return titled[0];
            }
            if (entry.DefaultVs > 0 && entry.DefaultVs <= instances.Count && unknown.Contains(instances[entry.DefaultVs - 1]))
                return instances[entry.DefaultVs - 1];
            return null;
        }

        /// <summary>计算一条登记与查询的得分。/ Scores one entry against the query.</summary>
        public static SolutionCandidate Score(SolutionEntry e, string query)
        {
            var best = new SolutionCandidate { Entry = e, Score = 0 };
            void Take(int score, string reason) { if (score > best.Score) { best.Score = score; best.Reason = reason; } }

            string q = Normalize(query), qc = Core(query);
            if (q.Length == 0) return best;
            if (LooksLikePath(query) && SamePath(e.Path, query)) Take(100, "路径一致 / same path");

            var names = new List<(string Text, int Exact, string Kind)>
            {
                (e.Alias, 100, "别名 / alias"),
                (e.FileName, 90, "解决方案文件名 / solution file name")
            };
            foreach (var s in e.Synonyms ?? new List<string>()) names.Add((s, 95, "同义词 / synonym"));

            foreach (var (text, exact, kind) in names)
            {
                string n = Normalize(text), nc = Core(text);
                if (n.Length == 0) continue;
                if (n == q) { Take(exact, kind + " 完全一致 / exact"); continue; }
                if (nc.Length > 0 && nc == qc) { Take(88, kind + " 一致（忽略后缀）/ same ignoring suffix"); continue; }
                if (qc.Length >= 2 && nc.Length >= 2 && (nc.Contains(qc) || qc.Contains(nc))) { Take(70, kind + " 包含 / contains"); continue; }
                if (qc.Length >= 2 && IsSubsequence(qc, nc)) { Take(50, kind + " 字符顺序匹配 / characters in order"); continue; }
                double dice = Dice(qc, nc);
                if (dice >= 0.5) Take(30 + (int)Math.Round(dice * 20), kind + " 相似 / similar");
            }
            string d = Normalize(e.Description);
            if (qc.Length >= 2 && d.Length > 0 && d.Contains(qc)) Take(35, "说明包含 / description contains");
            return best;
        }

        private static bool IsSubsequence(string needle, string hay)
        {
            int i = 0;
            foreach (char c in hay) if (i < needle.Length && needle[i] == c) i++;
            return i == needle.Length;
        }

        /// <summary>字符二元组的 Dice 相似度（0–1）。/ Dice coefficient over character bigrams (0–1).</summary>
        internal static double Dice(string a, string b)
        {
            if (a.Length < 2 || b.Length < 2) return 0;
            var x = new List<string>();
            for (int i = 0; i < a.Length - 1; i++) x.Add(a.Substring(i, 2));
            var y = new List<string>();
            for (int i = 0; i < b.Length - 1; i++) y.Add(b.Substring(i, 2));
            int hit = 0;
            foreach (var g in x)
            {
                int k = y.IndexOf(g);
                if (k >= 0) { hit++; y.RemoveAt(k); }
            }
            return 2.0 * hit / (a.Length - 1 + b.Length - 1);
        }
    }
}
