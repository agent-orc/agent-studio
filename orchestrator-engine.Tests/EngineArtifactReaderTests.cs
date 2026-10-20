using System.Security.Cryptography;
using System.Text;
using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class EngineArtifactReaderTests
{
    [Fact]
    public async Task Redacted_window_has_immutable_digest_and_no_source_path()
    {
        var secret = "api_key=" + new string('q', 24);
        var content = string.Join('\n', Enumerable.Range(0, 90).Select(i => $"line {i}")) + "\n" + secret;
        var original = Artifact(content);
        var reader = new EngineArtifactReader(new Source(original));

        var window = await reader.ReadWindowAsync("run-1", "artifact-1", default);

        Assert.DoesNotContain(secret, window.Content);
        Assert.Contains("[omitted: secret]", window.Content);
        Assert.Contains("line-cap", window.OmissionReasons);
        Assert.Contains("secret", window.OmissionReasons);
        Assert.True(window.Content.Split('\n').Length <= EngineArtifactReader.MaxWindowLines);
        Assert.True(Encoding.UTF8.GetByteCount(window.Content) <= EngineArtifactReader.MaxWindowBytes);
        Assert.Equal("sha256:" + original.Sha256, window.Source.Digest);
        Assert.Equal("artifact:run-1/artifact-1", window.Source.Ref);
        Assert.DoesNotContain("/private", window.Source.Ref);
    }

    [Fact]
    public async Task Changed_content_under_the_same_artifact_identity_is_rejected()
    {
        var original = Artifact("first snapshot");
        var changed = original with { ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("rotated content")) };

        await Assert.ThrowsAsync<InvalidDataException>(() => new EngineArtifactReader(new Source(changed))
            .ReadWindowAsync("run-1", "artifact-1", default));
    }

    [Fact]
    public async Task Oversize_source_has_an_omission_reason_without_transferring_text_to_the_model()
    {
        var oversized = Artifact("small") with { SizeBytes = EngineArtifactReader.MaxSourceBytes + 1 };

        var window = await new EngineArtifactReader(new Source(oversized))
            .ReadWindowAsync("run-1", "artifact-1", default);

        Assert.Equal("", window.Content);
        Assert.Equal(["oversize-source"], window.OmissionReasons);
    }

    private static ArtifactContentDto Artifact(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new("artifact-1", "run-1", "observations.log", "text/plain",
            Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToBase64String(bytes), bytes.Length,
            "/private/rotatable/log-path");
    }

    private sealed class Source(ArtifactContentDto artifact) : IEngineArtifactSource
    {
        public Task<ArtifactContentDto> GetArtifactContentAsync(string runId, string artifactId, CancellationToken ct)
            => Task.FromResult(artifact);
    }
}
