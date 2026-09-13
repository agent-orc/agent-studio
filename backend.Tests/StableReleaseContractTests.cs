extern alias UpdSvc;
using AgentStudio.TaskServer.Contracts;
using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StableReleaseContractTests
{
    [Fact]
    public void CleanUpgrade_IsAllowed()
    {
        var installed = Manifest("1.2.0", "aaa");
        var candidate = Manifest("1.3.0", "bbb");

        var result = StableReleaseContract.Compare(installed, installed, candidate, "v1.3.0", offline: false);

        Assert.True(result.Allowed);
        Assert.Equal(ReleaseDirection.Upgrade, result.Direction);
    }

    [Fact]
    public void SameVersionAndArtifact_IsAnAuditableNoOp()
    {
        var manifest = Manifest("1.2.0", "aaa");
        var result = StableReleaseContract.Compare(manifest, manifest, manifest, "v1.2.0", offline: false);
        Assert.True(result.Allowed);
        Assert.Equal(ReleaseDirection.SameVersion, result.Direction);
    }

    [Fact]
    public void Downgrade_IsRefusedWithoutExplicitApproval()
    {
        var installed = Manifest("1.3.0", "bbb");
        var candidate = Manifest("1.2.0", "aaa");
        var result = StableReleaseContract.Compare(installed, installed, candidate, "v1.2.0", offline: false);
        Assert.False(result.Allowed);
        Assert.Equal(ReleaseDirection.Downgrade, result.Direction);
        Assert.Contains(result.Errors, e => e.Contains("explicit approval"));
    }

    [Fact]
    public void DirtyCandidate_IsRefused()
    {
        var installed = Manifest("1.2.0", "aaa");
        var candidate = Manifest("1.3.0", "bbb") with { Dirty = true };
        var result = StableReleaseContract.Compare(installed, installed, candidate, "v1.3.0", offline: false);
        Assert.False(result.Allowed);
        Assert.Contains(result.Errors, e => e.Contains("dirty"));
    }

    [Fact]
    public void MissingTag_IsRefused()
    {
        var installed = Manifest("1.2.0", "aaa");
        var candidate = Manifest("1.3.0", "bbb") with { Tag = "untagged", Legacy = true };
        var result = StableReleaseContract.Compare(installed, installed, candidate, null, offline: true);
        Assert.False(result.Allowed);
        Assert.Contains(result.Errors, e => e.Contains("tag is missing"));
    }

    [Fact]
    public void MismatchedPackageTagAndVersion_IsRefused()
    {
        var installed = Manifest("1.2.0", "aaa");
        var candidate = Manifest("1.3.0", "bbb") with
        {
            CodingAgentChat = Artifact("coding-agent-chat", "0.2.0", "cac") with { Tag = "v0.1.0" }
        };
        var result = StableReleaseContract.Compare(installed, installed, candidate, "v1.3.0", offline: false);
        Assert.False(result.Allowed);
        Assert.Contains(result.Errors, e => e.Contains("Coding Agent Chat tag/version mismatch"));
    }

    [Fact]
    public void RollbackDowngrade_IsAllowedOnlyWhenExplicit()
    {
        var running = Manifest("1.3.0", "bbb");
        var rollback = Manifest("1.2.0", "aaa");
        var result = StableReleaseContract.Compare(running, running, rollback, "v1.2.0", offline: true, allowDowngrade: true);
        Assert.True(result.Allowed);
        Assert.Equal(ReleaseDirection.Downgrade, result.Direction);
    }

    [Fact]
    public void OfflineComparison_UsesCachedApprovedTagAndDoesNotInferFreshness()
    {
        var installed = Manifest("1.2.0", "aaa");
        var candidate = Manifest("1.3.0", "bbb");
        var result = StableReleaseContract.Compare(installed, installed, candidate, "v1.3.0", offline: true);
        Assert.True(result.Allowed);
        Assert.True(result.Offline);
        Assert.Contains("cached approval", result.Summary);
    }

    [Fact]
    public void RunningInstalledDivergence_IsRefused()
    {
        var running = Manifest("1.2.0", "aaa");
        var installed = Manifest("1.2.0", "other");
        var candidate = Manifest("1.3.0", "bbb");
        var result = StableReleaseContract.Compare(running, installed, candidate, "v1.3.0", offline: false);
        Assert.False(result.Allowed);
        Assert.Contains(result.Errors, e => e.Contains("running identity diverges"));
    }

    [Fact]
    public void MissingLatestApprovedTag_IsRefused()
    {
        var installed = Manifest("1.2.0", "aaa");
        var candidate = Manifest("1.3.0", "bbb");

        var result = StableReleaseContract.Compare(installed, installed, candidate, null, offline: false);

        Assert.False(result.Allowed);
        Assert.Contains(result.Errors, e => e.Contains("latest approved tag is missing"));
    }

    [Fact]
    public void LegacyUntaggedInstallation_CanMigrateToFirstApprovedRelease()
    {
        var legacy = Manifest("0.0.0-migration", "legacy") with
        {
            Tag = "untagged",
            Dirty = true,
            BuiltAt = null,
            Integrity = "unverified",
            Legacy = true
        };
        var candidate = Manifest("1.0.0", "first");

        var result = StableReleaseContract.Compare(legacy, legacy, candidate, "v1.0.0", offline: false);

        Assert.True(result.Allowed);
        Assert.Equal(ReleaseDirection.Upgrade, result.Direction);
    }

    [Fact]
    public void WrongPackageIdentity_IsRefused()
    {
        var installed = Manifest("1.2.0", "aaa");
        var candidate = Manifest("1.3.0", "bbb") with
        {
            CodingAgentChat = Artifact("some-local-dist", "0.1.0", "cac")
        };

        var result = StableReleaseContract.Compare(installed, installed, candidate, "v1.3.0", offline: false);

        Assert.False(result.Allowed);
        Assert.Contains(result.Errors, e => e.Contains("package name mismatch"));
    }

    [Fact]
    public void SameVersionWithDifferentPackageArtifact_IsDivergence()
    {
        var installed = Manifest("1.2.0", "aaa");
        var candidate = installed with
        {
            CodingAgentChat = Artifact("coding-agent-chat", "0.1.0", "different-package")
        };

        var result = StableReleaseContract.Compare(installed, installed, candidate, "v1.2.0", offline: false);

        Assert.False(result.Allowed);
        Assert.Equal(ReleaseDirection.Divergence, result.Direction);
        Assert.Contains(result.Errors, e => e.Contains("different release artifacts"));
    }

    [Fact]
    public void CandidateDependencies_MustMatchExactImmutableLockArtifacts()
    {
        var manifest = Manifest("1.3.0", "bbb");

        var errors = StableReleaseContract.ValidateCandidateDependencies(
            manifest,
            LockFileDefinition(),
            LockFileSources(
                """{"version":1,"dependencies":{"net10.0":{"CodingAgentRunner":{"resolved":"0.5.0","contentHash":"package"}}}}""",
                """{"dependencies":{"coding-agent-chat":"0.1.0"}}""",
                """{"packages":{"node_modules/coding-agent-chat":{"version":"0.1.0","resolved":"https://registry.example/coding-agent-chat-0.1.0.tgz","integrity":"sha512-package"}}}"""));

        Assert.Empty(errors);
    }

    [Fact]
    public void CandidateLocalCacDist_IsRefusedEvenWhenManifestClaimsARelease()
    {
        var manifest = Manifest("1.3.0", "bbb");

        var errors = StableReleaseContract.ValidateCandidateDependencies(
            manifest,
            LockFileDefinition(),
            LockFileSources(
                """{"version":1,"dependencies":{"net10.0":{"CodingAgentRunner":{"resolved":"0.5.0","contentHash":"package"}}}}""",
                """{"dependencies":{"coding-agent-chat":"file:C:/Projects/coding-agent-chat/dist/coding-agent-chat"}}""",
                """{"packages":{"node_modules/coding-agent-chat":{"version":"0.1.0","resolved":"file:../../../coding-agent-chat/dist/coding-agent-chat"}}}"""));

        Assert.Contains(errors, e => e.Contains("local file: dist artifact"));
        Assert.Contains(errors, e => e.Contains("not an immutable registry artifact"));
        Assert.Contains(errors, e => e.Contains("integrity is missing"));
    }

    [Fact]
    public void CandidatePackageManifestAndLockMismatch_IsRefused()
    {
        var manifest = Manifest("1.3.0", "bbb");

        var errors = StableReleaseContract.ValidateCandidateDependencies(
            manifest,
            LockFileDefinition(),
            LockFileSources(
                """{"version":1,"dependencies":{"net10.0":{"CodingAgentRunner":{"resolved":"0.4.0","contentHash":"other"}}}}""",
                """{"dependencies":{"coding-agent-chat":"0.2.0"}}""",
                """{"packages":{"node_modules/coding-agent-chat":{"version":"0.2.0","resolved":"https://registry.example/coding-agent-chat-0.2.0.tgz","integrity":"sha512-other"}}}"""));

        Assert.Contains(errors, e => e.Contains("CodingAgentRunner version mismatch"));
        Assert.Contains(errors, e => e.Contains("CodingAgentRunner integrity mismatch"));
        Assert.Contains(errors, e => e.Contains("coding-agent-chat package.json version mismatch"));
        Assert.Contains(errors, e => e.Contains("coding-agent-chat locked version mismatch"));
        Assert.Contains(errors, e => e.Contains("coding-agent-chat integrity mismatch"));
    }

    [Fact]
    public void CandidateDependencies_AcceptExactPinAndRegistryHashRules()
    {
        var manifest = Manifest("1.3.0", "bbb");
        var definition = Definition("""
              identity:
                - package: CodingAgentRunner
                  ecosystem: nuget
                  version: 0.5.0
                  integrity: sha512-package
                - package: coding-agent-chat
                  ecosystem: npm
                  version: 0.1.0
                  integrity: sha512-package
            """);

        var errors = StableReleaseContract.ValidateCandidateDependencies(
            manifest, definition, new Dictionary<string, string>());

        Assert.Empty(errors);
    }

    [Fact]
    public void CandidateDependencies_RefuseExactPinAndRegistryHashMismatch()
    {
        var manifest = Manifest("1.3.0", "bbb");
        var definition = Definition("""
              identity:
                - package: CodingAgentRunner
                  ecosystem: nuget
                  version: 0.6.0
                  integrity: sha512-othercar
                - package: coding-agent-chat
                  ecosystem: npm
                  version: 0.2.0
                  integrity: sha512-othercac
            """);

        var errors = StableReleaseContract.ValidateCandidateDependencies(
            manifest, definition, new Dictionary<string, string>());

        Assert.Contains(errors, error => error.Contains("CodingAgentRunner version mismatch"));
        Assert.Contains(errors, error => error.Contains("CodingAgentRunner integrity mismatch"));
        Assert.Contains(errors, error => error.Contains("coding-agent-chat version mismatch"));
        Assert.Contains(errors, error => error.Contains("coding-agent-chat integrity mismatch"));
    }

    [Fact]
    public void CandidateDependencies_RefuseProjectWithoutReleaseRule()
    {
        var manifest = Manifest("1.3.0", "bbb");
        var definition = ProjectDefinitionReader.Parse(BaseDefinition + "\n").Definition!;

        var errors = StableReleaseContract.ValidateCandidateDependencies(
            manifest, definition, new Dictionary<string, string>());

        Assert.Contains(errors, error => error.Contains("does not declare a release contract"));
    }

    private static ProjectExecutionDefinition LockFileDefinition() => Definition("""
          identity:
            - package: CodingAgentRunner
              ecosystem: nuget
              source: backend/packages.lock.json
            - package: coding-agent-chat
              ecosystem: npm
              source: frontend/package-lock.json
        """);

    private static ProjectExecutionDefinition Definition(string releaseBody)
    {
        var read = ProjectDefinitionReader.Parse($"{BaseDefinition}\nrelease:\n{releaseBody}\n  restore:\n    - restore dependencies\n");
        Assert.True(read.IsValid, string.Join(Environment.NewLine, read.Issues.Select(issue => issue.Message)));
        return read.Definition!;
    }

    private static IReadOnlyDictionary<string, string> LockFileSources(
        string nugetLock,
        string npmPackage,
        string npmLock) => new Dictionary<string, string>
        {
            ["backend/packages.lock.json"] = nugetLock,
            ["frontend/package.json"] = npmPackage,
            ["frontend/package-lock.json"] = npmLock,
        };

    private const string BaseDefinition = """
        schemaVersion: 1
        stack: [dotnet, node]
        toolVersions:
        commands:
          prepare: .agent-studio/prepare
          build:
          test:
          lint:
        testSuites:
        cachePaths: [frontend/node_modules]
        capabilities: [linux]
        environment:
          CI: "true"
        """;

    private static ReleaseManifest Manifest(string version, string commit) => new(
        1, "Agent Studio", $"v{version}", version, commit, false,
        DateTimeOffset.Parse("2026-07-12T10:00:00Z"), "sha256-app",
        Artifact("CodingAgentRunner", "0.5.0", "car"),
        Artifact("coding-agent-chat", "0.1.0", "cac"));

    private static ReleaseArtifact Artifact(string name, string version, string commit) =>
        new(name, version, $"v{version}", commit, "sha512-package");
}
