using System.Globalization;
using System.Windows.Data;
using MaggotTask.Core;
using MaggotTask.Render;

namespace MaggotTask.View;

/// <summary>rot → 分段色（设计系统 §1 的 --rot-low / --rot-mid / --rot-high）。</summary>
public sealed class RotBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double rot = value is double d ? d : value is float f ? f : 0.0;
        System.Windows.Media.Color c = rot switch
        {
            < 0.40 => Palette.HexPublic("#7CB342"),
            < 0.70 => Palette.HexPublic("#D4A017"),
            _ => Palette.HexPublic("#C0392B"),
        };
        return new System.Windows.Media.SolidColorBrush(c);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>rot → 归一化宽度比例（rot / FLY_T，clamp 到 0..1）。</summary>
public sealed class RotToRatioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double rot = value is double d ? d : value is float f ? f : 0.0;
        double ratio = Math.Clamp(rot / LifecycleEngine.FlyT, 0.0, 1.0);
        return new System.Windows.GridLength(ratio, System.Windows.GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>rot → chip 文案（"ROT 62%"）。</summary>
public sealed class RotToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double rot = value is double d ? d : value is float f ? f : 0.0;
        return Math.Clamp(rot / LifecycleEngine.RotMax, 0.0, 1.0).ToString("P0", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
