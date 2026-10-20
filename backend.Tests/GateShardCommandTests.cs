using System.Xml.Linq;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3232: shard invocations keep the suite's own category filter, cover
/// every test through the complement shard, and carry the filter in run
/// settings. The test-class catalog only balances; it never decides coverage.
/// </summary>
public sealed class GateShardCommandTests
{
    private const string Suite =
        "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound";

    [Fact]
    public void The_suite_filter_is_separated_from_the_invocation()
    {
        var parsed = GateShardCommand.TryParse(Suite)!;

        Assert.Equal("Category!=MachineBound", parsed.BaseFilter);
        Assert.Equal("dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build", parsed.CommandWithoutFilter);
        Assert.Equal("backend.Tests/OrchestratorApi.Tests.csproj", parsed.TestProject);
    }

    [Fact]
    public void A_quoted_compound_filter_is_kept_whole()
    {
        var parsed = GateShardCommand.TryParse(
            "dotnet test A.Tests/A.Tests.csproj --filter \"Category!=MachineBound&Category!=Flaky\" --no-build")!;

        Assert.Equal("Category!=MachineBound&Category!=Flaky", parsed.BaseFilter);
        Assert.Equal("dotnet test A.Tests/A.Tests.csproj --no-build", parsed.CommandWithoutFilter);
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet test agent-taskboard.sln --no-build")]
    [InlineData("dotnet test A.csproj --settings custom.runsettings")]
    [InlineData("dotnet test A.csproj --list-tests")]
    [InlineData("dotnet test A.csproj --no-build && echo done")]
    [InlineData("npm --prefix frontend run test:ci")]
    [InlineData("dotnet build A.csproj")]
    public void Commands_the_gate_cannot_shard_safely_are_left_whole(string command)
        => Assert.Null(GateShardCommand.TryParse(command));

    [Fact]
    public void Each_shard_intersects_the_suite_filter_and_the_complement_excludes_every_other_shard()
    {
        var parsed = GateShardCommand.TryParse(Suite)!;
        var plan = new GateShardPlan("backend", 3, GateShardCountSources.Profile, GateShardStrategies.Hash,
        [
            new(0, ["AlphaTests", "BetaTests"], 0),
            new(1, ["GammaTests"], 0),
            new(2, ["DeltaTests"], 0),
        ], "digest");

        Assert.Equal("(Category!=MachineBound)&(FullyQualifiedName~.AlphaTests.|FullyQualifiedName~.BetaTests.)",
            GateShardCommand.Filter(parsed, plan, 0));
        Assert.Equal("(Category!=MachineBound)&(FullyQualifiedName~.GammaTests.)",
            GateShardCommand.Filter(parsed, plan, 1));
        Assert.Equal(
            "(Category!=MachineBound)&(FullyQualifiedName!~.AlphaTests.&FullyQualifiedName!~.BetaTests.&FullyQualifiedName!~.GammaTests.)",
            GateShardCommand.Filter(parsed, plan, 2));
    }

    [Fact]
    public void A_suite_without_a_filter_shards_on_the_class_partition_alone()
    {
        var parsed = GateShardCommand.TryParse("dotnet test A.Tests/A.Tests.csproj --no-build")!;
        var plan = new GateShardPlan("a", 2, GateShardCountSources.Profile, GateShardStrategies.Hash,
            [new(0, ["AlphaTests"], 0), new(1, [], 0)], "digest");

        Assert.Null(parsed.BaseFilter);
        Assert.Equal("FullyQualifiedName~.AlphaTests.", GateShardCommand.Filter(parsed, plan, 0));
        Assert.Equal("FullyQualifiedName!~.AlphaTests.", GateShardCommand.Filter(parsed, plan, 1));
    }

    [Fact]
    public void Run_settings_carry_the_filter_escaped_and_the_invocation_points_at_them()
    {
        var filter = "(Category!=MachineBound)&(FullyQualifiedName~.AlphaTests.)";
        var settings = XDocument.Parse(GateShardCommand.RunSettings(filter));

        Assert.Equal(filter, settings.Root!.Element("RunConfiguration")!.Element("TestCaseFilter")!.Value);
        Assert.Contains("&amp;", GateShardCommand.RunSettings(filter));
        Assert.Equal("dotnet test A.Tests/A.Tests.csproj --no-build --settings \"C:/temp/s-0.runsettings\"",
            GateShardCommand.ShardInvocation(GateShardCommand.TryParse("dotnet test A.Tests/A.Tests.csproj --no-build")!,
                "C:\\temp\\s-0.runsettings"));
    }

    [Fact]
    public void The_shard_invocation_is_still_a_targetable_test_command()
    {
        var invocation = GateShardCommand.ShardInvocation(GateShardCommand.TryParse(Suite)!, "/tmp/s-1.runsettings");

        Assert.True(GateFlakyRerunPolicy.IsTargetableDotNetTest(invocation));
        Assert.DoesNotContain("--filter", invocation);
    }

    [Fact]
    public void The_catalog_reads_top_level_test_classes_of_file_scoped_and_block_namespaces()
    {
        const string fileScoped = """
            namespace AgentStudio.Tests;

            public sealed class StoreTests
            {
                [Fact] public void Writes() { }
                private sealed class NestedFixture { }
            }

            public abstract class StoreTestBase { }
            internal static class StoreData { }
            public sealed partial class SecondTests { }
            """;
        const string block = """
            namespace AgentStudio.Tests
            {
                public class LegacyTests
                {
                    [Theory] public void Reads(int value) { }
                    public class Inner { }
                }
            }
            """;
        const string helper = """
            namespace AgentStudio.Tests;
            public sealed class FakeClock { }
            """;

        Assert.Equal(["StoreTests", "SecondTests"], GateShardTestCatalog.DeclaredTestClasses(fileScoped));
        Assert.Equal(["LegacyTests"], GateShardTestCatalog.DeclaredTestClasses(block));
        Assert.Empty(GateShardTestCatalog.DeclaredTestClasses(helper));
    }
}
