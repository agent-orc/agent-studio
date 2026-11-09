using System.Net.Http.Headers;
using System.Text;

namespace AgentStudio.Security;

/// <summary>
/// The off-host, append-only copy of the control plane audit (security model
/// SM-1, D4 option A2). An adapter appends stored lines unchanged and reads
/// them back for verification. Appends are idempotent per sequence: a batch
/// re-sent after a crash between acknowledgement and the recorded position
/// must not create a second copy or overwrite the first.
/// </summary>
public interface IControlPlaneAuditSink
{
    /// <summary>The adapter name shown in the activity view, for example <c>https</c>.</summary>
    string Kind { get; }

    Task AppendAsync(IReadOnlyList<ControlPlaneAuditEntry> entries, CancellationToken ct);

    /// <summary>Stored lines with a sequence of at least <paramref name="fromSequence"/>, oldest first.</summary>
    Task<IReadOnlyList<string>> ReadAsync(long fromSequence, int limit, CancellationToken ct);
}

/// <summary>Sink configuration under <c>ControlPlaneAudit:Sink</c>.</summary>
public sealed record ControlPlaneAuditSinkOptions(string Kind, Uri? Url, string? AppendTokenFile, string? ReadTokenFile)
{
    public const string None = "none";
    public const string Https = "https";

    public bool Configured => Kind != None;

    public static ControlPlaneAuditSinkOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("ControlPlaneAudit:Sink");
        var kind = (section["Kind"] ?? None).Trim().ToLowerInvariant();
        if (kind is not (None or Https))
            throw new InvalidOperationException($"ControlPlaneAudit:Sink:Kind must be '{None}' or '{Https}'.");
        if (kind == None) return new ControlPlaneAuditSinkOptions(None, null, null, null);
        if (!Uri.TryCreate(section["Url"], UriKind.Absolute, out var url))
            throw new InvalidOperationException("ControlPlaneAudit:Sink:Url must be an absolute URL.");
        // Plain HTTP is accepted only on loopback, for a local relay or a test sink.
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            throw new InvalidOperationException("ControlPlaneAudit:Sink:Url must use HTTPS.");
        return new ControlPlaneAuditSinkOptions(Https, url, section["AppendTokenFile"], section["ReadTokenFile"]);
    }
}

/// <summary>
/// Appends NDJSON batches to an HTTPS endpoint (<c>POST {url}/entries</c>) and
/// reads them back (<c>GET {url}/entries?from=&amp;limit=</c>). The append
/// credential should allow appending only; the endpoint, or the object store
/// with write-once retention behind it, must refuse every change or deletion.
/// Credentials are read from files on each call so rotation needs no restart.
/// </summary>
public sealed class HttpsControlPlaneAuditSink(HttpClient http, ControlPlaneAuditSinkOptions options) : IControlPlaneAuditSink
{
    public const string HttpClientName = "control-plane-audit-sink";

    public string Kind => ControlPlaneAuditSinkOptions.Https;

    public async Task AppendAsync(IReadOnlyList<ControlPlaneAuditEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("entries"))
        {
            Content = new StringContent(string.Concat(entries.Select(entry => entry.Raw + "\n")), Encoding.UTF8, "application/x-ndjson"),
        };
        Authorize(request, options.AppendTokenFile);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The audit sink answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public async Task<IReadOnlyList<string>> ReadAsync(long fromSequence, int limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint($"entries?from={fromSequence}&limit={limit}"));
        Authorize(request, options.ReadTokenFile ?? options.AppendTokenFile);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The audit sink answered {(int)response.StatusCode}.", null, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
    }

    private Uri Endpoint(string relative)
    {
        var root = options.Url!.ToString();
        return new Uri(root.EndsWith('/') ? root + relative : root + "/" + relative);
    }

    private static void Authorize(HttpRequestMessage request, string? tokenFile)
    {
        if (string.IsNullOrWhiteSpace(tokenFile)) return;
        var token = File.ReadAllText(tokenFile).Trim();
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
