using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Fleet-wide count of one gate-failure fingerprint (AGT-3009). The same failing
/// item on several cards is one cause, so the router needs to know which other
/// cards already failed on it.
/// </summary>
public interface IGateFailureFingerprintCounter
{
    /// <summary>
    /// Records this card's observation (idempotent per <paramref name="reportKey"/>)
    /// and returns every distinct card key seen with <paramref name="fingerprint"/>
    /// inside the counting window, including this one. Null when the counter
    /// cannot be read: an unreadable counter never invents a shared cause.
    /// </summary>
    Task<IReadOnlyList<string>?> RecordAndReadCardsAsync(
        string fingerprint, string cardKey, string reportKey, CancellationToken ct);
}

/// <summary>
/// Counts through the Task Server failure-fingerprint store (AGT-2916), the
/// same store the cause breaker (AGT-2917) reads. No second counter exists: a
/// gate observation is one more event under its own source.
/// </summary>
public sealed class TaskServerGateFailureFingerprintCounter : IGateFailureFingerprintCounter
{
    public const string Source = "gate-triage";
    public const string Executor = "merge-gate";

    /// <summary>
    /// How far back another card's failure still counts as the same cause. One
    /// red baseline test held every gate red for five days before a cause card
    /// existed (AGT-3007), so the window covers a working week.
    /// </summary>
    public static readonly TimeSpan CountingWindow = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<TaskServerGateFailureFingerprintCounter> _logger;
    private readonly TimeProvider _time;

    public TaskServerGateFailureFingerprintCounter(
        IHttpClientFactory clients,
        ILogger<TaskServerGateFailureFingerprintCounter> logger,
        TimeProvider? time = null)
    {
        _clients = clients;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<string>?> RecordAndReadCardsAsync(
        string fingerprint, string cardKey, string reportKey, CancellationToken ct)
    {
        try
        {
            var client = _clients.CreateClient(TaskServerPlaneProxy.ClientName);
            if (client.BaseAddress is null) return null;
            using (var response = await client.PostAsJsonAsync("/api/v1/failure-fingerprints",
                       new RecordFailureFingerprintRequest(fingerprint, cardKey, Executor, Source, reportKey), ct)
                       .ConfigureAwait(false))
                response.EnsureSuccessStatusCode();
            var since = _time.GetUtcNow().UtcDateTime - CountingWindow;
            var path = "/api/v1/failure-fingerprints?fingerprint=" + Uri.EscapeDataString(fingerprint)
                + "&sinceUtc=" + Uri.EscapeDataString(since.ToString("O"));
            var history = await client.GetFromJsonAsync<FailureFingerprintHistoryDto[]>(path, ct)
                .ConfigureAwait(false);
            return history?.FirstOrDefault()?.CardKeys ?? [cardKey];
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(exception,
                "gate-failure-fingerprint counter unavailable fingerprint={Fingerprint} card={CardKey}",
                fingerprint, cardKey);
            return null;
        }
    }
}
