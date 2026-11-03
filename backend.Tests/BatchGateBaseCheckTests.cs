using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>AGT-3433: what a red batch candidate repeats on its base, and when a verdict carries.</summary>
public sealed class BatchGateBaseCheckPolicyTests
{
    private const string TestCommand = "dotnet test Product.Tests.csproj --no-build --filter Category!=MachineBound";

    [Fact]
    public void Guard_violation_repeats_the_guard_step()
    {
        var check = BatchGateBaseCheckPolicy.Narrow(Red(VerifyCommandKind.Test) with { GuardViolation = true });

        Assert.Equal(GateNarrowCheckKinds.GuardStep, check?.Kind);
    }

    [Fact]
    public void Build_failure_repeats_the_build()
        => Assert.Equal(GateNarrowCheckKinds.Build,
            BatchGateBaseCheckPolicy.Narrow(Red(VerifyCommandKind.Build))?.Kind);

    [Fact]
    public void Red_test_command_repeats_its_failed_tests_and_not_the_targeted_rerun()
    {
        var candidate = Red(VerifyCommandKind.Test,
            Process(TestCommand, "  Failed P.Tests.OrderTests.Totals [12 ms]\n  Failed P.Tests.OrderTests.Tax [3 ms]\n"),
            Process("dotnet test Product.Tests.csproj --no-build --filter \"FullyQualifiedName=P.Tests.OrderTests.Tax\"",
                "  Failed P.Tests.OrderTests.Tax [3 ms]\n", phase: "flaky-rerun"));

        var check = BatchGateBaseCheckPolicy.Narrow(candidate)!;

        Assert.Equal(GateNarrowCheckKinds.FailedTests, check.Kind);
        Assert.Equal(["P.Tests.OrderTests.Tax", "P.Tests.OrderTests.Totals"], check.Tests);
        Assert.Equal(TestCommand, check.Command);
    }

    public static TheoryData<string, BuildTestGateResult> FullGateFallbacks => new()
    {
        { "no failed step kind", Red(null) },
        { "lint failure", Red(VerifyCommandKind.Lint) },
        { "no test names", Red(VerifyCommandKind.Test, Process(TestCommand, "exit 1")) },
        { "unfilterable theory case", Red(VerifyCommandKind.Test, Process(TestCommand, "  Failed P.Tests.T.Case(x: 1) [1 ms]")) },
        { "chained test command", Red(VerifyCommandKind.Test, Process("dotnet test A.csproj && echo", "  Failed P.Tests.T.A [1 ms]")) },
        { "too many failures", Red(VerifyCommandKind.Test, Process(TestCommand, string.Concat(
            Enumerable.Range(0, BatchGateBaseCheckPolicy.MaxNarrowTests + 1).Select(i => $"  Failed P.Tests.T.Case{i} [1 ms]\n")))) },
        { "infrastructure failure", Red(VerifyCommandKind.Test) with { GuardViolation = true, FailureKind = BuildTestGateFailureKind.Environment } },
        { "green candidate", Red(VerifyCommandKind.Test) with { GuardViolation = true, Verdict = BuildTestGateVerdict.Ok } },
    };

    [Theory]
    [MemberData(nameof(FullGateFallbacks))]
    public void Failure_that_names_nothing_repeatable_falls_back_to_the_full_gate(string _, BuildTestGateResult candidate)
        => Assert.Null(BatchGateBaseCheckPolicy.Narrow(candidate));

    [Theory]
    [InlineData(true, new[] { "docs/review-gate-packages/workbench.json" }, true)]
    [InlineData(true, new[] { "docs/a.md", "README.md" }, true)]
    [InlineData(false, new[] { "docs/a.md" }, false)]
    [InlineData(true, new[] { "docs/a.md", "backend/Program.cs" }, false)]
    [InlineData(true, new string[0], false)]
    [InlineData(true, null, false)]
    public void Verdict_carries_only_to_a_descendant_with_no_gated_change(
        bool ancestor, string[]? changed, bool expected)
        => Assert.Equal(expected, BatchGateBaseCheckPolicy.CarriesVerdict(ancestor, changed));

