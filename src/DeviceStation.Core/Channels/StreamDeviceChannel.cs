using System.Threading.Channels;

namespace DeviceStation.Core.Channels;

/// <summary>
/// 基于 <see cref="Stream"/> 的通道基类（串口、TCP 共用）。
/// <para>
/// 打开后启动一个后台读循环，把收到的数据块放进内存队列；<see cref="ReadAsync"/> 只从队列取数据。
/// 这样做的原因：SerialPort.BaseStream 在部分平台上并不响应 CancellationToken，
/// 直接在业务代码里 await 它会导致超时/取消失效。读循环把“阻塞在硬件上”和“业务等待”解耦，
/// 业务侧的等待总是可以被取消；关闭通道时通过关闭底层流来结束读循环。
/// </para>
/// </summary>
public abstract class StreamDeviceChannel : IDeviceChannel
{
    private readonly Channel<byte[]> _inbox =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private ReadOnlyMemory<byte> _pending;
    private Stream? _stream;
    private CancellationTokenSource? _loopCts;
    private Task? _readLoop;

    public abstract string Description { get; }

    public bool IsOpen => _stream is not null;

    public async Task OpenAsync(CancellationToken ct)
    {
        if (_stream is not null) return;
        var stream = await OpenStreamAsync(ct).ConfigureAwait(false);
        _stream = stream;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _readLoop = Task.Run(() => ReadLoopAsync(stream, token));
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                _inbox.Writer.TryWrite(buffer.AsSpan(0, n).ToArray());
            }
            _inbox.Writer.TryComplete();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            _inbox.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _inbox.Writer.TryComplete(new IOException($"{Description} 读取失败：{ex.Message}", ex));
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException($"{Description} 尚未打开");
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException($"{Description} 尚未打开");

        while (_pending.IsEmpty)
        {
            if (!await _inbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
            if (_inbox.Reader.TryRead(out var chunk)) _pending = chunk;
        }

        int n = Math.Min(buffer.Length, _pending.Length);
        _pending.Span[..n].CopyTo(buffer.Span);
        _pending = _pending[n..];
        return n;
    }

    public void DiscardInput()
    {
        _pending = default;
        while (_inbox.Reader.TryRead(out _)) { }
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        try { CloseStream(); } catch { /* 关闭时的异常不影响结果 */ }

        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* 读循环异常已经写入队列 */ }
        }

        _loopCts?.Dispose();
        _stream = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>打开底层连接并返回其数据流。</summary>
    protected abstract Task<Stream> OpenStreamAsync(CancellationToken ct);

    /// <summary>关闭底层连接；必须能让读循环中挂起的 ReadAsync 返回。</summary>
    protected abstract void CloseStream();
}
