using DeviceStation.Core.Util;

namespace DeviceStation.Core.Jobs.Examples;

/// <summary>
/// 固件镜像。只加载一次，以 <see cref="ReadOnlyMemory{T}"/> 在所有设备之间共享：
/// 8 台设备同时刷同一个 4 MB 固件也只占一份内存，各设备只是按偏移切片（Slice 不复制）。
/// </summary>
public sealed class FirmwareImage
{
    private FirmwareImage(string name, byte[] data)
    {
        Name = name;
        Data = data;
        Crc32 = Util.Crc32.Compute(data);
    }

    public string Name { get; }

    public ReadOnlyMemory<byte> Data { get; }

    public int Length => Data.Length;

    public uint Crc32 { get; }

    public static async Task<FirmwareImage> LoadAsync(string path, CancellationToken ct = default) =>
        new(Path.GetFileName(path), await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));

    public static FirmwareImage FromBytes(string name, byte[] data) => new(name, data.ToArray());

    /// <summary>生成一份确定性的伪随机固件，用于演示和测试。</summary>
    public static FirmwareImage CreateDemo(int size = 512 * 1024, int seed = 42)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return new FirmwareImage($"demo-{size / 1024}k.bin", data);
    }

    public override string ToString() => $"{Name} ({Length / 1024} KB, CRC32 {Crc32:X8})";
}
