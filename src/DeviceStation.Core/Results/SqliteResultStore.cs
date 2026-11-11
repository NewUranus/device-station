using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceStation.Core.Results;

public sealed record SqliteResultStoreOptions
{
    /// <summary>一批记录最多尝试写几次（含第一次）。</summary>
    public int MaxWriteAttempts { get; init; } = 3;

    /// <summary>两次尝试之间的等待。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>数据库被别的连接锁住时，单次写入最多等多久（秒）。</summary>
    public int BusyTimeoutSeconds { get; init; } = 5;

    /// <summary>备用文件路径；为空时用 <c>{数据库路径}.pending.jsonl</c>。</summary>
    public string? PendingFilePath { get; init; }
}

/// <summary>
/// SQLite 结果库。设备线程只把记录放进内存队列；一个后台写线程独占连接，按批次在事务里写入。
/// 这样 8 台（或 32 台）设备同时出结果也不会互相抢 SQLite 的写锁，也不会让设备线程等磁盘。
/// <para>
/// 结果不丢：一批写失败会重试（每次重新打开连接）；重试用尽仍失败，就把这一批追加到备用文件
/// （JSON Lines，一行一条）。下次启动时先把备用文件里的记录补进数据库，成功后删除备用文件。
/// </para>
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

    private static readonly JsonSerializerOptions PendingJson = new() { WriteIndented = false };

    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _connectionString;
    private readonly SqliteResultStoreOptions _options;
    private readonly ILogger _logger;
    private readonly Task _writer;

    public SqliteResultStore(string databasePath, ILogger<SqliteResultStore>? logger = null,
        SqliteResultStoreOptions? options = null)
    {
        _options = options ?? new SqliteResultStoreOptions();
        DatabasePath = Path.GetFullPath(databasePath);
        PendingFilePath = Path.GetFullPath(_options.PendingFilePath ?? DatabasePath + ".pending.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
            DefaultTimeout = Math.Max(1, _options.BusyTimeoutSeconds),
        }.ToString();
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }

        ImportPending();
        _writer = Task.Run(WriteLoopAsync);
    }

    public string DatabasePath { get; }

    /// <summary>数据库写不进去时，记录暂存的备用文件。</summary>
    public string PendingFilePath { get; }

    public void Add(ResultRecord record) => _queue.Writer.TryWrite(record);

    /// <summary>等待此前登记的记录全部处理完（写进数据库，或写进备用文件）。</summary>
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
        SqliteConnection? conn = null;
        var batch = new List<ResultRecord>(256);
        var flushes = new List<TaskCompletionSource>();

        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < 500 && _queue.Reader.TryRead(out var item))
                {
                    if (item is ResultRecord r) batch.Add(r);
                    else if (item is TaskCompletionSource tcs) flushes.Add(tcs);
                }

                if (batch.Count > 0)
                {
                    conn = await WriteWithRetryAsync(conn, batch).ConfigureAwait(false);
                    batch.Clear();
                }

                foreach (var f in flushes) f.TrySetResult();
                flushes.Clear();
            }
        }
        finally
        {
            if (conn is not null) await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>写一批：失败就重开连接重试；重试用尽就写备用文件。返回之后可继续使用的连接（可能为空）。</summary>
    private async Task<SqliteConnection?> WriteWithRetryAsync(SqliteConnection? conn, List<ResultRecord> batch)
    {
        int attempts = Math.Max(1, _options.MaxWriteAttempts);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (conn is null)
                {
                    conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);
                }
                WriteBatch(conn, batch);
                if (attempt > 1)
                    _logger.LogInformation("结果库第 {Attempt} 次写入成功（{Count} 条）", attempt, batch.Count);
                return conn;
            }
            catch (Exception ex)
            {
                // 任何异常都不能让写线程退出（否则后面的结果都没人写了）。连接可能已经坏了（文件被删、磁盘错误），下次重新打开。
                if (conn is not null) await conn.DisposeAsync().ConfigureAwait(false);
                conn = null;

                if (attempt < attempts)
                {
                    _logger.LogWarning("写入结果库失败（第 {Attempt}/{Max} 次）：{Error}，{Delay} ms 后重试",
                        attempt, attempts, ex.Message, _options.RetryDelay.TotalMilliseconds);
                    await Task.Delay(_options.RetryDelay).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogError("写入结果库失败 {Max} 次：{Error}；{Count} 条记录转存到备用文件 {File}",
                        attempts, ex.Message, batch.Count, PendingFilePath);
                    AppendPending(batch);
                }
            }
        }
        return conn;
    }

    private void AppendPending(List<ResultRecord> batch)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var r in batch) sb.Append(JsonSerializer.Serialize(r, PendingJson)).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(PendingFilePath)!);
            File.AppendAllText(PendingFilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 数据库和备用文件都写不了：只能在日志里留下记录内容，至少可以人工找回。
            _logger.LogCritical(ex, "备用文件也写入失败，{Count} 条结果丢失", batch.Count);
            foreach (var r in batch) _logger.LogCritical("丢失的结果：{Record}", r);
        }
    }

    /// <summary>启动时把上次没写进数据库的记录补进去；成功后删除备用文件。</summary>
    private void ImportPending()
    {
        if (!File.Exists(PendingFilePath)) return;

        var records = new List<ResultRecord>();
        int bad = 0;
        foreach (var line in File.ReadLines(PendingFilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<ResultRecord>(line, PendingJson) is { } r) records.Add(r);
                else bad++;
            }
            catch (JsonException)
            {
                bad++; // 例如上次写到一半断电留下的半行
            }
        }

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            if (records.Count > 0) WriteBatch(conn, records);
        }
        catch (Exception ex)
        {
            _logger.LogError("补写备用文件中的 {Count} 条结果失败：{Error}；保留备用文件，下次启动再试", records.Count, ex.Message);
            return;
        }

        if (bad > 0)
        {
            // 解析不了的行留个副本以便人工检查，再删除原文件
            var badCopy = PendingFilePath + $".bad-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(PendingFilePath, badCopy, overwrite: true);
            _logger.LogWarning("备用文件中有 {Bad} 行无法解析，原文件已另存为 {Copy}", bad, badCopy);
        }
        try
        {
            File.Delete(PendingFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("备用文件中的结果已补写，但删除备用文件失败：{Error}；请手动删除 {File}，否则下次启动会重复补写",
                ex.Message, PendingFilePath);
            return;
        }
        _logger.LogInformation("已把备用文件中的 {Count} 条结果补写进结果库", records.Count);
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
