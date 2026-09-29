using System.Text;

namespace AgentStudio.Shared;

/// <summary>
/// Size-bounded reads for files the reader does not control: CLI logs, JSONL
/// ledgers, sidecars, and agent-written <c>results/</c> files. A whole-file
/// read of such a file lets one runaway writer turn every poll, sweep, or
/// request that touches it into an unbounded allocation (AGT-2991).
///
/// <para>
/// Caps are chosen well above the sizes these files reach in normal
/// operation, so a bounded reader sees exactly what the whole-file read saw.
/// Only a pathological file degrades: whole-content readers refuse it and
/// line readers keep the newest line-aligned window.
/// </para>
///
/// <para>
/// Every read opens with <see cref="FileShare.ReadWrite"/> and
/// <see cref="FileShare.Delete"/> because logs and ledgers are appended to
/// while they are read. I/O failures propagate exactly as they do from
/// <see cref="File.ReadAllText(string)"/>, so existing catch blocks keep
/// their meaning.
/// </para>
/// </summary>
public static class BoundedFileRead
{
    /// <summary>Default cap for a single JSON sidecar, receipt, or marker file.</summary>
    public const int SidecarBytes = 1024 * 1024;

    /// <summary>Default cap for a JSONL ledger such as <c>timeline.jsonl</c>.</summary>
    public const int LedgerBytes = 16 * 1024 * 1024;

    /// <summary>Default cap for a text evidence file read whole for inspection.</summary>
    public const int EvidenceTextBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads the whole file as text when it holds at most
    /// <paramref name="maxBytes"/> bytes. Returns <c>false</c> and an empty
    /// string when the file is larger, including when it grows past the cap
    /// during the read. Byte-order marks are honoured like
    /// <see cref="File.ReadAllText(string)"/>.
    /// </summary>
    public static bool TryReadAllText(string path, int maxBytes, out string text)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        text = string.Empty;
        using var stream = OpenShared(path);
        if (stream.Length > maxBytes) return false;

