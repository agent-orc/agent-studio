using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner.Tests;

/// <summary>Generates small, header-valid screenshot images and their run artifacts in memory.</summary>
internal static class VisualScreenshotFixture
{
    /// <summary>An RGB PNG with real IHDR, IDAT, and IEND chunks.</summary>
    internal static byte[] Png(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        WritePngChunk(output, "IHDR"u8, header);
        using var pixels = new MemoryStream();
        using (var compressed = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++) compressed.Write(row);
        }
        WritePngChunk(output, "IDAT"u8, pixels.ToArray());
        WritePngChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>A lossless (VP8L) WebP container whose header states the given size.</summary>
    internal static byte[] Webp(int width, int height)
    {
        var w = (uint)(width - 1);
        var h = (uint)(height - 1);
        byte[] payload =
        [
            0x2f,
            (byte)(w & 0xff),
            (byte)(((w >> 8) & 0x3f) | ((h & 0x03) << 6)),
            (byte)((h >> 2) & 0xff),
            (byte)((h >> 10) & 0x0f),
            0, 0, 0,
        ];
        var result = new byte[20 + payload.Length];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), 12 + payload.Length);
        "WEBP"u8.CopyTo(result.AsSpan(8));
        "VP8L"u8.CopyTo(result.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), payload.Length);
        payload.CopyTo(result, 20);
        return result;
    }

    internal static ArtifactDto Artifact(string id, string runId, string name, string mediaType, byte[] bytes) => new(
        id, runId, name, mediaType, Convert.ToHexStringLower(SHA256.HashData(bytes)),
        bytes.Length, "key-" + id, 1, DateTime.UnixEpoch);

    internal static ArtifactContentDto Content(ArtifactDto artifact, byte[] bytes) => new(
        artifact.ArtifactId, artifact.RunId, artifact.Name, artifact.MediaType,
        artifact.Sha256, Convert.ToBase64String(bytes), artifact.SizeBytes);

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, data.Length);
        output.Write(size);
        output.Write(type);
        output.Write(data);
        uint crc = 0xffffffff;
        foreach (var value in type) crc = Crc(crc, value);
        foreach (var value in data) crc = Crc(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(size, ~crc);
        output.Write(size);
    }

    private static uint Crc(uint crc, byte value)
    {
        crc ^= value;
        for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }
}
