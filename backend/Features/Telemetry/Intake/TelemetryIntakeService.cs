using System.IO.Compression;
using System.Net.Http.Headers;
using AgentStudio.Security;

namespace AgentStudio.Telemetry;

public sealed record OtlpIntakeOutcome(int StatusCode, byte[] Body, string ContentType, int? RetryAfterSeconds = null);

/// <summary>
/// OTLP/HTTP intake for <c>/v1/metrics</c> and <c>/v1/logs</c>. Order: media
/// type, ingest token and signal scope, bounded body read (gzip allowed),
/// decoding, time-window admission, then a non-blocking enqueue. Nothing here
/// touches the store; a full queue answers 429 with Retry-After.
/// </summary>
public sealed class TelemetryIntakeService
{
    public const int RetryAfterSeconds = 2;
    private readonly ITelemetryIngestCredentials _credentials;
    private readonly TelemetryIntakeQueue _queue;
    private readonly TelemetryOptions _options;
    private readonly TimeProvider _clock;

    public TelemetryIntakeService(ITelemetryIngestCredentials credentials, TelemetryIntakeQueue queue,
        TelemetryOptions options, TimeProvider clock)
    {
        _credentials = credentials;
        _queue = queue;
        _options = options;
        _clock = clock;
    }

    public async Task<OtlpIntakeOutcome> ReceiveAsync(string signal, string? contentType, string? contentEncoding,
        string? authorization, long? contentLength, Stream body, CancellationToken cancellationToken)
    {
        var encoding = Encoding(contentType);
        if (encoding is null)
            return Error(OtlpEncoding.Json, 415, 3, "Content-Type must be application/x-protobuf or application/json.");
        var principal = _credentials.Authenticate(Bearer(authorization));
        if (principal is null) return Error(encoding.Value, 401, 16, "A valid telemetry ingest token is required.");
        if (!principal.Signals.Contains(signal))
            return Error(encoding.Value, 403, 7, $"The ingest token does not allow {signal}.");
        if (contentLength > _options.MaxRequestBytes) return TooLarge(encoding.Value);

        byte[]? payload;
        try
        {
            payload = await ReadAsync(body, contentEncoding, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Error(encoding.Value, 400, 3, "The gzip body is corrupt.");
        }
        catch (NotSupportedException)
        {
            return Error(encoding.Value, 415, 3, "Content-Encoding must be gzip or identity.");
        }
        if (payload is null) return TooLarge(encoding.Value);

        var now = _clock.GetUtcNow();
        var factory = new OtlpRecordFactory(OtlpLimits.From(_options), TelemetryAdmissionPolicy.UnixNanos(now));
        var environment = new TelemetryEnvironmentKey(principal.Project, principal.Environment);
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
                (batch, rejected, message) = (new TelemetryIngestBatch(environment, admitted.Items, []), admitted.Rejected, admitted.RejectionMessage);
            }
            else
            {
                var decoded = encoding == OtlpEncoding.Protobuf
                    ? OtlpProtobufDecoder.DecodeLogs(payload, factory)
                    : OtlpJsonDecoder.DecodeLogs(payload, factory);
                var admitted = TelemetryAdmissionPolicy.AdmitLogs(decoded, now, _options);
                (batch, rejected, message) = (new TelemetryIngestBatch(environment, [], admitted.Items), admitted.Rejected, admitted.RejectionMessage);
            }
        }
        catch (OtlpFormatException ex)
        {
            return Error(encoding.Value, 400, 3, ex.Message);
        }

        switch (_queue.TryEnqueue(batch))
        {
            case TelemetryEnqueueResult.Busy:
                return Error(encoding.Value, 429, 14, "Telemetry intake is busy; retry later.") with { RetryAfterSeconds = RetryAfterSeconds };
            case TelemetryEnqueueResult.TooLarge:
                return TooLarge(encoding.Value);
        }
        var rejectedField = signal == TelemetrySignals.Metrics ? "rejectedDataPoints" : "rejectedLogRecords";
        return new OtlpIntakeOutcome(200, OtlpResponses.Success(encoding.Value, rejected, message, rejectedField),
            OtlpResponses.ContentType(encoding.Value));
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
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > _options.MaxRequestBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private OtlpIntakeOutcome TooLarge(OtlpEncoding encoding)
        => Error(encoding, 413, 8, $"The request is larger than the intake limit of {_options.MaxRequestBytes} bytes or {_options.MaxQueuedRecords} records.");

    private static OtlpIntakeOutcome Error(OtlpEncoding encoding, int status, int code, string message)
        => new(status, OtlpResponses.Status(encoding, code, message), OtlpResponses.ContentType(encoding));
}
