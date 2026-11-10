using System.Text;
using System.Threading.Channels;
using DeviceStation.Core.Channels;
using DeviceStation.Core.Protocol;

namespace DeviceStation.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task DiscardBuffered_DropsLateReplyAlreadyQueuedInChannel()
    {
        var stream = new ScriptedDeviceStream();
        await using var channel = new ScriptedChannel(stream);
        await channel.OpenAsync(CancellationToken.None);
        var client = new AsciiLineClient(channel);

        // 第一次：设备不响应，命令超时。
        stream.Respond = _ => null;
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryAsync("PING", cts.Token));

        // 迟到的响应到了，已经被读循环收进通道的接收队列。
        await stream.PushAndWaitDeliveredAsync("OK late\n");

        // 重试：先清缓冲，再发命令，必须拿到这一次的响应而不是迟到的那条。
        stream.Respond = _ => "OK fresh";
        client.DiscardBuffered();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("fresh", await client.QueryAsync("PING", timeout.Token));
    }
}

/// <summary>基于 <see cref="ScriptedDeviceStream"/> 的通道，走真实的 <see cref="StreamDeviceChannel"/> 读循环。</summary>
internal sealed class ScriptedChannel(ScriptedDeviceStream stream) : StreamDeviceChannel
{
    public override string Description => "scripted";

    protected override Task<Stream> OpenStreamAsync(CancellationToken ct) => Task.FromResult<Stream>(stream);

    protected override void CloseStream() => stream.Dispose();
}

/// <summary>按脚本应答的内存设备：收到一行命令就调用 <see cref="Respond"/>，返回非空则作为一行响应发回。</summary>
internal sealed class ScriptedDeviceStream : Stream
{
    private readonly Channel<byte[]> _toHost = Channel.CreateUnbounded<byte[]>();
    private readonly List<byte> _line = new();
    private TaskCompletionSource? _nextRead;

    public Func<string, string?> Respond { get; set; } = _ => null;

    /// <summary>推送数据，并等到读循环把它取走（读循环每取走一块都会立即发起下一次读）。</summary>
    public Task PushAndWaitDeliveredAsync(string text)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _nextRead, tcs);
        Push(text);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private void Push(string text) => _toHost.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _nextRead, null)?.TrySetResult();
        if (!await _toHost.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
        if (!_toHost.Reader.TryRead(out var chunk)) return 0;
        chunk.CopyTo(buffer);
        return chunk.Length;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        foreach (byte b in buffer)
        {
            if (b != (byte)'\n')
            {
                _line.Add(b);
                continue;
            }
            var command = Encoding.UTF8.GetString(_line.ToArray());
            _line.Clear();
            if (Respond(command) is { } reply) Push(reply + "\n");
        }
    }

    protected override void Dispose(bool disposing)
    {
        _toHost.Writer.TryComplete();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
