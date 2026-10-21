using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Decides whether a host may lease a coding card for the named model it
/// would launch. A blank model is the CLI's implicit default, which the
/// provider-auth real request already proves.
/// </summary>
public static class CodingRouteAcceptancePolicy
{
    public sealed record NamedRoute(string CliType, string Model)
    {
        public string Key => CapabilityProtocol.ModelAcceptance(CliType, Model);
    }

    /// <summary>The host's acceptance row for one route and the latest rejection.</summary>
    public sealed record AcceptanceEvidence(
        string? AdvertisedStatus,
        DateTime? FreshUntil,
        DateTime? ProbeServerWatermark,
        DateTime? LatestRejectionAt);

    /// <summary>
    /// The route in the order the runner resolves its run spec: a required
    /// mechanical route, an explicit operator continuation, a provider
    /// fallback, then the card's primary fields. A permit carries only the
    /// mechanical route, so it passes no continuation or fallback.
    /// </summary>
    public static NamedRoute? EffectiveNamedRoute(
        MechanicalFreshRunRoute? mechanical,
        ContinuationIntentProjection? intent,
        ProviderModelFallback? fallback,
        string? primaryCli,
        string? primaryModel)
    {
        if (mechanical is not null) return Named(mechanical.CliType, mechanical.Model);
        if (intent is { Receipt.ExplicitSelection: true })
        {
            var fields = intent.Selection;
            var cli = fields is null || fields.CliType || fields.Model || fields.ThinkingLevel
                ? intent.CliType : fallback?.CliType;
            var model = fields is null || fields.Model ? intent.Model : fallback?.To;
            return Named(cli ?? primaryCli, model);
        }
        return fallback is not null
            ? Named(fallback.CliType, fallback.To)
            : Named(primaryCli, primaryModel);
    }

    /// <summary>
    /// Null admits the route. Server time orders the probe after the
    /// rejection: the probe's watermark came from a server response, so host
    /// and server clocks are never compared.
    /// </summary>
    public static string? Refusal(NamedRoute? route, AcceptanceEvidence evidence, DateTime now)
    {
        if (route is null) return null;
        if (evidence.LatestRejectionAt is { } rejectedAt
            && (evidence.ProbeServerWatermark is not { } watermark || watermark <= rejectedAt))
            return $"model-capability-held: the host refused {route.CliType}/{route.Model} and has no later accepted probe";
        return evidence.AdvertisedStatus == "ready" && evidence.FreshUntil > now
            ? null
            : $"model-acceptance-unverified: no fresh accepted probe for {route.CliType}/{route.Model} on this host";
    }

    private static NamedRoute? Named(string? cli, string? model)
        => string.IsNullOrWhiteSpace(model)
            ? null
            : new NamedRoute(
                string.IsNullOrWhiteSpace(cli) ? "claude" : cli.Trim().ToLowerInvariant(),
                model.Trim().ToLowerInvariant());
}
