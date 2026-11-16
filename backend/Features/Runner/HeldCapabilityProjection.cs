using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-3840: the held capabilities of one runner role, as Execution Hosts lists
/// them. The registry knows the keys it withholds from claims; the refused
/// review claims add keys the role never registered, which no advertisement
/// shows. Pure, so both sources are pinned directly.
/// </summary>
public static class HeldCapabilityProjection
{
    public static IReadOnlyList<Contract.HeldCapabilityDto> Merge(
        IReadOnlyList<Contract.HeldCapabilityDto> holds,
        IEnumerable<ReviewUnclaimableObservation> refusals)
    {
        var byKey = holds.ToDictionary(hold => hold.Key, StringComparer.Ordinal);
        var waiting = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var refusal in refusals.Where(item => item.NamesCapabilities))
        {
            foreach (var key in refusal.MissingCapabilities.Distinct(StringComparer.Ordinal))
            {
                if (!waiting.TryGetValue(key, out var tasks))
                    waiting[key] = tasks = new SortedSet<string>(StringComparer.Ordinal);
                tasks.Add(refusal.TaskKey);
                if (byKey.TryGetValue(key, out var existing))
                {
                    if (existing.ObservedAt is null) byKey[key] = existing with { ObservedAt = refusal.ObservedAt };
                    continue;
                }
                byKey[key] = new Contract.HeldCapabilityDto(
                    key,
                    Contract.HeldCapabilityStates.Held,
                    Contract.HeldCapabilityCauses.NotRegistered,
                    "This role did not register the key, so it cannot claim work that needs it.",
                    refusal.ObservedAt);
            }
        }
        return byKey.Values
            .Select(hold => waiting.TryGetValue(hold.Key, out var tasks)
                ? hold with { WaitingTaskKeys = tasks.ToArray() }
                : hold)
            .OrderBy(hold => hold.Key, StringComparer.Ordinal)
            .ToArray();
    }
}
