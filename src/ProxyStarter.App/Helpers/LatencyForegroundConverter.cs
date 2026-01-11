using System;
using System.Globalization;
using System.Windows.Data;

namespace ProxyStarter.App.Helpers;

public sealed class LatencyForegroundConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush UnknownBrush = CreateBrush(System.Windows.Media.Color.FromRgb(160, 160, 160));
    private static readonly System.Windows.Media.Brush GoodBrush = CreateBrush(System.Windows.Media.Color.FromRgb(60, 203, 127));
    private static readonly System.Windows.Media.Brush WarningBrush = CreateBrush(System.Windows.Media.Color.FromRgb(243, 183, 27));
    private static readonly System.Windows.Media.Brush BadBrush = CreateBrush(System.Windows.Media.Color.FromRgb(232, 77, 79));

    public int GoodThreshold { get; set; } = 150;
    public int WarningThreshold { get; set; } = 300;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int latency || latency < 0)
        {
            return UnknownBrush;
        }

        if (latency <= GoodThreshold)
        {
            return GoodBrush;
        }

        if (latency <= WarningThreshold)
        {
            return WarningBrush;
        }

        return BadBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }

    private static System.Windows.Media.SolidColorBrush CreateBrush(System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
