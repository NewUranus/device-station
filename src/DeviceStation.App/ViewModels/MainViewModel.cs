using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceStation.App.Logging;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Execution;
using Microsoft.Extensions.Logging;

namespace DeviceStation.App.ViewModels;

public enum LogFilter { All, Warning, Error }

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
    private readonly ConcurrentQueue<DeviceSnapshot> _finished = new();
    private readonly List<UiLogEntry> _allLogs = new();
    private readonly DispatcherTimer _timer;

    public MainViewModel(DeviceManager manager, UiLogSink logSink, IReadOnlyList<double> stepWeights, string jobText,
        string jobDetail, string dataDirectory)
    {
        _manager = manager;
        _logSink = logSink;
        JobText = jobText;
        JobDetail = jobDetail;
        DataDirectory = dataDirectory;
        _maxConcurrency = manager.MaxConcurrency;

        Devices = new ObservableCollection<DeviceCardViewModel>(
            manager.Sessions.Select(s => new DeviceCardViewModel(manager, s.Snapshot(), stepWeights)));
        _cardsById = Devices.ToDictionary(d => d.Id);

        _manager.SessionChanged += OnSessionChanged;
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => OnTick());
        _timer.Start();
        UpdateCounters();
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; }

    /// <summary>按当前筛选条件显示的日志。</summary>
    public ObservableCollection<UiLogEntry> Logs { get; } = new();

    /// <summary>当前作业的说明，显示在标题下方。</summary>
    public string JobText { get; }

    /// <summary>作业的补充说明（如固件信息），鼠标悬停时显示。</summary>
    public string JobDetail { get; }

    public string DataDirectory { get; }

    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private int _passedTotal;
    [ObservableProperty] private int _failedTotal;
    [ObservableProperty] private int _cancelledTotal;
    [ObservableProperty] private string _yieldText = "—";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private bool _isLogExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll), nameof(IsFilterWarning), nameof(IsFilterError))]
    private LogFilter _logFilter = LogFilter.All;

    public bool IsFilterAll => LogFilter == LogFilter.All;
    public bool IsFilterWarning => LogFilter == LogFilter.Warning;
    public bool IsFilterError => LogFilter == LogFilter.Error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAllCommand), nameof(StopAllCommand), nameof(IncreaseConcurrencyCommand),
        nameof(DecreaseConcurrencyCommand))]
    private bool _anyBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecreaseConcurrencyCommand))]
    private int _maxConcurrency;

    private bool CanIncreaseConcurrency() => !AnyBusy && MaxConcurrency < 64;
    private bool CanDecreaseConcurrency() => !AnyBusy && MaxConcurrency > 1;

    [RelayCommand(CanExecute = nameof(CanIncreaseConcurrency))]
    private void IncreaseConcurrency() => SetConcurrency(MaxConcurrency + 1);

    [RelayCommand(CanExecute = nameof(CanDecreaseConcurrency))]
    private void DecreaseConcurrency() => SetConcurrency(MaxConcurrency - 1);

    private void SetConcurrency(int value)
    {
        try
        {
            _manager.MaxConcurrency = value;
            MaxConcurrency = value;
            StatusText = $"并发上限已设为 {value}";
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
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
    private void SetLogFilter(LogFilter filter) => LogFilter = filter;

    [RelayCommand]
    private void ToggleLog() => IsLogExpanded = !IsLogExpanded;

    [RelayCommand]
    private void ClearLog()
    {
        _allLogs.Clear();
        Logs.Clear();
        WarningCount = ErrorCount = 0;
    }

    partial void OnLogFilterChanged(LogFilter value)
    {
        Logs.Clear();
        foreach (var e in _allLogs.Where(Matches)) Logs.Add(e);
    }

    private bool Matches(UiLogEntry e) => LogFilter switch
    {
        LogFilter.Warning => e.Level == LogLevel.Warning,
        LogFilter.Error => e.Level >= LogLevel.Error,
        _ => true,
    };

    // 后台线程：只记最新快照，不碰 UI。
    // 终态快照另外排队：它可能在下一次刷新前就被同一工位新一轮的快照覆盖（例如刚结束就被“全部开始”重新拉起），
    // 只靠 _pending 统计会漏算这一轮。每次运行只会产生一个终态快照，所以按队列计数不重不漏。
    private void OnSessionChanged(object? sender, DeviceSnapshot snapshot)
    {
        if (snapshot.State.IsTerminal()) _finished.Enqueue(snapshot);
        _pending[snapshot.Id] = snapshot;
    }

    // UI 线程：每 100 ms 一次。
    private void OnTick()
    {
        foreach (var id in _pending.Keys)
        {
            if (!_pending.TryRemove(id, out var snap) || !_cardsById.TryGetValue(id, out var card)) continue;
            card.Apply(snap);
        }

        while (_finished.TryDequeue(out var done))
            if (_cardsById.TryGetValue(done.Id, out var card)) CountFinished(card, done);

        foreach (var card in Devices)
            if (card.IsBusy) card.RefreshElapsed();

        UpdateCounters();
        DrainLogs();
    }

    private void CountFinished(DeviceCardViewModel card, DeviceSnapshot snap)
    {
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
            _allLogs.Add(entry);
            if (entry.IsWarning) WarningCount++;
            if (entry.IsError)
            {
                ErrorCount++;
                StatusText = $"[{entry.DeviceId}] {entry.Message}";
            }
            if (Matches(entry)) Logs.Add(entry);
        }

        if (_allLogs.Count > MaxLogLines) _allLogs.RemoveRange(0, _allLogs.Count - MaxLogLines);
        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
    }

    public void Dispose()
    {
        _timer.Stop();
        _manager.SessionChanged -= OnSessionChanged;
    }
}
