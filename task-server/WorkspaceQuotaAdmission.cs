using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public sealed record WorkspaceQuotaVerdict(bool Allowed, string? Message, bool MeterUnavailable = false);

public interface IWorkspaceQuotaAdmission
{
    Task<WorkspaceQuotaVerdict> AdmitAsync(string projectId, CancellationToken ct);
}

/// <summary>Management status values for <c>TaskServerStatusDto.QuotaAdmission</c>.</summary>
public static class WorkspaceQuotaAdmissionStates
{
    /// <summary>No meter URL is configured: claims are not metered and raise no quota events.</summary>
    public const string Off = "off";
    public const string On = "on";
    /// <summary>The latest verdict of at least one project says the meter is unavailable.</summary>
    public const string Unreachable = "unreachable";
}

/// <summary>A project's meter changed between available and unavailable.</summary>
public sealed record WorkspaceQuotaTransition(string ProjectName, bool MeterUnavailable, string? Message);

/// <summary>
/// Per-project admission verdicts. The store refreshes them outside its write
/// gate and claim transactions read only this cache, so no SQLite write waits on
/// the monolith. A project is asked at most once per <see cref="Ttl"/>, and
/// concurrent refreshes of one project share one request.
/// </summary>
public sealed class WorkspaceQuotaVerdictCache(IWorkspaceQuotaAdmission admission, TimeProvider clock)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<WorkspaceQuotaTransition?>>> _inFlight =
        new(StringComparer.Ordinal);

    // CardNoticePending: the next claimed card of this project still owes one meter-unavailable event.
    private sealed record Entry(WorkspaceQuotaVerdict Verdict, DateTimeOffset FetchedAt, bool CardNoticePending);

    public string State => _entries.Values.Any(entry => entry.Verdict.MeterUnavailable)
        ? WorkspaceQuotaAdmissionStates.Unreachable : WorkspaceQuotaAdmissionStates.On;

    /// <summary>Latest verdict, or null before the first answer (the caller admits without an event).</summary>
    public WorkspaceQuotaVerdict? Read(string projectName)
        => _entries.TryGetValue(projectName, out var entry) ? entry.Verdict : null;

    /// <summary>Returns the outage message once per outage, for the first claimed card of the project.</summary>
    public string? TryConsumeCardNotice(string projectName)
    {
        while (_entries.TryGetValue(projectName, out var entry) && entry.CardNoticePending)
            if (_entries.TryUpdate(projectName, entry with { CardNoticePending = false }, entry))
                return entry.Verdict.Message ?? "Quota meter is unavailable.";
        return null;
    }

    /// <summary>
    /// Asks the meter for every listed project whose verdict is missing or older
    /// than the TTL. Returns the availability transitions this call observed; a
    /// joined in-flight request reports its transition only to its owner.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceQuotaTransition>> RefreshAsync(
        IEnumerable<string> projectNames, CancellationToken ct)
    {
        var owned = new List<Task<WorkspaceQuotaTransition?>>();
        var joined = new List<Task<WorkspaceQuotaTransition?>>();
        foreach (var name in projectNames.Distinct(StringComparer.Ordinal))
        {
            if (IsFresh(name)) continue;
            var mine = new Lazy<Task<WorkspaceQuotaTransition?>>(() => FetchAsync(name));
            var shared = _inFlight.GetOrAdd(name, mine);
            if (ReferenceEquals(shared, mine)) owned.Add(RunOwnedAsync(name, mine));
            else joined.Add(shared.Value);
        }
        await Task.WhenAll(joined.Concat(owned)).WaitAsync(ct);
        return owned.Select(task => task.Result).OfType<WorkspaceQuotaTransition>().ToList();
    }

    private bool IsFresh(string name)
        => _entries.TryGetValue(name, out var entry) && clock.GetUtcNow() - entry.FetchedAt < Ttl;

    private async Task<WorkspaceQuotaTransition?> RunOwnedAsync(
        string name, Lazy<Task<WorkspaceQuotaTransition?>> mine)
    {
        try { return await mine.Value; }
        finally { _inFlight.TryRemove(KeyValuePair.Create(name, mine)); }
    }

    private async Task<WorkspaceQuotaTransition?> FetchAsync(string name)
    {
        // A refresh that finished between the freshness check and this request already answered.
        if (IsFresh(name)) return null;
        WorkspaceQuotaVerdict verdict;
        try
        {
            // Shared by every waiter, so no caller token; the HTTP client timeout bounds the wait.
            verdict = await admission.AdmitAsync(name, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            verdict = new(true,
                $"Quota admission failed ({ex.GetType().Name}); the claim proceeded and the meter needs attention.", true);
        }
        var previous = _entries.TryGetValue(name, out var known) ? known : null;
        var wasUnavailable = previous?.Verdict.MeterUnavailable == true;
        _entries[name] = new Entry(verdict, clock.GetUtcNow(),
            verdict.MeterUnavailable && (!wasUnavailable || previous!.CardNoticePending));
        // The first answer after start reports only an outage; there is nothing to recover from yet.
        return verdict.MeterUnavailable == wasUnavailable ? null
            : new WorkspaceQuotaTransition(name, verdict.MeterUnavailable, verdict.Message);
    }
}

/// <summary>The monolith owns quota meters; the Task Server asks it before assigning new work.</summary>
public sealed class HttpWorkspaceQuotaAdmission(
    IHttpClientFactory clients, IOptions<TaskServerOptions> options) : IWorkspaceQuotaAdmission
{
    public async Task<WorkspaceQuotaVerdict> AdmitAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var baseUrl = options.Value.QuotaAdmissionBaseUrl?.TrimEnd('/')
                ?? throw new InvalidOperationException("Quota admission backend URL is not configured.");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/api/quota/admission/{Uri.EscapeDataString(projectId)}");
            if (!string.IsNullOrWhiteSpace(options.Value.QuotaAdmissionBearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", options.Value.QuotaAdmissionBearerToken);
            using var response = await clients.CreateClient(nameof(HttpWorkspaceQuotaAdmission))
                .SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<WorkspaceQuotaVerdict>(cancellationToken: ct)
                ?? new(false, "Quota admission returned no verdict.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException or JsonException or NotSupportedException or FormatException)
        {
            return new(true, "Quota admission is temporarily unavailable; the claim proceeded and the meter needs attention.", true);
        }
    }
}
