using System.IO.Ports;
using System.Text.Json.Serialization;

namespace DeviceStation.Core.Channels;

/// <summary>
/// 通道配置。以 "kind" 字段区分具体类型，随工位一起序列化成 JSON。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SerialChannelOptions), "serial")]
[JsonDerivedType(typeof(TcpChannelOptions), "tcp")]
[JsonDerivedType(typeof(SimulatedChannelOptions), "simulated")]
public abstract record ChannelOptions;

public sealed record SerialChannelOptions : ChannelOptions
{
    public required string PortName { get; init; }
    public int BaudRate { get; init; } = 115200;
    public Parity Parity { get; init; } = Parity.None;
    public int DataBits { get; init; } = 8;
    public StopBits StopBits { get; init; } = StopBits.One;
}

public sealed record TcpChannelOptions : ChannelOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
}

/// <summary>
/// 模拟设备：随机延时、随机失败/无响应，以及供测试用的确定性故障注入。
/// </summary>
public sealed record SimulatedChannelOptions : ChannelOptions
{
    /// <summary>随机种子；为空时每个通道实例随机。</summary>
    public int? Seed { get; init; }

    /// <summary>每条命令的响应延时区间（毫秒）。</summary>
    public int MinDelayMs { get; init; } = 20;
    public int MaxDelayMs { get; init; } = 80;

    public int ConnectDelayMs { get; init; } = 300;
    public int EraseDelayMs { get; init; } = 1500;
    public int MeasureDelayMs { get; init; } = 150;

    /// <summary>每条命令返回 ERR 的概率。</summary>
    public double FailureRate { get; init; }

    /// <summary>每条命令不响应（用于触发超时）的概率。</summary>
    public double HangRate { get; init; }

    /// <summary>打开通道失败的概率。</summary>
    public double ConnectFailureRate { get; init; }

    /// <summary>测量值超出规格的概率。</summary>
    public double OutOfSpecRate { get; init; }

    public string SerialPrefix { get; init; } = "SIM";

    /// <summary>这些命令总是返回 ERR（确定性故障注入）。</summary>
    public IReadOnlyList<string> FailCommands { get; init; } = [];

    /// <summary>这些命令永远不响应（确定性超时注入）。</summary>
    public IReadOnlyList<string> HangCommands { get; init; } = [];

    /// <summary>命令 → 前 N 次返回 ERR，之后恢复正常（用于验证重试）。按通道实例计数。</summary>
    public IReadOnlyDictionary<string, int> TransientFailures { get; init; } = new Dictionary<string, int>();
}
