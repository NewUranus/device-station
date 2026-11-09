using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DeviceStation.Core.Devices;

namespace DeviceStation.App.Converters;

/// <summary>设备状态 → 颜色。Parameter 为 "bg" 时返回浅色背景，否则返回强调色。</summary>
public sealed class DeviceStateBrushConverter : IValueConverter
{
    public static DeviceStateBrushConverter Instance { get; } = new();

    private static readonly Dictionary<DeviceState, (Color Accent, Color Background)> Palette = new()
    {
        [DeviceState.Idle] = (Color.Parse("#8A94A6"), Color.Parse("#F4F5F7")),
        [DeviceState.Queued] = (Color.Parse("#6B7A99"), Color.Parse("#EEF1F7")),
        [DeviceState.Connecting] = (Color.Parse("#D98E04"), Color.Parse("#FFF6E5")),
        [DeviceState.Running] = (Color.Parse("#2F6FED"), Color.Parse("#EAF1FF")),
        [DeviceState.Passed] = (Color.Parse("#1E9E5A"), Color.Parse("#E7F6EE")),
        [DeviceState.Failed] = (Color.Parse("#D64545"), Color.Parse("#FDECEC")),
        [DeviceState.Cancelled] = (Color.Parse("#B26B00"), Color.Parse("#F7F0E6")),
    };

    private static readonly Dictionary<(DeviceState, bool), IBrush> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DeviceState state || !Palette.TryGetValue(state, out var c)) return Brushes.Gray;
        bool bg = parameter as string == "bg";
        if (!Cache.TryGetValue((state, bg), out var brush))
            Cache[(state, bg)] = brush = new SolidColorBrush(bg ? c.Background : c.Accent).ToImmutable();
        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
