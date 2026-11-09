using System.Collections.Concurrent;
using DeviceStation.Core.Channels;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Jobs;
using DeviceStation.Core.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceStation.Core.Execution;

public sealed record DeviceManagerOptions
{
    /// <summary>同时处于“连接/运行”状态的设备上限（受 USB Hub 带宽、电源、上位机串口数等限制）。</summary>
    public int MaxConcurrency { get; init; } = 8;
}

/// <summary>
/// 多工位调度器。每台设备一个独立的运行任务、独立的 CancellationTokenSource、独立的通道实例；
/// 用 <see cref="SemaphoreSlim"/> 限制同时运行的数量。任何一台的异常、超时、取消都只落在它自己的会话上。
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly List<DeviceSession> _sessions;
    private readonly Dictionary<string, DeviceSession> _byId;
    private readonly ConcurrentDictionary<string, Task<DeviceSnapshot>> _runs = new();
    private readonly Func<DeviceSlot, IJob> _jobFactory;
    private readonly IChannelFactory _channelFactory;
    private readonly IResultStore _results;
    private readonly ILogger _logger;
    private readonly IProgress<DeviceSnapshot>? _progress;
    private readonly object _gateLock = new();
    private SemaphoreSlim _gate;
    private int _maxConcurrency;

    public DeviceManager(
        IEnumerable<DeviceSlot> slots,
        Func<DeviceSlot, IJob> jobFactory,
        IChannelFactory? channelFactory = null,
        IResultStore? results = null,
        ILoggerFactory? loggerFactory = null,
        DeviceManagerOptions? options = null,
        IProgress<DeviceSnapshot>? progress = null)
    {
        _sessions = slots.Select(s => new DeviceSession(s)).ToList();
        _byId = _sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        _jobFactory = jobFactory;
        _channelFactory = channelFactory ?? DefaultChannelFactory.Instance;
        _results = results ?? new InMemoryResultStore();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("DeviceStation.Device");
        _progress = progress;

        _maxConcurrency = Math.Max(1, (options ?? new DeviceManagerOptions()).MaxConcurrency);
        _gate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

        foreach (var s in _sessions) s.Changed += OnSessionChanged;
    }

    /// <summary>任何设备状态变化时触发（后台线程）。</summary>
    public event EventHandler<DeviceSnapshot>? SessionChanged;

    public IReadOnlyList<DeviceSession> Sessions => _sessions;

    public bool IsAnyBusy => _sessions.Any(s => s.State.IsBusy());

    /// <summary>并发上限。只能在没有设备运行时修改。</summary>
    public int MaxConcurrency
    {
        get { lock (_gateLock) return _maxConcurrency; }
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gateLock)
            {
                if (value == _maxConcurrency) return;
                if (IsAnyBusy) throw new InvalidOperationException("有设备正在运行，不能修改并发上限");
                _gate = new SemaphoreSlim(value, value);
                _maxConcurrency = value;
            }
        }
    }

    public DeviceSession Get(string id) =>
        _byId.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"没有工位 {id}");

    /// <summary>
    /// 开始一台设备。已在运行时返回正在进行的那次运行。返回的任务不会因设备失败而抛异常，
    /// 结果看返回快照的 <see cref="DeviceSnapshot.State"/>。
    /// </summary>
    public Task<DeviceSnapshot> Start(string id)
    {
        var session = Get(id);
        SemaphoreSlim gate;
        Guid runId;
        CancellationToken token;

        lock (_gateLock)
        {
            if (!session.TryBeginRun(out runId, out token))
                return _runs.TryGetValue(id, out var existing) ? existing : Task.FromResult(session.Snapshot());
            gate = _gate;
            var task = Task.Run(() => RunAsync(session, runId, gate, token));
            _runs[id] = task;
            return task;
        }
    }

    /// <summary>开始所有启用的工位，返回全部完成时结束的任务。</summary>
    public Task<DeviceSnapshot[]> StartAll() =>
        Task.WhenAll(_sessions.Where(s => s.Slot.Enabled).Select(s => Start(s.Id)).ToList());

    public void Stop(string id) => Get(id).Cancel();

    public void StopAll()
    {
        foreach (var s in _sessions) s.Cancel();
    }

    /// <summary>等待当前所有运行结束。</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_runs.Values.ToArray());

    private async Task<DeviceSnapshot> RunAsync(DeviceSession session, Guid runId, SemaphoreSlim gate, CancellationToken ct)
    {
        using var scope = _logger.BeginScope("DeviceId:{DeviceId} RunId:{RunId}", session.Id, runId.ToString("N")[..8]);
        bool acquired = false;
        IDeviceChannel? channel = null;
        DeviceContext? ctx = null;
        DeviceState final;
        string? failedStep = null;
        string? message;

        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            var job = _jobFactory(session.Slot);
            channel = _channelFactory.Create(session.Id, session.Slot.Channel);
            ctx = new DeviceContext(session, channel, _logger, _results, runId);
            _logger.LogInformation("开始 {Job}，通道 {Channel}", job.Name, channel.Description);

            (final, failedStep, message) = await RunStepsAsync(job, session, ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            final = DeviceState.Cancelled;
            message = acquired ? "用户停止" : "排队时被停止";
            _logger.LogWarning("已停止（{Where}）", message);
        }
        catch (Exception ex)
        {
            // 作业之外的意外（例如通道工厂抛异常），同样只影响本设备。
            final = DeviceState.Failed;
            message = ex.Message;
            _logger.LogError(ex, "运行异常");
        }
        finally
        {
            // 先关闭通道、归还名额，再把状态切到终态：保证 UI 上看到“完成”后立刻重新开始时，串口已经释放。
            if (channel is not null)
            {
                try { await channel.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "关闭通道时出错"); }
            }
            if (acquired) gate.Release();
        }

        if (ctx is not null)
        {
            _results.Add(new ResultRecord(runId, session.SerialNumber, session.Id, session.Slot.Name, "RESULT", null, null,
                null, null, final == DeviceState.Passed, final == DeviceState.Passed ? null : $"{final}: {message}",
                DateTimeOffset.Now));
        }
        session.Finish(final, failedStep, message);
        return session.Snapshot();
    }

    private async Task<(DeviceState, string?, string?)> RunStepsAsync(IJob job, DeviceSession session, DeviceContext ctx,
        CancellationToken ct)
    {
        double totalWeight = Math.Max(job.Steps.Sum(s => s.Weight), double.Epsilon);
        double doneWeight = 0;

        for (int i = 0; i < job.Steps.Count; i++)
        {
            var step = job.Steps[i];
            ct.ThrowIfCancellationRequested();
            double baseWeight = doneWeight, weight = step.Weight;
            session.BeginStep(step.Name, i, job.Steps.Count, step.IsConnect ? DeviceState.Connecting : DeviceState.Running,
                baseWeight / totalWeight);
            ctx.StepProgressSink = (f, detail) =>
                session.SetProgress((baseWeight + weight * Math.Clamp(f, 0, 1)) / totalWeight, f, detail);
            ctx.StepRecorded = false;

            try
            {
                await StepRunner.RunAsync(step, ctx, session, ct).ConfigureAwait(false);
                if (!ctx.StepRecorded) ctx.Record(step.Name, passed: true);
            }
            catch (StepFailedException ex)
            {
                // 测试项超限时 TestJob 已经写过带测量值的记录，这里不再重复写。
                if (!ctx.StepRecorded || ex.InnerException is not Jobs.Examples.LimitCheckFailedException)
                    ctx.Record(step.Name, passed: false, message: ex.InnerException?.Message);
                _logger.LogError("{Message}", ex.Message);
                return (DeviceState.Failed, step.Name, ex.Message);
            }

            doneWeight += weight;
        }

        _logger.LogInformation("通过，SN={SerialNumber}，用时 {Elapsed:0.0} s", session.SerialNumber,
            session.Snapshot().Elapsed?.TotalSeconds);
        return (DeviceState.Passed, null, "全部步骤通过");
    }

    private void OnSessionChanged(DeviceSnapshot snapshot)
    {
        SessionChanged?.Invoke(this, snapshot);
        _progress?.Report(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        StopAll();
        try { await WhenIdleAsync().ConfigureAwait(false); }
        catch { /* 运行任务内部已处理异常 */ }
        foreach (var s in _sessions) s.Changed -= OnSessionChanged;
    }
}
