using DeviceStation.Core.Logging;
using DeviceStation.Core.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static DeviceStation.Core.Tests.TestSupport;

namespace DeviceStation.Core.Tests;

/// <summary>追溯相关：日志按设备落盘、结果库写不进去时不丢数据。</summary>
public sealed class TraceabilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeviceStationTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    [Fact]
    public async Task FileLog_SplitsByDevice_AndCarriesRunId()
    {
        await using var fileLog = new DeviceFileLoggerProvider(Path.Combine(_dir, "logs"));
        using (var factory = LoggerFactory.Create(b => b.AddProvider(fileLog)))
        {
            var logger = factory.CreateLogger("DeviceStation.Device");
            using (logger.BeginScope("DeviceId:{DeviceId} RunId:{RunId}", "S01", "ab12cd34"))
                logger.LogWarning("步骤 写入 第 1/4 次失败");
            using (logger.BeginScope("DeviceId:{DeviceId} RunId:{RunId}", "S02", "ffff0000"))
                logger.LogError(new IOException("端口已断开"), "连接失败");
            factory.CreateLogger("DeviceStation.Startup").LogInformation("启动完成");
            await fileLog.FlushAsync();
        }

        var s01 = await File.ReadAllTextAsync(fileLog.PathFor("S01", Today));
        Assert.Contains("WARN  run=ab12cd34 步骤 写入 第 1/4 次失败", s01);
        Assert.DoesNotContain("连接失败", s01);

        var s02 = await File.ReadAllTextAsync(fileLog.PathFor("S02", Today));
        Assert.Contains("ERROR run=ffff0000 连接失败", s02);
        Assert.Contains("System.IO.IOException: 端口已断开", s02); // 异常堆栈也写进去

        var app = await File.ReadAllTextAsync(fileLog.PathFor(null, Today));
        Assert.Contains("DeviceStation.Startup: 启动完成", app);
    }

    [Fact]
    public async Task FileLog_RealRun_WritesEachDevicesLogToItsOwnFile()
    {
        await using var fileLog = new DeviceFileLoggerProvider(Path.Combine(_dir, "logs"));
        using var factory = LoggerFactory.Create(b => b.AddProvider(fileLog));
        await using (var manager = new Execution.DeviceManager(Slots(3), _ => FlashAndTest(), loggerFactory: factory))
        {
            var snaps = await manager.StartAll();
            await fileLog.FlushAsync();

            foreach (var snap in snaps)
            {
                var text = await File.ReadAllTextAsync(fileLog.PathFor(snap.Id, Today));
                Assert.Contains($"run={snap.RunId.ToString("N")[..8]}", text); // 能和结果库 run_id 对上
                Assert.Contains($"SN={snap.SerialNumber}", text);
            }
        }
    }

    [Fact]
    public void FileLog_DeletesFilesOlderThanRetention()
    {
        var logs = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(Path.Combine(logs, "S01"));
        var old = Path.Combine(logs, "S01", Today.AddDays(-31).ToString("yyyy-MM-dd") + ".log");
        var recent = Path.Combine(logs, "S01", Today.AddDays(-29).ToString("yyyy-MM-dd") + ".log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");

        using var _ = new DeviceFileLoggerProvider(logs, retentionDays: 30);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task ResultStore_WhenDatabaseStaysLocked_SavesToPendingFile_AndImportsOnNextStart()
    {
        var dbPath = Path.Combine(_dir, "results.db");
        var options = new SqliteResultStoreOptions
        {
            MaxWriteAttempts = 2,
            RetryDelay = TimeSpan.FromMilliseconds(50),
            BusyTimeoutSeconds = 1,
        };
        var records = Enumerable.Range(1, 3).Select(i => Record($"SN{i}")).ToList();

        string pendingFile;
        await using (var store = new SqliteResultStore(dbPath, options: options))
        {
            pendingFile = store.PendingFilePath;
            using var locker = Lock(dbPath); // 另一个连接一直占着写锁
            foreach (var r in records) store.Add(r);
            await store.FlushAsync();

            Assert.True(File.Exists(pendingFile));
            Assert.Equal(3, File.ReadAllLines(pendingFile).Length);
        }

        // 锁已释放：下次启动时补写，并删除备用文件
        await using (var store = new SqliteResultStore(dbPath, options: options))
        {
            Assert.False(File.Exists(pendingFile));
            foreach (var r in records)
            {
                var row = Assert.Single(await store.QueryBySerialAsync(r.SerialNumber!));
                Assert.Equal(r, row);
            }
        }
    }

    [Fact]
    public async Task ResultStore_WhenLockIsReleasedBeforeRetriesRunOut_WritesToDatabase()
    {
        var dbPath = Path.Combine(_dir, "results.db");
        var options = new SqliteResultStoreOptions
        {
            MaxWriteAttempts = 5,
            RetryDelay = TimeSpan.FromMilliseconds(200),
            BusyTimeoutSeconds = 1,
        };

        await using var store = new SqliteResultStore(dbPath, options: options);
        var locker = Lock(dbPath);
        store.Add(Record("SN-RETRY"));
        _ = Task.Delay(1500).ContinueWith(_ => locker.Dispose()); // 第一次尝试失败之后才释放锁
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(File.Exists(store.PendingFilePath));
        Assert.Single(await store.QueryBySerialAsync("SN-RETRY"));
    }

    [Fact]
    public async Task ResultStore_PendingFileWithBrokenLine_ImportsGoodLines_AndKeepsACopy()
    {
        var dbPath = Path.Combine(_dir, "results.db");
        var good = Record("SN-GOOD");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(dbPath + ".pending.jsonl",
            System.Text.Json.JsonSerializer.Serialize(good) + "\n{\"RunId\":\"半行，断电");

        await using var store = new SqliteResultStore(dbPath);

        Assert.Single(await store.QueryBySerialAsync("SN-GOOD"));
        Assert.False(File.Exists(store.PendingFilePath));
        Assert.Single(Directory.GetFiles(_dir, "results.db.pending.jsonl.bad-*"));
    }

    private static ResultRecord Record(string sn) => new(
        Guid.NewGuid(), sn, "S01", "工位 1", "VBAT", 3.3, 3.2, 3.4, "V", true, null,
        new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(8)));

    /// <summary>用另一个连接拿住数据库的独占写锁，模拟“数据库被占用/写不进去”。</summary>
    private static SqliteConnection Lock(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "BEGIN EXCLUSIVE;";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
