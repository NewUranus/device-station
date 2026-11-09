using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DeviceStation.Core.Devices;

namespace DeviceStation.App.Converters;

/// <summary>
/// 设备状态 → 状态色。ConverterParameter 为 "soft" 时返回 14% 透明度的同色（状态胶囊底色），否则返回实色。
/// </summary>
public sealed class DeviceStateBrushConverter : IValueConverter
{
    public static DeviceStateBrushConverter Instance { get; } = new();

    private static readonly Dictionary<DeviceState, Color> Palette = new()
    {
        [DeviceState.Idle] = Color.Parse("#475569"),
        [DeviceState.Queued] = Color.Parse("#64748B"),
        [DeviceState.Connecting] = Color.Parse("#F59E0B"),
        [DeviceState.Running] = Color.Parse("#3B82F6"),
        [DeviceState.Passed] = Color.Parse("#22C55E"),
        [DeviceState.Failed] = Color.Parse("#EF4444"),
        [DeviceState.Cancelled] = Color.Parse("#D97706"),
    };

    private static readonly Dictionary<(DeviceState, bool), IBrush> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DeviceState state || !Palette.TryGetValue(state, out var c)) return Brushes.Gray;
        bool soft = parameter as string == "soft";
        if (!Cache.TryGetValue((state, soft), out var brush))
            Cache[(state, soft)] = brush = new SolidColorBrush(soft ? Color.FromArgb(0x24, c.R, c.G, c.B) : c).ToImmutable();
        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
