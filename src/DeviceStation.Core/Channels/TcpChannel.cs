using System.Net.Sockets;

namespace DeviceStation.Core.Channels;

/// <summary>TCP 通道（例如串口服务器、带网口的治具）。</summary>
public sealed class TcpChannel(TcpChannelOptions options) : StreamDeviceChannel
{
    private TcpClient? _client;

    public override string Description => $"{options.Host}:{options.Port}";

    protected override async Task<Stream> OpenStreamAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(options.Host, options.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        return client.GetStream();
    }

    protected override void CloseStream()
    {
        var client = _client;
        _client = null;
        client?.Dispose();
    }
}
