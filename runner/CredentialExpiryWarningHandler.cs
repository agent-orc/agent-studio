using System.Globalization;

namespace AgentRunner;

/// <summary>
/// SM-9: the server names the end of this host's runner credential on every
/// answer. In its last days the host warns once a day, so the operator rotates
/// before claims stop instead of after.
/// </summary>
internal sealed class CredentialExpiryWarningHandler(HttpMessageHandler inner, Action<string> warn, TimeProvider clock)
    : DelegatingHandler(inner)
{
    public const string Header = "X-Principal-Credential-Expires-At";
    private DateTime? _lastWarnedAt;
    private readonly object _gate = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues(Header, out var values)
            && DateTime.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiresAt))
        {
            var now = clock.GetUtcNow().UtcDateTime;
            string? message = null;
            lock (_gate)
            {
                if (CredentialExpiryWarning.ShouldWarn(expiresAt, now, _lastWarnedAt))
                {
                    _lastWarnedAt = now;
                    message = CredentialExpiryWarning.Message(expiresAt, now);
                }
            }
            if (message is not null) warn(message);
        }
        return response;
    }
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
