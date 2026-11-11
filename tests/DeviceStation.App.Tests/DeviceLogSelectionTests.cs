using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeviceStation.App.Logging;
using DeviceStation.App.ViewModels;
using DeviceStation.App.Views;
using DeviceStation.Core.Channels;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Execution;
using DeviceStation.Core.Jobs;
using Microsoft.Extensions.Logging;

[assembly: AvaloniaTestApplication(typeof(DeviceStation.App.Tests.TestAppBuilder))]

namespace DeviceStation.App.Tests;

public static class TestAppBuilder
{
    // 用真实的 App（含资源和主题），但走无界面平台；App 只在桌面生命周期下才创建设备，这里不会触发。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>在真实窗口里点击设备卡片，验证日志面板按设备切换。</summary>
public sealed class DeviceLogSelectionTests : IAsyncDisposable
{
    private readonly UiLogSink _sink = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceManager _manager;
    private readonly MainViewModel _vm;
    private readonly MainWindow _window;

    public DeviceLogSelectionTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_sink));
        var slots = Enumerable.Range(1, 3).Select(i => new DeviceSlot
        {
            Id = $"S{i:00}",
            Name = $"工位 {i}",
            Channel = new SimulatedChannelOptions { Seed = i, MinDelayMs = 1, MaxDelayMs = 2, ConnectDelayMs = 1 },
        }).ToList();
        var job = new Job("测试作业", [new JobStep(new DelegateStep("空步骤", (_, _) => Task.CompletedTask), StepPolicy.Default)]);
        _manager = new DeviceManager(slots, _ => job, loggerFactory: _loggerFactory);
        _vm = new MainViewModel(_manager, _sink, [1.0], job.Name, "", Path.GetTempPath());
        _window = new MainWindow { DataContext = _vm, Width = 1320, Height = 880 };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ClickingCard_ShowsOnlyThatDevicesLog_AndClickingAgainShowsAll()
    {
        LogFor("S01", LogLevel.Error, "S01 出错");
        LogFor("S02", LogLevel.Information, "S02 正常");
        LogFor("S01", LogLevel.Information, "S01 正常");
        PumpUntil(() => _vm.Logs.Count == 3);

        var s01 = _vm.Devices.Single(d => d.Id == "S01");
        Assert.True(s01.HasUnreadError); // 没在看 S01 时出了错误：卡片亮红点

        Click(CardHeaderPoint("S01"));
        Assert.Same(s01, _vm.SelectedDevice);
        Assert.True(s01.IsSelected);
        Assert.False(s01.HasUnreadError);
        Assert.Equal("工位 1 的日志", _vm.LogScopeText);
        Assert.All(_vm.Logs, e => Assert.Equal("S01", e.DeviceId));
        Assert.Equal(2, _vm.Logs.Count);
        Assert.Equal(1, _vm.ErrorCount);

        // 选中期间，别的设备的新日志不进面板，本设备的新日志照常追加
        LogFor("S02", LogLevel.Warning, "S02 警告");
        LogFor("S01", LogLevel.Information, "S01 又一条");
        PumpUntil(() => _vm.Logs.Count == 3);
        Assert.All(_vm.Logs, e => Assert.Equal("S01", e.DeviceId));

        // 再点一次同一张卡片：回到全部设备（换个位置点，避免和上一次点击连成双击）
        Click(CardHeaderPoint("S01", x: 120));
        Assert.Null(_vm.SelectedDevice);
        Assert.False(s01.IsSelected);
        Assert.Equal("全部设备", _vm.LogScopeText);
        Assert.Equal(5, _vm.Logs.Count);
    }

    [AvaloniaFact]
    public void SwitchingBetweenCards_MovesSelection_AndLevelFilterStillApplies()
    {
        LogFor("S01", LogLevel.Warning, "S01 警告");
        LogFor("S02", LogLevel.Information, "S02 信息");
        LogFor("S02", LogLevel.Warning, "S02 警告");
        PumpUntil(() => _vm.Logs.Count == 3);

        Click(CardHeaderPoint("S01"));
        Click(CardHeaderPoint("S02"));
        Assert.Equal("S02", _vm.SelectedDevice?.Id);
        Assert.False(_vm.Devices.Single(d => d.Id == "S01").IsSelected);
        Assert.Equal(2, _vm.Logs.Count);

        _vm.SetLogFilterCommand.Execute(LogFilter.Warning);
        Assert.Equal("S02 警告", Assert.Single(_vm.Logs).Message);

        _vm.ShowAllDevicesCommand.Execute(null);
        Assert.Equal(2, _vm.Logs.Count); // 全部设备里的两条警告
    }

    [AvaloniaFact]
    public void ClickingStartButtonInsideCard_StartsDevice_WithoutSelectingIt()
    {
        var start = CardOf("S03").GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "▶"));
        Click(CenterOf(start));

        Assert.Null(_vm.SelectedDevice);
        PumpUntil(() => _manager.Get("S03").State.IsTerminal());
        Assert.Equal(DeviceState.Passed, _manager.Get("S03").State);
    }

    private void LogFor(string deviceId, LogLevel level, string message)
    {
        var logger = _loggerFactory.CreateLogger("test");
        using (logger.BeginScope(new Dictionary<string, object?> { ["DeviceId"] = deviceId }))
            logger.Log(level, "{Message}", message);
    }

    private Border CardOf(string id) =>
        _window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("card") && b.DataContext is DeviceCardViewModel { } c && c.Id == id);

    // 卡片顶部编号附近（避开开始/停止按钮）
    private Point CardHeaderPoint(string id, double x = 40) => CardOf(id).TranslatePoint(new Point(x, 24), _window)!.Value;

    private Point CenterOf(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), _window)!.Value;

    private void Click(Point p)
    {
        _window.MouseMove(p);
        _window.MouseDown(p, MouseButton.Left);
        _window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // MainViewModel 每 100 ms 刷新一次界面；在 UI 线程上推动消息循环直到条件满足。
    private static void PumpUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("等待界面刷新超时");
            // 推一小段真实的消息循环，让 DispatcherTimer 有机会触发
            var frame = new DispatcherFrame();
            DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(20));
            Dispatcher.UIThread.PushFrame(frame);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _window.Close();
        _vm.Dispose();
        await _manager.DisposeAsync();
        _loggerFactory.Dispose();
    }
}
