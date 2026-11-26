using AgentStudio.Runner;
using AgentStudio.Shared;

namespace AgentStudio.Dispatch;

public static class DispatchSourceMetadata
{
    public static bool Valid(CreateTaskRequest card) => (card.WaveId is null
        || !string.IsNullOrWhiteSpace(card.WaveId) && card.WaveId.Length <= 200 && !card.WaveId.Any(char.IsControl))
        && card.CitedPaths is { Count: <= 200 } && card.CitedPaths.All(path => !string.IsNullOrWhiteSpace(path)
            && path.Length <= 1000 && !path.Any(char.IsControl) && ValidPath(path.Trim()));

    private static bool ValidPath(string path) => !path.Contains('\\') && !path.StartsWith('/') && !path.Contains(':')
        && !path.Split('/').Any(part => part is "." or ".." or "");

    public static IReadOnlyList<CreateTaskRequest> Prepare(IReadOnlyList<CreateTaskRequest> cards)
    {
        if (cards is not { Count: > 0 and <= 200 } || cards.Any(c => c is null || !Valid(c)))
            throw new ArgumentException("Supply 1 to 200 cards, wave ids of at most 200 characters and at most 200 repository-relative cited paths per card.");
        string Source(CreateTaskRequest c) => string.IsNullOrWhiteSpace(c.CreationSource) ? "human" : c.CreationSource.Trim().ToLowerInvariant();
        var waves = cards.GroupBy(Source)
            .Where(g => MonolithTransitionAdapter.DispatchNeedsWave(g.Count()))
            .ToDictionary(g => g.Key, _ => "wave-" + Guid.NewGuid().ToString("N"));
        return cards.Select(c => c with
        {
            WaveId = c.WaveId?.Trim() ?? waves.GetValueOrDefault(Source(c)),
            CitedPaths = c.CitedPaths.Select(p => p.Trim()).Distinct(StringComparer.Ordinal).ToArray(),
        }).ToArray();
    }
}
