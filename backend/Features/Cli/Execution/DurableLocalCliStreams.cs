using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Backend half of the durable worker's stdin channel (AGT-2821). CAR writes
/// the one-shot prompt into this stream and closes it; the bytes land in the
/// worker's <c>input.bin</c> and the close writes the <c>input.done</c> marker
/// with the exact byte count. The worker forwards the file to the real CLI, so
/// the prompt survives the backend that produced it and no anonymous pipe has
/// to stay open between the two processes.
/// </summary>
internal sealed class DurableLocalCliInputStream : Stream
{
    private readonly string _completedPath;
    private readonly FileStream _file;
    private long _written;
    private bool _completed;

    public DurableLocalCliInputStream(string inputPath, string completedPath)
    {
        _completedPath = completedPath;
        _file = new FileStream(
            inputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.WriteThrough);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _file.Write(buffer);
        _written += buffer.Length;
        _file.Flush();
    }

    public override void Flush() => _file.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            _completed = true;
            try
            {
                _file.Flush();
                _file.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                SilentCatch.Note(ex, "DurableLocalCliInputStream: prompt file flush");
            }

            // The marker carries the byte count so the worker can tell a fully
            // written prompt apart from a file it observed mid-write.
            try
            {
                DurableLocalCliProcess.WriteAtomic(
                    _completedPath,
                    _written.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "DurableLocalCliInputStream: prompt completion marker");
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Backend half of the durable worker's output channel (AGT-2821). It tails the
/// worker's append-only <c>output.jsonl</c> and replays the lines of one stream
/// ("stdout" or "stderr") as a byte stream, so CAR's <see cref="StreamReader"/>
/// sees exactly what the CLI wrote. The file is also what a replacement backend
/// reads after a restart, so live and recovered delivery share one source.
/// <para>End of stream is the worker's terminal result (or a worker that is
/// gone) plus a final drain, so CAR's post-exit read drain always completes.
/// </para>
/// </summary>
internal sealed class DurableLocalCliOutputStream : Stream
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly string _logPath;
    private readonly string _resultPath;
    private readonly string _stream;
    private readonly int _processId;
    private long _offset;
    private byte[] _pending = [];
    private int _pendingRead;
    private bool _terminalObserved;
    private Process? _worker;
    private bool _workerGone;

    public DurableLocalCliOutputStream(string logPath, string resultPath, int processId, string stream)
    {
        _logPath = logPath;
        _resultPath = resultPath;
        _processId = processId;
        _stream = stream;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _offset;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (TryDrain(buffer.AsSpan(offset, count), out var read)) return read;
            if (IsFinished()) return 0;
            Thread.Sleep(PollInterval);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDrain(buffer.Span, out var read)) return read;
            if (IsFinished()) return 0;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Copies whatever is already decoded into <paramref name="destination"/>,
    /// refilling from the worker log first. False means nothing is available
    /// yet, which is a wait, not an end of stream.
    /// </summary>
    private bool TryDrain(Span<byte> destination, out int read)
    {
        read = 0;
        if (destination.Length == 0) return true;
        if (_pendingRead >= _pending.Length && !Refill()) return false;

        read = Math.Min(destination.Length, _pending.Length - _pendingRead);
        _pending.AsSpan(_pendingRead, read).CopyTo(destination);
        _pendingRead += read;
        return read > 0;
    }

    /// <summary>
    /// Reads the complete lines the worker has appended since the last call and
    /// turns the ones belonging to this stream back into raw CLI output. A
    /// trailing partial line is left for the next poll: the offset only ever
    /// advances past a newline.
    /// </summary>
    private bool Refill()
    {
        _pending = [];
        _pendingRead = 0;
        if (!File.Exists(_logPath)) return false;

        byte[] raw;
        try
        {
            using var stream = new FileStream(
                _logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= _offset) return false;
            stream.Seek(_offset, SeekOrigin.Begin);
            raw = new byte[stream.Length - _offset];
            var filled = stream.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false);
            if (filled < raw.Length) raw = raw[..filled];
        }
        catch (IOException ex)
        {
            SilentCatch.Note(ex, "DurableLocalCliOutputStream: worker log temporarily unreadable");
            return false;
        }

        var lastNewline = Array.LastIndexOf(raw, (byte)'\n');
        if (lastNewline < 0) return false;
        _offset += lastNewline + 1;

        var text = Encoding.UTF8.GetString(raw, 0, lastNewline + 1);
        var decoded = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            // A worker started by an older build prefixes its log with a UTF-8
            // byte-order mark, and a Windows worker terminates lines with CRLF.
            // Neither is part of the JSON record.
            var payload = line.Trim('\uFEFF', '\r', ' ', '\t');
            if (payload.Length == 0) continue;
            DurableLocalCliLogLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize<DurableLocalCliLogLine>(payload, DurableLocalCliProcess.Json);
            }
            catch (JsonException ex)
            {
                // A line the worker could not complete is never replayed twice:
                // the offset has already moved past it.
                SilentCatch.Note(ex, "DurableLocalCliOutputStream: unreadable worker log line");
                continue;
            }
            if (entry is null) continue;
            if (!string.Equals(entry.Stream, _stream, StringComparison.OrdinalIgnoreCase)) continue;
            decoded.Append(entry.Text).Append('\n');
        }

        if (decoded.Length == 0) return false;
        _pending = Encoding.UTF8.GetBytes(decoded.ToString());
        return _pending.Length > 0;
    }

    /// <summary>
    /// The worker has reached its terminal result, or is gone. The first
    /// observation only arms the end: one more drain runs so the lines written
    /// immediately before the result are still delivered.
    /// </summary>
    private bool IsFinished()
    {
        if (_terminalObserved) return true;
        if (!File.Exists(_resultPath) && !WorkerIsGone()) return false;
        _terminalObserved = true;
        return false;
    }

    private bool WorkerIsGone()
    {
        if (_workerGone) return true;
        try
        {
            _worker ??= Process.GetProcessById(_processId);
            if (!_worker.HasExited) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SilentCatch.Note(ex, "DurableLocalCliOutputStream: worker process handle");
        }

        _workerGone = true;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _worker?.Dispose(); }
            catch (Exception ex) { SilentCatch.Note(ex, "DurableLocalCliOutputStream: worker handle dispose"); }
            _worker = null;
        }

        base.Dispose(disposing);
    }
}
