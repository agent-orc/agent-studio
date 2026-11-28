namespace AgentStudio.OrchestrationDecisions;

public sealed record DispatchLeaseWindow(string Key, string Persona, int Epoch, DateTimeOffset OpenedAt);
public sealed record DispatchLiveWaveResult(string Key, int Epoch, string Persona, double? Value, bool Complete);
public sealed record DispatchChallenger(string Persona, string Strategy, DispatchCompetitionFacts Facts);
public sealed record DispatchGuardSample(string Digest, int Cards, double? LiveConflictsPer100,
    double? BaselineShadowConflictsPer100, bool SameCards);
public sealed record DispatchGuardFallback(string Id, string Persona, string Sample, DateTimeOffset At);
public sealed record DispatchLeaseState(string Champion = "queue-keeper", string? Challenger = null,
    int Epoch = 0, bool ChallengerNext = false, bool Stopped = false, string? Pinned = null,
    DispatchGuardFallback? Guard = null, string? UndoneSample = null)
{
    public IReadOnlyList<DispatchLeaseWindow> Windows { get; init; } = [];
}
public sealed record DispatchLeaseFacts(DateTimeOffset Now, string? Wave, string? CurrentPersona,
    IReadOnlyList<DispatchChallenger> Challengers, IReadOnlyList<string> Available,
    IReadOnlyList<DispatchLiveWaveResult> Results, DispatchGuardSample Guard,
    string? OwnerAction = null, string? OwnerPersona = null, string? ExpectedGuard = null);
public sealed record DispatchLeaseDecision(DispatchLeaseState State, string Persona, string Window,
    string Reason, bool Allowed = true, int? FeedImportance = null)
{
    public string AuthorityTier => "T1";
}
public sealed record DispatchLeaseOccurrence(DispatchLeaseState State, DispatchLeaseFacts Facts,
    long ExpectedFence, DateTimeOffset? LeaseExpiresAt);

/// <summary>Project-local competition and owner controls. Hosts freeze evidence and fence the returned lease.</summary>
public static class DispatchLeasePolicy
{
    public static string Window(string? wave, DateTimeOffset now)
        => string.IsNullOrWhiteSpace(wave) ? $"day:{now.UtcDateTime:yyyy-MM-dd}" : "wave:" + wave;

    public static DispatchLeaseDecision Decide(DispatchLeaseState state, DispatchLeaseFacts facts)
    {
        var window = Window(facts.Wave, facts.Now);
        var old = state;
        if (facts.OwnerAction is { } action)
        {
            if (action == "pin")
            {
                if (facts.OwnerPersona is not { } persona || !facts.Available.Contains(persona)
                    || facts.Challengers.Any(c => c.Persona == persona && c.Strategy == DispatchStrategies.PackageThenChain))
                    return new(old, facts.CurrentPersona ?? "queue-keeper", window, "persona-not-authorized", false);
                state = state with { Pinned = persona, Stopped = true };
            }
            else if (action == "stop") state = state with { Stopped = true, Pinned = facts.CurrentPersona ?? state.Champion };
            else if (action == "undo-guard" && state.Guard is { } guard && guard.Id == facts.ExpectedGuard
                && facts.Available.Contains(guard.Persona))
            {
                state = state with { Guard = null, UndoneSample = guard.Sample };
                if (!state.Windows.Any(w => w.Key == window))
                    state = state with { Windows = [.. state.Windows, new(window, guard.Persona, state.Epoch, facts.Now)] };
                return new(state, guard.Persona, window, "guard-undone");
            }
            else return new(old, facts.CurrentPersona ?? "queue-keeper", window, "stale-or-unknown-control", false);
        }

        var sample = facts.Guard;
        var exceeds = DispatchCompetitionPolicy.Evaluate(new(null, null, 0, 0, 0, false,
            sample.Cards, sample.LiveConflictsPer100, sample.BaselineShadowConflictsPer100, sample.SameCards)).GuardExceeded;
        if (state.Guard is null && facts.CurrentPersona is { } current && current != "queue-keeper"
            && sample.Digest != state.UndoneSample && exceeds)
        {
            var fallback = new DispatchGuardFallback($"guard:{state.Epoch}:{sample.Digest}", current, sample.Digest, facts.Now);
            return new(state with { Guard = fallback }, "queue-keeper", window, "guard-fallback", FeedImportance: 70);
        }
        if (state.Guard is not null) return new(state, "queue-keeper", window, "guard-active");
        if (state.Pinned is { } pinned)
            return Assign(pinned, "owner-pinned");
        if (state.Stopped) return Assign(state.Champion, "alternation-stopped");

        if (state.Challenger is { } rival)
        {
            var completed = state.Windows.Where(w => w.Epoch == state.Epoch).Take(8)
                .Select(w => facts.Results.FirstOrDefault(r => r.Key == w.Key && r.Epoch == w.Epoch && r.Complete)).ToArray();
            var champions = completed.OfType<DispatchLiveWaveResult>().Where(r => r.Persona == state.Champion).ToArray();
            var challengers = completed.OfType<DispatchLiveWaveResult>().Where(r => r.Persona == rival).ToArray();
            // Four paired windows of each persona must settle; null scores and a single loss cannot promote.
            if (champions.Length >= 4 && challengers.Length >= 4 && completed.All(r => r is not null)
                && champions.Take(4).Zip(challengers.Take(4)).All(pair => pair.First.Value is { } a
                    && pair.Second.Value is { } b && double.IsFinite(a) && double.IsFinite(b) && b > a))
                state = state with { Champion = rival, Challenger = null, Epoch = state.Epoch + 1, ChallengerNext = false };
        }
        // A promotion changes the comparison reference. Collect scores against the new champion before choosing again.
        if (state.Challenger is null && state.Champion == old.Champion)
        {
            var candidate = facts.Challengers.Where(c => c.Persona != state.Champion
                    && facts.Available.Contains(c.Persona) && c.Strategy != DispatchStrategies.PackageThenChain
                    && (state.Champion != "queue-keeper" || c.Strategy == DispatchStrategies.OnePerArea)
                    && DispatchCompetitionPolicy.Evaluate(c.Facts).EligibleForAlternatingLive)
                .OrderByDescending(c => c.Facts.ChallengerValue).ThenBy(c => c.Persona, StringComparer.Ordinal).FirstOrDefault();
            if (candidate is not null)
                state = state with { Challenger = candidate.Persona, Epoch = state.Epoch + 1, ChallengerNext = false };
        }
        var existing = state.Windows.FirstOrDefault(w => w.Key == window);
        if (existing is not null)
            return Assign(existing.Persona, "window-retained");
        var selected = state.Challenger is not null && state.ChallengerNext ? state.Challenger : state.Champion;
        state = state with { Windows = [.. state.Windows, new(window, selected, state.Epoch, facts.Now)],
            ChallengerNext = state.Challenger is not null && !state.ChallengerNext };
        return Assign(selected, "window-opened");

        DispatchLeaseDecision Assign(string assigned, string reason)
            => assigned == "queue-keeper" || facts.Available.Contains(assigned)
                ? new(state, assigned, window, reason)
                : new(state, "queue-keeper", window, "assigned-persona-unavailable");
    }

    /// <summary>Only the later admission of a named collision owns its conflict, regardless of which card integrated last.</summary>
    public static string? LaterCard(IReadOnlyList<(string Card, DateTimeOffset At)> admissions)
        => admissions.Count == 0 ? null : admissions.OrderByDescending(a => a.At)
            .ThenByDescending(a => a.Card, StringComparer.Ordinal).First().Card;
}
