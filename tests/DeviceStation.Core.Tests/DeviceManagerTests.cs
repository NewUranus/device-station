using DeviceStation.Core.Devices;
using DeviceStation.Core.Jobs;
using DeviceStation.Core.Jobs.Examples;
using DeviceStation.Core.Results;
using static DeviceStation.Core.Tests.TestSupport;

namespace DeviceStation.Core.Tests;

public class DeviceManagerTests
{
    [Fact]
    public async Task AllDevicesPass_WithFlashAndTest()
    {
        var results = new InMemoryResultStore();
        await using var manager = Manager(Slots(8), FlashAndTest(), results: results);

        var snaps = await manager.StartAll();

        Assert.All(snaps, s => Assert.Equal(DeviceState.Passed, s.State));
        Assert.All(snaps, s => Assert.StartsWith("SIM", s.SerialNumber));
        Assert.Equal(8, snaps.Select(s => s.SerialNumber).Distinct().Count());
        // 每台一条 RESULT 记录，且测量记录带上了 SN
        Assert.Equal(8, results.Records.Count(r => r.Step == "RESULT" && r.Passed));
        Assert.All(results.Records.Where(r => r.Value is not null), r => Assert.NotNull(r.SerialNumber));
    }

    [Fact]
    public async Task ConcurrencyLimit_IsRespected()
    {
        int current = 0, peak = 0;
        var probe = new DelegateStep("占用", async (ctx, ct) =>
        {
            int now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            try { await Task.Delay(80, ct); }
            finally { Interlocked.Decrement(ref current); }
        });
        var job = new Job("probe",
        [
            new JobStep(new ConnectStep(), StepPolicy.Default) { IsConnect = true },
            new JobStep(probe, StepPolicy.Default),
        ]);

        await using var manager = Manager(Slots(8), job, maxConcurrency: 3);
        var startAll = manager.StartAll();

        // 刚开始时应有设备处于排队状态
        await Task.Delay(30);
        Assert.Contains(manager.Sessions, s => s.State == DeviceState.Queued);

        var snaps = await startAll;
        Assert.All(snaps, s => Assert.Equal(DeviceState.Passed, s.State));
        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task StoppingOneDevice_DoesNotAffectOthers()
    {
        var factory = new RecordingChannelFactory();
        // 放慢所有设备，确保停止 S02 时其它设备都还在运行
        var slots = Slots(4, i => FastSim(i) with { MinDelayMs = 15, MaxDelayMs = 20 });
        await using var manager = Manager(slots, FlashAndTest(), factory: factory);

        var all = manager.StartAll();
        await WaitForAsync(manager, "S02", s => s.CurrentStep == FlashJob.StepWrite && s.Progress > 0.3);
        Assert.All(manager.Sessions, s => Assert.True(s.State.IsBusy()));

        manager.Stop("S02");
        var snaps = (await all).ToDictionary(s => s.Id);

        Assert.Equal(DeviceState.Cancelled, snaps["S02"].State);
        var s02 = factory.For("S02")[0];
        Assert.Contains("WRITE", s02.ReceivedCommands);
        Assert.DoesNotContain("VERIFY", s02.ReceivedCommands); // 停在写入中途，没有继续往下走
        Assert.False(factory.For("S02")[0].IsOpen); // 停止后通道已关闭
        foreach (var id in new[] { "S01", "S03", "S04" })
            Assert.Equal(DeviceState.Passed, snaps[id].State);
    }

    [Fact]
    public async Task FailingDevice_IsIsolated()
    {
        var slots = Slots(5, i => i == 3 ? FastSim(i) with { FailCommands = ["ERASE"] } : FastSim(i));
        var results = new InMemoryResultStore();
        await using var manager = Manager(slots, FlashAndTest(), results: results);

        var snaps = (await manager.StartAll()).ToDictionary(s => s.Id);

        Assert.Equal(DeviceState.Failed, snaps["S03"].State);
        Assert.Equal(FlashJob.StepErase, snaps["S03"].FailedStep);
        Assert.Contains("ERASE", snaps["S03"].Message);
        Assert.All(snaps.Values.Where(s => s.Id != "S03"), s => Assert.Equal(DeviceState.Passed, s.State));
        Assert.Contains(results.Records, r => r.SlotId == "S03" && r.Step == FlashJob.StepErase && !r.Passed);
    }

    [Fact]
    public async Task ExceptionOutsideJob_IsIsolated()
    {
        // 作业工厂对某个工位直接抛异常，也只影响该工位
        var job = FlashAndTest();
        await using var manager = new Core.Execution.DeviceManager(Slots(3),
            slot => slot.Id == "S02" ? throw new InvalidOperationException("配置错误") : job);

        var snaps = (await manager.StartAll()).ToDictionary(s => s.Id);

        Assert.Equal(DeviceState.Failed, snaps["S02"].State);
        Assert.Equal("配置错误", snaps["S02"].Message);
        Assert.Equal(DeviceState.Passed, snaps["S01"].State);
        Assert.Equal(DeviceState.Passed, snaps["S03"].State);
    }

    [Fact]
    public async Task TransientFailure_IsRetried_UntilSuccess()
    {
        var factory = new RecordingChannelFactory();
        var slots = new[] { Slot(1, FastSim() with { TransientFailures = new Dictionary<string, int> { ["BOOT"] = 2 } }) };
        await using var manager = Manager(slots, FlashAndTest(), factory: factory);

        var snap = await manager.Start("S01");

        Assert.Equal(DeviceState.Passed, snap.State);
        Assert.Equal(3, factory.For("S01")[0].ReceivedCommands.Count(c => c == "BOOT")); // 失败 2 次 + 成功 1 次
    }

    [Fact]
    public async Task TransientFailure_FailsWhenRetriesExhausted()
    {
        var slots = new[] { Slot(1, FastSim() with { TransientFailures = new Dictionary<string, int> { ["BOOT"] = 3 } }) };
        await using var manager = Manager(slots, FlashAndTest()); // EnterBootloader 最多重试 2 次

        var snap = await manager.Start("S01");

        Assert.Equal(DeviceState.Failed, snap.State);
        Assert.Equal(FlashJob.StepEnterBootloader, snap.FailedStep);
        Assert.Contains("尝试 3 次", snap.Message);
    }

    [Fact]
    public async Task WriteRetry_ResumesFromLastGoodBlock()
    {
        var factory = new RecordingChannelFactory();
        var slots = new[] { Slot(1, FastSim() with { TransientFailures = new Dictionary<string, int> { ["WRITE"] = 2 } }) };
        await using var manager = Manager(slots, FlashAndTest(), factory: factory);

        var snap = await manager.Start("S01");

        Assert.Equal(DeviceState.Passed, snap.State); // VERIFY 的 CRC 通过，说明续写没有错位
        int blocks = SmallFirmware.Length / FastFlash().BlockSize;
        Assert.Equal(blocks + 2, factory.For("S01")[0].ReceivedCommands.Count(c => c == "WRITE"));
    }

    [Fact]
    public async Task UnresponsiveDevice_TimesOut_AsFailureNotCancel()
    {
        var slots = new[] { Slot(1, FastSim() with { HangCommands = ["ERASE"] }) };
        var flash = FastFlash() with
        {
            Erase = new StepPolicy { Timeout = TimeSpan.FromMilliseconds(150), MaxRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(5) },
        };
        await using var manager = Manager(slots, FlashAndTest(flash));

        var started = DateTimeOffset.Now;
        var snap = await manager.Start("S01");

        Assert.Equal(DeviceState.Failed, snap.State);
        Assert.Equal(FlashJob.StepErase, snap.FailedStep);
        Assert.Contains("超时", snap.Message);
        Assert.True(DateTimeOffset.Now - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task StoppingQueuedDevice_CancelsWithoutOpeningChannel()
    {
        var factory = new RecordingChannelFactory();
        var hold = new DelegateStep("占用", (_, ct) => Task.Delay(300, ct));
        var job = new Job("hold", [new JobStep(new ConnectStep(), StepPolicy.Default) { IsConnect = true }, new JobStep(hold, StepPolicy.Default)]);
        await using var manager = Manager(Slots(2), job, maxConcurrency: 1, factory: factory);

        var first = manager.Start("S01");
        await WaitForAsync(manager, "S01", s => s.State == DeviceState.Running);
        var second = manager.Start("S02");
        Assert.Equal(DeviceState.Queued, manager.Get("S02").State);

        manager.Stop("S02");

        Assert.Equal(DeviceState.Cancelled, (await second).State);
        Assert.Empty(factory.For("S02"));
        Assert.Equal(DeviceState.Passed, (await first).State);
    }

    [Fact]
    public async Task OutOfSpecMeasurement_FailsTestStep_WithMeasuredValueRecorded()
    {
        var results = new InMemoryResultStore();
        var slots = new[] { Slot(1, FastSim() with { OutOfSpecRate = 1.0 }) };
        await using var manager = Manager(slots, FlashAndTest(), results: results);

        var snap = await manager.Start("S01");

        Assert.Equal(DeviceState.Failed, snap.State);
        Assert.Equal("VBAT", snap.FailedStep);
        var vbat = results.Records.Where(r => r.Step == "VBAT").ToList();
        Assert.Equal(2, vbat.Count); // 首测 + 复测各一条
        Assert.All(vbat, r => { Assert.False(r.Passed); Assert.NotNull(r.Value); Assert.Equal(3.20, r.Low); });
    }

    [Fact]
    public async Task Device_CanBeRestarted_AfterFinishing()
    {
        await using var manager = Manager(Slots(1), FlashAndTest());

        var first = await manager.Start("S01");
        var second = await manager.Start("S01");

        Assert.Equal(DeviceState.Passed, first.State);
        Assert.Equal(DeviceState.Passed, second.State);
        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public async Task StartWhileRunning_ReturnsSameRun()
    {
        var slots = Slots(1, i => FastSim(i) with { MinDelayMs = 10, MaxDelayMs = 10 });
        await using var manager = Manager(slots, FlashAndTest());

        var a = manager.Start("S01");
        var b = manager.Start("S01");

        Assert.Same(a, b);
        Assert.Equal(DeviceState.Passed, (await a).State);
    }

    [Fact]
    public async Task StopAll_CancelsEveryBusyDevice()
    {
        var slots = Slots(4, i => FastSim(i) with { MinDelayMs = 20, MaxDelayMs = 20 });
        await using var manager = Manager(slots, FlashAndTest(), maxConcurrency: 2);

        var all = manager.StartAll();
        await WaitForAsync(manager, "S01", s => s.State == DeviceState.Running);
        manager.StopAll();

        Assert.All(await all, s => Assert.Equal(DeviceState.Cancelled, s.State));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
