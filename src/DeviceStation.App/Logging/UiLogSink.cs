using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DeviceStation.App.Logging;

public sealed record UiLogEntry(DateTime Time, LogLevel Level, string? DeviceId, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level switch
    {
        LogLevel.Warning => "WARN",
        LogLevel.Error or LogLevel.Critical => "ERROR",
        LogLevel.Debug or LogLevel.Trace => "DEBUG",
        _ => "INFO",
    };
    public bool IsWarning => Level == LogLevel.Warning;
    public bool IsError => Level >= LogLevel.Error;
}

/// <summary>
/// 把日志放进一个无锁队列，由 UI 定时器批量取走显示。日志线程永远不碰 UI 线程。
/// 从日志作用域里取出 DeviceId，用来在界面上标注是哪台设备。
/// </summary>
public sealed class UiLogSink : ILoggerProvider, ISupportExternalScope
{
    private const int MaxQueued = 5000;
    private readonly ConcurrentQueue<UiLogEntry> _queue = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this);

    public bool TryDequeue(out UiLogEntry entry) => _queue.TryDequeue(out entry!);

    public void Dispose() { }

    private void Enqueue(UiLogEntry entry)
    {
        _queue.Enqueue(entry);
        while (_queue.Count > MaxQueued && _queue.TryDequeue(out _)) { } // UI 跟不上时丢最旧的
    }

    private sealed class SinkLogger(UiLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => sink._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string? deviceId = null;
            sink._scopes.ForEachScope((scope, _) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> kvs)
                    foreach (var kv in kvs)
                        if (kv.Key == "DeviceId") deviceId = kv.Value?.ToString();
            }, (object?)null);

            var message = formatter(state, exception);
            if (exception is not null) message += $"（{exception.GetType().Name}: {exception.Message}）";
            sink.Enqueue(new UiLogEntry(DateTime.Now, logLevel, deviceId, message));
        }
    }
}
