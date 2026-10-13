using System.Reflection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BuildIdentityTests
{
    [Fact]
    public void PublishedArtifactRead_ReturnsConfiguredReleaseSha()
    {
        var directory = Path.Combine(Path.GetTempPath(), "studio-build-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sha = new string('a', 40);
        try
        {
            File.WriteAllText(Path.Combine(directory, "RELEASE-SHA"), sha + "\n");
            var legacy = BuildIdentity.Load(new ConfigurationBuilder().Build(), Assembly.GetExecutingAssembly());

            Assert.Equal(sha, PublishedArtifactSha.Load(directory, legacy).Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingManifest_ProducesExplicitLegacyDirtyIdentityWithoutTimestampFreshness()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Release:BuildManifestPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json")
        }).Build();

        var identity = BuildIdentity.Load(config, Assembly.GetExecutingAssembly());

        Assert.True(identity.Legacy);
        Assert.True(identity.Dirty);
        Assert.Equal("untagged", identity.Tag);
        Assert.Null(identity.BuiltAt);
    }

    [Fact]
    public void ManifestWithMismatchedPackageIdentity_IsRejected()
    {
        var identity = new BuildIdentity(
            1, "Agent Studio", "v1.2.0", "1.2.0", "abcdef0", false,
            DateTimeOffset.Parse("2026-07-17T10:00:00Z"), "sha256-app",
            new ReleaseArtifactIdentity("CodingAgentRunner", "0.5.0", "v0.5.0", "car0000", "sha512-car"),
            new ReleaseArtifactIdentity("local-dist", "0.1.0", "v0.1.0", "cac0000", "sha512-cac"));

        var error = Assert.Throws<InvalidDataException>(() => BuildIdentity.Validate(identity));

        Assert.Contains("codingAgentChat.name mismatch", error.Message);
    }
}
