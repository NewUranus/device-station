using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceStation.App.Logging;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Execution;

namespace DeviceStation.App.ViewModels;

/// <summary>
/// 主界面。UI 更新节流的做法：
/// 设备线程触发 SessionChanged 时只把“最新快照”写进字典（同一台设备的旧快照直接被覆盖），
/// UI 线程上的定时器每 100 ms 取一次并应用。不管设备上报多频繁，每台设备每秒最多刷新 10 次界面。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 400;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly DeviceManager _manager;
    private readonly UiLogSink _logSink;
    private readonly Dictionary<string, DeviceCardViewModel> _cardsById;
    private readonly ConcurrentDictionary<string, DeviceSnapshot> _pending = new();
    private readonly HashSet<Guid> _countedRuns = new();
    private readonly DispatcherTimer _timer;

    public MainViewModel(DeviceManager manager, UiLogSink logSink, string jobText, string dataDirectory)
    {
        _manager = manager;
        _logSink = logSink;
        JobText = jobText;
        DataDirectory = dataDirectory;
        _maxConcurrency = manager.MaxConcurrency;

        Devices = new ObservableCollection<DeviceCardViewModel>(
            manager.Sessions.Select(s => new DeviceCardViewModel(manager, s.Snapshot())));
        _cardsById = Devices.ToDictionary(d => d.Id);

        _manager.SessionChanged += OnSessionChanged;
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => OnTick());
        _timer.Start();
        UpdateCounters();
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; }

    public ObservableCollection<UiLogEntry> Logs { get; } = new();

    /// <summary>当前作业的说明，显示在标题下方。</summary>
    public string JobText { get; }

    public string DataDirectory { get; }

    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private int _passedTotal;
    [ObservableProperty] private int _failedTotal;
    [ObservableProperty] private int _cancelledTotal;
    [ObservableProperty] private string _yieldText = "—";
    [ObservableProperty] private string _statusText = "就绪";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAllCommand), nameof(StopAllCommand))]
    private bool _anyBusy;

    [ObservableProperty] private decimal? _maxConcurrency;

    partial void OnMaxConcurrencyChanged(decimal? value)
    {
        if (value is not { } v || v < 1) return;
        try
        {
            _manager.MaxConcurrency = (int)v;
            StatusText = $"并发上限已设为 {(int)v}";
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
            MaxConcurrency = _manager.MaxConcurrency;
        }
    }

    private bool CanStartAll() => Devices.Any(d => !d.IsBusy);

    [RelayCommand(CanExecute = nameof(CanStartAll))]
    private void StartAll()
    {
        _ = _manager.StartAll();
        StatusText = "全部开始";
    }

    [RelayCommand(CanExecute = nameof(AnyBusy))]
    private void StopAll()
    {
        _manager.StopAll();
        StatusText = "已请求全部停止";
    }

    [RelayCommand]
    private void ResetStatistics()
    {
        PassedTotal = FailedTotal = CancelledTotal = 0;
        foreach (var d in Devices) d.PassCount = d.FailCount = 0;
        UpdateCounters();
        StatusText = "统计已清零";
    }

    [RelayCommand]
    private void ClearLog() => Logs.Clear();

    // 后台线程：只记最新快照，不碰 UI。
    private void OnSessionChanged(object? sender, DeviceSnapshot snapshot) => _pending[snapshot.Id] = snapshot;

    // UI 线程：每 100 ms 一次。
    private void OnTick()
    {
        foreach (var id in _pending.Keys)
        {
            if (!_pending.TryRemove(id, out var snap) || !_cardsById.TryGetValue(id, out var card)) continue;
            card.Apply(snap);
            CountIfFinished(card, snap);
        }

        foreach (var card in Devices)
            if (card.IsBusy) card.RefreshElapsed();

        UpdateCounters();
        DrainLogs();
    }

    private void CountIfFinished(DeviceCardViewModel card, DeviceSnapshot snap)
    {
        // 节流会丢掉中间状态，但终态一定是该次运行的最后一个快照，所以按 RunId 计数不会漏也不会重。
        if (!snap.State.IsTerminal() || !_countedRuns.Add(snap.RunId)) return;
        switch (snap.State)
        {
            case DeviceState.Passed: PassedTotal++; card.PassCount++; break;
            case DeviceState.Failed: FailedTotal++; card.FailCount++; break;
            case DeviceState.Cancelled: CancelledTotal++; break;
        }
    }

    private void UpdateCounters()
    {
        RunningCount = Devices.Count(d => d.State is DeviceState.Connecting or DeviceState.Running);
        QueuedCount = Devices.Count(d => d.State == DeviceState.Queued);
        AnyBusy = RunningCount + QueuedCount > 0;
        StartAllCommand.NotifyCanExecuteChanged();

        int judged = PassedTotal + FailedTotal; // 停止的不计入良率
        YieldText = judged == 0 ? "—" : $"{100.0 * PassedTotal / judged:0.0}%";
    }

    private void DrainLogs()
    {
        int n = 0;
        while (n++ < 200 && _logSink.TryDequeue(out var entry))
        {
            Logs.Add(entry);
            if (entry.IsError) StatusText = $"[{entry.DeviceId}] {entry.Message}";
        }
        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
    }

    public void Dispose()
    {
        _timer.Stop();
        _manager.SessionChanged -= OnSessionChanged;
    }
}
