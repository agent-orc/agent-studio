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
    /// Line form of <see cref="TryReadAllText"/> for callers that rewrite the
    /// file from what they read and therefore need every line or none.
    /// Splits exactly like <see cref="File.ReadAllLines(string)"/>.
    /// </summary>
    public static bool TryReadAllLines(string path, int maxBytes, out List<string> lines)
    {
        if (!TryReadAllText(path, maxBytes, out var text))
        {
            lines = [];
            return false;
        }
        lines = SplitLines(text);
        return true;
    }

    /// <summary>
    /// Reads the newest window of at most <paramref name="maxBytes"/> bytes
    /// as text. When the file is larger, the window starts after the first
    /// line break inside it, so no partial line or split UTF-8 sequence is
    /// returned. A window with no line break at all yields an empty string.
    /// </summary>
    public static string ReadTailText(string path, int maxBytes, out bool truncated)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var stream = OpenShared(path);
        var length = stream.Length;
        truncated = length > maxBytes;
        if (!truncated)
        {
            var whole = ReadUpTo(stream, maxBytes);
            return Decode(whole, whole.Length);
        }

        stream.Seek(-maxBytes, SeekOrigin.End);
        var window = ReadUpTo(stream, maxBytes);
        var newline = Array.IndexOf(window, (byte)'\n');
        if (newline < 0) return string.Empty;
        return Encoding.UTF8.GetString(window, newline + 1, window.Length - newline - 1);
    }

    /// <summary>
    /// Returns the last <paramref name="maxChars"/> characters of the file,
    /// the same value as <c>File.ReadAllText(path)[^maxChars..]</c> for a
    /// longer file, while reading at most four bytes per character.
    /// </summary>
    public static string ReadTailChars(string path, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        var maxBytes = checked(maxChars * 4);
        using var stream = OpenShared(path);
        string text;
        if (stream.Length <= maxBytes)
        {
            var whole = ReadUpTo(stream, maxBytes);
            text = Decode(whole, whole.Length);
        }
        else
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
            var window = ReadUpTo(stream, maxBytes);
            // Skip the continuation bytes of a sequence cut by the window start.
            var start = 0;
            while (start < window.Length && start < 3 && (window[start] & 0xC0) == 0x80) start++;
            text = Encoding.UTF8.GetString(window, start, window.Length - start);
        }
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

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
