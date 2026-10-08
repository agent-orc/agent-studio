using System.Text.Json;
using AgentStudio.Tasks;

namespace AgentStudio.Tests;

internal static class CompletionContentFixture
{
    public static void RecordPassedReview(string folder, string aspect = "requirement-fit")
    {
        BriefVersionStore.ReadOrCreate(folder);
        CompletionContentEvidence.StampLocalRun(folder);
        File.WriteAllText(Path.Combine(folder, CompletionContentEvidence.LocalContextFile),
            JsonSerializer.Serialize(new
            {
                briefVersion = CompletionContentEvidence.ReadLocalRunVersion(folder),
            }));
        File.WriteAllText(Path.Combine(folder, $"aspect-{aspect}.json"),
            JsonSerializer.Serialize(new
            {
                status = "pass",
                summary = "The delivered content satisfies the current brief.",
            }));
    }
}
