using System.Runtime.CompilerServices;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins npm-shim repair ownership after the CAR migration. CAR owns repair for
/// card runs; only the bounded non-agent Claude one-shot uses the Studio helper.
/// </summary>
public class NpmShimRepairContractTests
{
    [Fact]
    public void Repair_helper_is_wired_only_to_the_non_agent_one_shot_path()
    {
        var root = RepoRoot();
        var helper = Source(root, "backend/Features/Cli/Execution/NpmShimHealer.cs");
        var behaviors = Source(root, "backend/Features/Cli/Execution/BuiltInCliBehaviors.cs");
        var oneShot = Source(root, "backend/Features/Cli/Routing/OneShot/ClaudeOneShot.cs");
        var car = Source(root, "backend/Features/Cli/Execution/BackendCarExecution.cs");

        Assert.Contains("TryHealClaudeAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("NpmShimHealer", behaviors, StringComparison.Ordinal);
        Assert.Equal(1, Count(oneShot, "NpmShimHealer.TryHealClaudeAsync"));
        Assert.DoesNotContain("NpmShimHealer", car, StringComparison.Ordinal);
    }

    private static string Source(string root, string relativePath)
        => File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var current = Path.GetDirectoryName(sourceFile);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException("agent-taskboard.sln not found above test source.");
    }
}
