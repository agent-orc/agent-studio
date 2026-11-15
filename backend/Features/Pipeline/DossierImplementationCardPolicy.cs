using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Card-cutting policy for approved Dossier recommendations. One descriptor
/// item is one independently reviewable slice and therefore one coding card.
/// An open-ended "implement all recommendations" item is rejected unless the
/// descriptor supplies an explicit bounded-slice acceptance scope.
/// </summary>
public static class DossierImplementationCardPolicy
{
    private static readonly string[] OpenEndedPhrases =
    [
        "implement all recommendations",
        "implement every recommendation",
        "deliver all recommendations",
        "complete all recommendations",
    ];

    private static readonly Regex AllRecommendations = new(
        @"\b(?:implement|deliver|complete)\s+(?:all|every)\b.{0,80}\brecommendations?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static TaskAcceptanceScope AcceptanceScopeFor(ConceptImplementationTask item)
    {
        var explicitScope = TaskAcceptanceScopes.Normalize(item.AcceptanceScope);
        if (item.AcceptanceScope is not null && explicitScope is null)
            throw new InvalidDataException(
                "A Dossier implementation item's acceptanceScope is invalid or incomplete.");
        if (explicitScope is not null)
        {
            if (explicitScope.DeliveryMode != TaskAcceptanceDeliveryModes.BoundedSlice)
                throw new InvalidDataException(
                    "A Dossier implementation item must use acceptanceScope.deliveryMode 'bounded-slice'.");
            return explicitScope;
        }

        if (IsOpenEnded(item))
            throw new InvalidDataException(
                "A Dossier cannot promote an open-ended 'implement all recommendations' item. " +
                "Cut one implementationTasks entry per slice and give each entry a bounded acceptanceScope.");

        return TaskAcceptanceScopes.BoundedSlice(
            item.Title,
            item.PromptMarkdown.Trim());
    }

    public static string? Validate(ConceptImplementationTask item)
    {
        try
        {
            _ = AcceptanceScopeFor(item);
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Checks slice ids and the <c>dependsOn</c> lists between descriptor items.
    /// A dependency names the id of an earlier item, so descriptor order is a
    /// valid delivery order and the slice graph cannot form a cycle.
    /// </summary>
    public static IReadOnlyList<string> ValidateDependencies(IReadOnlyList<ConceptImplementationTask> items)
    {
        var findings = new List<string>();
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var id = items[i].Id?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            if (!indexById.TryAdd(id, i))
                findings.Add($"implementationTasks[{i}] id '{id}' is already used by implementationTasks[{indexById[id]}].");
        }

        for (var i = 0; i < items.Count; i++)
        {
            foreach (var raw in items[i].DependsOn ?? [])
            {
                var target = raw?.Trim();
                if (string.IsNullOrEmpty(target))
                    findings.Add($"implementationTasks[{i}].dependsOn contains an empty slice id.");
                else if (!indexById.TryGetValue(target, out var index))
                    findings.Add($"implementationTasks[{i}].dependsOn names unknown slice id '{target}'.");
                else if (index == i)
                    findings.Add($"implementationTasks[{i}] cannot depend on itself.");
                else if (index > i)
                    findings.Add($"implementationTasks[{i}].dependsOn names the later slice '{target}'. " +
                        "List every slice after the slices it depends on.");
            }
        }
        return findings;
    }

    /// <summary>
    /// Descriptor indexes of the earlier items that the item at
    /// <paramref name="index"/> directly depends on, in descriptor order.
    /// Unknown, self, and forward ids are ignored; the contract rejects them.
    /// </summary>
    public static IReadOnlyList<int> PredecessorIndexes(IReadOnlyList<ConceptImplementationTask> items, int index)
    {
        var wanted = new HashSet<string>(
            (items[index].DependsOn ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var predecessors = new List<int>();
        for (var candidate = 0; candidate < index && wanted.Count > 0; candidate++)
        {
            // Remove, so a duplicated id resolves to its first item only.
            if (items[candidate].Id?.Trim() is { Length: > 0 } id && wanted.Remove(id))
                predecessors.Add(candidate);
        }
        return predecessors;
    }

    private static bool IsOpenEnded(ConceptImplementationTask item)
    {
        var text = $"{item.Title}\n{item.PromptMarkdown}";
        return AllRecommendations.IsMatch(text)
               || OpenEndedPhrases.Any(phrase =>
                   text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }
}
