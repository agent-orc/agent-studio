using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-W57 D4 option A: quarantine with expiry and a card. Covers the expiry
/// transition, the partition the gate applies, and the report projection.
/// </summary>
public sealed class TestQuarantinePolicyTests
{
    private static readonly DateOnly Expiry = new(2026, 10, 15);

    private static TestQuarantineEntry Entry(string test = "P.StoreTests.A", DateOnly? resolved = null)
        => new(test, "AGT-3001", Expiry, resolved);

    [Theory]
    [InlineData("2026-10-01", TestQuarantineStatus.Active)]
    [InlineData("2026-10-15", TestQuarantineStatus.Active)]
    [InlineData("2026-10-16", TestQuarantineStatus.Expired)]
    public void Expiry_transition_arms_the_test_on_the_day_after_the_expiry(string today, TestQuarantineStatus expected)
        => Assert.Equal(expected, TestQuarantinePolicy.Evaluate(Entry(), DateOnly.Parse(today)));

    [Fact]
    public void A_card_resolution_ends_the_quarantine_before_and_after_the_expiry()
    {
        var entry = Entry(resolved: new DateOnly(2026, 10, 5));

        Assert.Equal(TestQuarantineStatus.Active, TestQuarantinePolicy.Evaluate(entry, new DateOnly(2026, 10, 4)));
        Assert.Equal(TestQuarantineStatus.Resolved, TestQuarantinePolicy.Evaluate(entry, new DateOnly(2026, 10, 5)));
        Assert.Equal(TestQuarantineStatus.Resolved, TestQuarantinePolicy.Evaluate(entry, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void Partition_ignores_only_exact_active_names()
    {
        var entries = new[] { Entry("P.StoreTests.A"), Entry("P.StoreTests.Expired") with { ExpiresOn = new DateOnly(2026, 9, 1) } };

        var partition = TestQuarantinePolicy.Partition(
            ["P.StoreTests.A", "P.StoreTests.Expired", "P.StoreTests.AB"], entries, new DateOnly(2026, 10, 1));

        Assert.Equal(["P.StoreTests.A"], partition.Ignored.Select(hit => hit.Test));
        Assert.Equal("AGT-3001", partition.Ignored.Single().Card);
        Assert.Equal(["P.StoreTests.Expired", "P.StoreTests.AB"], partition.Blocking);
        Assert.False(partition.AllQuarantined);
    }

    [Fact]
    public void An_empty_failure_list_is_never_all_quarantined()
        => Assert.False(TestQuarantinePolicy.Partition([], [Entry()], new DateOnly(2026, 10, 1)).AllQuarantined);

    [Fact]
    public void File_entries_without_card_or_valid_expiry_quarantine_nothing_and_are_reported()
    {
        var read = TestQuarantineFile.Parse("""
            { "tests": [
              { "test": "P.A", "card": "AGT-1", "expiresOn": "2026-10-15" },
              { "test": "P.B", "expiresOn": "2026-10-15" },
              { "test": "P.C", "card": "AGT-1", "expiresOn": "soon" },
              { "test": "P.D", "card": "AGT-1", "expiresOn": "2026-10-15", "resolvedOn": "2026-10-02" }
            ] }
            """);

        Assert.Equal(["P.A", "P.D"], read.Entries.Select(entry => entry.Test));
        Assert.Equal(new DateOnly(2026, 10, 2), read.Entries[1].ResolvedOn);
        Assert.Equal(2, read.Issues.Count);
        Assert.Contains(read.Issues, issue => issue.Contains("P.B: card is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_json_quarantines_nothing()
    {
        var read = TestQuarantineFile.Parse("{ not json");

        Assert.Empty(read.Entries);
        Assert.Single(read.Issues);
    }

    [Fact]
    public void Fleet_report_keeps_expired_and_resolved_entries_visible_and_counts_ignored_failures()
    {
        var weekEnd = new DateOnly(2026, 10, 20);
        var rows = TestQuarantineReport.ProjectFleetWeek(
        [
            new TestQuarantineProjectWeek(
                "studio",
                [
                    new TestQuarantineEntry("S.A", "AGT-1", new DateOnly(2026, 10, 31)),
                    new TestQuarantineEntry("S.B", "AGT-2", new DateOnly(2026, 10, 10)),
                    new TestQuarantineEntry("S.C", "AGT-3", new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 18)),
                ],
                [
                    new TestQuarantineHit("S.A", "AGT-1", new DateOnly(2026, 10, 31)),
                    new TestQuarantineHit("S.A", "AGT-1", new DateOnly(2026, 10, 31)),
                    new TestQuarantineHit("S.B", "AGT-2", new DateOnly(2026, 10, 10)),
                ]),
            new TestQuarantineProjectWeek(
                "other",
                [new TestQuarantineEntry("O.A", "OTH-1", new DateOnly(2026, 10, 31))],
                []),
        ], weekEnd);

        Assert.Equal(
            [
                ("studio", "S.A", TestQuarantineStatus.Active, 2),
                ("other", "O.A", TestQuarantineStatus.Active, 0),
                ("studio", "S.B", TestQuarantineStatus.Expired, 1),
                ("studio", "S.C", TestQuarantineStatus.Resolved, 0),
            ],
            rows.Select(row => (row.Project, row.Test, row.Status, row.IgnoredFailures)));
    }

    [Fact]
    public void Run_report_names_the_quarantined_failures_in_the_gate_reason()
    {
        var reason = BuildTestGateRunner.QuarantineReason(
            "verify gate passed",
            [new TestQuarantineHit("S.A", "AGT-1", new DateOnly(2026, 10, 31))]);

        Assert.Equal(
            "verify gate passed; test-quarantine: S.A (AGT-1, until 2026-10-31) failed and did not block",
            reason);
    }
}

public sealed class GuardFirstGatePlanTests
{
    [Fact]
    public void Guard_steps_run_after_builds_and_before_every_test_command()
    {
        var plan = GuardFirstGatePlan.Apply(
        [
            new(VerifyEcosystem.Custom, VerifyCommandKind.Build, "", "dotnet build x.sln"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "dotnet test A.csproj --no-build --filter Category!=MachineBound"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "npm --prefix frontend run test:ci"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "dotnet test B.csproj --no-build"),
        ]);

        Assert.Equal(
            [
                "dotnet build x.sln",
                "dotnet test A.csproj --no-build --filter \"(Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard)\"",
                "dotnet test B.csproj --no-build --filter \"FullyQualifiedName~.Architecture.|Category=Guard\"",
                "dotnet test A.csproj --no-build --filter Category!=MachineBound",
                "npm --prefix frontend run test:ci",
                "dotnet test B.csproj --no-build",
            ],
            plan.Select(command => command.Command));
        Assert.True(GuardFirstGatePlan.IsGuardStep(plan[1]));
        Assert.False(GuardFirstGatePlan.IsGuardStep(plan[3]));
    }

    [Fact]
    public void Non_blocking_or_untargetable_test_commands_get_no_guard_step()
    {
        IReadOnlyList<VerifyCommand> commands =
        [
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "dotnet test A.csproj") { BlocksWorkPackage = false },
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "dotnet test B.csproj && echo done"),
        ];

        Assert.Same(commands, GuardFirstGatePlan.Apply(commands));
    }
}

/// <summary>
/// The card's proof against the real gate loop with a fake <c>dotnet</c>: the
/// quarantined store tests do not block yet stay in the report, and a guard
/// violation ends the gate before the full suite starts.
/// </summary>
public sealed class TestQuarantineGateBehaviorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "test-quarantine-gate-" + Guid.NewGuid().ToString("N"));

    private static readonly string[] StoreFlakes =
    [
        "Product.Tests.TaskStoreTests.ConcurrentWrite",
        "Product.Tests.TaskStoreTests.ReloadAfterRename",
        "Product.Tests.ReviewStoreTests.ParallelAppend",
        "Product.Tests.SettlementStoreTests.RecoverTorn",
        "Product.Tests.MessageBusStoreTests.DrainOrder",
    ];

    private string FakeDotNet => Path.Combine(_root, "dotnet");
    private string Invocations => Path.Combine(_root, "invocations.txt");
    private static string ShellPath(string path) => path.Replace('\\', '/');

    public TestQuarantineGateBehaviorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public async Task Five_quarantined_store_flakes_do_not_block_and_stay_in_the_report()
    {
        WriteQuarantine(StoreFlakes, expiresOn: "2999-12-31");
        WriteFakeDotNet(guardFails: false, suiteFailures: StoreFlakes);

        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(StoreFlakes.Order(StringComparer.Ordinal), result.QuarantinedFailures.Select(hit => hit.Test));
        Assert.All(result.QuarantinedFailures, hit => Assert.Equal("AGT-3001", hit.Card));
        Assert.False(result.RetryPerformed);
        Assert.Contains("test-quarantine:", result.Reason);
        Assert.Contains(StoreFlakes[0], result.Output);
    }

    [Fact]
    public async Task An_expired_quarantine_arms_the_test_again()
    {
        WriteQuarantine([StoreFlakes[0]], expiresOn: "2020-01-01");
        WriteFakeDotNet(guardFails: false, suiteFailures: [StoreFlakes[0]]);

        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Empty(result.QuarantinedFailures);
    }

    [Fact]
    public async Task A_quarantined_flake_next_to_a_real_failure_still_blocks()
    {
        WriteQuarantine([StoreFlakes[0]], expiresOn: "2999-12-31");
        WriteFakeDotNet(guardFails: false, suiteFailures: [StoreFlakes[0], "Product.Tests.RealTests.Broken"]);

        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal([StoreFlakes[0]], result.QuarantinedFailures.Select(hit => hit.Test));
    }

    [Fact]
    public async Task A_guard_violation_returns_the_verdict_without_running_the_full_suite()
    {
        WriteFakeDotNet(guardFails: true, suiteFailures: []);

        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.True(result.GuardViolation);
        Assert.StartsWith("guard violation, full suite not run", result.Reason);
        // The guard step and its one targeted re-run; the full suite never started.
        Assert.All(File.ReadAllLines(Invocations), line => Assert.Contains("Architecture", line));
    }

    private Task<BuildTestGateResult> RunGateAsync()
    {
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            Path.Combine(_root, "cache"));
        return runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false)
            {
                RequiredTestLevel = TestExecutionLevels.Full,
            },
            changedFiles: null,
            new BuildProfile
            {
                TestCmds = [$"{ShellPath(FakeDotNet)} test Product.Tests.csproj --filter Category!=MachineBound"],
            },
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
    }

    private void WriteQuarantine(IEnumerable<string> tests, string expiresOn)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agent-studio"));
        var entries = tests.Select(test =>
            $"{{ \"test\": \"{test}\", \"card\": \"AGT-3001\", \"expiresOn\": \"{expiresOn}\" }}");
        File.WriteAllText(
            Path.Combine(_root, TestQuarantineFile.RelativePath),
            $"{{ \"tests\": [ {string.Join(", ", entries)} ] }}");
    }

    /// <summary>
    /// A fake <c>dotnet</c>: the guard step (its filter names Architecture)
    /// fails with one guard test when asked to, the full suite prints the given
    /// failures in the VSTest console shape on every run.
    /// </summary>
    private void WriteFakeDotNet(bool guardFails, IReadOnlyList<string> suiteFailures)
    {
        var suite = suiteFailures.Count == 0
            ? "exit 0\n"
            : string.Concat(suiteFailures.Select(test => $"echo \"  Failed {test} [12 ms]\"\n")) + "exit 1\n";
        File.WriteAllText(FakeDotNet,
            "#!/bin/sh\n" +
            $"echo \"$@\" >> \"{ShellPath(Invocations)}\"\n" +
            "case \"$*\" in\n" +
            "  *Architecture*)\n" +
            (guardFails
                ? "    echo \"  Failed Product.Tests.Architecture.FeatureFolderBoundaryTests.NoCrossFeatureImports [3 ms]\"\n    exit 1;;\n"
                : "    exit 0;;\n") +
            "esac\n" +
            suite);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                FakeDotNet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
