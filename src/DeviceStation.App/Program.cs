using Avalonia;

namespace DeviceStation.App;

internal static class Program
{
    /// <summary>
    /// 命令行参数（均可选）：
    /// <c>--data &lt;目录&gt;</c> 数据目录（工位 JSON 与 results.db），
    /// <c>--max-concurrency &lt;n&gt;</c> 并发上限，
    /// <c>--firmware &lt;文件&gt;</c> 固件文件（缺省用内置的 512 KB 演示固件），
    /// <c>--autostart</c> 启动后自动“全部开始”（演示用）。
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        AppOptions.Current = AppOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia 设计器也会调用这个方法，不要删。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

internal sealed record AppOptions
{
    public static AppOptions Current { get; set; } = new();

    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceStation");

    public int MaxConcurrency { get; init; } = 8;

    public string? FirmwarePath { get; init; }

    public bool AutoStart { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--data" when next is not null:
                    o = o with { DataDirectory = Path.GetFullPath(next) };
                    i++;
                    break;
                case "--max-concurrency" when int.TryParse(next, out var n) && n > 0:
                    o = o with { MaxConcurrency = n };
                    i++;
                    break;
                case "--firmware" when next is not null:
                    o = o with { FirmwarePath = Path.GetFullPath(next) };
                    i++;
                    break;
                case "--autostart":
                    o = o with { AutoStart = true };
                    break;
            }
        }
        return o;
    }
}
