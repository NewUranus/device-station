using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Execution;

namespace DeviceStation.App.ViewModels;

/// <summary>一张设备卡片。只在 UI 线程上由 <see cref="MainViewModel"/> 的节流定时器更新。</summary>
public sealed partial class DeviceCardViewModel : ObservableObject
{
    private readonly DeviceManager _manager;
    private DeviceSnapshot _snapshot;

    public DeviceCardViewModel(DeviceManager manager, DeviceSnapshot initial, IReadOnlyList<double> stepWeights)
    {
        _manager = manager;
        _snapshot = initial;
        Id = initial.Id;
        Name = initial.Name;
        StepWeights = stepWeights;
        Number = new string(Id.Where(char.IsDigit).ToArray()) is { Length: > 0 } n ? n : Id;
        Apply(initial);
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>卡片左上角的大号工位编号（S01 → 01）。</summary>
    public string Number { get; }

    /// <summary>作业各步骤的进度权重，供分段步骤轨使用。</summary>
    public IReadOnlyList<double> StepWeights { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsBusy), nameof(IsFailed))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private DeviceState _state;

    [ObservableProperty] private string? _currentStep;
    [ObservableProperty] private string? _stepDetail;
    [ObservableProperty] private int _stepIndex = -1;
    [ObservableProperty] private double _stepFraction;
    [ObservableProperty] private string _stepPositionText = "";
    [ObservableProperty] private string _progressText = "0%";
    [ObservableProperty] private string? _serialNumber;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string _elapsedText = "—";
    [ObservableProperty] private int _passCount;
    [ObservableProperty] private int _failCount;

    public bool IsBusy => State.IsBusy();

    public bool IsFailed => State == DeviceState.Failed;

    public string StateText => State switch
    {
        DeviceState.Idle => "空闲",
        DeviceState.Queued => "排队",
        DeviceState.Connecting => "连接中",
        DeviceState.Running => "运行中",
        DeviceState.Passed => "PASS",
        DeviceState.Failed => "FAIL",
        DeviceState.Cancelled => "已停止",
        _ => State.ToString(),
    };

    public void Apply(DeviceSnapshot s)
    {
        _snapshot = s;
        State = s.State;
        CurrentStep = s.CurrentStep ?? "—";
        StepDetail = s.State.IsBusy() && s.Attempt > 1 && s.StepDetail is null ? $"第 {s.Attempt} 次尝试" : s.StepDetail;
        StepIndex = s.StepIndex;
        StepFraction = s.StepFraction;
        StepPositionText = s.StepIndex >= 0 && s.StepIndex < s.StepCount ? $"步骤 {s.StepIndex + 1}/{s.StepCount}" : "";
        ProgressText = $"{s.Progress * 100:0}%";
        SerialNumber = s.SerialNumber ?? "—";
        ResultText = s.State.IsTerminal() ? s.Message : null;
        RefreshElapsed();
    }

    public void RefreshElapsed()
    {
        var e = _snapshot.State == DeviceState.Queued ? null : _snapshot.Elapsed;
        ElapsedText = e is { } t ? $"{t.TotalSeconds:0.0} s" : "—";
    }

    private bool CanStart() => !State.IsBusy();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => _ = _manager.Start(Id);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _manager.Stop(Id);
}
