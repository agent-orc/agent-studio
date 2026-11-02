using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Keeps inline review evidence below the Task Server's request body budget.</summary>
internal static class ReviewReportPayloadPolicy
{
    internal const int MaxInlineArtifactWireBytes = 16 * 1024 * 1024;

    internal static (IReadOnlyList<ReviewArtifactEvidenceDto> Artifacts, int Omitted)
        Bound(IReadOnlyList<ReviewArtifactEvidenceDto> artifacts,
            int maxWireBytes = MaxInlineArtifactWireBytes,
            IReadOnlySet<string>? priorityHashes = null)
    {
        var retain = new bool[artifacts.Count];
        var used = 0L;
        foreach (var (artifact, index) in artifacts.Select((artifact, index) => (artifact, index))
                     .OrderByDescending(item => priorityHashes?.Contains(item.artifact.Sha256) == true))
        {
            if (artifact.ContentBase64 is null) continue;
            var wireBytes = JsonSerializer.SerializeToUtf8Bytes(artifact.ContentBase64).LongLength;
            if (used + wireBytes <= maxWireBytes)
            {
                retain[index] = true;
                used += wireBytes;
            }
        }
        var result = new List<ReviewArtifactEvidenceDto>(artifacts.Count);
        var omitted = 0;
        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            if (artifact.ContentBase64 is null || retain[index]) result.Add(artifact);
            else
            {
                result.Add(artifact with { ContentBase64 = null });
                omitted++;
            }
        }
        return (result, omitted);
    }
}
