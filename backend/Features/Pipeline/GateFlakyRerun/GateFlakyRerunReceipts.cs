using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-2853: durable card-side record of every test the integration gate
/// quarantined as flaky. The gate log under <c>post-steps/</c> is the evidence
/// an operator reads while debugging one merge; the timeline is the ledger that
/// makes the flake list accumulate across cards, which is the whole point of not
/// absorbing the re-run silently.
/// </summary>
public static class GateFlakyRerunReceipts
{
    /// <summary>Detail key carrying the comma-separated quarantined test names.</summary>
    public const string TestsKey = "tests";

    /// <summary>Detail key carrying the evidence prefix of the gate that re-ran them.</summary>
    public const string GateKey = "gate";

    /// <summary>Detail key carrying the exact SHA the gate verified.</summary>
    public const string ShaKey = "sha";

    /// <summary>Detail key carrying the classification shared with the review executor.</summary>
    public const string ClassificationKey = "classification";

    public static bool Record(
        TimelineLog timeline,
        string jobFolderPath,
        string gate,
        string? testedSha,
        IReadOnlyList<string> flakyQuarantined)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (flakyQuarantined.Count == 0) return false;

        var names = string.Join(", ", flakyQuarantined);
        return timeline.Append(
            jobFolderPath,
            TimelineEventKinds.IntegrationGateFlakyRerun,
            TimelineActors.System,
            $"{gate}: {flakyQuarantined.Count} test(s) failed in the full run and passed on the " +
            $"targeted re-run; recorded as flaky: {names}.",
            details: new Dictionary<string, string>
            {
                [GateKey] = gate,
                [ShaKey] = testedSha ?? "unknown",
                [ClassificationKey] = ReviewFlakyQuarantine.Classification,
                [TestsKey] = names,
            });
    }
}
