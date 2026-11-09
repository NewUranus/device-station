using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeviceStation.App.Logging;
using DeviceStation.App.ViewModels;
using DeviceStation.App.Views;
using DeviceStation.Core.Channels;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Execution;
using DeviceStation.Core.Jobs;
using DeviceStation.Core.Jobs.Examples;
using DeviceStation.Core.Results;
using Microsoft.Extensions.Logging;

namespace DeviceStation.App;

public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private DeviceManager? _manager;
    private SqliteResultStore? _results;
    private MainViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = AppOptions.Current;
            var uiLog = new UiLogSink();
            _loggerFactory = LoggerFactory.Create(b => b
                .SetMinimumLevel(LogLevel.Information)
                .AddSimpleConsole(c =>
                {
                    c.IncludeScopes = true;
                    c.SingleLine = true;
                    c.TimestampFormat = "HH:mm:ss.fff ";
                })
                .AddProvider(uiLog));

            var slotStore = new DeviceSlotStore(Path.Combine(options.DataDirectory, "slots"));
            var slots = slotStore.LoadAll();
            if (slots.Count == 0)
            {
                slots = DemoSlots.Create(8);
                foreach (var s in slots) slotStore.Save(s);
            }

            // 固件只加载一次，所有工位共享同一个 ReadOnlyMemory<byte>。
            var firmware = options.FirmwarePath is { } path
                ? FirmwareImage.FromBytes(Path.GetFileName(path), File.ReadAllBytes(path))
                : FirmwareImage.CreateDemo();
            // 示例作业：固件烧录 + 功能测试。换成别的场景时，只需在这里换成自己的 IJob。
            IJob job = new CompositeJob("示例作业：固件烧录 + 功能测试", new DemoFlashJob(firmware), new DemoTestJob(DemoTestJob.DefaultItems));

            _results = new SqliteResultStore(Path.Combine(options.DataDirectory, "results.db"),
                _loggerFactory.CreateLogger<SqliteResultStore>());
            _manager = new DeviceManager(slots, _ => job, DefaultChannelFactory.Instance, _results, _loggerFactory,
                new DeviceManagerOptions { MaxConcurrency = options.MaxConcurrency });

            _viewModel = new MainViewModel(_manager, uiLog, $"{job.Name} · 固件 {firmware}", options.DataDirectory);
            desktop.MainWindow = new MainWindow { DataContext = _viewModel };
            if (options.AutoStart)
                desktop.MainWindow.Opened += (_, _) => _viewModel.StartAllCommand.Execute(null);
            desktop.ShutdownRequested += (_, _) => Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown()
    {
        _viewModel?.Dispose();
        // 关闭时停掉所有设备并等待结果落盘；同步等待是有意为之（进程即将退出）。
        _manager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _results?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _loggerFactory?.Dispose();
    }
}

/// <summary>首次运行时生成的 8 个模拟工位：随机延时、少量随机故障，演示重试、超时和失败隔离。</summary>
internal static class DemoSlots
{
    public static IReadOnlyList<DeviceSlot> Create(int count) =>
        Enumerable.Range(1, count).Select(i => new DeviceSlot
        {
            Id = $"S{i:00}",
            Name = $"工位 {i}",
            Channel = new SimulatedChannelOptions
            {
                SerialPrefix = $"DS{i:00}-",
                MinDelayMs = 20,
                MaxDelayMs = 70,
                ConnectDelayMs = 400,
                EraseDelayMs = 1500,
                MeasureDelayMs = 200,
                FailureRate = 0.004,
                HangRate = 0.001,
                ConnectFailureRate = 0.05,
                OutOfSpecRate = 0.12,
            },
        }).ToList();
}
