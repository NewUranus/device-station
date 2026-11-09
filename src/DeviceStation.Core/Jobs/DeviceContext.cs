using DeviceStation.Core.Channels;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Results;
using Microsoft.Extensions.Logging;

namespace DeviceStation.Core.Jobs;

/// <summary>
/// 一台设备一次运行的上下文：通道、日志、SN、进度上报、结果记录、以及按运行隔离的临时状态。
/// 作业对象（含固件数据）在所有设备间共享，所以每台设备自己的状态必须放在这里而不是作业字段里。
/// </summary>
public sealed class DeviceContext
{
    private readonly DeviceSession _session;
    private readonly IResultStore _results;
    private readonly Dictionary<Type, object> _items = new();

    internal DeviceContext(DeviceSession session, IDeviceChannel channel, ILogger logger, IResultStore results, Guid runId)
    {
        _session = session;
        Channel = channel;
        Logger = logger;
        _results = results;
        RunId = runId;
    }

    public DeviceSlot Slot => _session.Slot;

    public IDeviceChannel Channel { get; }

    /// <summary>已带 DeviceId 作用域的日志。</summary>
    public ILogger Logger { get; }

    public Guid RunId { get; }

    /// <summary>当前步骤的第几次尝试（从 1 开始）。</summary>
    public int Attempt { get; internal set; }

    public string? SerialNumber
    {
        get => _session.SerialNumber;
        set => _session.SetSerialNumber(value);
    }

    /// <summary>当前步骤内是否已经写过结果记录（写过则管理器不再补一条步骤记录）。</summary>
    internal bool StepRecorded { get; set; }

    internal Action<double, string?>? StepProgressSink { get; set; }

    /// <summary>上报当前步骤内的进度（0~1），可附带一段说明，例如 "128/512 KB"。</summary>
    public void ReportStepProgress(double fraction, string? detail = null) => StepProgressSink?.Invoke(fraction, detail);

    /// <summary>写一条结果记录（测量值可为空）。写入是异步排队的，不会阻塞设备线程。</summary>
    public void Record(string step, bool passed, double? value = null, double? low = null, double? high = null,
        string? unit = null, string? message = null)
    {
        StepRecorded = true;
        _results.Add(new ResultRecord(RunId, SerialNumber, Slot.Id, Slot.Name, step, value, low, high, unit, passed,
            message, DateTimeOffset.Now));
    }

    /// <summary>取（或创建）本次运行内的一个状态对象，例如协议客户端、写入进度。</summary>
    public T GetOrAdd<T>(Func<T> factory) where T : class
    {
        if (_items.TryGetValue(typeof(T), out var existing)) return (T)existing;
        var created = factory();
        _items[typeof(T)] = created;
        return created;
    }
}
