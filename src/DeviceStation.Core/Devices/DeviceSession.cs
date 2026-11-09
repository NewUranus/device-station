namespace DeviceStation.Core.Devices;

public enum DeviceState
{
    Idle,
    /// <summary>已请求开始，正在等待并发名额（<see cref="Execution.DeviceManagerOptions.MaxConcurrency"/>）。</summary>
    Queued,
    Connecting,
    Running,
    Passed,
    Failed,
    Cancelled,
}

public static class DeviceStateExtensions
{
    public static bool IsBusy(this DeviceState s) => s is DeviceState.Queued or DeviceState.Connecting or DeviceState.Running;

    public static bool IsTerminal(this DeviceState s) => s is DeviceState.Passed or DeviceState.Failed or DeviceState.Cancelled;
}

/// <summary>某一时刻某台设备状态的不可变快照，可以安全地跨线程传递给 UI。</summary>
public sealed record DeviceSnapshot(
    string Id,
    string Name,
    DeviceState State,
    Guid RunId,
    string? CurrentStep,
    string? StepDetail,
    double Progress,
    int Attempt,
    string? SerialNumber,
    string? FailedStep,
    string? Message,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt)
{
    public TimeSpan? Elapsed => StartedAt is { } s ? (EndedAt ?? DateTimeOffset.Now) - s : null;

    /// <summary>当前（或停下时所在）步骤在作业中的序号；未开始任何步骤时为 -1。</summary>
    public int StepIndex { get; init; } = -1;

    /// <summary>作业的步骤总数；未开始时为 0。</summary>
    public int StepCount { get; init; }

    /// <summary>当前步骤内的进度（0~1）。</summary>
    public double StepFraction { get; init; }
}

/// <summary>
/// 一个工位的运行时状态。每台设备一个实例，持有自己独立的 <see cref="CancellationTokenSource"/>，
/// 所以停止一台不会影响其它设备。状态由 <see cref="Execution.DeviceManager"/> 在后台线程更新，
/// 外部只通过 <see cref="Snapshot"/> 或 <see cref="Changed"/> 读取。
/// </summary>
public sealed class DeviceSession
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    private DeviceState _state = DeviceState.Idle;
    private Guid _runId;
    private string? _currentStep;
    private string? _stepDetail;
    private double _progress;
    private int _stepIndex = -1;
    private int _stepCount;
    private double _stepFraction;
    private int _attempt;
    private string? _serialNumber;
    private string? _failedStep;
    private string? _message;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;

    public DeviceSession(DeviceSlot slot) => Slot = slot;

    public DeviceSlot Slot { get; }

    public string Id => Slot.Id;

    /// <summary>状态变化时在后台线程触发。订阅方不要在回调里做重活（UI 应节流）。</summary>
    public event Action<DeviceSnapshot>? Changed;

    public DeviceState State
    {
        get { lock (_gate) return _state; }
    }

    public string? SerialNumber
    {
        get { lock (_gate) return _serialNumber; }
    }

    public DeviceSnapshot Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    /// <summary>请求停止本设备当前的运行（排队中、连接中、运行中均可）。空闲时无操作。</summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _state.IsBusy() ? _cts : null;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* 运行刚好结束 */ }
    }

    /// <summary>原子地从非忙碌状态切到 Queued；已在运行时返回 false。</summary>
    internal bool TryBeginRun(out Guid runId, out CancellationToken token)
    {
        DeviceSnapshot snap;
        lock (_gate)
        {
            if (_state.IsBusy())
            {
                runId = _runId;
                token = default;
                return false;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            runId = _runId = Guid.NewGuid();
            _state = DeviceState.Queued;
            _currentStep = "等待空闲名额";
            _stepDetail = null;
            _progress = 0;
            _stepIndex = -1;
            _stepCount = 0;
            _stepFraction = 0;
            _attempt = 0;
            _serialNumber = null;
            _failedStep = null;
            _message = null;
            _startedAt = DateTimeOffset.Now;
            _endedAt = null;
            snap = SnapshotLocked();
        }
        Changed?.Invoke(snap);
        return true;
    }

    internal void BeginStep(string name, int index, int count, DeviceState state, double progress) =>
        Update(() =>
        {
            if (_state == DeviceState.Queued) _startedAt = DateTimeOffset.Now; // 用时从拿到名额开始算，不含排队
            _state = state;
            _currentStep = name;
            _stepIndex = index;
            _stepCount = count;
            _stepFraction = 0;
            _stepDetail = null;
            _attempt = 1;
            _progress = progress;
        });

    internal void SetAttempt(int attempt, string? detail) => Update(() =>
    {
        _attempt = attempt;
        _stepDetail = detail;
    });

    internal void SetProgress(double progress, double stepFraction, string? detail) => Update(() =>
    {
        _progress = Math.Clamp(progress, 0, 1);
        _stepFraction = Math.Clamp(stepFraction, 0, 1);
        if (detail is not null) _stepDetail = detail;
    });

    internal void SetSerialNumber(string? sn) => Update(() => _serialNumber = sn);

    internal void Finish(DeviceState state, string? failedStep, string? message) => Update(() =>
    {
        if (_state == DeviceState.Queued) _startedAt = null; // 没真正开始过，不显示用时
        _state = state;
        _failedStep = failedStep;
        _message = message;
        _stepDetail = null;
        _endedAt = DateTimeOffset.Now;
        if (state == DeviceState.Passed)
        {
            _progress = 1;
            _stepIndex = _stepCount;
        }
        _currentStep = state switch
        {
            DeviceState.Passed => "完成",
            DeviceState.Cancelled => "已停止",
            _ => failedStep ?? _currentStep,
        };
    });

    private void Update(Action mutate)
    {
        DeviceSnapshot snap;
        lock (_gate)
        {
            mutate();
            snap = SnapshotLocked();
        }
        Changed?.Invoke(snap);
    }

    private DeviceSnapshot SnapshotLocked() => new(
        Slot.Id, Slot.Name, _state, _runId, _currentStep, _stepDetail, _progress, _attempt,
        _serialNumber, _failedStep, _message, _startedAt, _endedAt)
    {
        StepIndex = _stepIndex,
        StepCount = _stepCount,
        StepFraction = _stepFraction,
    };
}
