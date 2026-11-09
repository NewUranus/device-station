namespace DeviceStation.Core.Jobs.Examples;

public sealed record FlashOptions
{
    /// <summary>每块写入的字节数。</summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>单个块的写入超时（写入步骤本身通常不限总时长，而是限制单块）。</summary>
    public TimeSpan BlockTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public StepPolicy Connect { get; init; } = new() { Timeout = TimeSpan.FromSeconds(5), MaxRetries = 2 };
    public StepPolicy EnterBootloader { get; init; } = new() { Timeout = TimeSpan.FromSeconds(3), MaxRetries = 2 };
    public StepPolicy Erase { get; init; } = new() { Timeout = TimeSpan.FromSeconds(10), MaxRetries = 1 };

    /// <summary>写入失败重试时从上次成功的块继续，不从头开始。</summary>
    public StepPolicy Write { get; init; } = new() { MaxRetries = 3 };

    public StepPolicy Verify { get; init; } = new() { Timeout = TimeSpan.FromSeconds(5), MaxRetries = 1 };
    public StepPolicy Reboot { get; init; } = new() { Timeout = TimeSpan.FromSeconds(3), MaxRetries = 1 };
}

/// <summary>
/// 示例作业：固件烧录骨架。连接 → 进 Bootloader → 擦除 → 分块写入（报进度）→ 校验 → 重启。
/// 步骤编排、超时、重试、断点续写、进度都在这里；具体设备协议由子类实现抽象方法。
/// 一个实例可同时服务所有设备（固件共享），每台设备的状态放在 <see cref="DeviceContext"/> 里。
/// </summary>
public abstract class FlashJob : IJob
{
    public const string StepEnterBootloader = "进入 Bootloader";
    public const string StepErase = "擦除";
    public const string StepWrite = "写入";
    public const string StepVerify = "校验";
    public const string StepReboot = "重启";

    protected FlashJob(FirmwareImage firmware, FlashOptions? options = null)
    {
        Firmware = firmware;
        Options = options ?? new FlashOptions();
        if (Options.BlockSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), "BlockSize 必须大于 0");

        Steps =
        [
            new JobStep(new ConnectStep(HandshakeAsync), Options.Connect) { IsConnect = true },
            new JobStep(new DelegateStep(StepEnterBootloader, EnterBootloaderAsync), Options.EnterBootloader),
            new JobStep(new DelegateStep(StepErase, RunEraseAsync), Options.Erase) { Weight = 2 },
            new JobStep(new DelegateStep(StepWrite, RunWriteAsync), Options.Write) { Weight = 10 },
            new JobStep(new DelegateStep(StepVerify, (c, t) => VerifyAsync(c, Firmware, t)), Options.Verify),
            new JobStep(new DelegateStep(StepReboot, RebootAsync), Options.Reboot),
        ];
    }

    public virtual string Name => "固件烧录";

    public FirmwareImage Firmware { get; }

    public FlashOptions Options { get; }

    public IReadOnlyList<JobStep> Steps { get; }

    /// <summary>通道打开后的握手，返回读到的 SN（读不到返回 null）。</summary>
    protected virtual Task<string?> HandshakeAsync(DeviceContext ctx, CancellationToken ct) => Task.FromResult<string?>(null);

    // TODO(协议)：发送进入 Bootloader 的命令/时序（例如拉 BOOT 脚复位、发送魔术字），并等待 Bootloader 应答。
    protected abstract Task EnterBootloaderAsync(DeviceContext ctx, CancellationToken ct);

    // TODO(协议)：按固件大小擦除对应扇区。整片擦除通常较慢，注意配合 FlashOptions.Erase 的超时。
    protected abstract Task EraseAsync(DeviceContext ctx, FirmwareImage firmware, CancellationToken ct);

    // TODO(协议)：写入一块数据。实现应当是幂等的：同一偏移重复写入同样的数据不应出错（续写会重发失败块）。
    protected abstract Task WriteBlockAsync(DeviceContext ctx, int offset, ReadOnlyMemory<byte> block, CancellationToken ct);

    // TODO(协议)：让设备计算已写入区域的校验值并与 firmware.Crc32（或 SHA-256）比较，不一致时抛异常。
    protected abstract Task VerifyAsync(DeviceContext ctx, FirmwareImage firmware, CancellationToken ct);

    // TODO(协议)：退出 Bootloader 并重启进入应用程序。
    protected abstract Task RebootAsync(DeviceContext ctx, CancellationToken ct);

    private async Task RunEraseAsync(DeviceContext ctx, CancellationToken ct)
    {
        await EraseAsync(ctx, Firmware, ct).ConfigureAwait(false);
        ctx.GetOrAdd(() => new WriteCursor()).NextOffset = 0; // 重新擦除后必须从头写
    }

    private async Task RunWriteAsync(DeviceContext ctx, CancellationToken ct)
    {
        var cursor = ctx.GetOrAdd(() => new WriteCursor());
        var data = Firmware.Data;
        int total = data.Length;

        if (cursor.NextOffset > 0)
            ctx.ReportStepProgress((double)cursor.NextOffset / total, $"从 {cursor.NextOffset / 1024} KB 处续写");

        while (cursor.NextOffset < total)
        {
            ct.ThrowIfCancellationRequested();
            int offset = cursor.NextOffset;
            int length = Math.Min(Options.BlockSize, total - offset);

            using (var blockCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                blockCts.CancelAfter(Options.BlockTimeout);
                try
                {
                    await WriteBlockAsync(ctx, offset, data.Slice(offset, length), blockCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"写入 0x{offset:X6} 处的块超时（{Options.BlockTimeout.TotalMilliseconds:0} ms）");
                }
            }

            cursor.NextOffset = offset + length;
            ctx.ReportStepProgress((double)cursor.NextOffset / total, $"{cursor.NextOffset / 1024}/{total / 1024} KB");
        }
    }

    /// <summary>每台设备的写入进度（断点续写用）。</summary>
    private sealed class WriteCursor
    {
        public int NextOffset { get; set; }
    }
}
