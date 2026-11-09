namespace DeviceStation.Core.Jobs;

/// <summary>作业中的一个步骤。实现必须响应 ExecuteAsync 的 CancellationToken（超时和停止都通过它传达）。</summary>
public interface IJobStep
{
    string Name { get; }

    Task ExecuteAsync(DeviceContext ctx, CancellationToken ct);
}

/// <summary>步骤的超时与重试策略。</summary>
public sealed record StepPolicy
{
    public static StepPolicy Default { get; } = new();

    /// <summary>单次尝试的超时；为空表示不限时（仍可被停止）。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>失败后最多再重试几次（0 表示不重试）。</summary>
    public int MaxRetries { get; init; }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>作业中配置好的一步：步骤本身 + 策略 + 进度权重。</summary>
public sealed record JobStep(IJobStep Step, StepPolicy Policy)
{
    /// <summary>该步在整体进度条中所占权重（写入固件这种长步骤可以给大一些）。</summary>
    public double Weight { get; init; } = 1;

    /// <summary>为 true 时设备状态显示为 Connecting，否则显示为 Running。</summary>
    public bool IsConnect { get; init; }

    public string Name => Step.Name;
}

public interface IJob
{
    string Name { get; }

    IReadOnlyList<JobStep> Steps { get; }
}

/// <summary>由一组步骤直接组成的作业。</summary>
public sealed class Job(string name, IEnumerable<JobStep> steps) : IJob
{
    public string Name { get; } = name;

    public IReadOnlyList<JobStep> Steps { get; } = steps.ToList();
}

/// <summary>按顺序串联多个作业，例如示例里的“固件烧录 + 功能测试”。</summary>
public sealed class CompositeJob(string name, params IJob[] jobs) : IJob
{
    public string Name { get; } = name;

    public IReadOnlyList<JobStep> Steps { get; } = jobs.SelectMany(j => j.Steps).ToList();
}

/// <summary>用委托实现的步骤。</summary>
public sealed class DelegateStep(string name, Func<DeviceContext, CancellationToken, Task> body) : IJobStep
{
    public string Name { get; } = name;

    public Task ExecuteAsync(DeviceContext ctx, CancellationToken ct) => body(ctx, ct);
}

/// <summary>
/// 打开通道（已打开则跳过），然后可选地执行握手（例如读 SN）。
/// 示例作业都以它开头，串联时后面的作业会直接复用已打开的通道。
/// </summary>
public sealed class ConnectStep(Func<DeviceContext, CancellationToken, Task<string?>>? handshake = null) : IJobStep
{
    public const string StepName = "连接";

    public string Name => StepName;

    public async Task ExecuteAsync(DeviceContext ctx, CancellationToken ct)
    {
        if (!ctx.Channel.IsOpen)
            await ctx.Channel.OpenAsync(ct).ConfigureAwait(false);

        if (handshake is not null)
        {
            var sn = await handshake(ctx, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(sn)) ctx.SerialNumber = sn;
        }
    }
}

/// <summary>步骤在所有重试用尽后仍然失败。</summary>
public sealed class StepFailedException(string stepName, int attempts, Exception inner)
    : Exception($"{stepName} 失败（尝试 {attempts} 次）：{inner.Message}", inner)
{
    public string StepName { get; } = stepName;
    public int Attempts { get; } = attempts;
}
