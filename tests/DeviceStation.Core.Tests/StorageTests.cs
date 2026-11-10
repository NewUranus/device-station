using DeviceStation.Core.Channels;
using DeviceStation.Core.Devices;
using DeviceStation.Core.Results;
using static DeviceStation.Core.Tests.TestSupport;

namespace DeviceStation.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeviceStationTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    [Fact]
    public void SlotStore_RoundTripsEveryChannelKind_OneFilePerSlot()
    {
        var store = new DeviceSlotStore(_dir);
        DeviceSlot[] slots =
        [
            new() { Id = "S01", Name = "串口工位", Channel = new SerialChannelOptions { PortName = "/dev/tty.usbserial-1", BaudRate = 921600 } },
            new() { Id = "S02", Name = "网口工位", Channel = new TcpChannelOptions { Host = "10.0.0.5", Port = 5000 }, Enabled = false },
            new() { Id = "S03", Name = "模拟工位", Channel = FastSim() with { FailCommands = ["ERASE"], TransientFailures = new Dictionary<string, int> { ["BOOT"] = 1 } } },
        ];
        foreach (var s in slots) store.Save(s);

        Assert.Equal(3, Directory.GetFiles(_dir, "*.json").Length);
        Assert.Contains("\"kind\": \"serial\"", File.ReadAllText(Path.Combine(_dir, "S01.json")));

        var loaded = store.LoadAll();
        Assert.Equal(["S01", "S02", "S03"], loaded.Select(s => s.Id));
        var serial = Assert.IsType<SerialChannelOptions>(loaded[0].Channel);
        Assert.Equal(921600, serial.BaudRate);
        Assert.False(loaded[1].Enabled);
        var sim = Assert.IsType<SimulatedChannelOptions>(loaded[2].Channel);
        Assert.Equal(["ERASE"], sim.FailCommands);
        Assert.Equal(1, sim.TransientFailures["BOOT"]);
    }

    [Fact]
    public void SlotStore_SkipsBrokenAndDuplicateFiles_AndKeepsTheRest()
    {
        var store = new DeviceSlotStore(_dir);
        store.Save(Slot(1));
        store.Save(Slot(2));
        File.WriteAllText(Path.Combine(_dir, "S03.json"), "{ 这不是 JSON");
        File.Copy(Path.Combine(_dir, "S01.json"), Path.Combine(_dir, "S99.json")); // 内容里的 Id 仍是 S01

        var errors = new List<string>();
        var loaded = store.LoadAll((file, _) => errors.Add(Path.GetFileName(file)));

        Assert.Equal(["S01", "S02"], loaded.Select(s => s.Id));
        Assert.Equal(["S03.json", "S99.json"], errors);
    }

    [Fact]
    public async Task SqliteStore_PersistsResultsFromParallelDevices()
    {
        var dbPath = Path.Combine(_dir, "results.db");
        await using (var store = new SqliteResultStore(dbPath))
        {
            await using var manager = Manager(Slots(4), FlashAndTest(), results: store);
            var snaps = await manager.StartAll();
            await store.FlushAsync();

            foreach (var snap in snaps)
            {
                var rows = await store.QueryBySerialAsync(snap.SerialNumber!);
                Assert.Contains(rows, r => r.Step == "RESULT" && r.Passed && r.SlotId == snap.Id);
                Assert.Contains(rows, r => r.Step == "VBAT" && r.Value is > 3.2 and < 3.4);
                Assert.All(rows, r => Assert.Equal(snap.RunId, r.RunId));
            }
        }

        Assert.True(File.Exists(dbPath));
    }
}
