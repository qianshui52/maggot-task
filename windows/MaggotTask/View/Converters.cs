using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MaggotTask.View;

/// <summary>double → GridLength（按比例分配宽度）。</summary>
public sealed class RatioToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double ratio = value is double d ? d : 0.0;
        if (double.IsNaN(ratio) || ratio < 0) ratio = 0;
        return new GridLength(ratio, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>double → 百分比文案（0.42 → "42%"）。</summary>
public sealed class RatioToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double ratio = value is double d ? d : 0.0;
        return $"{Math.Round(ratio * 100.0),3:0}%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Stage → 阶段色画刷（控制面板计数点 / 任务列表 chip 共用）。</summary>
public sealed class StageBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MaggotTask.Core.Stage stage)
        {
            return stage switch
            {
                MaggotTask.Core.Stage.Egg => new SolidColorBrush(MaggotTask.Render.Palette.Egg),
                MaggotTask.Core.Stage.Larva => new SolidColorBrush(MaggotTask.Render.Palette.Larva),
                MaggotTask.Core.Stage.Pupa => new SolidColorBrush(MaggotTask.Render.Palette.Pupa),
                MaggotTask.Core.Stage.Fly => new SolidColorBrush(MaggotTask.Render.Palette.FlyRim),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x60, 0x4A)),
            };
        }
        return new SolidColorBrush(System.Windows.Media.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（true 可见）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
