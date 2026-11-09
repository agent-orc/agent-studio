using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Shared;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The one place that decides CLI, model and thinking level (Dossier
/// model-selection-and-fallback, D1 option B). Pure: every input is a typed
/// fact, so the monolith, Task Server and runner decide the same way.
///
/// <para>Rules apply in the Dossier order and the first rule that decides is
/// named in <see cref="SelectionResult.Rule"/>:</para>
/// <list type="number">
///   <item>hard pin and same-session continuation keep their route or wait;</item>
///   <item>the opt-in nearby-reset wait for cheap cards (CAR 0.6);</item>
///   <item>the operator switch moves work off a provider until its reset;</item>
///   <item>a capped or projected-to-breach primary switches to the operator
///   profile or the Token Economy equivalent (<see cref="ResolveRoute"/>);</item>
///   <item>a fallback must clear the card's correctness floor and the host's
///   accepted models, and review consumers keep the capability class;</item>
///   <item>no equivalent: wait (or throttle a projected breach) until the
///   earliest reset.</item>
/// </list>
/// <para>The quota, fallback and floor rules moved here unchanged from
/// <c>QuotaAdmissionPlanner.Plan</c>, <c>CliQuotaFallbackService.Resolve</c>,
/// <c>ModelRoutingPolicyRegistry.RouteMeetsFloor</c> and the provider-rejection
/// sibling lookups (MS-5); a golden matrix holds them to the recorded
/// behaviour.</para>
/// </summary>
public static class ModelSelectionPlanner
{
    private const string Claude = "claude";
    private const string Codex = "codex";

    /// <summary>Fresh-round reasons after which a resumed mechanical route is re-scored.</summary>
    public static readonly IReadOnlySet<string> FreshRunReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "pending-mechanical-continuation", "semantic-conflict", "failed-deterministic-gate",
        "invalid-session-after-resume", "resume-token-ceiling", "resume-duration-ceiling",
        "resumed-round-inconclusive",
    };

    public static SelectionResult Plan(SelectionRequest request, SelectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(facts);
        var continuation = request.Continuation is { SessionCli: { Length: > 0 } sessionCli } session
            ? request.Pins with { CliType = sessionCli, Model = session.SessionModel ?? request.Pins.Model }
            : null;
        var pins = continuation ?? request.Pins;
        var result = ApplyCostGuard(facts, ApplyFloor(request, facts, PlanQuota(request, pins, facts)));
        return continuation is not null || pins.Mode == SelectionPinMode.Hard
            ? HoldPinnedRoute(result, continuation is not null ? "same-session continuation" : "hard pin")
            : result;
    }

    /// <summary>
    /// The primary/fallback route for one CLI model. The operator profile is
    /// advisory until the primary reaches a cap (or the operator switch is on);
    /// then the profile fallback or the catalogue equivalent of the other
    /// provider family is chosen, subject to class, thinking, conservation,
    /// host and fallback-quota checks.
    /// </summary>
    public static CliRouteDecision ResolveRoute(
        string? requestedCliType,
        string? requestedModel,
        string? requestedThinkingLevel,
        SelectionRoutingFacts routing,
        Func<string?, CapEvaluation> evaluateQuota,
        bool preferFallback = false,
        Func<string, string?, bool>? modelAccepted = null,
        bool sameClassOnly = false,
        Func<string, bool>? fallbackProviderAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(routing);
        var cli = Clean(requestedCliType)?.ToLowerInvariant() ?? Claude;
        var profile = routing.Profile(cli);
        var equivalence = routing.Equivalence;

        var primaryModel = Clean(requestedModel) ?? profile?.PrimaryModel;
        var primaryThinking = Clean(requestedThinkingLevel) ?? profile?.PrimaryThinkingLevel;
        var cap = evaluateQuota(cli);
        if (!cap.Blocked && !preferFallback)
            return new(cli, primaryModel, primaryThinking, false, null, cap);

        var effective = EffectiveFallback(cli, profile, primaryModel, primaryThinking, equivalence);
        if (preferFallback && effective is { } selected
            && string.Equals(selected.CliType, cli, StringComparison.OrdinalIgnoreCase))
        {
            var other = OtherFamily(cli);
            var crossProvider = other is null ? null
                : equivalence?.TryGetRoute(cli, primaryModel, primaryThinking, other);
            effective = crossProvider is null ? null
                : (other!, crossProvider.ToModel, crossProvider.ToThinkingLevel, crossProvider.CapabilityClass);
        }
        if (effective is null)
            return new(cli, primaryModel, primaryThinking, false,
                preferFallback ? "operator prefers fallback, but no comparable route is available" : cap.DescribeReason(), cap);

        var (fallbackCli, fallbackModel, fallbackThinking, capabilityClass) = effective.Value;
        if (sameClassOnly && (equivalence is null
            || !equivalence.SameCapabilityClass(primaryModel, fallbackModel)))
            return new(cli, primaryModel, primaryThinking, false,
                "review requires a fallback in the same Token Economy capability class", cap);
        if (sameClassOnly && ThinkingRank(primaryThinking) is { } required)
        {
            var mapped = routing.ThinkingLevelsFor(fallbackCli, fallbackModel)
                .Where(level => ThinkingRank(level) is { } rank && rank >= required)
                .OrderBy(level => ThinkingRank(level))
                .FirstOrDefault();
            if (mapped is null)
                return new(cli, primaryModel, primaryThinking, false,
                    "review fallback has no supported thinking level at or above the configured route", cap);
            fallbackThinking = mapped;
        }
        if (fallbackProviderAllowed is not null && !fallbackProviderAllowed(fallbackCli))
            return new(cli, primaryModel, primaryThinking, false,
                $"operator is conserving provider {fallbackCli} until its quota reset", cap);
        if (modelAccepted is not null && !modelAccepted(fallbackCli, fallbackModel))
            return new(cli, primaryModel, primaryThinking, false,
                $"fallback {fallbackCli}/{fallbackModel} is not accepted by the claiming host", cap);
        var fallbackCap = string.Equals(fallbackCli, cli, StringComparison.OrdinalIgnoreCase)
            ? CapEvaluation.NotBlocked
            : evaluateQuota(fallbackCli);
        if (fallbackCap.Blocked)
            return new(cli, primaryModel, primaryThinking, false,
                $"primary {cap.DescribeReason()}; fallback {fallbackCap.DescribeReason()}", cap)
                { TargetQuotaBlocked = true };

        var isOverride = profile is not null && !string.IsNullOrWhiteSpace(profile.FallbackModel)
            && (!preferFallback || !string.Equals(profile.FallbackCliType ?? cli, cli,
                StringComparison.OrdinalIgnoreCase));
        return new(
            fallbackCli,
            fallbackModel,
            fallbackThinking,
            true,
            preferFallback ? "operator preference" : cap.DescribeReason(),
            cap,
            preferFallback ? "operator-preference" : "quota-cap",
            isOverride ? "override" : "catalogue",
            isOverride ? null : equivalence?.Version,
            isOverride ? null : capabilityClass);
    }

    /// <summary>
    /// The fallback that would apply for <paramref name="cli"/> right now: the
    /// operator profile when it names a fallback model, else the catalogue
    /// equivalent on the other provider family. Null when neither source has
    /// an answer.
    /// </summary>
    public static (string CliType, string? Model, string? ThinkingLevel, string? CapabilityClass)? EffectiveFallback(
        string cli, CliModelRouteProfile? profile, string? primaryModel, string? primaryThinking,
        IModelEquivalenceCatalog? equivalence)
    {
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.FallbackModel))
            return (profile.FallbackCliType ?? cli, profile.FallbackModel, profile.FallbackThinkingLevel, null);

        var otherFamily = OtherFamily(cli);
        if (otherFamily is null || equivalence is null) return null;
        var equivalent = equivalence.TryGetRoute(cli, primaryModel, primaryThinking, otherFamily);
        return equivalent is null
            ? null
            : (otherFamily, equivalent.ToModel, equivalent.ToThinkingLevel, equivalent.CapabilityClass);
    }

    /// <summary>
    /// The counterpart CLI family a catalogue-derived fallback routes to. Only
    /// Claude and Codex have a documented equivalence tier; any other CLI
    /// needs an explicit operator profile.
    /// </summary>
    public static string? OtherFamily(string cli) => cli switch
    {
        _ when string.Equals(cli, Codex, StringComparison.OrdinalIgnoreCase) => Claude,
        _ when string.Equals(cli, Claude, StringComparison.OrdinalIgnoreCase) => Codex,
        _ => null,
    };

    /// <summary>
    /// Whether a concrete route clears the named policy floor. Older
    /// generations of the same Claude family inherit the current generation's
    /// tier, and the GPT-5.6 Sol/Luna siblings inherit their GPT-6 tier
    /// (AGT-2903), so a pre-GPT-6 pin or a provider-rejection sibling keeps
    /// clearing the floor it cleared before the re-base.
    /// </summary>
    public static bool RouteMeetsFloor(
        IReadOnlyList<ModelRoutingTier> tiers,
        string model,
        string? thinkingLevel,
        ModelRoutingTier? floor,
        Func<string?, string> normalizeModelId)
    {
        if (floor is null) return true;
        var normalizedModel = normalizeModelId(model);
        var normalizedThinking = thinkingLevel?.Trim().ToLowerInvariant();
        var rank = tiers
            .Where(tier => RouteMatchesTier(tier, normalizedModel, normalizedThinking, normalizeModelId))
            .Select(tier => (int?)tier.Rank)
            .Max();
        return rank is not null && rank.Value >= floor.Rank;
    }

    /// <summary>
    /// The explicitly declared same-provider sibling for a provider refusal of
    /// <paramref name="model"/> on <paramref name="cliType"/>, or null. Hosts
    /// pass their own normalisation; without one, ids compare as given.
    /// </summary>
    public static T? RejectionSibling<T>(
        IEnumerable<T> rules,
        Func<T, (string CliType, string FromModel)> key,
        string? cliType,
        string? model,
        Func<string?, string>? normalizeModelId = null,
        Func<string?, string>? normalizeCli = null)
        where T : class
    {
        var normalizeModel = normalizeModelId ?? (value => value ?? "");
        var cli = (normalizeCli ?? (value => value ?? ""))(cliType);
        var normalizedModel = normalizeModel(model);
        return rules.FirstOrDefault(candidate =>
        {
            var (candidateCli, fromModel) = key(candidate);
            return string.Equals(candidateCli, cli, StringComparison.OrdinalIgnoreCase)
                && string.Equals(normalizeModel(fromModel), normalizedModel, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// <paramref name="modelId"/> followed by its declared provider-rejection
    /// siblings (gpt-6-sol, then gpt-5.6-sol), each once.
    /// </summary>
    public static IEnumerable<string> SiblingChain<T>(
        IEnumerable<T> rules,
        Func<T, (string FromModel, string ToModel)> edge,
        string? modelId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var candidate = modelId; !string.IsNullOrWhiteSpace(candidate) && visited.Add(candidate);)
        {
            yield return candidate;
            candidate = rules.Select(edge).FirstOrDefault(item =>
                string.Equals(item.FromModel, candidate, StringComparison.OrdinalIgnoreCase)).ToModel;
        }
    }

    /// <summary>
    /// Re-score a fresh round after a resumed mechanical round found real work.
    /// Returns <paramref name="current"/> itself when the route already
    /// clears the fresh-round floor or the card pins its model.
    /// </summary>
    public static SelectionRoute QualifyFreshRun(
        string? freshReason,
        bool modelExplicit,
        SelectionRoute current,
        Func<ModelRoutingTier?> policyFloor,
        IReadOnlyList<ModelRoutingTier> tiers,
        Func<string?, string> normalizeModelId)
    {
        if (freshReason is null || !FreshRunReasons.Contains(freshReason))
            return current;
        if (modelExplicit && !string.IsNullOrWhiteSpace(current.Model))
            return current;

        var policy = policyFloor();
        var minimumId = freshReason == "semantic-conflict" ? "sol-medium" : "terra-medium";
        var minimum = tiers.First(tier => tier.Id == minimumId);
        var floor = policy is not null && policy.Rank > minimum.Rank ? policy : minimum;
        if (!string.IsNullOrWhiteSpace(current.Model)
            && RouteMeetsFloor(tiers, current.Model, current.ThinkingLevel, floor, normalizeModelId))
            return current;

        var anthropic = string.Equals(current.CliType, Claude, StringComparison.OrdinalIgnoreCase);
        var vendor = anthropic && floor.VendorOverrides.TryGetValue("anthropic", out var overrideRoute)
            ? overrideRoute : null;
        return current with
        {
            Model = vendor?.Model ?? floor.Model,
            ThinkingLevel = vendor?.ThinkingLevel ?? floor.ThinkingLevel,
        };
    }

    /// <summary>
    /// The run route a remote claim carries to the runner. A required
    /// mechanical route wins. An explicit operator continuation selection
    /// comes next; once the operator selects any field, the provider-refusal
    /// fallback no longer fills the others, because mixing the two can cross
    /// provider boundaries. A provider-refusal fallback runs in a clean context.
    /// </summary>
    public static ClaimRouteSelection ClaimRoute(
        MechanicalFreshRunRoute? mechanicalRoute,
        ContinuationIntentProjection? continuationIntent,
        ProviderModelFallback? providerFallback)
    {
        if (mechanicalRoute is not null)
            return new(mechanicalRoute.CliType, mechanicalRoute.Model, mechanicalRoute.ThinkingLevel,
                CleanContext: true, HasRoute: true);
        var selected = continuationIntent is { Receipt.ExplicitSelection: true } intent ? intent : null;
        var fields = selected?.Selection;
        var fallback = selected is null ? providerFallback : null;
        return new(
            selected is not null && (fields is null || fields.CliType || fields.Model || fields.ThinkingLevel)
                ? selected.CliType : fallback?.CliType,
            selected is not null && (fields is null || fields.Model)
                ? selected.Model : fallback?.To,
            selected is not null && (fields is null || fields.ThinkingLevel)
                ? selected.ThinkingLevel : fallback?.ThinkingLevel,
            CleanContext: fallback is not null,
            HasRoute: selected is not null || fallback is not null);
    }

    private static SelectionResult PlanQuota(SelectionRequest request, SelectionPins pins, SelectionFacts facts)
    {
        var requestedModel = pins.Model;
        var requestedThinking = pins.ThinkingLevel;
        var cli = string.IsNullOrWhiteSpace(pins.CliType)
            ? Claude
            : pins.CliType!.Trim().ToLowerInvariant();
        var caps = facts.Caps;
        var nowUtc = facts.NowUtc;
        var snapshotFor = facts.SnapshotFor;
        var waitPolicy = facts.WaitPolicy;
        var preference = facts.OperatorSwitch;
        var primarySnapshot = snapshotFor(cli);
        var projectionWarning = SelectionQuotaMath.FindWarning(primarySnapshot, nowUtc);
        var pinRule = string.IsNullOrWhiteSpace(requestedModel) ? SelectionRules.ConsumerDefault : SelectionRules.Pin;

        // Strict = already over the configured cap. Admission = strict OR
        // projected-to-breach-before-reset. The router is fed the admission
        // view so a primary about to hit the wall routes to the fallback now.
        CapEvaluation Strict(string? c) => SelectionQuotaMath.EvaluateCap(snapshotFor(c), caps);
        CapEvaluation Admission(string? c)
        {
            var strict = Strict(c);
            if (strict.Blocked) return strict;
            return SelectionQuotaMath.EvaluateProjectedBreach(snapshotFor(c), caps, nowUtc)
                   ?? CapEvaluation.NotBlocked;
        }
        QuotaProjection? Worst(QuotaSnapshot? snapshot) => SelectionQuotaMath.WorstProjection(snapshot, caps, nowUtc);
        SelectionResult Decided(
            SelectionOutcome outcome, string? model, string? thinking, string rule, string reason,
            DateTime? retryAt, QuotaProjection? projection, bool nearby = false)
        {
            var route = new SelectionRoute(cli, model, thinking);
            return new SelectionResult
            {
                Outcome = outcome,
                Route = route,
                Primary = route,
                Rule = rule,
                Reason = reason,
                RetryAt = retryAt,
                Quota = new SelectionQuota(null, null, projection, projectionWarning),
                NearbyResetWait = nearby,
            };
        }

        // 1) The opt-in CAR 0.6 wait policy owns the first decision when the
        // primary is already capped and its confirmed reset is nearby. This is
        // intentionally before route resolution: nearby wait -> model switch ->
        // throttle. Unknown, elapsed, or distant resets fail open to the
        // existing fallback/admission route.
        //
        // Situation awareness (AGT-2751, requirement 4): a nearby reset is
        // only worth a quiet wait when the card is cheap. An expensive card
        // (explicit high/xhigh/ultra/max reasoning) switches to an
        // equal-strength provider immediately instead of sitting in the queue
        // until the reset, even inside the configured wait threshold.
        var strictPrimaryEarly = Strict(cli);
        var nearbyReset = SelectionQuotaMath.EarliestReset(primarySnapshot, nowUtc, caps, blockedOnly: true);
        if (waitPolicy?.Enabled == true
            && preference?.Active != true
            && strictPrimaryEarly.Blocked
            && !strictPrimaryEarly.Suspicious
            && IsCheap(requestedThinking)
            && nearbyReset?.ResetAt is { } resetAt)
        {
            var remaining = resetAt - nowUtc;
            if (remaining > TimeSpan.Zero && remaining < TimeSpan.FromMinutes(waitPolicy.ThresholdMinutes))
            {
                var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
                return Decided(
                    SelectionOutcome.Wait, requestedModel, requestedThinking, SelectionRules.QuotaCap,
                    $"waiting for quota reset {resetAt:HH:mm} UTC, {minutes} min remaining (threshold {waitPolicy.ThresholdMinutes} min)",
                    resetAt, Worst(primarySnapshot), nearby: true);
            }
        }

        var holdSwitch = ShouldHoldSwitch(facts, primarySnapshot, cli);
        var preferFallback = preference?.Active == true || holdSwitch;
        var route = facts.Routing is null ? null : ResolveConfiguredRoute();
        if (holdSwitch && preference?.Active != true && route?.IsFallback == true)
            route = route with
            {
                Reason = "holding the quota fallback until the primary return rule clears",
                FallbackReason = "quota-cap",
                FallbackSource = facts.ActiveSwitch?.Equivalence?.Source ?? route.FallbackSource,
                CatalogueVersion = facts.ActiveSwitch?.Equivalence?.CatalogueVersion ?? route.CatalogueVersion,
                CapabilityClass = facts.ActiveSwitch?.Equivalence?.CapabilityClass ?? route.CapabilityClass,
            };

        CliRouteDecision ResolveConfiguredRoute()
        {
            var routing = facts.Routing!;
            var accepted = facts.HostAcceptsFallback ?? facts.HostAccepts;
            var configured = holdSwitch && facts.ActiveSwitch is { } active
                ? (IReadOnlyList<SelectionRoute>)[active.To]
                : facts.ConfiguredFallbacks;
            if (configured is not { Count: > 0 })
                return ResolveRoute(cli, requestedModel, requestedThinking, routing, Admission,
                    preferFallback, accepted, request.Required.SameClassOnly, facts.FallbackProviderAllowed);

            // Keep the list in planner facts so every host applies the same
            // admission checks and the ledger records the route actually used.
            CliRouteDecision? rejected = null;
            for (var index = 0; index < configured.Count; index++)
            {
                var candidate = configured[index];
                if (string.IsNullOrWhiteSpace(candidate.Model) || string.IsNullOrWhiteSpace(candidate.CliType))
                    continue;
                if (Admission(candidate.CliType).Blocked)
                {
                    rejected = new CliRouteDecision(cli, requestedModel, requestedThinking, false,
                        $"configured fallback {index + 1} provider quota is exhausted", Admission(cli))
                        { TargetQuotaBlocked = true };
                    continue;
                }
                var candidateRouting = routing with { Profile = candidateCli =>
                {
                    var previous = routing.Profile(candidateCli);
                    return (previous ?? new CliModelRouteProfile { CliType = candidateCli }) with
                    {
                        FallbackCliType = candidate.CliType,
                        FallbackModel = candidate.Model,
                        FallbackThinkingLevel = candidate.ThinkingLevel,
                    };
                } };
                var evaluated = ResolveRoute(cli, requestedModel, requestedThinking, candidateRouting,
                    Admission, preferFallback, accepted, request.Required.SameClassOnly,
                    facts.FallbackProviderAllowed);
                if (!evaluated.IsFallback)
                {
                    rejected = evaluated;
                    continue;
                }
                var floor = request.CorrectnessFloor ?? facts.CorrectnessFloor?.Invoke();
                if (floor is not null && !RouteMeetsFloor(facts.Tiers,
                    evaluated.Model ?? string.Empty, evaluated.ThinkingLevel, floor, facts.NormalizeModelId))
                {
                    rejected = evaluated with { IsFallback = false,
                        Reason = $"configured fallback {index + 1} does not clear the correctness floor" };
                    continue;
                }
                return evaluated with
                {
                    Reason = $"{evaluated.Reason}; selected configured fallback {index + 1} of {configured.Count}",
                    FallbackSource = "policy",
                };
            }
            return rejected ?? new CliRouteDecision(cli, requestedModel, requestedThinking, false,
                "no configured fallback route is usable", Admission(cli));
        }

        if (preferFallback && route?.IsFallback != true)
            return Decided(SelectionOutcome.Wait,
                route?.Model ?? requestedModel, route?.ThinkingLevel ?? requestedThinking, SelectionRules.OperatorSwitch,
                $"waiting: operator switch cannot use {cli} and {route?.Reason ?? "no equivalent route is available"}",
                preference?.ExpiresAt, Worst(primarySnapshot));

        // 2) The router switched us to the fallback (primary capped or projected;
        //    a usable fallback exists). Documented model switch before start.
        if (route?.IsFallback == true)
        {
            var primaryReset = SelectionQuotaMath.EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: false);
            var fallbackReason = route.FallbackReason ?? (preferFallback ? "operator-preference" : "quota-cap");
            var constrainedWindow = primarySnapshot?.Windows
                .Where(window => window.UsedPct is not null)
                .OrderByDescending(window => window.UsedPct!.Value
                    / Math.Max(1, caps.GetCap(cli, window.Label)))
                .FirstOrDefault();
            var usedWindow = route.PrimaryCap.WindowLabel ?? constrainedWindow?.Label;
            var usedPct = route.PrimaryCap.Blocked
                ? route.PrimaryCap.UsedPct
                : constrainedWindow?.UsedPct;
            var primary = PrimaryRoute(cli, requestedModel, requestedThinking, facts.Routing);
            return new SelectionResult
            {
                Outcome = SelectionOutcome.Fallback,
                Route = new SelectionRoute(route.CliType, route.Model, route.ThinkingLevel),
                Primary = primary,
                Rule = preference?.Active == true ? SelectionRules.OperatorSwitch : SelectionRules.QuotaCap,
                Reason = AppendReset(BuildSwitchReason(cli, route), primaryReset),
                ReturnToPrimaryAt = facts.ActiveSwitch?.ReturnToPrimaryAt ?? primaryReset?.ResetAt,
                Quota = new SelectionQuota(usedWindow, usedPct, Worst(snapshotFor(cli)), projectionWarning),
                Equivalence = new SelectionEquivalence(
                    route.CapabilityClass,
                    route.CatalogueVersion,
                    string.Equals(primary.ThinkingLevel, route.ThinkingLevel, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : $"{primary.ThinkingLevel ?? "default"} -> {route.ThinkingLevel ?? "default"}",
                    route.FallbackSource),
                Fallback = new ModelFallbackInfo(
                    RouteLabel(requestedModel, requestedThinking),
                    RouteLabel(route.Model, route.ThinkingLevel),
                    fallbackReason,
                    route.CliType,
                    route.ThinkingLevel,
                    usedWindow,
                    usedPct,
                    route.CatalogueVersion),
            };
        }

        // Primary path (no fallback taken): resolve the concrete primary model
        // from the route (which may carry the workspace-configured primary).
        var model = route?.Model ?? requestedModel;
        var thinking = route?.ThinkingLevel ?? requestedThinking;
        var strictPrimary = Strict(cli);
        var admissionPrimary = route?.PrimaryCap ?? Admission(cli);
        var modelAccepted = facts.HostAccepts;

        if (modelAccepted is not null && !modelAccepted(cli, model))
        {
            var key = string.IsNullOrWhiteSpace(model) ? $"provider-auth:{cli}"
                : $"model-acceptance:{cli}:{model}";
            return Decided(SelectionOutcome.Refused, model, thinking, SelectionRules.HostCapability,
                $"Host capability missing on {request.Host?.HostId ?? "claiming host"}: {key}.",
                null, Worst(primarySnapshot));
        }

        // 3) Primary is ACTUALLY over cap and no fallback saved us: everything is
        //    exhausted. Wait quietly with a reason and the next reset time.
        if (strictPrimary.Blocked)
        {
            var blockedReset =
                SelectionQuotaMath.EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: true)
                ?? SelectionQuotaMath.EarliestReset(snapshotFor(route?.CliType ?? cli), nowUtc, caps, blockedOnly: true)
                ?? SelectionQuotaMath.EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: false);
            var detail = !string.IsNullOrWhiteSpace(route?.Reason) ? route!.Reason : strictPrimary.DescribeReason();
            return Decided(SelectionOutcome.Wait, model, thinking, SelectionRules.QuotaCap,
                AppendReset($"waiting: all quotas exhausted ({detail})", blockedReset),
                blockedReset?.ResetAt, Worst(snapshotFor(cli)));
        }

        // 4) Primary is only PROJECTED to breach and there is no usable fallback.
        //    Throttle (reduce parallelism) rather than block - but never throttle
        //    to zero: the first/only run always proceeds, else nothing ever runs.
        if (admissionPrimary.Blocked)
        {
            var proj = Worst(snapshotFor(cli));
            var reset = SelectionQuotaMath.EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: false);
            if (route?.TargetQuotaBlocked == true)
                return Decided(SelectionOutcome.Wait, model, thinking, SelectionRules.QuotaCap,
                    AppendReset($"waiting: fallback target would cross its quota cap ({route.Reason})", reset),
                    reset?.ResetAt, proj);
            if (modelAccepted is not null)
                return Decided(SelectionOutcome.Wait, model, thinking, SelectionRules.QuotaCap,
                    AppendReset($"waiting: no accepted fallback on the claiming host ({route?.Reason ?? admissionPrimary.DescribeReason()})", reset),
                    reset?.ResetAt, proj);
            var throttle = facts.OccupiedSlots > 0;
            var verb = throttle ? "throttling" : "launching (projection-flagged)";
            return Decided(
                throttle ? SelectionOutcome.Throttle : SelectionOutcome.Primary,
                model, thinking, throttle ? SelectionRules.QuotaCap : pinRule,
                AppendReset($"{verb}: {admissionPrimary.DescribeReason()}", reset),
                reset?.ResetAt, proj);
        }

        // 5) Healthy: launch on primary.
        return Decided(SelectionOutcome.Primary, model, thinking, pinRule,
            projectionWarning is null ? "launch: quota ok" : $"launch: quota projection ignored ({projectionWarning.Reason})",
            null, Worst(snapshotFor(cli)));
    }

    /// <summary>Rule 7: a fallback below the card's correctness floor waits instead.</summary>
    private static SelectionResult ApplyFloor(SelectionRequest request, SelectionFacts facts, SelectionResult result)
    {
        if (result.Outcome != SelectionOutcome.Fallback) return result;
        var floor = request.CorrectnessFloor ?? facts.CorrectnessFloor?.Invoke();
        if (floor is null
            || RouteMeetsFloor(facts.Tiers, result.Route.Model ?? string.Empty, result.Route.ThinkingLevel, floor,
                facts.NormalizeModelId))
            return result;
        return result with
        {
            Outcome = SelectionOutcome.Wait,
            Rule = SelectionRules.Floor,
            Reason = $"waiting: no host-accepted fallback clears this card's correctness floor; next reset {result.ReturnToPrimaryAt:O}",
            RetryAt = result.ReturnToPrimaryAt,
            ReturnToPrimaryAt = null,
        };
    }

    private static SelectionResult ApplyCostGuard(SelectionFacts facts, SelectionResult result)
    {
        if (result.Outcome != SelectionOutcome.Fallback
            || result.Rule == SelectionRules.OperatorSwitch
            || result.Equivalence?.Source == "policy") return result;
        var primary = facts.PricePerMTok(result.Primary.Model);
        var target = facts.PricePerMTok(result.Route.Model);
        if (primary is not > 0 || target is null
            || target <= primary * (decimal)facts.CostGuardFactor) return result;
        return result with
        {
            Outcome = SelectionOutcome.Wait,
            Route = result.Primary,
            Rule = SelectionRules.CostGuard,
            Reason = $"waiting: fallback price {target:0.##} per MTok exceeds {facts.CostGuardFactor:0.##} times the primary price {primary:0.##}",
            RetryAt = result.ReturnToPrimaryAt,
            ReturnToPrimaryAt = null,
            Equivalence = null,
            Fallback = null,
        };
    }

    private static bool ShouldHoldSwitch(SelectionFacts facts, QuotaSnapshot? snapshot, string cli)
    {
        var active = facts.ActiveSwitch;
        if (active?.Rule != SelectionRules.QuotaCap || active.From.CliType != cli) return false;
        if (snapshot is null || snapshot.Suspicious || !string.IsNullOrWhiteSpace(snapshot.Error)) return true;
        if (SelectionQuotaMath.EvaluateCap(snapshot, facts.Caps).Blocked) return true;
        if (SelectionQuotaMath.EvaluateProjectedBreach(snapshot, facts.Caps, facts.NowUtc)?.Blocked == true) return true;
        if (active.ReturnToPrimaryAt is { } reset && facts.NowUtc >= reset)
            return snapshot.FetchedAt < reset;
        if (facts.NowUtc - active.At < TimeSpan.FromMinutes(facts.MinDwellMinutes)) return true;
        return !snapshot.Windows.Where(w => w.UsedPct is not null).Any()
            || snapshot.Windows.Where(w => w.UsedPct is not null)
                .Any(w => w.UsedPct!.Value > facts.Caps.GetCap(cli, w.Label) - facts.ReturnBelowCapPoints);
    }

    /// <summary>Rules 1 and 2: a hard pin or a same-session resume never switches; it waits.</summary>
    private static SelectionResult HoldPinnedRoute(SelectionResult result, string why)
    {
        if (result.Outcome != SelectionOutcome.Fallback) return result;
        return result with
        {
            Outcome = SelectionOutcome.Wait,
            Route = result.Primary,
            Rule = SelectionRules.Pin,
            Reason = $"waiting: {why} keeps {result.Primary.CliType}/{result.Primary.Model ?? "<default>"} ({result.Reason})",
            RetryAt = result.ReturnToPrimaryAt,
            ReturnToPrimaryAt = null,
            Equivalence = null,
            Fallback = null,
        };
    }

    private static SelectionRoute PrimaryRoute(
        string cli, string? requestedModel, string? requestedThinking, SelectionRoutingFacts? routing)
    {
        if (routing is null) return new(cli, requestedModel, requestedThinking);
        var profile = routing.Profile(cli);
        return new(cli,
            Clean(requestedModel) ?? profile?.PrimaryModel,
            Clean(requestedThinking) ?? profile?.PrimaryThinkingLevel);
    }

    private static bool RouteMatchesTier(
        ModelRoutingTier tier,
        string? model,
        string? thinkingLevel,
        Func<string?, string> normalizeModelId)
    {
        var route = tier.VendorOverrides.Values
            .Append(new ModelRoutingVendorOverride { Model = tier.Model, ThinkingLevel = tier.ThinkingLevel });
        return route.Any(candidate =>
            string.Equals(FamilyEquivalent(normalizeModelId(candidate.Model)), FamilyEquivalent(model), StringComparison.OrdinalIgnoreCase)
            && ThinkingAtLeast(thinkingLevel, candidate.ThinkingLevel));
    }

    /// <summary>Older generations count as their current family for the floor check.</summary>
    private static string FamilyEquivalent(string? value) => value switch
    {
        "claude-opus-4-8" or "claude-opus-4-7" or "claude-opus-4-6" or "claude-opus-4-5" => "claude-opus-5",
        "claude-sonnet-4-6" or "claude-sonnet-4-5" => "claude-sonnet-5",
        "gpt-5.6-sol" => "gpt-6-sol",
        "gpt-5.6-luna" => "gpt-6-luna",
        _ => value ?? string.Empty,
    };

    private static bool ThinkingAtLeast(string? actual, string required)
    {
        string[] order = ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
        var actualRank = Array.FindIndex(order, value => string.Equals(value, actual, StringComparison.OrdinalIgnoreCase));
        var requiredRank = Array.FindIndex(order, value => string.Equals(value, required, StringComparison.OrdinalIgnoreCase));
        return actualRank >= 0 && requiredRank >= 0 && actualRank >= requiredRank;
    }

    private static int? ThinkingRank(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        "xhigh" => 4,
        "max" => 5,
        "ultra" => 6,
        _ => null,
    };

    /// <summary>
    /// A card's expected cost class, standing in for "how expensive is this
    /// card" until a richer signal (task type, expected scope) is available.
    /// Unset/low/medium reasoning is cheap enough to sit out a nearby reset;
    /// an explicit high/xhigh/ultra/max pin is not - that card switches to the
    /// equal-strength fallback instead of waiting.
    /// </summary>
    private static bool IsCheap(string? thinkingLevel) => thinkingLevel?.Trim().ToLowerInvariant() switch
    {
        null or "" => true,
        "high" or "xhigh" or "ultra" or "max" => false,
        _ => true,
    };

    private static string BuildSwitchReason(string primaryCli, CliRouteDecision route)
    {
        var why = !string.IsNullOrWhiteSpace(route.Reason) ? route.Reason : route.PrimaryCap.DescribeReason();
        return $"model switched pre-launch: {primaryCli} -> {route.CliType}/{route.Model ?? "<default>"}, reason: {why}";
    }

    private static string RouteLabel(string? model, string? thinking)
        => string.IsNullOrWhiteSpace(thinking)
            ? model ?? "provider default"
            : $"{model ?? "provider default"} {thinking}";

    private static string AppendReset(string reason, QuotaWindow? resetWindow)
    {
        if (resetWindow?.ResetAt is null) return reason;
        var human = !string.IsNullOrWhiteSpace(resetWindow.ResetLabel)
            ? resetWindow.ResetLabel!
            : resetWindow.ResetAt.Value.ToString("HH:mm 'UTC'");
        return $"{reason}, next reset {human}";
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The route fields a remote claim carries; <see cref="HasRoute"/> is false when nothing overrides the host default.</summary>
public sealed record ClaimRouteSelection(
    string? CliType,
    string? Model,
    string? ThinkingLevel,
    bool CleanContext,
    bool HasRoute);
