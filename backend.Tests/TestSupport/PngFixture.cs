using System.Buffers.Binary;
using System.IO.Compression;

namespace AgentStudio.Tests;

internal static class PngFixture
{
    internal static byte[] Create(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR"u8, header);
        using var pixels = new MemoryStream();
        using (var compressed = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++) compressed.Write(row);
        }
        WriteChunk(output, "IDAT"u8, pixels.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    internal static void Write(string path, int width, int height) => File.WriteAllBytes(path, Create(width, height));

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, data.Length);
        output.Write(size);
        output.Write(type);
        output.Write(data);
        uint crc = 0xffffffff;
        foreach (var value in type) crc = Update(crc, value);
        foreach (var value in data) crc = Update(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(size, ~crc);
        output.Write(size);
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }
}
