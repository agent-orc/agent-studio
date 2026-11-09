using System.Buffers.Binary;

namespace AgentStudio.Shared;

/// <summary>Reads image dimensions from bounded container headers without decoding pixels.</summary>
public static class ImageHeaderReader
{
    public const int MaximumHeaderBytes = 65_536;

    public static ImageHeader? ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        var prefix = new byte[33];
        var count = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        if (count < 2 || prefix[0] != 0xff || prefix[1] != 0xd8)
            return Read(prefix.AsSpan(0, count));
        var buffer = new byte[MaximumHeaderBytes];
        prefix.CopyTo(buffer, 0);
        count += stream.ReadAtLeast(buffer.AsSpan(count), MaximumHeaderBytes - count, throwOnEndOfStream: false);
        return Read(buffer.AsSpan(0, count));
    }

    public static ImageHeader? Read(ReadOnlySpan<byte> data)
    {
        data = data[..Math.Min(data.Length, MaximumHeaderBytes)];
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (data.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 ||
                !data.Slice(12, 4).SequenceEqual("IHDR"u8) || !ValidPngFormat(data[24], data[25]) ||
                data[26] != 0 || data[27] != 0 || data[28] > 1)
                return null;
            return Valid("png", BinaryPrimitives.ReadUInt32BigEndian(data[16..]), BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        }
        if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
            return Valid("gif", BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
        if (data.Length >= 20 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            var riffLength = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
            if (riffLength < 12 || chunkLength > riffLength - 12) return null;
            var payload = data[20..];
            if (data.Slice(12, 4).SequenceEqual("VP8X"u8) && chunkLength >= 10 && payload.Length >= 10)
                return Valid("webp", 1u + UInt24(payload[4..]), 1u + UInt24(payload[7..]));
            if (data.Slice(12, 4).SequenceEqual("VP8L"u8) && chunkLength >= 5 && payload.Length >= 5 && payload[0] == 0x2f && (payload[4] & 0xe0) == 0)
                return Valid("webp", 1u + (uint)(payload[1] | ((payload[2] & 0x3f) << 8)),
                    1u + (uint)((payload[2] >> 6) | (payload[3] << 2) | ((payload[4] & 0xf) << 10)));
            if (data.Slice(12, 4).SequenceEqual("VP8 "u8) && chunkLength >= 10 && payload.Length >= 10 &&
                payload.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                return Valid("webp", (uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3fff),
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3fff));
            return null;
        }
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return null;
        for (var offset = 2; offset < data.Length;)
        {
            if (data[offset++] != 0xff) return null;
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset >= data.Length) return null;
            var marker = data[offset++];
            if (marker is 0xd8 or 0xd9 or 0xda) return null;
            if (marker is >= 0xd0 and <= 0xd7 or 0x01) continue;
            if (offset + 2 > data.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (length < 2 || offset + length > data.Length) return null;
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8 || data[offset + 2] == 0) return null;
                return Valid("jpeg", BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]));
            }
            offset += length;
        }
        return null;
    }

    private static uint UInt24(ReadOnlySpan<byte> value) => (uint)(value[0] | value[1] << 8 | value[2] << 16);
    private static bool ValidPngFormat(byte depth, byte color) => color switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => depth is 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        _ => false,
    };
    private static ImageHeader? Valid(string format, uint width, uint height) =>
        width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue
            ? new ImageHeader(format, (int)width, (int)height) : null;
}

public sealed record ImageHeader(string Format, int Width, int Height);