    internal static BuildTestGateResult Red(VerifyCommandKind? step, params BuildTestGateProcessEvidence[] processes)
        => new(BuildTestGateVerdict.Fail, 1, 1, "failed", "red", false, false)
        {
            FailureKind = BuildTestGateFailureKind.Code,
            FailedStepKind = step,
            Processes = processes,
        };

    private static BuildTestGateProcessEvidence Process(string command, string output, string phase = "verification")
        => new() { Command = command, ExitCode = 1, StandardOutput = output, Phase = phase };
}

public sealed class GateNarrowCheckPlanTests
{
    private static readonly IReadOnlyList<VerifyCommand> Plan =
    [
        new(VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build App.sln"),
        new(VerifyEcosystem.Node, VerifyCommandKind.Lint, "frontend", "npm run lint"),
        new(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test A.Tests.csproj --no-build --filter Category!=MachineBound"),
        new(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test B.Tests.csproj --no-build"),
        new(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm run test:ci"),
    ];

    [Fact]
    public void Guard_check_keeps_the_build_and_one_guard_step_per_test_project()
    {
        var commands = GateNarrowCheckPlan.Apply(Plan, new GateNarrowCheck(GateNarrowCheckKinds.GuardStep, []))!;

        Assert.Equal(
        [
            "dotnet build App.sln",
            "dotnet test A.Tests.csproj --no-build --filter \"(Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard)\"",
            "dotnet test B.Tests.csproj --no-build --filter \"FullyQualifiedName~.Architecture.|Category=Guard\"",
        ], commands.Select(command => command.Command));
        Assert.All(commands.Skip(1), command => Assert.True(GuardFirstGatePlan.IsGuardStep(command)));
    }

    [Fact]
    public void Failed_test_check_retargets_only_the_red_command()
    {
        var commands = GateNarrowCheckPlan.Apply(Plan, new GateNarrowCheck(GateNarrowCheckKinds.FailedTests,
            ["A.Tests.OrderTests.Totals"], "dotnet test A.Tests.csproj --no-build --filter Category!=MachineBound"))!;

        Assert.Equal(
        [
            "dotnet build App.sln",
            "dotnet test A.Tests.csproj --no-build --filter \"FullyQualifiedName=A.Tests.OrderTests.Totals\"",
        ], commands.Select(command => command.Command));
    }

    [Fact]
    public void Build_check_keeps_only_the_build()
        => Assert.Equal(["dotnet build App.sln"], GateNarrowCheckPlan.Apply(Plan,
            new GateNarrowCheck(GateNarrowCheckKinds.Build, []))!.Select(command => command.Command));

    [Theory]
    [InlineData(GateNarrowCheckKinds.FailedTests, "dotnet test C.Tests.csproj")]
    [InlineData(GateNarrowCheckKinds.GuardStep, null)]
    [InlineData(GateNarrowCheckKinds.Build, null)]
    public void Check_with_nothing_to_repeat_is_no_plan(string kind, string? command)
    {
        IReadOnlyList<VerifyCommand> plan = kind == GateNarrowCheckKinds.FailedTests
            ? Plan
            : [new(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm run test:ci")];

        Assert.Null(GateNarrowCheckPlan.Apply(plan, new GateNarrowCheck(kind, ["A.Tests.T.A"], command)));
    }
}

/// <summary>
/// The measured proof against the real gate loop with a fake <c>dotnet</c>
/// whose full suite takes two seconds: a narrow base check never starts it.
/// </summary>
public sealed class GateNarrowCheckRunnerTests(ITestOutputHelper output) : IDisposable
{
    private const string FullSuiteFilter = "Category!=MachineBound";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gate-narrow-check-" + Guid.NewGuid().ToString("N"));

    private string FakeDotNet => Path.Combine(_root, "dotnet");
    private string Invocations => Path.Combine(_root, "invocations.txt");
    private string TestCommand => $"{FakeDotNet.Replace('\\', '/')} test Product.Tests.csproj --no-build --filter {FullSuiteFilter}";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public async Task Guard_check_runs_the_build_and_the_guard_step_but_never_the_full_suite()
    {
        WriteFakeDotNet();
        var full = await RunGateAsync(null);
        var fullInvocations = ReadInvocations();
        File.Delete(Invocations);

        var guard = await RunGateAsync(new GateNarrowCheck(GateNarrowCheckKinds.GuardStep, []));

        var lines = ReadInvocations();
        output.WriteLine($"full gate: {full.DurationMs} ms, {fullInvocations.Length} invocation(s)");
        output.WriteLine($"guard-step base check: {guard.DurationMs} ms, {lines.Length} invocation(s): {string.Join(" | ", lines)}");
        Assert.Contains(fullInvocations, IsFullSuite);
        Assert.Equal(BuildTestGateVerdict.Ok, guard.Verdict);
        // The gate appends its own logger arguments to every test step.
        Assert.Equal(2, lines.Length);
        Assert.Equal("build Product.sln", lines[0]);
        Assert.StartsWith("test Product.Tests.csproj --no-build --filter (Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard)", lines[1]);
        Assert.DoesNotContain(lines, IsFullSuite);
        Assert.True(guard.DurationMs < full.DurationMs, $"guard {guard.DurationMs} ms, full {full.DurationMs} ms");
    }

    [Fact]
    public async Task Failed_test_check_runs_only_the_named_tests_and_reports_the_red_base()
    {
        WriteFakeDotNet();

        var result = await RunGateAsync(new GateNarrowCheck(GateNarrowCheckKinds.FailedTests,
            ["Product.Tests.OrderTests.Totals"], TestCommand));

        var lines = ReadInvocations();
        output.WriteLine($"failed-tests base check: {result.DurationMs} ms: {string.Join(" | ", lines)}");
        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.Equal("build Product.sln", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.Contains("FullyQualifiedName=Product.Tests.OrderTests.Totals", line));
        Assert.DoesNotContain(lines, IsFullSuite);
    }

    [Fact]
    public async Task Build_check_runs_only_the_build()
    {
        WriteFakeDotNet();

        var result = await RunGateAsync(new GateNarrowCheck(GateNarrowCheckKinds.Build, []));

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(["build Product.sln"], ReadInvocations());
    }

    [Fact]
    public async Task Check_that_matches_no_command_runs_nothing_and_answers_nothing()
    {
        WriteFakeDotNet();

        var result = await RunGateAsync(new GateNarrowCheck(GateNarrowCheckKinds.FailedTests,
            ["Product.Tests.OrderTests.Totals"], "dotnet test Other.Tests.csproj"));

        Assert.Equal(BuildTestGateVerdict.Skipped, result.Verdict);
        Assert.Empty(ReadInvocations());
    }

    private static bool IsFullSuite(string line)
        => (line + " ").Contains($"--filter {FullSuiteFilter} ", StringComparison.Ordinal);

    private string[] ReadInvocations() => File.Exists(Invocations) ? File.ReadAllLines(Invocations) : [];

    private Task<BuildTestGateResult> RunGateAsync(GateNarrowCheck? narrow)
    {
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            Path.Combine(_root, "cache"));
        return runner.RunAsync(
            new BuildTestGateRequest(_root, null, "batch-base", RequireExactSubject: false)
            {
                RequiredTestLevel = TestExecutionLevels.Full,
                NarrowCheck = narrow,
            },
            changedFiles: null,
            new BuildProfile
            {
                BuildCmds = [$"{FakeDotNet.Replace('\\', '/')} build Product.sln"],
                TestCmds = [TestCommand],
            },
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
    }

    /// <summary>
    /// The build and the guard step pass at once; a targeted test filter
    /// fails one test; the full suite takes two seconds and passes.
    /// </summary>
    private void WriteFakeDotNet()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FakeDotNet,
            "#!/bin/sh\n" +
            $"echo \"$@\" >> \"{Invocations.Replace('\\', '/')}\"\n" +
            "case \"$*\" in\n" +
            "  build*) exit 0;;\n" +
            "  *Architecture*) exit 0;;\n" +
            "  *FullyQualifiedName=*)\n" +
            "    echo \"  Failed Product.Tests.OrderTests.Totals [4 ms]\"\n    exit 1;;\n" +
            "esac\n" +
            "sleep 2\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FakeDotNet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
