using Microsoft.Extensions.Logging;

namespace DeviceStation.Core.Jobs.Examples;

/// <summary>一个测试项：测量一个值，按 [Low, High] 判定。</summary>
public sealed record TestItem(string Name, double Low, double High, string Unit = "")
{
    /// <summary>协议相关的测量命令/通道号；为空时由具体实现按 <see cref="Name"/> 决定。</summary>
    public string? Command { get; init; }

    /// <summary>本项的超时/重试策略；为空时用作业的默认策略。</summary>
    public StepPolicy? Policy { get; init; }

    public bool IsWithinLimits(double value) => value >= Low && value <= High;
}

public sealed class LimitCheckFailedException(TestItem item, double value)
    : Exception($"{item.Name} = {value:G6}{item.Unit} 超出范围 [{item.Low:G6}, {item.High:G6}]{item.Unit}")
{
    public TestItem Item { get; } = item;
    public double Value { get; } = value;
}

/// <summary>
/// 功能测试作业：每个测试项是一步，测量 → 按上下限判定 → 写结果。遇到不合格项立即停止（产线常用的 stop-on-fail）。
/// 超限也会按策略重试（相当于复测），每次测量都会写一条记录便于追溯。
/// </summary>
public abstract class TestJob : IJob
{
    public static readonly StepPolicy DefaultItemPolicy = new() { Timeout = TimeSpan.FromSeconds(3), MaxRetries = 1 };

    protected TestJob(IEnumerable<TestItem> items, StepPolicy? defaultItemPolicy = null, StepPolicy? connectPolicy = null)
    {
        Items = items.ToList();
        var itemPolicy = defaultItemPolicy ?? DefaultItemPolicy;

        var steps = new List<JobStep>
        {
            new(new ConnectStep(HandshakeAsync), connectPolicy ?? new StepPolicy { Timeout = TimeSpan.FromSeconds(5), MaxRetries = 2 })
            {
                IsConnect = true,
            },
        };
        steps.AddRange(Items.Select(item =>
            new JobStep(new DelegateStep(item.Name, (ctx, ct) => RunItemAsync(ctx, item, ct)), item.Policy ?? itemPolicy)));
        Steps = steps;
    }

    public virtual string Name => "功能测试";

    public IReadOnlyList<TestItem> Items { get; }

    public IReadOnlyList<JobStep> Steps { get; }

    protected virtual Task<string?> HandshakeAsync(DeviceContext ctx, CancellationToken ct) => Task.FromResult<string?>(null);

    // TODO(协议)：通过治具/仪器读取测量值（ADC、电流表、频率计……），返回以 item.Unit 为单位的数值。
    protected abstract Task<double> MeasureAsync(DeviceContext ctx, TestItem item, CancellationToken ct);

    private async Task RunItemAsync(DeviceContext ctx, TestItem item, CancellationToken ct)
    {
        double value = await MeasureAsync(ctx, item, ct).ConfigureAwait(false);
        bool pass = item.IsWithinLimits(value);
        ctx.Record(item.Name, pass, value, item.Low, item.High, item.Unit, pass ? null : "超限");
        ctx.ReportStepProgress(1, $"{value:G6} {item.Unit}");

        if (!pass) throw new LimitCheckFailedException(item, value);
        ctx.Logger.LogDebug("{Item} = {Value} {Unit} 合格", item.Name, value, item.Unit);
    }
}