        var bytes = ReadUpTo(stream, maxBytes + 1);
        if (bytes.Length > maxBytes) return false;
        text = Decode(bytes, bytes.Length);
        return true;
    }

    /// <summary>
    /// Returns the whole file as text, or <c>null</c> when it exceeds
    /// <paramref name="maxBytes"/>. Convenience form of
    /// <see cref="TryReadAllText"/> for callers that already treat a missing
    /// value as "unreadable".
    /// </summary>
    public static string? ReadAllTextOrNull(string path, int maxBytes) =>
        TryReadAllText(path, maxBytes, out var text) ? text : null;

    /// <summary>
    /// Reads the file's lines, keeping only the newest window of at most
    /// <paramref name="maxBytes"/> bytes. When the file is larger, the first,
    /// partial line of the window is dropped so every returned line is
    /// complete; a JSONL reader therefore never sees a torn leading row.
    /// </summary>
    public static IReadOnlyList<string> ReadTailLines(string path, int maxBytes) =>
        ReadTailLines(path, maxBytes, out _);

    /// <inheritdoc cref="ReadTailLines(string, int)"/>
    /// <param name="truncated">True when older content was left unread.</param>
    public static IReadOnlyList<string> ReadTailLines(string path, int maxBytes, out bool truncated) =>
        SplitLines(ReadTailText(path, maxBytes, out truncated));

    /// <summary>
    /// Reads the newest window of at most <paramref name="maxBytes"/> bytes
    /// as text. When the file is larger, the window starts after the first
    /// line break inside it, so no partial line or split UTF-8 sequence is
    /// returned. A window with no line break at all yields an empty string.
    /// </summary>
    public static string ReadTailText(string path, int maxBytes, out bool truncated)
    {
        var (_, text, isTruncated) = ReadTailWindowText(path, maxBytes);
        truncated = isTruncated;
        return text;
    }

    /// <summary>
    /// Line form of <see cref="ReadTailText"/> that also returns the byte
    /// offset at which the first returned line starts. A caller that edits a
    /// row inside the window rewrites the file from that offset only, so the
    /// older rows before it keep their bytes and are never loaded
    /// (AGT-2991). The offset is 0 when the whole file fits the window.
    /// </summary>
    public static TailLineWindow ReadTailLineWindow(string path, int maxBytes)
    {
        var (offset, text, truncated) = ReadTailWindowText(path, maxBytes);
        return new TailLineWindow(offset, SplitLines(text), truncated);
    }

    private static (long Offset, string Text, bool Truncated) ReadTailWindowText(string path, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var stream = OpenShared(path);
        var length = stream.Length;
        if (length <= maxBytes)
        {
            var whole = ReadUpTo(stream, maxBytes);
            return (0, Decode(whole, whole.Length), false);
        }

        var start = stream.Seek(-maxBytes, SeekOrigin.End);
        var window = ReadUpTo(stream, maxBytes);
        var newline = Array.IndexOf(window, (byte)'\n');
        if (newline < 0) return (start + window.Length, string.Empty, true);
        return (start + newline + 1,
            Encoding.UTF8.GetString(window, newline + 1, window.Length - newline - 1),
            true);
    }

    /// <summary>
    /// Returns the last <paramref name="maxChars"/> characters of the file,
    /// the same value as <c>File.ReadAllText(path)[^maxChars..]</c> for a
    /// longer file, while reading at most four bytes per character. Like
    /// <see cref="File.ReadAllText(string)"/>, a UTF-16 or UTF-32 byte-order
    /// mark selects the encoding; the tail window is then aligned to that
    /// encoding's code units so it never starts inside one.
    /// </summary>
    public static string ReadTailChars(string path, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        var maxBytes = checked(maxChars * 4);
        using var stream = OpenShared(path);
        if (stream.Length <= maxBytes)
        {
            var whole = ReadUpTo(stream, maxBytes);
            return Suffix(Decode(whole, whole.Length), maxChars);
        }

        var (bomLength, unitBytes, bigEndian) = DetectBom(ReadUpTo(stream, 4));
        // UTF-16 needs two bytes per char; UTF-8 and UTF-32 at most four.
        var windowBytes = unitBytes == 2 ? checked(maxChars * 2) : maxBytes;
        var windowStart = stream.Length - windowBytes;
        windowStart -= (windowStart - bomLength) % unitBytes;
        stream.Seek(windowStart, SeekOrigin.Begin);
        var window = ReadUpTo(stream, (int)(stream.Length - windowStart));

        string text;
        if (unitBytes == 2)
        {
            // Decode code units directly so a surrogate pair cut by the window
            // keeps its low half, exactly as the whole-file suffix does.
            var chars = new char[window.Length / 2];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = bigEndian
                    ? (char)((window[2 * i] << 8) | window[2 * i + 1])
                    : (char)(window[2 * i] | (window[2 * i + 1] << 8));
            text = new string(chars);
        }
        else if (unitBytes == 4)
        {
            text = new UTF32Encoding(bigEndian, byteOrderMark: false).GetString(window);
        }
        else
        {
            // Skip the continuation bytes of a sequence cut by the window start.
            var start = 0;
            while (start < window.Length && start < 3 && (window[start] & 0xC0) == 0x80) start++;
            text = Encoding.UTF8.GetString(window, start, window.Length - start);
        }
        return Suffix(text, maxChars);
    }

    private static string Suffix(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[^maxChars..];

    /// <summary>
    /// Byte-order mark detection matching <see cref="StreamReader"/>: returns
    /// the mark length, the code-unit width, and the byte order. Files without
    /// a mark are UTF-8, the <see cref="File.ReadAllText(string)"/> default.
    /// </summary>
    private static (int BomLength, int UnitBytes, bool BigEndian) DetectBom(byte[] head) => head switch
    {
        [0xFF, 0xFE, 0x00, 0x00, ..] => (4, 4, false),
        [0x00, 0x00, 0xFE, 0xFF, ..] => (4, 4, true),
        [0xFF, 0xFE, ..] => (2, 2, false),
        [0xFE, 0xFF, ..] => (2, 2, true),
        [0xEF, 0xBB, 0xBF, ..] => (3, 1, false),
        _ => (0, 1, false),
    };

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes from the start of the
    /// file as text. A multi-byte UTF-8 sequence cut by the cap is dropped
    /// rather than decoded into a replacement character.
    /// </summary>
    public static string ReadHeadText(string path, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var stream = OpenShared(path);
        var bytes = ReadUpTo(stream, maxBytes);
        var usable = stream.Length > bytes.Length ? CompleteUtf8Prefix(bytes) : bytes.Length;
        return Decode(bytes, usable);
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.SequentialScan);

    private static byte[] ReadUpTo(Stream stream, int limit)
    {
        var buffer = new byte[(int)Math.Min(limit, Math.Max(0, stream.Length - stream.Position) + 1)];
        var read = 0;
        while (true)
        {
            if (read == buffer.Length)
            {
                if (buffer.Length >= limit) break;
                Array.Resize(ref buffer, (int)Math.Min(limit, (long)buffer.Length * 2));
            }
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) break;
            read += count;
        }
        if (read != buffer.Length) Array.Resize(ref buffer, read);
        return buffer;
    }

    private static string Decode(byte[] bytes, int count)
    {
        if (count == 0) return string.Empty;
        using var reader = new StreamReader(
            new MemoryStream(bytes, 0, count, writable: false),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static int CompleteUtf8Prefix(byte[] bytes)
    {
        // Walk back over at most three continuation bytes to the lead byte of
        // the final sequence and drop that sequence when it is incomplete.
        var end = bytes.Length;
        var lead = end - 1;
        while (lead >= 0 && end - lead <= 4 && (bytes[lead] & 0xC0) == 0x80) lead--;
        if (lead < 0) return end;
        var expected = bytes[lead] switch
        {
            < 0x80 => 1,
            >= 0xF0 => 4,
            >= 0xE0 => 3,
            >= 0xC0 => 2,
            _ => 1,
        };
        return end - lead < expected ? lead : end;
    }
}

/// <summary>
/// The newest line-aligned window of a file as returned by
/// <see cref="BoundedFileRead.ReadTailLineWindow"/>.
/// </summary>
/// <param name="Offset">Byte offset of the first line in <paramref name="Lines"/>.</param>
/// <param name="Lines">Complete lines from <paramref name="Offset"/> to the end of the file.</param>
/// <param name="Truncated">True when older content before <paramref name="Offset"/> was left unread.</param>
public sealed record TailLineWindow(long Offset, List<string> Lines, bool Truncated);
