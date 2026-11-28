using System.Globalization;
using Protocol = AgentStudio.TaskServer.Contracts.RunnerCredentialRenewalProtocol;

namespace AgentRunner;

/// <summary>
/// Warns once a day in the final days when renewal is off or has failed.
/// </summary>
internal sealed class CredentialExpiryWarningHandler(HttpMessageHandler inner, Action<string> warn, TimeProvider clock,
    bool renews = false)
    : DelegatingHandler(inner)
{
    public const string Header = "X-Principal-Credential-Expires-At";
    private DateTime? _lastWarnedAt;
    private readonly object _gate = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var state = RunnerCredentialRenewalHandler.ReadState(response);
        if ((state is not (Protocol.Scheduled or Protocol.Due) || !renews) && ReadTime(response, Header) is { } expiresAt)
        {
            var authorityNow = ReadTime(response, Protocol.ServerTimeHeader) ?? clock.GetUtcNow().UtcDateTime;
            string? message = null;
            lock (_gate)
            {
                if (CredentialExpiryWarning.ShouldWarn(expiresAt, authorityNow, _lastWarnedAt))
                {
                    _lastWarnedAt = authorityNow;
                    message = state is Protocol.Failing or Protocol.Due or Protocol.Scheduled
                        ? $"WARNING: automatic runner credential renewal is failing. Check the writable token file and renewal status in Studio. This credential expires at {expiresAt:O}."
                        : CredentialExpiryWarning.Message(expiresAt, authorityNow);
                }
            }
            if (message is not null) warn(message);
        }
        return response;
    }

    private static DateTime? ReadTime(HttpResponseMessage response, string header)
        => response.Headers.TryGetValues(header, out var values)
           && DateTime.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}

/// <summary>When and how a host warns about its credential's end.</summary>
internal static class CredentialExpiryWarning
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static bool ShouldWarn(DateTime expiresAt, DateTime now, DateTime? lastWarnedAt)
        => expiresAt - now <= Window && (lastWarnedAt is null || now - lastWarnedAt.Value >= Interval);

    public static string Message(DateTime expiresAt, DateTime now)
        => expiresAt <= now
            ? $"WARNING: this host's runner credential expired at {expiresAt:O}. Rotate it in Studio and restart the service."
            : $"WARNING: this host's runner credential expires at {expiresAt:O} (in {Math.Max(1, (int)Math.Ceiling((expiresAt - now).TotalDays))} day(s)). Rotate it before then.";
}
