using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner;

public enum ProviderProbeOutcome
{
    Healthy,
    Indeterminate,
    CredentialInvalid,
    ProviderIncident,
    QuotaExhausted,
    NetworkFailure,
}

public sealed record ProviderIncidentEvidence(
    string Id, string Provider, string Service, DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt, DateTimeOffset ObservedAt, string Provenance);

public sealed record ProviderIncidentSnapshot(
    IReadOnlyList<ProviderIncidentEvidence> Incidents, DateTimeOffset RetrievedAt,
    bool Available, string Provenance);

public sealed record ProviderComparisonEvidence(
    string Provider, string Service, string RequestShape, string FailureSignature,
    DateTimeOffset LastIndependentSuccessAt, DateTimeOffset ObservedAt,
    bool IndependentCredential, bool ComparableEndpoint,
    string HostId = "", string CredentialIdentity = "", bool ExecutedOnComparisonHost = false);

public sealed record ProviderComparisonQuery(
    string Provider, string Service, string RequestShape, string FailureSignature,
    string EffectiveSource, string? Generation);

public sealed record ProviderComparisonSnapshot(
    string? CredentialIdentity, ProviderComparisonEvidence? Comparison);

public sealed record ProviderProbeRequest(
    string Provider, string AccountMode, string Service, string EffectiveSource,
    string? Generation, DateTimeOffset ObservedAt, ProcessResult? RealRequest = null,
    bool StatusCommandUnsupported = false, DateTimeOffset? ExplicitNonRefreshableExpiry = null,
    IReadOnlyList<ProviderIncidentEvidence>? Incidents = null,
    ProviderComparisonEvidence? Comparison = null, string RequestShape = "minimal-text-v1",
    string HostId = "", string CredentialIdentity = "");

public sealed record ProviderProbeDecision(
    ProviderProbeOutcome Outcome, string Detail, string? FailureSignature = null,
    string? EvidenceId = null, DateTimeOffset? ResetAt = null);

/// <summary>Pure decision boundary. Status-page facts and CLI output enter as bounded observations.</summary>
public static partial class ProviderProbeClassifier
{
    public static readonly TimeSpan IncidentFreshness = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan KnownGoodFreshness = TimeSpan.FromMinutes(10);

    public static ProviderProbeDecision Classify(ProviderProbeRequest request)
    {
        if (request.EffectiveSource == "absent")
            return new(ProviderProbeOutcome.CredentialInvalid, "Effective credential source is absent.");
        if (request.ExplicitNonRefreshableExpiry is { } expiry && expiry <= request.ObservedAt)
            return new(ProviderProbeOutcome.CredentialInvalid, "Issuer-confirmed nonrefreshable credential expired.");
        if (request.RealRequest is null)
            return new(ProviderProbeOutcome.Indeterminate, request.StatusCommandUnsupported
                ? "Installed CLI does not support the local status command; real access remains unverified."
                : "No real provider request has been observed.");

        var result = request.RealRequest;
        if (result.ExitCode == 0)
            return new(ProviderProbeOutcome.Healthy, "Real provider request succeeded.");

        var output = $"{result.StdOut}\n{result.StdErr}";
        if (NetworkRegex().IsMatch(output))
            return new(ProviderProbeOutcome.NetworkFailure, "Provider request failed before a trustworthy authentication response.");
        if (QuotaRegex().IsMatch(output))
        {
            var match = ResetRegex().Match(output);
            var reset = match.Success && DateTimeOffset.TryParse(match.Groups[1].Value, out var at) ? at : (DateTimeOffset?)null;
            return new(ProviderProbeOutcome.QuotaExhausted, "Provider reported account quota exhaustion.", ResetAt: reset);
        }
        // A generic 429 may be transient rate limiting; the account-quota path
        // requires an explicit exhausted/insufficient quota signature above.
        var unauthorized = UnauthorizedRegex().IsMatch(output);
        if (!unauthorized)
            return new(ProviderProbeOutcome.Indeterminate, "Provider request failed without a classified cause.");

        var signature = output.Contains("sk-svcacct-", StringComparison.OrdinalIgnoreCase)
            ? "unauthorized:service-account-shaped"
            : "unauthorized";
        var incident = request.Incidents?.FirstOrDefault(item =>
            item.Provenance == "official-status"
            && item.Provider == request.Provider && item.Service == request.Service
            && item.StartedAt <= request.ObservedAt
            && (item.ResolvedAt is null || item.ResolvedAt >= request.ObservedAt)
            && item.ObservedAt <= request.ObservedAt.AddMinutes(1)
            && request.ObservedAt - item.ObservedAt <= IncidentFreshness);
        if (incident is not null)
            return new(ProviderProbeOutcome.ProviderIncident, "Fresh applicable official incident corroborates the unauthorized request.", signature, incident.Id);

        var comparison = request.Comparison;
        if (comparison is not null && comparison.IndependentCredential && comparison.ComparableEndpoint
            && comparison.ExecutedOnComparisonHost
            && request.HostId.Length > 0 && comparison.HostId.Length > 0
            && request.CredentialIdentity.Length > 0 && comparison.CredentialIdentity.Length > 0
            && comparison.HostId != request.HostId && comparison.CredentialIdentity != request.CredentialIdentity
            && comparison.Provider == request.Provider && comparison.Service == request.Service
            && comparison.RequestShape == request.RequestShape && comparison.FailureSignature == signature
            && comparison.LastIndependentSuccessAt <= comparison.ObservedAt
            && comparison.ObservedAt <= request.ObservedAt.AddMinutes(1)
            && request.ObservedAt - comparison.ObservedAt <= KnownGoodFreshness
            && comparison.ObservedAt - comparison.LastIndependentSuccessAt <= KnownGoodFreshness)
            return new(ProviderProbeOutcome.ProviderIncident, "Independent known-good host reproduced the comparable request failure.", signature);

        // Repetition and a successful feed lookup do not prove credential refresh.
        // Until a trusted renewal observation exists, an uncorroborated 401 stays unknown.
        return new(ProviderProbeOutcome.Indeterminate, "Unauthorized request needs incident or independent corroboration.", signature);
    }

