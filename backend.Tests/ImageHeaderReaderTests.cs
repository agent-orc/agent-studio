using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ImageHeaderReaderTests
{
    [Theory]
    [InlineData("png", 320, 180)]
    [InlineData("jpeg", 320, 180)]
    [InlineData("gif", 320, 180)]
    [InlineData("vp8", 320, 180)]
    [InlineData("vp8l", 320, 180)]
    [InlineData("vp8x", 320, 180)]
    public void Reads_dimensions_without_pixels(string format, int width, int height)
    {
        var result = ImageHeaderReader.Read(Header(format));
        Assert.NotNull(result);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(format.StartsWith("vp8") ? "webp" : format, result.Format);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("gif")]
    [InlineData("vp8")]
    [InlineData("vp8l")]
    [InlineData("vp8x")]
    public void Rejects_truncated_and_malformed_headers(string format)
    {
        var valid = Header(format);
        Assert.Null(ImageHeaderReader.Read(valid.AsSpan(0, format == "jpeg" ? valid.Length - 4 : valid.Length - 1)));
        var malformed = valid.ToArray();
        malformed[format switch { "png" => 12, "jpeg" => 6, "gif" => 0, _ => 8 }] = 0;
        Assert.Null(ImageHeaderReader.Read(malformed));
    }

    [Fact]
    public void Reads_only_bounded_prefix_of_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            File.WriteAllBytes(path, PngFixture.Create(320, 180).Concat(new byte[ImageHeaderReader.MaximumHeaderBytes + 1]).ToArray());
            Assert.Equal(320, ImageHeaderReader.ReadFile(path)?.Width);
        }
        finally { File.Delete(path); }
    }

    private static byte[] Header(string format) => format switch
    {
        "png" => PngFixture.Create(320, 180)[..33],
        "jpeg" => [0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0, 180, 1, 64, 3, 1, 0, 2, 0, 3, 0],
        "gif" => [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 64, 1, 180, 0],
        "vp8" => Webp("VP8 ", [0, 0, 0, 0x9d, 1, 0x2a, 64, 1, 180, 0]),
        "vp8l" => Webp("VP8L", [0x2f, 63, 193, 44, 0]),
        "vp8x" => Webp("VP8X", [0, 0, 0, 0, 63, 1, 0, 179, 0, 0]),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static byte[] Webp(string fourcc, byte[] payload)
    {
        var result = new byte[20 + payload.Length];
        "RIFF"u8.CopyTo(result);
        BitConverter.GetBytes(12 + payload.Length).CopyTo(result, 4);
        "WEBP"u8.CopyTo(result.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(fourcc).CopyTo(result, 12);
        BitConverter.GetBytes(payload.Length).CopyTo(result, 16);
        payload.CopyTo(result, 20);
        return result;
    }
}
