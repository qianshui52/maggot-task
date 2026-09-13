using System.Globalization;
using System.Windows.Media;

namespace MaggotTask.Render;

/// <summary>
/// 字体装配。
///
/// Space Grotesk（400/500/600/700）与 JetBrains Mono（400/500/700）都**没有 CJK 字形**，
/// 因此这里构造复合 FontFamily 链：字体文件里的族名在前，中文回退在后。
/// WPF 逐字形回退 —— 拉丁字符走 Space Grotesk，汉字落到 Microsoft YaHei UI。
///
/// 注意：字体文件内嵌族名实测为 "Space Grotesk" / "JetBrains Mono"
/// （nameID 1/16 均为完整族名，nameID 2/17 为 Regular/Medium/SemiBold/Bold）。
/// </summary>
public static class ThemeFonts
{
    /// <summary>拉丁正文/展示 —— Space Grotesk，中文回退 YaHei UI。</summary>
    public static readonly FontFamily Display =
        new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Space Grotesk, Microsoft YaHei UI");

    /// <summary>数据/标签/编号 —— JetBrains Mono，中文回退 YaHei UI。</summary>
    public static readonly FontFamily Mono =
        new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#JetBrains Mono, Microsoft YaHei UI");

    /// <summary>纯中文（面板正文），直接交给系统。</summary>
    public static readonly FontFamily Cjk = new("Microsoft YaHei UI, Microsoft YaHei, SimSun");

    /// <summary>回退链报告，供诊断 / 自测打印。</summary>
    public static string DescribeResolution()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Display FamilyNames : {Join(Display.FamilyNames.Values)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Mono    FamilyNames : {Join(Mono.FamilyNames.Values)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Cjk     FamilyNames : {Join(Cjk.FamilyNames.Values)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Display Typefaces   : {Describe(Display)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Mono    Typefaces   : {Describe(Mono)}");
        return sb.ToString();
    }

    private static string Join(System.Collections.Generic.ICollection<string> values)
        => values.Count == 0 ? "(未解析)" : string.Join(" | ", values);

    private static string Describe(FontFamily family)
    {
        var parts = new List<string>();
        foreach (Typeface tf in family.GetTypefaces())
        {
            if (tf.TryGetGlyphTypeface(out GlyphTypeface gtf))
            {
                parts.Add($"{gtf.FamilyNames.Values.FirstOrDefault() ?? "?"}/{gtf.Weight}");
            }
            else
            {
                parts.Add($"{tf.FontFamily}/{tf.Weight}(未找到文件)");
            }
        }
        return parts.Count == 0 ? "(无)" : string.Join(", ", parts);
    }
}
