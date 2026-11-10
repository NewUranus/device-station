using System.Text;
using DeviceStation.Core.Channels;

namespace DeviceStation.Core.Protocol;

/// <summary>设备返回了 ERR 或无法解析的响应。</summary>
public sealed class DeviceProtocolException(string command, string reason)
    : Exception($"{command}: {reason}")
{
    public string Command { get; } = command;
    public string Reason { get; } = reason;
}

/// <summary>
/// 演示用的文本行协议客户端（UTF-8，命令本身是 ASCII）：发送 "CMD args\n"，等待一行 "OK [payload]" 或 "ERR reason"。
/// 真实设备的协议（二进制帧、XMODEM、厂商 Bootloader 协议等）按同样思路另写一个客户端即可。
/// </summary>
public sealed class AsciiLineClient(IDeviceChannel channel)
{
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly List<byte> _rx = new();

    public IDeviceChannel Channel => channel;

    /// <summary>发送命令并返回 OK 之后的负载；设备返回 ERR 时抛出 <see cref="DeviceProtocolException"/>。</summary>
    public async Task<string> QueryAsync(string command, CancellationToken ct)
    {
        await channel.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), ct).ConfigureAwait(false);
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        var name = command.Split(' ', 2)[0];

        if (line == "OK") return string.Empty;
        if (line.StartsWith("OK ", StringComparison.Ordinal)) return line[3..];
        if (line.StartsWith("ERR", StringComparison.Ordinal)) throw new DeviceProtocolException(name, line[3..].Trim());
        throw new DeviceProtocolException(name, $"无法解析的响应：{line}");
    }

    /// <summary>
    /// 丢弃已缓冲但未消费的数据（重试前调用，避免读到上一次的迟到响应）。
    /// 客户端自己的行缓冲和通道里已收到的数据都要清：迟到响应往往还停在通道的接收队列里。
    /// 清理之后才到达的响应无法靠清缓冲识别，需要协议带序号或回显命令名来对应请求。
    /// </summary>
    public void DiscardBuffered()
    {
        _rx.Clear();
        channel.DiscardInput();
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            int idx = _rx.IndexOf((byte)'\n');
            if (idx >= 0)
            {
                var line = Encoding.UTF8.GetString(_rx.GetRange(0, idx).ToArray()).TrimEnd('\r');
                _rx.RemoveRange(0, idx + 1);
                return line;
            }

            int n = await channel.ReadAsync(_readBuffer, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException($"{channel.Description} 连接已关闭");
            for (int i = 0; i < n; i++) _rx.Add(_readBuffer[i]);
        }
    }
}
