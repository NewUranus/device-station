using System.Collections.Concurrent;
using DeviceStation.Core.Channels;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Execution;
using DeviceStation.Core.Jobs;
using DeviceStation.Core.Jobs.Examples;
using DeviceStation.Core.Results;

namespace DeviceStation.Core.Tests;

/// <summary>记录每个工位创建过的 SimulatedChannel，便于断言设备侧收到的命令。</summary>
internal sealed class RecordingChannelFactory : IChannelFactory
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SimulatedChannel>> _created = new();

    public IDeviceChannel Create(string slotId, ChannelOptions options)
    {
        var ch = new SimulatedChannel((SimulatedChannelOptions)options, slotId);
        _created.GetOrAdd(slotId, _ => new()).Enqueue(ch);
        return ch;
    }

    public IReadOnlyList<SimulatedChannel> For(string slotId) =>
        _created.TryGetValue(slotId, out var q) ? q.ToArray() : [];
}

internal static class TestSupport
{
    /// <summary>快速、确定、无随机故障的模拟设备配置。</summary>
    public static SimulatedChannelOptions FastSim(int seed = 1) => new()
    {
        Seed = seed,
        MinDelayMs = 1,
        MaxDelayMs = 3,
        ConnectDelayMs = 5,
        EraseDelayMs = 10,
        MeasureDelayMs = 2,
    };

    public static DeviceSlot Slot(int i, SimulatedChannelOptions? sim = null) => new()
    {
        Id = $"S{i:00}",
        Name = $"工位 {i}",
        Channel = sim ?? FastSim(i),
    };

    public static List<DeviceSlot> Slots(int count, Func<int, SimulatedChannelOptions>? sim = null) =>
        Enumerable.Range(1, count).Select(i => Slot(i, sim?.Invoke(i))).ToList();

    /// <summary>短重试间隔、短超时的固件烧录示例配置。</summary>
    public static FlashOptions FastFlash() => new()
    {
        BlockSize = 1024,
        BlockTimeout = TimeSpan.FromSeconds(1),
        Connect = new() { Timeout = TimeSpan.FromSeconds(2), MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(5) },
        EnterBootloader = new() { Timeout = TimeSpan.FromSeconds(1), MaxRetries = 2, RetryDelay = TimeSpan.FromMilliseconds(5) },
        Erase = new() { Timeout = TimeSpan.FromSeconds(1), MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(5) },
        Write = new() { MaxRetries = 3, RetryDelay = TimeSpan.FromMilliseconds(5) },
        Verify = new() { Timeout = TimeSpan.FromSeconds(1), MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(5) },
        Reboot = new() { Timeout = TimeSpan.FromSeconds(1), MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(5) },
    };

    public static FirmwareImage SmallFirmware { get; } = FirmwareImage.CreateDemo(16 * 1024);

    public static DeviceManager Manager(
        IEnumerable<DeviceSlot> slots,
        IJob job,
        int maxConcurrency = 8,
        IChannelFactory? factory = null,
        IResultStore? results = null) =>
        new(slots, _ => job, factory, results, options: new DeviceManagerOptions { MaxConcurrency = maxConcurrency });

    public static IJob FlashAndTest(FlashOptions? flash = null) =>
        new CompositeJob("烧录+测试",
            new DemoFlashJob(SmallFirmware, flash ?? FastFlash()),
            new DemoTestJob(DemoTestJob.DefaultItems,
                new StepPolicy { Timeout = TimeSpan.FromSeconds(1), MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(5) }));

    /// <summary>等待某个工位的快照满足条件（事件驱动，带超时）。</summary>
    public static Task WaitForAsync(DeviceManager manager, string id, Func<DeviceSnapshot, bool> predicate,
        TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.SessionChanged += (_, s) =>
        {
            if (s.Id == id && predicate(s)) tcs.TrySetResult();
        };
        if (predicate(manager.Get(id).Snapshot())) tcs.TrySetResult();
        return tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
    }
}
