using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using AgentStudio.Security;

namespace AgentStudio.Telemetry;

public sealed record OtlpIntakeOutcome(int StatusCode, byte[] Body, string ContentType, int? RetryAfterSeconds = null);

/// <summary>
/// OTLP/HTTP intake for <c>/v1/metrics</c> and <c>/v1/logs</c>. Order: media
/// type, ingest token, a still-declared environment, signal scope, room in the
/// queue for the environment, a free request slot (in total and for the
/// environment), bounded body read (gzip allowed), decoding with record, scope,
/// attribute, memory, and stored-byte caps, time-window admission, then a
/// non-blocking enqueue. Body read and decoding
/// share one deadline; past it the request gets 408 and frees its slot.
/// Nothing here touches the store; a full queue or no free slot answers 429
/// with Retry-After.
/// </summary>
public sealed class TelemetryIntakeService
{
    public const int RetryAfterSeconds = 2;
    /// <summary>How long a project's declared environments are trusted before they are read again.</summary>
    public static readonly TimeSpan EnvironmentRecheck = TimeSpan.FromSeconds(30);
    private readonly ITelemetryIngestCredentials _credentials;
    private readonly ITelemetryEnvironmentDirectory _directory;
    private readonly TelemetryIntakeQueue _queue;
    private readonly TelemetryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TelemetryIntakeService> _logger;
    private readonly TelemetryIntakeSlots _slots;
    private readonly ConcurrentDictionary<string, DeclaredEnvironments> _declared = new(StringComparer.OrdinalIgnoreCase);

    public TelemetryIntakeService(ITelemetryIngestCredentials credentials, ITelemetryEnvironmentDirectory directory,
        TelemetryIntakeQueue queue, TelemetryOptions options, TimeProvider clock, ILogger<TelemetryIntakeService> logger)
    {
        _credentials = credentials;
        _directory = directory;
        _queue = queue;
        _options = options;
        _clock = clock;
        _logger = logger;
        _slots = new TelemetryIntakeSlots(options.MaxConcurrentRequests, options.MaxConcurrentRequestsPerEnvironment);
    }