    [GeneratedRegex(@"(?i)\b(?:dns|enotfound|eai_again|tls|certificate|connection refused|network unreachable|timed? out|timeout|econnreset)\b")]
    private static partial Regex NetworkRegex();
    [GeneratedRegex(@"(?i)\b(?:insufficient_quota|quota exhausted|quota exceeded|usage limit reached|session limit reached)\b")]
    private static partial Regex QuotaRegex();
    [GeneratedRegex(@"(?i)\b(?:401|unauthorized|incorrect api key|invalid api key|invalid_grant|refresh token expired)\b")]
    private static partial Regex UnauthorizedRegex();
    [GeneratedRegex(@"(?i)resets? at\s+(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)")]
    private static partial Regex ResetRegex();
}

/// <summary>One cached official status retrieval per provider, with a bounded response and no HTML storage.</summary>
public sealed class ProviderStatusIncidentAdapter
{
    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, (DateTimeOffset At, Task<ProviderIncidentSnapshot> Value)> _cache = new();
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public ProviderStatusIncidentAdapter(Func<string, CancellationToken, Task<string>> fetch, Func<DateTimeOffset>? clock = null)
    { _fetch = fetch; _clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public Task<ProviderIncidentSnapshot> GetAsync(string provider, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(provider, out var entry) && _clock() - entry.At < CacheTtl)
                return entry.Value.WaitAsync(ct);
            // The shared retrieval is bounded by its own timeout only, so one
            // cancelled caller cannot cache "unavailable" for the others.
            var task = FetchAsync(provider);
            _cache[provider] = (_clock(), task);
            return task.WaitAsync(ct);
        }
    }

    private async Task<ProviderIncidentSnapshot> FetchAsync(string provider)
    {
        if (provider is not ("codex" or "claude")) return new([], _clock(), false, "unsupported-provider");
        using var bounded = new CancellationTokenSource(Timeout);
        try
        {
            var json = await _fetch(provider, bounded.Token);
            if (json.Length > 256_000) return new([], _clock(), false, "oversized-official-response");
            using var document = JsonDocument.Parse(json);
            var observed = _clock();
            var incidents = new List<ProviderIncidentEvidence>();
            foreach (var item in document.RootElement.GetProperty("incidents").EnumerateArray().Take(50))
            {
                var id = item.GetProperty("id").GetString();
                var name = item.GetProperty("name").GetString() ?? "";
                if (item.TryGetProperty("components", out var components)
                    && components.ValueKind == JsonValueKind.Array)
                    name += " " + string.Join(' ', components.EnumerateArray()
                        .Take(20)
                        .Select(component => component.TryGetProperty("name", out var componentName)
                            ? componentName.GetString() : null));
                if (id is null || id.Length > 120 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) continue;
                var service = ServiceFor(provider, name);
                if (service is null) continue;
                var started = item.GetProperty("created_at").GetDateTimeOffset();
                DateTimeOffset? resolved = item.TryGetProperty("resolved_at", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetDateTimeOffset() : null;
                incidents.Add(new(id, provider, service, started, resolved, observed, "official-status"));
            }
            return new(incidents, observed, true, "official-status");
        }
        catch (Exception)
        {
            // Any feed failure is missing evidence, never a classification input.
            return new([], _clock(), false, "official-status-unavailable");
        }
    }

    private static string? ServiceFor(string provider, string name)
    {
        var text = name.ToLowerInvariant();
        // Broad platform incidents require a named affected service. Avoid
        // mapping unrelated ChatGPT, Sora, Console or API incidents to Codex.
        var accessPath = new[] { "auth", "login", "request", "error", "outage", "degrad", "api" }
            .Any(term => text.Contains(term, StringComparison.Ordinal));
        var unrelatedDependency = new[] { "github", "webhook", "file creation", "workspace setup" }
            .Any(term => text.Contains(term, StringComparison.Ordinal));
        if (!accessPath || unrelatedDependency) return null;
        if (provider == "codex" && text.Contains("codex")) return "codex-exec";
        if (provider == "claude" && (text.Contains("claude code") || text.Contains("claude api"))) return "claude-code";
        return null;
    }

    public static ProviderStatusIncidentAdapter Official(HttpClient client)
        => new(async (provider, ct) =>
        {
            var host = provider == "codex" ? "status.openai.com" : "status.claude.com";
            using var response = await client.GetAsync($"https://{host}/api/v2/incidents.json", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(256_000, ct);
            return await response.Content.ReadAsStringAsync(ct);
        });
}
