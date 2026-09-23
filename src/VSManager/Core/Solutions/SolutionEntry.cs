using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace VSManager
{
    /// <summary>
    /// 解决方案登记表中的一条：用户口语别名、解决方案完整路径、可选说明、同义词与默认 VS 实例编号。字段名即 solutions.json 的字段名。
    /// One solution registry entry: the spoken alias, the full solution path, an optional description, synonyms and the
    /// default VS instance number. Field names are the solutions.json field names.
    /// </summary>
    [DataContract]
    public sealed class SolutionEntry
    {
        /// <summary>别名（如「订单项目」）。/ Alias (for example "Order project").</summary>
        [DataMember] public string Alias;
        /// <summary>解决方案完整路径（.sln / .slnx）。/ Full solution path (.sln / .slnx).</summary>
        [DataMember] public string Path;
        /// <summary>可选说明。/ Optional description.</summary>
        [DataMember(EmitDefaultValue = false)] public string Description;
        /// <summary>同义词（如「订单」「下单」「order」）。/ Synonyms (for example "order", "ordering").</summary>
        [DataMember] public List<string> Synonyms = new List<string>();
        /// <summary>
        /// 可选默认 VS 实例编号（与 list_vs 中的 #编号一致，0 表示未设置）：无法读取某个 VS 的解决方案路径时，用它判断该解决方案是否已在该实例中打开。
        /// Optional default VS instance number (the #number in list_vs; 0 = not set): used to decide whether the solution is
        /// open in that instance when the VS solution path cannot be read.
        /// </summary>
        [DataMember] public int DefaultVs;

        /// <summary>解决方案文件名（不含扩展名）。/ Solution file name without extension.</summary>
        public string FileName
        {
            get
            {
                try { return string.IsNullOrWhiteSpace(Path) ? "" : System.IO.Path.GetFileNameWithoutExtension(Path.Trim()); }
                catch { return ""; }
            }
        }

        /// <summary>同义词显示文本（顿号分隔）。/ Synonyms as display text (separated by "、").</summary>
        public string SynonymText => string.Join("、", (Synonyms ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)));

        /// <summary>把用户输入的同义词文本（逗号、顿号、分号、竖线或换行分隔）拆分并去重。/ Splits user-entered synonym text (comma, 、, semicolon, bar or newline separated) and removes duplicates.</summary>
        public static List<string> ParseSynonyms(string text) =>
            (text ?? "").Split(new[] { ',', '，', '、', ';', '；', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public SolutionEntry Clone() => new SolutionEntry
        {
            Alias = Alias, Path = Path, Description = Description, DefaultVs = DefaultVs,
            Synonyms = (Synonyms ?? new List<string>()).ToList()
        };

        public override string ToString() => Alias + "  —  " + Path;
    }
}
