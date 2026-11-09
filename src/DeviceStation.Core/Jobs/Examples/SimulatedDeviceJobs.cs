using System.Globalization;
using DeviceStation.Core.Protocol;

namespace DeviceStation.Core.Jobs.Examples;

/// <summary>演示协议（<see cref="Channels.SimulatedChannel"/> 能听懂的 ASCII 行协议）的共用部分。</summary>
internal static class DemoProtocol
{
    /// <summary>取本次运行的协议客户端；重试时丢掉上一次尝试残留的数据。</summary>
    public static AsciiLineClient Client(DeviceContext ctx)
    {
        var client = ctx.GetOrAdd(() => new AsciiLineClient(ctx.Channel));
        if (ctx.Attempt > 1) client.DiscardBuffered();
        return client;
    }

    public static async Task<string?> ReadSerialAsync(DeviceContext ctx, CancellationToken ct) =>
        await Client(ctx).QueryAsync("SN?", ct).ConfigureAwait(false);
}

/// <summary>用演示协议实现的固件烧录示例作业。</summary>
public sealed class DemoFlashJob(FirmwareImage firmware, FlashOptions? options = null) : FlashJob(firmware, options)
{
    protected override async Task<string?> HandshakeAsync(DeviceContext ctx, CancellationToken ct) =>
        await DemoProtocol.ReadSerialAsync(ctx, ct).ConfigureAwait(false);

    protected override Task EnterBootloaderAsync(DeviceContext ctx, CancellationToken ct) =>
        DemoProtocol.Client(ctx).QueryAsync("BOOT", ct);

    protected override Task EraseAsync(DeviceContext ctx, FirmwareImage fw, CancellationToken ct) =>
        DemoProtocol.Client(ctx).QueryAsync($"ERASE {fw.Length}", ct);

    protected override Task WriteBlockAsync(DeviceContext ctx, int offset, ReadOnlyMemory<byte> block, CancellationToken ct) =>
        DemoProtocol.Client(ctx).QueryAsync($"WRITE {offset} {Convert.ToBase64String(block.Span)}", ct);

    protected override Task VerifyAsync(DeviceContext ctx, FirmwareImage fw, CancellationToken ct) =>
        DemoProtocol.Client(ctx).QueryAsync($"VERIFY {fw.Crc32:X8}", ct);

    protected override Task RebootAsync(DeviceContext ctx, CancellationToken ct) =>
        DemoProtocol.Client(ctx).QueryAsync("RESET", ct);
}

/// <summary>用演示协议实现的功能测试作业（<c>MEAS &lt;name&gt;</c>）。</summary>
public sealed class DemoTestJob(IEnumerable<TestItem> items, StepPolicy? defaultItemPolicy = null)
    : TestJob(items, defaultItemPolicy)
{
    /// <summary>与模拟设备内置测量表对应的一组默认测试项。</summary>
    public static IReadOnlyList<TestItem> DefaultItems { get; } =
    [
        new("VBAT", 3.20, 3.40, "V"),
        new("VCC_IO", 1.75, 1.85, "V"),
        new("I_IDLE", 10.0, 14.0, "mA"),
        new("I_SLEEP", 0.030, 0.070, "mA"),
        new("OSC_FREQ", 32758, 32778, "Hz"),
        new("TEMP", 15, 35, "°C"),
    ];

    protected override async Task<string?> HandshakeAsync(DeviceContext ctx, CancellationToken ct) =>
        ctx.SerialNumber ?? await DemoProtocol.ReadSerialAsync(ctx, ct).ConfigureAwait(false);

    protected override async Task<double> MeasureAsync(DeviceContext ctx, TestItem item, CancellationToken ct)
    {
        var payload = await DemoProtocol.Client(ctx).QueryAsync($"MEAS {item.Command ?? item.Name}", ct).ConfigureAwait(false);
        if (!double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new DeviceProtocolException("MEAS", $"无法解析测量值：{payload}");
        return value;
    }
}
