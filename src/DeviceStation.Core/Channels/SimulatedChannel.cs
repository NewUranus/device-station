using System.Globalization;
using System.Text;
using System.Threading.Channels;
using DeviceStation.Core.Util;

namespace DeviceStation.Core.Channels;

/// <summary>
/// 模拟一台挂在通道另一端的设备，说一种简单的 ASCII 行协议（见 <see cref="Protocol.AsciiLineClient"/>）：
/// 每条命令以 '\n' 结尾，设备回复 "OK [payload]" 或 "ERR reason"。
/// <list type="bullet">
/// <item><c>PING</c>、<c>SN?</c>、<c>BOOT</c>、<c>ERASE &lt;size&gt;</c>、<c>WRITE &lt;offset&gt; &lt;base64&gt;</c>、
/// <c>VERIFY &lt;crc32-hex&gt;</c>、<c>RESET</c>、<c>MEAS &lt;name&gt;</c></item>
/// </list>
/// 设备真的会把写入的数据存下来并在 VERIFY 时计算 CRC，所以重试/续传写错位置会被校验发现。
/// 每个实例代表治具上的一块新板子（随机 SN），每次运行创建一个新实例。
/// </summary>
public sealed class SimulatedChannel : IDeviceChannel
{
    /// <summary>模拟设备内置的测量值表：名称 → (标称值, 标准差)。</summary>
    public static readonly IReadOnlyDictionary<string, (double Nominal, double Sigma)> DefaultMeasurements =
        new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["VBAT"] = (3.30, 0.02),
            ["VCC_IO"] = (1.80, 0.01),
            ["I_IDLE"] = (12.0, 0.4),
            ["I_SLEEP"] = (0.050, 0.003),
            ["OSC_FREQ"] = (32768, 2),
            ["TEMP"] = (25, 1.5),
        };

    private readonly SimulatedChannelOptions _o;
    private readonly Random _rng;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>();
    private readonly List<byte> _lineBuffer = new();
    private readonly Dictionary<string, int> _transientLeft;
    private readonly List<string> _received = new();
    private ReadOnlyMemory<byte> _pending;
    private bool _open;
    private bool _inBootloader;
    private byte[]? _flash;

    public SimulatedChannel(SimulatedChannelOptions options, string? name = null)
    {
        _o = options;
        _rng = options.Seed is { } seed ? new Random(seed) : new Random();
        _transientLeft = new Dictionary<string, int>(options.TransientFailures, StringComparer.OrdinalIgnoreCase);
        SerialNumber = $"{options.SerialPrefix}{_rng.Next(100000, 1000000)}";
        Description = $"sim:{name ?? SerialNumber}";
    }

    public string Description { get; }

    public bool IsOpen => _open;

    /// <summary>本块模拟板子的序列号。</summary>
    public string SerialNumber { get; }

    /// <summary>收到过的命令名（不含参数），供测试断言。</summary>
    public IReadOnlyList<string> ReceivedCommands
    {
        get { lock (_received) return _received.ToArray(); }
    }

    public int OpenCount { get; private set; }

    public async Task OpenAsync(CancellationToken ct)
    {
        if (_open) return;
        OpenCount++;
        await Task.Delay(Jitter(_o.ConnectDelayMs), ct).ConfigureAwait(false);
        if (Roll(_o.ConnectFailureRate))
            throw new IOException($"{Description}：模拟端口打开失败");
        _open = true;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!_open) throw new InvalidOperationException($"{Description} 尚未打开");

        // span 不能跨 await，所以先在同步方法里把完整的行切出来，再逐行处理。
        foreach (var line in SplitLines(data.Span))
            await HandleAsync(line, ct).ConfigureAwait(false);
    }

    private List<string> SplitLines(ReadOnlySpan<byte> data)
    {
        var lines = new List<string>();
        foreach (byte b in data)
        {
            if (b == (byte)'\n')
            {
                lines.Add(Encoding.UTF8.GetString(_lineBuffer.ToArray()).TrimEnd('\r'));
                _lineBuffer.Clear();
            }
            else
            {
                _lineBuffer.Add(b);
            }
        }
        return lines;
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (!_open) throw new InvalidOperationException($"{Description} 尚未打开");
        while (_pending.IsEmpty)
        {
            if (!await _outbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
            if (_outbox.Reader.TryRead(out var chunk)) _pending = chunk;
        }
        int n = Math.Min(buffer.Length, _pending.Length);
        _pending.Span[..n].CopyTo(buffer.Span);
        _pending = _pending[n..];
        return n;
    }

    public void DiscardInput()
    {
        _pending = default;
        while (_outbox.Reader.TryRead(out _)) { }
    }

    public ValueTask DisposeAsync()
    {
        _open = false;
        _outbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private async Task HandleAsync(string line, CancellationToken ct)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var cmd = parts[0].ToUpperInvariant();
        lock (_received) _received.Add(cmd);

        int delay = _rng.Next(_o.MinDelayMs, Math.Max(_o.MinDelayMs, _o.MaxDelayMs) + 1);
        if (cmd == "ERASE") delay += Jitter(_o.EraseDelayMs);
        if (cmd == "MEAS") delay += Jitter(_o.MeasureDelayMs);
        await Task.Delay(delay, ct).ConfigureAwait(false);

        if (Contains(_o.HangCommands, cmd) || Roll(_o.HangRate)) return; // 不回复 → 调用方超时
        if (Contains(_o.FailCommands, cmd)) { Reply("ERR 被配置为必定失败"); return; }
        if (_transientLeft.TryGetValue(cmd, out var left) && left > 0)
        {
            _transientLeft[cmd] = left - 1;
            Reply($"ERR 瞬时故障（剩余 {left - 1} 次）");
            return;
        }
        if (Roll(_o.FailureRate)) { Reply("ERR 随机通信错误"); return; }

        Reply(Execute(cmd, parts));
    }

    private string Execute(string cmd, string[] p)
    {
        switch (cmd)
        {
            case "PING":
                return "OK PONG";
            case "SN?":
                return "OK " + SerialNumber;
            case "BOOT":
                _inBootloader = true;
                return "OK";
            case "ERASE":
                if (!_inBootloader) return "ERR 不在 Bootloader 模式";
                if (p.Length < 2 || !int.TryParse(p[1], out var size) || size <= 0) return "ERR 参数错误";
                _flash = new byte[size];
                Array.Fill(_flash, (byte)0xFF);
                return "OK";
            case "WRITE":
            {
                if (!_inBootloader || _flash is null) return "ERR 未擦除";
                if (p.Length < 3 || !int.TryParse(p[1], out var offset)) return "ERR 参数错误";
                byte[] data;
                try { data = Convert.FromBase64String(p[2]); }
                catch (FormatException) { return "ERR 数据格式错误"; }
                if (offset < 0 || offset + data.Length > _flash.Length) return "ERR 地址越界";
                data.CopyTo(_flash, offset);
                return "OK";
            }
            case "VERIFY":
            {
                if (_flash is null) return "ERR 未写入";
                if (p.Length < 2 || !uint.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected))
                    return "ERR 参数错误";
                var actual = Crc32.Compute(_flash);
                return actual == expected ? "OK" : $"ERR CRC 不匹配 期望={expected:X8} 实际={actual:X8}";
            }
            case "RESET":
                _inBootloader = false;
                return "OK";
            case "MEAS":
            {
                if (p.Length < 2 || !DefaultMeasurements.TryGetValue(p[1], out var m)) return "ERR 未知测量项";
                double value = m.Nominal + m.Sigma * NextGaussian();
                if (Roll(_o.OutOfSpecRate)) value = m.Nominal * (_rng.Next(2) == 0 ? 0.7 : 1.3);
                return "OK " + value.ToString("G6", CultureInfo.InvariantCulture);
            }
            default:
                return "ERR 未知命令";
        }
    }

    private void Reply(string line) => _outbox.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));

    private bool Roll(double probability) => probability > 0 && _rng.NextDouble() < probability;

    private int Jitter(int baseMs) => baseMs <= 0 ? 0 : (int)(baseMs * (0.75 + _rng.NextDouble() * 0.5));

    private double NextGaussian()
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static bool Contains(IReadOnlyList<string> list, string cmd)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], cmd, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
