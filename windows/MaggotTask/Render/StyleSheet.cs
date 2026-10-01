using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaggotTask.Core;

namespace MaggotTask.Render;

/// <summary>
/// 生物外观标尺（诊断用，`--flysheet=&lt;path.png&gt;`）。
///
/// 用途：把某个阶段的画法按 **1×（实机 34px）/ 2× / 3×** 三档、浅底与深底两种背景
/// 并排画出来。改绘制代码之后跑一次，就能同时回答两个问题：
///   · 实机尺寸下还认得出是什么吗？（看 1× 那一档）
///   · 细节本身画对了没有？（看 2×/3×）
///
/// 刻意不依赖窗口与状态文件：它是画法的标尺，改一次画法就该重跑一次。
/// </summary>
public static class StyleSheet
{
    private const int Width = 560;
    private const int Height = 620;

    private static readonly Color LightBg = Color.FromRgb(0xEC, 0xEC, 0xE6);
    private static readonly Color DarkBg = Color.FromRgb(0x1B, 0x1E, 0x19);

    /// <summary>渲染标尺图并写盘。返回一行可打印的摘要。</summary>
    public static string Render(string path, Stage stage = Stage.Fly)
    {
        DrawingVisual dv = new();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(LightBg), null, new Rect(0, 0, Width, 300));
            dc.DrawRectangle(new SolidColorBrush(DarkBg), null, new Rect(0, 300, Width, Height - 300));

            DrawRow(dc, stage, 40, dark: false);
            DrawRow(dc, stage, 340, dark: true);
        }

        RenderTargetBitmap rtb = new(Width, Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        PngBitmapEncoder enc = new();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (FileStream fs = File.Create(path))
        {
            enc.Save(fs);
        }

        return $"标尺图已写出：{Path.GetFullPath(path)}"
             + $"（{Width}×{Height}，阶段={stage}，浅底/深底 × 1×/2×/3×）";
    }

    private static void DrawRow(DrawingContext dc, Stage stage, double top, bool dark)
    {
        Color fg = dark ? Color.FromRgb(0xEC, 0xEC, 0xE6) : Color.FromRgb(0x22, 0x26, 0x1F);
        DrawLabel(dc, $"{(dark ? "深底" : "浅底")} · {stage}", new Point(24, top + 6), fg, 15);

        double[] scales = { 1.0, 2.0, 3.0 };
        double[] xs = { 120, 280, 470 };

        for (int i = 0; i < scales.Length; i++)
        {
            double k = scales[i];
            CreatureVisual c = new("sheet", 0x51EEDu + (uint)i)
            {
                Stage = stage,
                Rot = 0.5f,
                SpawnProgress = 1.0,
                Heading = 0.0,
                Position = new Point(xs[i] / k, (top + 86) / k),
            };

            dc.PushTransform(new ScaleTransform(k, k));
            CreaturePainter.DrawShadow(dc, c, 1.0);
            CreaturePainter.DrawBody(dc, c, 1.0, reduceMotion: false);
            dc.Pop();

            DrawLabel(dc, $"{k:0}×", new Point(xs[i] - 10, top + 146), fg, 11);
        }
    }

    private static void DrawLabel(DrawingContext dc, string text, Point at, Color color, double size)
    {
        FormattedText ft = new(
            text,
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            96);
        dc.DrawText(ft, at);
    }
}
