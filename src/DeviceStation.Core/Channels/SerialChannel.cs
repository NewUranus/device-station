using System.IO.Ports;

namespace DeviceStation.Core.Channels;

/// <summary>串口通道。读取走 <see cref="SerialPort.BaseStream"/> 的 ReadAsync 读循环，不使用 DataReceived 事件。</summary>
public sealed class SerialChannel(SerialChannelOptions options) : StreamDeviceChannel
{
    private SerialPort? _port;

    public override string Description => $"{options.PortName}@{options.BaudRate}";

    protected override Task<Stream> OpenStreamAsync(CancellationToken ct)
    {
        // SerialPort.Open 是同步调用，某些驱动上会卡一会儿，放到线程池里避免占用调用方线程。
        return Task.Run<Stream>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var port = new SerialPort(options.PortName, options.BaudRate, options.Parity, options.DataBits, options.StopBits)
            {
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 5000,
            };
            port.Open();
            port.DiscardInBuffer();
            _port = port;
            return port.BaseStream;
        }, ct);
    }

    protected override void CloseStream()
    {
        var port = _port;
        _port = null;
        if (port is null) return;
        try { port.Close(); } finally { port.Dispose(); }
    }
}
