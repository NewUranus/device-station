using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceStation.Core.Results;

/// <summary>
/// SQLite 结果库。设备线程只把记录放进内存队列；一个后台写线程独占连接，按批次在事务里写入。
/// 这样 8 台（或 32 台）设备同时出结果也不会互相抢 SQLite 的写锁，也不会让设备线程等磁盘。
/// </summary>
public sealed class SqliteResultStore : IResultStore, IAsyncDisposable
{
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS results (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id        TEXT    NOT NULL,
            serial_number TEXT,
            slot_id       TEXT    NOT NULL,
            slot_name     TEXT    NOT NULL,
            step          TEXT    NOT NULL,
            value         REAL,
            low           REAL,
            high          REAL,
            unit          TEXT,
            passed        INTEGER NOT NULL,
            message       TEXT,
            timestamp     TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_results_sn  ON results(serial_number);
        CREATE INDEX IF NOT EXISTS ix_results_run ON results(run_id);
        """;

    private const string InsertSql = """
        INSERT INTO results (run_id, serial_number, slot_id, slot_name, step, value, low, high, unit, passed, message, timestamp)
        VALUES ($run, $sn, $slot, $slotName, $step, $value, $low, $high, $unit, $passed, $message, $ts);
        """;

    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly Task _writer;

    public SqliteResultStore(string databasePath, ILogger<SqliteResultStore>? logger = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }

        _writer = Task.Run(WriteLoopAsync);
    }

    public string DatabasePath { get; }

    public void Add(ResultRecord record) => _queue.Writer.TryWrite(record);

    public Task FlushAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(tcs)) return Task.CompletedTask; // 已关闭
        return tcs.Task.WaitAsync(ct);
    }

    /// <summary>按 SN 查询（最新在后）。</summary>
    public async Task<IReadOnlyList<ResultRecord>> QueryBySerialAsync(string serialNumber, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, serial_number, slot_id, slot_name, step, value, low, high, unit, passed, message, timestamp
            FROM results WHERE serial_number = $sn ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$sn", serialNumber);

        var list = new List<ResultRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ResultRecord(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt64(9) != 0,
                reader.IsDBNull(10) ? null : reader.GetString(10),
                DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture)));
        }
        return list;
    }

    private async Task WriteLoopAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        var batch = new List<ResultRecord>(256);
        var flushes = new List<TaskCompletionSource>();

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (batch.Count < 500 && _queue.Reader.TryRead(out var item))
            {
                if (item is ResultRecord r) batch.Add(r);
                else if (item is TaskCompletionSource tcs) flushes.Add(tcs);
            }

            if (batch.Count > 0)
            {
                try
                {
                    WriteBatch(conn, batch);
                }
                catch (Exception ex)
                {
                    // 结果库写失败不能拖垮设备运行；记日志，丢弃这一批。
                    _logger.LogError(ex, "写入结果库失败，丢弃 {Count} 条记录", batch.Count);
                }
                batch.Clear();
            }

            foreach (var f in flushes) f.TrySetResult();
            flushes.Clear();
        }
    }

    private static void WriteBatch(SqliteConnection conn, List<ResultRecord> batch)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertSql;
        var pRun = cmd.Parameters.Add("$run", SqliteType.Text);
        var pSn = cmd.Parameters.Add("$sn", SqliteType.Text);
        var pSlot = cmd.Parameters.Add("$slot", SqliteType.Text);
        var pSlotName = cmd.Parameters.Add("$slotName", SqliteType.Text);
        var pStep = cmd.Parameters.Add("$step", SqliteType.Text);
        var pValue = cmd.Parameters.Add("$value", SqliteType.Real);
        var pLow = cmd.Parameters.Add("$low", SqliteType.Real);
        var pHigh = cmd.Parameters.Add("$high", SqliteType.Real);
        var pUnit = cmd.Parameters.Add("$unit", SqliteType.Text);
        var pPassed = cmd.Parameters.Add("$passed", SqliteType.Integer);
        var pMessage = cmd.Parameters.Add("$message", SqliteType.Text);
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
        cmd.Prepare();

        foreach (var r in batch)
        {
            pRun.Value = r.RunId.ToString();
            pSn.Value = (object?)r.SerialNumber ?? DBNull.Value;
            pSlot.Value = r.SlotId;
            pSlotName.Value = r.SlotName;
            pStep.Value = r.Step;
            pValue.Value = (object?)r.Value ?? DBNull.Value;
            pLow.Value = (object?)r.Low ?? DBNull.Value;
            pHigh.Value = (object?)r.High ?? DBNull.Value;
            pUnit.Value = (object?)r.Unit ?? DBNull.Value;
            pPassed.Value = r.Passed ? 1 : 0;
            pMessage.Value = (object?)r.Message ?? DBNull.Value;
            pTs.Value = r.Timestamp.ToString("O", CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }
}
