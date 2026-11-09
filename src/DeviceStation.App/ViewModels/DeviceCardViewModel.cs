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

    public DeviceCardViewModel(DeviceManager manager, DeviceSnapshot initial)
    {
        _manager = manager;
        _snapshot = initial;
        Id = initial.Id;
        Name = initial.Name;
        Apply(initial);
    }

    public string Id { get; }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private DeviceState _state;

    [ObservableProperty] private string? _currentStep;
    [ObservableProperty] private string? _stepDetail;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string? _serialNumber;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private int _passCount;
    [ObservableProperty] private int _failCount;

    public bool IsBusy => State.IsBusy();

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

    public DeviceSnapshot Snapshot => _snapshot;

    public void Apply(DeviceSnapshot s)
    {
        _snapshot = s;
        State = s.State;
        CurrentStep = s.CurrentStep;
        StepDetail = s.State.IsBusy() && s.Attempt > 1 && s.StepDetail is null ? $"第 {s.Attempt} 次尝试" : s.StepDetail;
        ProgressPercent = Math.Round(s.Progress * 100, 1);
        SerialNumber = s.SerialNumber ?? "—";
        ResultText = s.State switch
        {
            DeviceState.Passed => s.Message,
            DeviceState.Failed or DeviceState.Cancelled => s.Message,
            _ => null,
        };
        RefreshElapsed();
    }

    public void RefreshElapsed()
    {
        var e = _snapshot.State == DeviceState.Queued ? null : _snapshot.Elapsed;
        ElapsedText = e is { } t ? $"{t.TotalSeconds:0.0} s" : "";
    }

    private bool CanStart() => !State.IsBusy();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => _ = _manager.Start(Id);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _manager.Stop(Id);
}
