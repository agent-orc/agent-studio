using System.Security.Cryptography;
using System.Text;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3429: a dependency-cache hit skipped Quality Studio's whole profile
/// install command, which also ran <c>dotnet restore</c> although its only
/// lockfile scope was <c>frontend</c>. A hit may now skip a preparation command
/// only when the plan marks the command as covered by its lockfile scopes.
/// </summary>
public sealed class DependencyPreparationSkipPolicyTests
{
    public static TheoryData<bool, string[], bool> Matrix => new()
    {
        { true, ["hit"], true },
        { true, ["hit", "hit"], true },
        { true, ["hit", "miss"], false },
        { true, ["miss"], false },
        { true, [], false },
        { false, ["hit"], false },
        { false, ["hit", "hit"], false },
        { false, ["miss"], false },
        { false, [], false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void A_hit_skips_only_a_covered_command_whose_every_scope_hits(
        bool covered,
        string[] states,
        bool expected)
    {
        var decisions = states
            .Select((state, index) => new ReviewDependencyCacheEvidenceDto(
                $"scope-{index}", state, "fixture", "lock", ["package-lock.json"], false))
            .ToArray();

        Assert.Equal(expected, DependencyPreparationSkipPolicy.MaySkip(covered, decisions));
    }

    [Fact]
    public void Profile_fingerprint_changes_only_when_the_install_coverage_opt_in_is_set()
    {
        var profile = new BuildProfile
        {
            InstallCmd = "npm --prefix frontend ci",
            Lockfiles = ["frontend/package-lock.json"],
        };
        // The payload every profile was validated against before AGT-3429.
        const string earlierPayload =
            """{"stack":null,"installCmd":"npm --prefix frontend ci","buildCmds":[],"testCmds":[],"lockfiles":["frontend/package-lock.json"],"preserveGlobs":[],"poolSize":null}""";
        var earlier = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(earlierPayload)))
            .ToLowerInvariant();

        Assert.Equal(earlier, BuildProfileValidationFingerprint.Create(profile));
        Assert.NotEqual(
            earlier,
            BuildProfileValidationFingerprint.Create(profile with { InstallCoveredByLockfiles = true }));
    }
}
