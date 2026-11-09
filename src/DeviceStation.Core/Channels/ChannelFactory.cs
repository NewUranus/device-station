namespace DeviceStation.Core.Channels;

public interface IChannelFactory
{
    /// <summary>为一次运行创建新的通道实例（调用方负责释放）。</summary>
    IDeviceChannel Create(string slotId, ChannelOptions options);
}

public sealed class DefaultChannelFactory : IChannelFactory
{
    public static DefaultChannelFactory Instance { get; } = new();

    public IDeviceChannel Create(string slotId, ChannelOptions options) => options switch
    {
        SerialChannelOptions s => new SerialChannel(s),
        TcpChannelOptions t => new TcpChannel(t),
        SimulatedChannelOptions m => new SimulatedChannel(m, slotId),
        _ => throw new NotSupportedException($"不支持的通道类型：{options.GetType().Name}"),
    };
}
