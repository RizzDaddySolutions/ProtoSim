using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.App;

public sealed class ServerStateToBrushConverter : IValueConverter
{
    private static readonly IBrush Running = new ImmutableSolidColorBrush(Color.FromRgb(0x22, 0xA8, 0x4A));
    private static readonly IBrush Faulted = new ImmutableSolidColorBrush(Color.FromRgb(0xD8, 0x3B, 0x3B));
    private static readonly IBrush Busy = new ImmutableSolidColorBrush(Color.FromRgb(0xE0, 0x9B, 0x1A));
    private static readonly IBrush Stopped = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ServerState.Running or true => Running,
        ServerState.Faulted => Faulted,
        ServerState.Starting or ServerState.Stopping => Busy,
        _ => Stopped,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

}

public sealed class DirectionToBrushConverter : IValueConverter
{
    private static readonly IBrush Rx = new ImmutableSolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8));
    private static readonly IBrush Tx = new ImmutableSolidColorBrush(Color.FromRgb(0x22, 0xA0, 0x60));
    private static readonly IBrush Warn = new ImmutableSolidColorBrush(Color.FromRgb(0xD0, 0x8A, 0x10));
    private static readonly IBrush Error = new ImmutableSolidColorBrush(Color.FromRgb(0xD8, 0x3B, 0x3B));
    private static readonly IBrush Info = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TrafficDirection.Rx => Rx,
        TrafficDirection.Tx => Tx,
        TrafficDirection.Warning => Warn,
        TrafficDirection.Error => Error,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>True when the value is non-null and not an empty string. Pass "invert" to flip.</summary>
public sealed class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value is string s ? s.Length > 0 : value != null;
        return parameter is "invert" ? !has : has;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
