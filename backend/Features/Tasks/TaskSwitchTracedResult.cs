using System.Diagnostics;

namespace AgentStudio.Tasks;

/// <summary>Measures the actual response write after the endpoint filter has returned.</summary>
internal sealed class TaskSwitchTracedResult(
    IResult inner,
    TaskSwitchTrace trace,
    (int Spawns, long GitMs, int FileReads)? git,
    int gitTimeouts,
    ILogger logger) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var original = httpContext.Response.Body;
        var counting = new CountingStream(original);
        var started = Stopwatch.GetTimestamp();
        var outcome = "ok";
        try
        {
            httpContext.Response.Body = counting;
            await inner.ExecuteAsync(httpContext);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            outcome = "aborted";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "timeout";
            throw;
        }
        catch
        {
            outcome = "error";
            throw;
        }
        finally
        {
            httpContext.Response.Body = original;
            trace.RecordWrite(started);
            if (httpContext.RequestAborted.IsCancellationRequested) outcome = "aborted";
            logger.LogInformation("task-switch-trace {Trace}",
                trace.Summary(outcome, httpContext.Response.StatusCode, counting.Bytes, git, gitTimeouts));
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        private long _bytes;
        public long Bytes => Interlocked.Read(ref _bytes);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Interlocked.Add(ref _bytes, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Interlocked.Add(ref _bytes, buffer.Length);
        }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken);
            Interlocked.Add(ref _bytes, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Interlocked.Add(ref _bytes, buffer.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
