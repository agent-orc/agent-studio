using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewPlanResourcePolicyTests
{
    [Fact]
    public void Shell_dotnet_tests_receive_cpu_and_collection_parallelism_limits()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "sh",
                ["-lc", "cd -- backend && dotnet test --filter Category!=MachineBound"],
                CompareToBaseline: true)],
            ["build-tests"]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            "cd -- backend && dotnet test -maxcpucount:2 -nodeReuse:false -p:ParallelizeTestCollections=false " +
            "--logger \"console;verbosity=normal\" --filter Category!=MachineBound",
            Assert.Single(limited.Commands).Arguments[1]);
    }

    [Fact]
    public void Direct_dotnet_tests_are_limited_without_changing_non_test_commands()
    {
        var test = new ReviewCommandDto(
            "verify-test", "build-tests", "dotnet", ["test", "runner.Tests"]);
        var build = new ReviewCommandDto(
            "verify-build", "build-tests", "dotnet", ["build", "runner"]);
        var plan = new ReviewPlanDto([test, build], ["build-tests"]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            [
                "test", "-maxcpucount:2", "-nodeReuse:false", "-p:ParallelizeTestCollections=false",
                "--logger", "console;verbosity=normal", "runner.Tests",
            ],
            limited.Commands[0].Arguments);
        // AGT-2820: the build is capped like the test step, but collection
        // parallelism is a test-only knob and must not leak onto it.
        Assert.Equal(
            ["build", "-maxcpucount:2", "-nodeReuse:false", "runner"],
            limited.Commands[1].Arguments);
    }

    [Fact]
    public void Non_dotnet_commands_are_left_alone()
    {
        var status = new ReviewCommandDto("verify-git", "build-tests", "git", ["status"]);
        var lint = new ReviewCommandDto("verify-lint", "build-tests", "sh", ["-lc", "npm run lint"]);
        var plan = new ReviewPlanDto([status, lint], ["build-tests"]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(status, limited.Commands[0]);
        Assert.Equal(lint, limited.Commands[1]);
    }

    /// <summary>
    /// AGT-2820: four parallel reviews each ran
    /// <c>sh -lc dotnet build agent-taskboard.sln --no-restore</c> with no CPU
    /// cap, driving agent-runner-01 to load 38 on 12 cores until a run was
    /// killed with exit 143 - while the test step right after it was careful to
    /// pass <c>-maxcpucount:2</c>.
    /// </summary>
    [Fact]
    public void Shell_dotnet_builds_are_capped_and_start_without_node_reuse()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-1",
                "build-tests",
                "sh",
                ["-lc", "dotnet build agent-taskboard.sln --no-restore"])],
            ["build-tests"]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            "dotnet build -maxcpucount:2 -nodeReuse:false agent-taskboard.sln --no-restore",
            Assert.Single(limited.Commands).Arguments[1]);
    }

    [Fact]
    public void An_existing_node_reuse_switch_is_replaced_rather_than_duplicated()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-1",
                "build-tests",
                "sh",
                ["-lc", "dotnet build -nodeReuse:true -maxcpucount:12 app.sln"])],
            ["build-tests"]);

        var first = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);
        var second = ReviewPlanResourcePolicy.Apply(first, dotNetMaxCpuCount: 2);

        Assert.Equal(first, second);
        Assert.Equal(
            "dotnet build -maxcpucount:2 -nodeReuse:false app.sln",
            Assert.Single(first.Commands).Arguments[1]);
    }

    [Fact]
    public void Existing_unbounded_values_are_replaced_idempotently()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "bash",
                ["-lc", "dotnet test -maxcpucount:12 -p:ParallelizeTestCollections=true"],
                CompareToBaseline: true)],
            ["build-tests"]);

        var first = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);
        var second = ReviewPlanResourcePolicy.Apply(first, dotNetMaxCpuCount: 2);

        Assert.Equal(first, second);
        Assert.Equal(
            "dotnet test -maxcpucount:2 -nodeReuse:false -p:ParallelizeTestCollections=false " +
            "--logger \"console;verbosity=normal\"",
            Assert.Single(first.Commands).Arguments[1]);
    }

    /// <summary>
    /// AGT-2851: a review host's default console logger prints nothing between
    /// test classes, which starved the silence watchdog of anything to reset its
    /// clock against. Reapplying the policy must not duplicate the logger flag.
    /// </summary>
    [Fact]
    public void An_existing_logger_flag_is_replaced_rather_than_duplicated()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "sh",
                ["-lc", "dotnet test --logger trx"],
                CompareToBaseline: true)],
            ["build-tests"]);

        var first = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);
        var second = ReviewPlanResourcePolicy.Apply(first, dotNetMaxCpuCount: 2);

        Assert.Equal(first, second);
        Assert.Equal(
            "dotnet test -maxcpucount:2 -nodeReuse:false -p:ParallelizeTestCollections=false " +
            "--logger \"console;verbosity=normal\"",
            Assert.Single(first.Commands).Arguments[1]);
    }

    [Fact]
    public void Shell_normalization_preserves_quoted_whitespace()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "sh",
                ["-lc", "dotnet test -maxcpucount:12 --filter \"Name~two  spaces\""],
                CompareToBaseline: true)],
            ["build-tests"]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            "dotnet test -maxcpucount:2 -nodeReuse:false -p:ParallelizeTestCollections=false " +
            "--logger \"console;verbosity=normal\" --filter \"Name~two  spaces\"",
            Assert.Single(limited.Commands).Arguments[1]);
    }

    [Fact]
    public void Preparation_and_verification_share_the_same_dotnet_resource_policy()
    {
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto("verify", "build-tests", "git", ["status"])],
            ["build-tests"],
            Preparation:
            [
                new ReviewPreparationCommandDto(
                    "prepare",
                    "bash",
                    ["-lc", "dotnet test --filter Category!=MachineBound && npm ci"]),
            ]);

        var limited = ReviewPlanResourcePolicy.Apply(plan, dotNetMaxCpuCount: 2);

        Assert.Equal(
            "dotnet test -maxcpucount:2 -nodeReuse:false -p:ParallelizeTestCollections=false " +
            "--logger \"console;verbosity=normal\" --filter Category!=MachineBound && npm ci",
            Assert.Single(limited.Preparation!).Arguments[1]);
    }
}
