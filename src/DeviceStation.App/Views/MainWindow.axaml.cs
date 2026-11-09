using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using DeviceStation.App.ViewModels;

namespace DeviceStation.App.Views;

public partial class MainWindow : Window
{
    private bool _scrollQueued;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.Logs.CollectionChanged += OnLogsChanged;
        };
    }

    // 日志自动滚到底：一次 tick 可能追加很多行，只排一次滚动。
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (LogList.ItemCount > 0) LogList.ScrollIntoView(LogList.ItemCount - 1);
        }, DispatcherPriority.Background);
    }
}
