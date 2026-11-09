using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceStation.Core.Channels;

namespace DeviceStation.Core.Devices;

/// <summary>工位：治具上的一个固定位置，对应一条通道。配置按工位各存一个 JSON 文件。</summary>
public sealed record DeviceSlot
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ChannelOptions Channel { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>工位配置存储：目录下每个工位一个 <c>{Id}.json</c>。</summary>
public sealed class DeviceSlotStore(string directory)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Directory { get; } = directory;

    public IReadOnlyList<DeviceSlot> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(Load)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    public void Save(DeviceSlot slot)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(slot.Id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(slot, JsonOptions));
        File.Move(tmp, path, overwrite: true); // 先写临时文件再替换，避免写一半断电留下坏文件
    }

    public void Delete(string id) => File.Delete(PathFor(id));

    private static DeviceSlot Load(string path)
    {
        var slot = JsonSerializer.Deserialize<DeviceSlot>(File.ReadAllText(path), JsonOptions);
        return slot ?? throw new InvalidDataException($"工位配置为空：{path}");
    }

    private string PathFor(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            if (id.Contains(c)) throw new ArgumentException($"工位 Id 含非法字符：{id}", nameof(id));
        return Path.Combine(Directory, id + ".json");
    }
}
