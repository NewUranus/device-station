using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DeviceStation.Core.Logging;

/// <summary>
/// 按设备、按天把日志写到文件：<c>{目录}/{DeviceId}/{yyyy-MM-dd}.log</c>；
/// 不属于任何设备的日志（启动、结果库等）写到 <c>{目录}/_app/</c>。
/// <para>
/// DeviceId 和 RunId 从日志作用域里取（<see cref="Execution.DeviceManager"/> 每次运行都会开这个作用域），
/// RunId 与结果库 results.run_id 的前 8 位一致，可以据此把日志和测试结果对上。
/// </para>
/// <para>
/// 写日志的线程只把记录放进内存队列，一个后台线程负责落盘，所以设备线程不会等磁盘。
/// 写文件出错只会丢这几行日志，不会影响设备运行。
/// </para>
/// </summary>
public sealed class DeviceFileLoggerProvider : ILoggerProvider, ISupportExternalScope, IAsyncDisposable
{
    public const string AppFolder = "_app";

    private readonly Channel<object> _queue =
        Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly LogLevel _minLevel;
    private readonly TimeProvider _time;
    private readonly Task _writer;
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private int _disposed;

    /// <param name="directory">日志根目录。</param>
    /// <param name="minLevel">最低记录级别。</param>
    /// <param name="retentionDays">启动时删除早于这么多天的日志文件；0 表示不清理。</param>
    /// <param name="time">时间来源（测试用）。</param>
    public DeviceFileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information, int retentionDays = 30,
        TimeProvider? time = null)
    {
        Directory = Path.GetFullPath(directory);
        _minLevel = minLevel;
        _time = time ?? TimeProvider.System;
        System.IO.Directory.CreateDirectory(Directory);
        if (retentionDays > 0) DeleteOldFiles(retentionDays);
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Directory { get; }

    /// <summary>某台设备某一天的日志文件路径。</summary>
    public string PathFor(string? deviceId, DateOnly date) =>
        Path.Combine(Directory, FolderFor(deviceId), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <summary>等待此前写入的日志全部落盘。</summary>
    public Task FlushAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(tcs)) return Task.CompletedTask;
        return tcs.Task.WaitAsync(ct);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { /* 退出时不无限等待磁盘 */ }
    }

    private sealed record Entry(DateTimeOffset Time, LogLevel Level, string Category, string? DeviceId, string? RunId,
        string Message, Exception? Exception);

    private void Enqueue(Entry entry) => _queue.Writer.TryWrite(entry);

    private async Task WriteLoopAsync()
    {
        var writers = new Dictionary<string, (DateOnly Date, StreamWriter Writer)>(StringComparer.Ordinal);
        var touched = new HashSet<StreamWriter>();
        var flushes = new List<TaskCompletionSource>();
        var sb = new StringBuilder();

        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    if (item is TaskCompletionSource tcs)
                    {
                        flushes.Add(tcs);
                        continue;
                    }

                    var e = (Entry)item;
                    try
                    {
                        var writer = WriterFor(writers, e.DeviceId, DateOnly.FromDateTime(e.Time.DateTime));
                        writer.Write(Format(sb, e));
                        touched.Add(writer);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 日志写不进去不能影响设备运行；丢掉这一行。
                    }
                }

                foreach (var w in touched)
                {
                    try { w.Flush(); } catch (IOException) { }
                }
                touched.Clear();
                foreach (var f in flushes) f.TrySetResult();
                flushes.Clear();
            }
        }
        finally
        {
            foreach (var (_, w) in writers.Values)
            {
                try { w.Dispose(); } catch (IOException) { }
            }
        }
    }

    private StreamWriter WriterFor(Dictionary<string, (DateOnly Date, StreamWriter Writer)> writers, string? deviceId,
        DateOnly date)
    {
        var folder = FolderFor(deviceId);
        if (writers.TryGetValue(folder, out var current))
        {
            if (current.Date == date) return current.Writer;
            current.Writer.Dispose(); // 跨天：换新文件
        }

        var path = PathFor(deviceId, date);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 允许别的程序（文本编辑器、tail -f）同时读这个文件
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writers[folder] = (date, writer);
        return writer;
    }

    private static string Format(StringBuilder sb, Entry e)
    {
        sb.Clear();
        sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append(LevelText(e.Level).PadRight(5)).Append(' ');
        if (e.RunId is not null) sb.Append("run=").Append(e.RunId).Append(' ');
        if (e.DeviceId is null) sb.Append(e.Category).Append(": ");
        sb.Append(e.Message.ReplaceLineEndings(" | "));
        sb.Append('\n');
        if (e.Exception is not null)
        {
            foreach (var line in e.Exception.ToString().Split('\n'))
                sb.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }
        return sb.ToString();
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => level.ToString(),
    };

    private static string FolderFor(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return AppFolder;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = deviceId.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }

    private void DeleteOldFiles(int retentionDays)
    {
        var cutoff = DateOnly.FromDateTime(_time.GetLocalNow().DateTime).AddDays(-retentionDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.log", SearchOption.AllDirectories))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) && date < cutoff)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class FileLogger(DeviceFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string? deviceId = null, runId = null;
            provider._scopes.ForEachScope((scope, _) =>
            {
                if (scope is not IEnumerable<KeyValuePair<string, object?>> kvs) return;
                foreach (var kv in kvs)
                {
                    if (kv.Key == "DeviceId") deviceId = kv.Value?.ToString();
                    else if (kv.Key == "RunId") runId = kv.Value?.ToString();
                }
            }, (object?)null);

            provider.Enqueue(new Entry(provider._time.GetLocalNow(), logLevel, category, deviceId, runId,
                formatter(state, exception), exception));
        }
    }
}
