using System.Net;
using System.Net.Http.Json;
using AgentStudio.Host;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Git.SourceFacts;

/// <summary>
/// Where card-open provenance and source file history come from (AGT-3318,
/// D7 A-B). <see cref="Derived"/> reads the facts the runner-host producer
/// published to the Task Server and starts no Git process in the request.
/// <see cref="Legacy"/> keeps the live-Git reads; it is the rollback setting
/// and the only answer on a dev seat without a Task Server.
/// </summary>
public enum SourceFactsReadMode
{
    Legacy,
    Derived,
}

public static class SourceFactsReadModePolicy
{
    public const string ConfigurationKey = "SourceFacts:ReadMode";

    /// <summary>
    /// An explicit setting wins. Without one, a configured Task Server plane
    /// means derived reads, because that is where the facts live.
    /// </summary>
    public static SourceFactsReadMode Resolve(string? configured, bool taskServerConfigured)
        => configured?.Trim().ToLowerInvariant() switch
        {
            "legacy" => SourceFactsReadMode.Legacy,
            "derived" => SourceFactsReadMode.Derived,
            _ => taskServerConfigured ? SourceFactsReadMode.Derived : SourceFactsReadMode.Legacy,
        };
}

public static class SourceFactStates
{
    /// <summary>Every fact the view needs was found.</summary>
    public const string Ready = "ready";
    /// <summary>At least one fact is missing; a demand was recorded and the producer computes it.</summary>
    public const string Warming = "warming";
    /// <summary>The Task Server could not be read; nothing falls back to Git.</summary>
    public const string Unavailable = "unavailable";
}

public sealed record SourceFactRead<T>(string State, T? Value) where T : class
{
    public static SourceFactRead<T> Ready(T value) => new(SourceFactStates.Ready, value);
    public static SourceFactRead<T> Missing { get; } = new(SourceFactStates.Warming, null);
    public static SourceFactRead<T> Unavailable { get; } = new(SourceFactStates.Unavailable, null);
}

public interface ISourceFactsReader
{
    SourceFactsReadMode Mode { get; }

    Task<SourceFactRead<SourceReachabilityView>> GetReachabilityAsync(
        string repositoryId, IReadOnlyList<string> shas, IReadOnlyList<string> targetRefs, CancellationToken ct);

    Task<SourceFactRead<SourcePathHistoryDto>> GetPathHistoryAsync(
        string repositoryId, string headSha, string path, CancellationToken ct);

    /// <summary>Records a cache-miss demand. Best effort: a failure is logged, never thrown.</summary>
    Task RequestAsync(string repositoryId, SourceFactDemandRequest demand, CancellationToken ct);
}

/// <summary>
/// Reads tier 2 of the target read path, the Task Server's derived source
/// data, through the authenticated plane client. Each call is bounded so a
/// slow Task Server degrades a card to warming instead of holding the request.
/// </summary>
public sealed class TaskServerSourceFactsReader : ISourceFactsReader
{
    internal static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(2);
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<TaskServerSourceFactsReader> _logger;

    public TaskServerSourceFactsReader(
        IHttpClientFactory clients,
        IConfiguration configuration,
        ILogger<TaskServerSourceFactsReader> logger)
    {
        _clients = clients;
        _logger = logger;
        Mode = SourceFactsReadModePolicy.Resolve(
            configuration[SourceFactsReadModePolicy.ConfigurationKey],
            TaskServerPlaneProxy.IsConfigured(configuration));
    }

    public SourceFactsReadMode Mode { get; }

    public async Task<SourceFactRead<SourceReachabilityView>> GetReachabilityAsync(
        string repositoryId, IReadOnlyList<string> shas, IReadOnlyList<string> targetRefs, CancellationToken ct)
    {
        var query = string.Join('&',
            shas.Select(sha => "sha=" + Uri.EscapeDataString(sha))
                .Concat(targetRefs.Select(target => "ref=" + Uri.EscapeDataString(target))));
        return await GetAsync<SourceReachabilityView>(
            $"{Root(repositoryId)}/reachability?{query}", "reachability", ct);
    }

    public Task<SourceFactRead<SourcePathHistoryDto>> GetPathHistoryAsync(
        string repositoryId, string headSha, string path, CancellationToken ct)
        => GetAsync<SourcePathHistoryDto>(
            $"{Root(repositoryId)}/path-histories?head={Uri.EscapeDataString(headSha)}&path={Uri.EscapeDataString(path)}",
            "path-history", ct);

    public async Task RequestAsync(string repositoryId, SourceFactDemandRequest demand, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(CallBudget);
        try
        {
            var client = _clients.CreateClient(TaskServerPlaneProxy.ClientName);
            if (client.BaseAddress is null) return;
            using var response = await client.PostAsJsonAsync($"{Root(repositoryId)}/demands", demand, bounded.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning(
                    "source-facts demand refused repository={Repository} kind={Kind} status={Status}",
                    repositoryId, demand.Kind, (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(exception,
                "source-facts demand not recorded repository={Repository} kind={Kind}", repositoryId, demand.Kind);
        }
    }

    private async Task<SourceFactRead<T>> GetAsync<T>(string url, string what, CancellationToken ct) where T : class
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(CallBudget);
        try
        {
            var client = _clients.CreateClient(TaskServerPlaneProxy.ClientName);
            if (client.BaseAddress is null) return SourceFactRead<T>.Unavailable;
            using var response = await client.GetAsync(url, bounded.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return SourceFactRead<T>.Missing;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("source-facts read failed fact={Fact} status={Status}", what, (int)response.StatusCode);
                return SourceFactRead<T>.Unavailable;
            }
            var value = await response.Content.ReadFromJsonAsync<T>(bounded.Token).ConfigureAwait(false);
            return value is null ? SourceFactRead<T>.Missing : SourceFactRead<T>.Ready(value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "source-facts read unavailable fact={Fact}", what);
            return SourceFactRead<T>.Unavailable;
        }
    }

    private static string Root(string repositoryId)
        => $"/api/v1/source-facts/{Uri.EscapeDataString(repositoryId)}";
}
