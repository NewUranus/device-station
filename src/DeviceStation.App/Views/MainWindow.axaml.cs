using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    // 点击设备卡片：切换日志面板只看这台设备。点在卡片里的按钮（开始/停止）上时不切换。
    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && (source as Button ?? source.FindAncestorOfType<Button>()) is not null) return;
        if (sender is Control { DataContext: DeviceCardViewModel card } && DataContext is MainViewModel vm)
            vm.SelectDeviceCommand.Execute(card);
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
