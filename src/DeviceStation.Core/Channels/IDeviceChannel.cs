namespace DeviceStation.Core.Channels;

/// <summary>
/// 与一台设备之间的字节流通道。每次运行（一台设备的一次作业）独占一个实例，
/// 同一实例不要求线程安全，但所有操作都必须响应 <see cref="CancellationToken"/>。
/// </summary>
public interface IDeviceChannel : IAsyncDisposable
{
    /// <summary>用于日志的人类可读描述，例如 "COM3@115200"、"10.0.0.5:5000"。</summary>
    string Description { get; }

    bool IsOpen { get; }

    Task OpenAsync(CancellationToken ct);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>读取至多 <paramref name="buffer"/> 长度的数据；返回 0 表示对端已关闭。</summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>
    /// 丢弃已经收到但还没被读走的数据。重试前调用：上一次尝试超时后，设备的迟到响应
    /// 可能已经进了接收缓冲，不清掉就会被当成下一条命令的响应。
    /// </summary>
    void DiscardInput();
}
