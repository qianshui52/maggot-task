using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace MaggotTask.Render;

/// <summary>
/// 字体解析诊断。
///
/// WPF 在字体缺失时会**静默回退**（不抛异常），所以「能编译、能显示」并不能证明
/// pack URI 真的解析到了仓库自带的 Space Grotesk / JetBrains Mono。
/// 这里用 GlyphTypeface + CharacterToGlyphMap 把事实打出来：
///   1) FontFamily 解析到了哪个族
///   2) 底层 GlyphTypeface 的族名与字体文件 URI（应当是 pack://…/Assets/Fonts/…）
///   3) 拉丁字形是否存在、CJK 字形是否**不**存在（证明必须依赖回退）
///   4) 复合字体族下「拉丁文本」与「中文文本」测出来的宽度不同（证明回退真的生效）
/// </summary>
public static class FontProbe
{
    public static string Describe()
    {
        StringBuilder sb = new();
        sb.AppendLine("── 字体解析 ──");
        DescribeFamily(sb, "Display (Space Grotesk)", ThemeFonts.Display, "Maggot");
        DescribeFamily(sb, "Mono (JetBrains Mono)", ThemeFonts.Mono, "ROT-0042");
        DescribeFamily(sb, "Cjk (中文回退)", ThemeFonts.Cjk, "蛆虫任务");

        sb.AppendLine();
        sb.AppendLine("── 中文渲染实测（复合字体族，逐字形回退）──");
        Measure(sb, "拉丁 'Maggot' via Display", ThemeFonts.Display, "Maggot");
        Measure(sb, "数字 '1.20' via Mono", ThemeFonts.Mono, "1.20");
        Measure(sb, "中文 '蛆虫任务' via Display", ThemeFonts.Display, "蛆虫任务");
        Measure(sb, "中文 '蛆虫任务' via Mono", ThemeFonts.Mono, "蛆虫任务");
        Measure(sb, "混排 '腐烂度 ROT 0.78'", ThemeFonts.Display, "腐烂度 ROT 0.78");
        Measure(sb, "纯中文族 '蛆虫任务' via Cjk", ThemeFonts.Cjk, "蛆虫任务");

        return sb.ToString();
    }

    /// <summary>判断某个字符能否被指定字体渲染（字符本身是否在其中）。</summary>
    public static bool CanRenderCjk(FontFamily family)
    {
        foreach (Typeface tf in family.GetTypefaces())
        {
            if (tf.TryGetGlyphTypeface(out GlyphTypeface gtf)
                && gtf.CharacterToGlyphMap.ContainsKey('蛆'))
            {
                return true;
            }
        }
        return false;
    }

    private static void DescribeFamily(StringBuilder sb, string label, FontFamily family, string sample)
    {
        sb.AppendLine($"[{label}]");
        sb.AppendLine($"  FontFamily.Source : {family.Source}");
        sb.AppendLine($"  FamilyNames       : {(family.FamilyNames.Count == 0 ? "(未解析)" : string.Join(" | ", family.FamilyNames.Values))}");

        int index = 0;
        bool anyLatin = false;
        bool anyCjk = false;

        foreach (Typeface tf in family.GetTypefaces())
        {
            index++;
            if (!tf.TryGetGlyphTypeface(out GlyphTypeface gtf))
            {
                sb.AppendLine($"  #{index} {tf.FontFamily} / {tf.Weight} / {tf.Style} → 字体文件未找到（回退）");
                continue;
            }

            string file = gtf.FontUri.IsFile ? gtf.FontUri.LocalPath : gtf.FontUri.ToString();
            string familyName = gtf.FamilyNames.Values.FirstOrDefault() ?? "(无名)";
            bool hasLatin = gtf.CharacterToGlyphMap.ContainsKey('M');
            bool hasCjk = gtf.CharacterToGlyphMap.ContainsKey('蛆');
            if (hasLatin) anyLatin = true;
            if (hasCjk) anyCjk = true;

            sb.AppendLine($"  #{index} {tf.Weight,-9} {tf.Style,-8} 族名='{familyName}' " +
                          $"拉丁'M'={(hasLatin ? "有" : "无")} 中文'蛆'={(hasCjk ? "有" : "无")}");
            sb.AppendLine($"       文件: {file}");
        }

        if (index == 0)
        {
            sb.AppendLine("  (没有任何 Typeface —— 资源字体完全没加载)");
        }

        sb.AppendLine($"  结论: 拉丁字形={(anyLatin ? "OK" : "缺失")} / " +
                      $"自带中文字形={(anyCjk ? "有（异常：设计上不应有 CJK）" : "无（符合预期，必须靠回退）")} " +
                      $"/ 样例'{sample}' 宽度={MeasureWidthPx(family, sample):0.0}px");
    }

    private static void Measure(StringBuilder sb, string label, FontFamily family, string text)
    {
        double w = MeasureWidthPx(family, text);
        sb.AppendLine($"  {label,-38} \"{text}\" → {w,7:0.0}px");
    }

    private static double MeasureWidthPx(FontFamily family, string text)
    {
        try
        {
            FormattedText ft = new(
                text,
                CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                15.0,
                Brushes.White,
                96.0);
            return ft.WidthIncludingTrailingWhitespace;
        }
        catch (Exception)
        {
            // 构造失败说明字体族整体不可用 —— 用一个明显的哨兵值标记。
            return -1.0;
        }
    }

    /// <summary>把诊断结果写到文件（WinExe 下没有可靠的控制台）。</summary>
    public static string WriteReport(string path)
    {
        string text = Describe();
        try
        {
            System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
        return text;
    }
}
