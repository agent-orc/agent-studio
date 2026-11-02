using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Bounds embedded review logs while retaining original digests and useful excerpts.</summary>
public static class ReviewReportArtifactBudget
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public static ReviewReportRequest Apply(ReviewReportRequest request)
    {
        var artifacts = new List<ReviewArtifactEvidenceDto>();
        foreach (var artifact in request.Artifacts)
        {
            if (artifact.ContentBase64 is null)
            {
                artifacts.Add(artifact);
                continue;
            }

            // The server verifies embedded bytes against their digest. Keep the full
            // log's digest as metadata and give an excerpt its own valid digest.
            var bytes = Convert.FromBase64String(artifact.ContentBase64);
            if (bytes.Length <= ReviewReportLimits.MaxEmbeddedArtifactBytes)
            {
                artifacts.Add(artifact);
                continue;
            }

            artifacts.Add(artifact with { ContentBase64 = null });
            var marker = Encoding.UTF8.GetBytes(
                $"\n[review log elided; full bytes={artifact.SizeBytes}; sha256={artifact.Sha256}]\n");
            var retained = ReviewReportLimits.MaxEmbeddedArtifactBytes - marker.Length;
            var head = retained / 2;
            var tail = retained - head;
            var excerpt = new byte[head + marker.Length + tail];
            bytes.AsSpan(0, head).CopyTo(excerpt);
            marker.CopyTo(excerpt, head);
            bytes.AsSpan(bytes.Length - tail).CopyTo(excerpt.AsSpan(head + marker.Length));
            artifacts.Add(new ReviewArtifactEvidenceDto(
                artifact.Name + ".excerpt",
                "text/plain",
                Convert.ToHexString(SHA256.HashData(excerpt)).ToLowerInvariant(),
                excerpt.Length,
                Convert.ToBase64String(excerpt)));
        }

        var result = request with
        {
            Artifacts = artifacts,
            ArtifactsReduced = artifacts.Count != request.Artifacts.Count,
        };
        if (WireBytes(result) <= ReviewReportLimits.TargetRequestBytes)
            return result;

        // Preserve every artifact's identity and digest. Drop embedded content
        // from the end until the serialized request fits the wire ceiling.
        for (var index = artifacts.Count - 1; index >= 0; index--)
        {
            if (artifacts[index].ContentBase64 is null) continue;
            artifacts[index] = artifacts[index] with { ContentBase64 = null };
            result = request with { Artifacts = artifacts.ToArray(), ArtifactsReduced = true };
            if (WireBytes(result) <= ReviewReportLimits.TargetRequestBytes)
                return result;
        }
        return result;
    }

    public static ReviewReportRequest Reduce(ReviewReportRequest request)
        => request with
        {
            Artifacts = request.Artifacts.Select(artifact => artifact with { ContentBase64 = null }).ToArray(),
            ArtifactsReduced = true,
        };

    public static int WireBytes(ReviewReportRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(request, WireJson).Length;
}
