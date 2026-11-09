using System.Collections.Concurrent;

namespace DeviceStation.Core.Results;

/// <summary>一条结果记录：SN、工位、步骤、测量值、是否通过、时间。</summary>
public sealed record ResultRecord(
    Guid RunId,
    string? SerialNumber,
    string SlotId,
    string SlotName,
    string Step,
    double? Value,
    double? Low,
    double? High,
    string? Unit,
    bool Passed,
    string? Message,
    DateTimeOffset Timestamp);

public interface IResultStore
{
    /// <summary>登记一条记录。必须是非阻塞的（设备线程会直接调用）。</summary>
    void Add(ResultRecord record);

    /// <summary>等待此前登记的记录全部落盘。</summary>
    Task FlushAsync(CancellationToken ct = default);
}

/// <summary>内存实现，供测试或不需要持久化的场景使用。</summary>
public sealed class InMemoryResultStore : IResultStore
{
    private readonly ConcurrentQueue<ResultRecord> _records = new();

    public IReadOnlyList<ResultRecord> Records => _records.ToArray();

    public void Add(ResultRecord record) => _records.Enqueue(record);

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}
