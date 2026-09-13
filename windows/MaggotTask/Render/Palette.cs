using System.Windows;
using System.Windows.Media;
using MaggotTask.Core;

namespace MaggotTask.Render;

/// <summary>设计系统 §1 的语义色（渲染层专用，避免在几何代码里散落裸 hex）。</summary>
public static class Palette
{
    public static readonly Color Egg = Hex("#F3EEDD");
    public static readonly Color EggDeep = Hex("#CFC6AC");
    public static readonly Color EggSpot = Hex("#8A7F5E");
    public static readonly Color Larva = Hex("#E9D9A3");
    public static readonly Color LarvaDeep = Hex("#C4A85F");
    public static readonly Color Pupa = Hex("#7A4A24");
    public static readonly Color PupaDeep = Hex("#4A2C14");
    public static readonly Color Fly = Hex("#2E3A33");
    public static readonly Color FlyRim = Hex("#8FD3C0");
    public static readonly Color WingA = Hex("#6E7FC4");
    public static readonly Color WingB = Hex("#4FA98E");
    public static readonly Color EyeRed = Hex("#B33A2E");
    public static readonly Color Outline = Color.FromArgb(140, 20, 24, 16); // rgba(20,24,16,0.55)
    public static readonly Color Shadow = Color.FromArgb(115, 0, 0, 0);     // rgba(0,0,0,0.45)
    public static readonly Color Highlight = Colors.White;

    public static readonly Brush OutlineBrush = Frozen(new SolidColorBrush(Outline));
    public static readonly Brush EggBrush = Frozen(new SolidColorBrush(Egg));
    public static readonly Brush EggDeepBrush = Frozen(new SolidColorBrush(EggDeep));
    public static readonly Brush EggSpotBrush = Frozen(new SolidColorBrush(EggSpot));
    public static readonly Brush LarvaBrush = Frozen(new SolidColorBrush(Larva));
    public static readonly Brush LarvaDeepBrush = Frozen(new SolidColorBrush(LarvaDeep));
    public static readonly Brush PupaBrush = Frozen(new SolidColorBrush(Pupa));
    public static readonly Brush PupaDeepBrush = Frozen(new SolidColorBrush(PupaDeep));
    public static readonly Brush FlyBrush = Frozen(new SolidColorBrush(Fly));
    public static readonly Brush EyeBrush = Frozen(new SolidColorBrush(EyeRed));

    /// <summary>高光：白色 55% / 30%。</summary>
    public static readonly Brush Highlight55 = Frozen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)));
    public static readonly Brush Highlight30 = Frozen(new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)));

    /// <summary>蛹的纵向渐变 --pupa → --pupa-deep。</summary>
    public static readonly Brush PupaGradient = Frozen(new LinearGradientBrush(
        Pupa, PupaDeep, new Point(0.15, 0), new Point(0.85, 1)));

    /// <summary>翅：--wing-a → --wing-b，35% 透明。</summary>
    public static readonly Brush WingGradient = Frozen(new LinearGradientBrush(
        Color.FromArgb(89, WingA.R, WingA.G, WingA.B),
        Color.FromArgb(89, WingB.R, WingB.G, WingB.B),
        new Point(0, 0), new Point(1, 1)));

    /// <summary>爆浆/体液色（按阶段取色，稍微提亮以保证可见）。</summary>
    public static Brush SplatBrush(Stage stage) => stage switch
    {
        Stage.Egg => Frozen(new SolidColorBrush(Hex("#F6F1E2"))),
        Stage.Larva => Frozen(new SolidColorBrush(Hex("#EFE0AE"))),
        Stage.Pupa => Frozen(new SolidColorBrush(Hex("#8A5528"))),
        _ => Frozen(new SolidColorBrush(Hex("#46564C"))),
    };

    public static Color StageColor(Stage stage) => stage switch
    {
        Stage.Egg => Egg,
        Stage.Larva => Larva,
        Stage.Pupa => Pupa,
        Stage.Fly => FlyRim,
        _ => Hex("#55604A"),
    };

    public static Brush Frozen(Brush b)
    {
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    /// <summary>#RRGGBB / #AARRGGBB → Color。</summary>
    public static Color HexPublic(string hex) => Hex(hex);

    private static Color Hex(string hex)
    {
        return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
    }
}
