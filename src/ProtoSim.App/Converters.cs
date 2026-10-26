using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.App;

public sealed class ServerStateToBrushConverter : IValueConverter
{
    private static readonly Brush Running = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xA8, 0x4A)));
    private static readonly Brush Faulted = Freeze(new SolidColorBrush(Color.FromRgb(0xD8, 0x3B, 0x3B)));
    private static readonly Brush Busy = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x9B, 0x1A)));
    private static readonly Brush Stopped = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ServerState.Running or true => Running,
        ServerState.Faulted => Faulted,
        ServerState.Starting or ServerState.Stopping => Busy,
        _ => Stopped,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

public sealed class DirectionToBrushConverter : IValueConverter
{
    private static readonly Brush Rx = ServerStateToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8)));
    private static readonly Brush Tx = ServerStateToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xA0, 0x60)));
    private static readonly Brush Warn = ServerStateToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0x8A, 0x10)));
    private static readonly Brush Error = ServerStateToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0xD8, 0x3B, 0x3B)));
    private static readonly Brush Info = ServerStateToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TrafficDirection.Rx => Rx,
        TrafficDirection.Tx => Tx,
        TrafficDirection.Warning => Warn,
        TrafficDirection.Error => Error,
        _ => Info,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible when the value is non-null and not an empty string. Pass "invert" to flip.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value is string s ? s.Length > 0 : value != null;
        if (parameter is "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter is "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