    public async Task<OtlpIntakeOutcome> ReceiveAsync(string signal, string? contentType, string? contentEncoding,
        string? authorization, long? contentLength, Stream body, CancellationToken cancellationToken)
    {
        var encoding = Encoding(contentType);
        if (encoding is null)
            return Error(OtlpEncoding.Json, 415, 3, "Content-Type must be application/x-protobuf or application/json.");
        var principal = _credentials.Authenticate(Bearer(authorization));
        if (principal is null) return Error(encoding.Value, 401, 16, "A valid telemetry ingest token is required.");
        switch (IsDeclared(principal))
        {
            case null:
                return Error(encoding.Value, 503, 14, "The environment declarations cannot be read; retry later.")
                    with { RetryAfterSeconds = RetryAfterSeconds };
            case false:
                return Error(encoding.Value, 401, 16, "The environment of this ingest token is no longer declared.");
        }
        if (!principal.Signals.Contains(signal))
            return Error(encoding.Value, 403, 7, $"The ingest token does not allow {signal}.");
        if (contentLength > _options.MaxRequestBytes) return TooLarge(encoding.Value);

        // Body, decoded records, and the JSON document live only while a slot is held.
        var environment = new TelemetryEnvironmentKey(principal.Project, principal.Environment);
        if (!_queue.HasRoom(environment)) return Busy(encoding.Value);
        using var slot = _slots.TryAcquire(environment);
        if (slot is null) return Busy(encoding.Value);
        using var deadline = new CancellationTokenSource(_options.RequestDeadline, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ReceiveAuthorizedAsync(signal, encoding.Value, contentEncoding, environment, body, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error(encoding.Value, 408, 4,
                $"The request was not received and decoded within {_options.RequestDeadlineSeconds} seconds.");
        }
    }

    private async Task<OtlpIntakeOutcome> ReceiveAuthorizedAsync(string signal, OtlpEncoding encoding, string? contentEncoding,
        TelemetryEnvironmentKey environment, Stream body, CancellationToken cancellationToken)
    {
        byte[]? payload;
        try
        {
            payload = await ReadAsync(body, contentEncoding, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Error(encoding, 400, 3, "The gzip body is corrupt.");
        }
        catch (NotSupportedException)
        {
            return Error(encoding, 415, 3, "Content-Encoding must be gzip or identity.");
        }
        if (payload is null) return TooLarge(encoding);

        var now = _clock.GetUtcNow();
        var factory = new OtlpRecordFactory(OtlpLimits.From(_options), TelemetryAdmissionPolicy.UnixNanos(now), cancellationToken);
        TelemetryIngestBatch batch;
        long rejected;
        string? message;
        try
        {
            if (signal == TelemetrySignals.Metrics)
            {
                var decoded = encoding == OtlpEncoding.Protobuf
                    ? OtlpProtobufDecoder.DecodeMetrics(payload, factory)
                    : OtlpJsonDecoder.DecodeMetrics(payload, factory);
                var admitted = TelemetryAdmissionPolicy.AdmitMetrics(decoded, now, _options);
                (batch, rejected, message) = (new TelemetryIngestBatch(environment, admitted.Items, [], admitted.EstimatedBytes,
                    admitted.StoredBytes), admitted.Rejected, admitted.RejectionMessage);
            }
            else
            {
                var decoded = encoding == OtlpEncoding.Protobuf
                    ? OtlpProtobufDecoder.DecodeLogs(payload, factory)
                    : OtlpJsonDecoder.DecodeLogs(payload, factory);
                var admitted = TelemetryAdmissionPolicy.AdmitLogs(decoded, now, _options);
                (batch, rejected, message) = (new TelemetryIngestBatch(environment, [], admitted.Items, admitted.EstimatedBytes,
                    admitted.StoredBytes), admitted.Rejected, admitted.RejectionMessage);
            }
        }
        catch (OtlpFormatException ex)
        {
            return Error(encoding, 400, 3, ex.Message);
        }
        catch (OtlpLimitException ex)
        {
            return Error(encoding, 413, 8, ex.Message);
        }

        switch (_queue.TryEnqueue(batch))
        {
            case TelemetryEnqueueResult.Busy:
                return Busy(encoding);
            case TelemetryEnqueueResult.TooLarge:
                return TooLarge(encoding);
        }
        var rejectedField = signal == TelemetrySignals.Metrics ? "rejectedDataPoints" : "rejectedLogRecords";
        return new OtlpIntakeOutcome(200, OtlpResponses.Success(encoding, rejected, message, rejectedField),
            OtlpResponses.ContentType(encoding));
    }

    /// <summary>
    /// Whether the token's environment is still declared, so a token stops
    /// working once its environment is removed. Null when the declarations
    /// cannot be read; the sender retries instead of losing its token. Both
    /// answers, the unreadable one included, are kept for
    /// <see cref="EnvironmentRecheck"/>, so a flood of requests reads the
    /// repository files at most once per interval.
    /// </summary>
    private bool? IsDeclared(TelemetryIngestPrincipal principal)
    {
        var now = _clock.GetUtcNow();
        if (!_declared.TryGetValue(principal.Project, out var declared) || now - declared.CheckedAt >= EnvironmentRecheck)
        {
            try
            {
                declared = new DeclaredEnvironments(now, Readable: true, _directory.Environments(principal.Project));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex,
                    "Telemetry intake cannot read the environment declarations of {Project}; ingest requests get 503 for {Seconds} seconds",
                    principal.Project, EnvironmentRecheck.TotalSeconds);
                declared = new DeclaredEnvironments(now, Readable: false, null);
            }
            _declared[principal.Project] = declared;
        }
        if (!declared.Readable) return null;
        return declared.Environments?.Contains(principal.Environment, StringComparer.Ordinal) == true;
    }

    internal static OtlpEncoding? Encoding(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var media)) return null;
        return media.MediaType?.ToLowerInvariant() switch
        {
            "application/x-protobuf" or "application/protobuf" => OtlpEncoding.Protobuf,
            "application/json" => OtlpEncoding.Json,
            _ => null
        };
    }

    private static string? Bearer(string? authorization)
        => authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? authorization[7..].Trim() : null;

    /// <summary>Reads at most <see cref="TelemetryOptions.MaxRequestBytes"/> bytes after decompression; null when larger.</summary>
    private async Task<byte[]?> ReadAsync(Stream body, string? contentEncoding, CancellationToken cancellationToken)
    {
        var compression = contentEncoding?.Trim().ToLowerInvariant();
        if (compression is null or "" or "identity") return await ReadLimitedAsync(body, cancellationToken);
        if (compression != "gzip") throw new NotSupportedException();
        var compressed = await ReadLimitedAsync(body, cancellationToken);
        if (compressed is null) return null;
        await using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        return await ReadLimitedAsync(gzip, cancellationToken);
    }

    private async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while (true)
        {
            // Probe at most one byte beyond the limit. Reading a full chunk here
            // would consume uncharged request bytes from a caller-owned stream.
            var remaining = _options.MaxRequestBytes - (int)buffer.Length;
            var readSize = remaining >= chunk.Length ? chunk.Length : remaining + 1;
            read = await stream.ReadAsync(chunk.AsMemory(0, readSize), cancellationToken);
            if (read == 0) break;
            if (read > remaining) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static OtlpIntakeOutcome Busy(OtlpEncoding encoding)
        => Error(encoding, 429, 14, "Telemetry intake is busy; retry later.") with { RetryAfterSeconds = RetryAfterSeconds };

    private OtlpIntakeOutcome TooLarge(OtlpEncoding encoding)
        => Error(encoding, 413, 8,
            $"The request is larger than the intake limit of {_options.MaxRequestBytes} bytes or {_options.MaxQueuedRecordsPerEnvironment} records.");

    private static OtlpIntakeOutcome Error(OtlpEncoding encoding, int status, int code, string message)
        => new(status, OtlpResponses.Status(encoding, code, message), OtlpResponses.ContentType(encoding));

    private sealed record DeclaredEnvironments(DateTimeOffset CheckedAt, bool Readable, IReadOnlyCollection<string>? Environments);
}
