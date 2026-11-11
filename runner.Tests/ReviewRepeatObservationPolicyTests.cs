using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3650: the fixtures are real Vitest 4 output of frontend `test:ci` with
/// a two-file include, once per reporter. The first run failed one test.
/// </summary>
public sealed class ReviewRepeatObservationPolicyTests
{
    private const string Failure =
        "|frontend| src/app/agt3650-flaky-fixture.spec.ts > ReviewFlakyFixture > nested group > fails only on the first run (AGT-3650)";

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "review-repeats", name));

    [Fact]
    public void Default_reporter_failure_parses_to_a_test_file_name()
    {
        var failures = RemoteReviewWorkspace.ParsedTestFailures(new ProcessResult(1,
            Fixture("vitest-default-failed.stdout.txt"), Fixture("vitest-default-failed.stderr.txt")));

        Assert.Equal([Failure], failures);
        Assert.True(ReviewRepeatObservationPolicy.HasTestFileNames(failures));
        Assert.Equal("|frontend| src/app/agt3650-flaky-fixture.spec.ts", ReviewRepeatObservationPolicy.TestFile(Failure));
    }

    [Fact]
    public void Verbose_reporter_names_the_failed_test_passing()
    {
        var proof = ReviewRepeatObservationPolicy.Observe(Fixture("vitest-verbose-passed.stdout.txt"), [Failure]);

        Assert.Equal(ReviewRepeatObservation.TestNames, proof?.Level);
        Assert.Equal([Failure], proof!.Tests);
    }

    [Fact]
    public void Default_reporter_proves_only_that_the_failed_test_file_passed()
    {
        var proof = ReviewRepeatObservationPolicy.Observe(Fixture("vitest-default-passed.stdout.txt"), [Failure]);

        Assert.Equal(ReviewRepeatObservation.TestFiles, proof?.Level);
        Assert.Equal([Failure], proof!.Tests);
    }

    [Theory]
    [InlineData("vitest-default-other-file.stdout.txt")]
    [InlineData("vitest-verbose-failed.stdout.txt")]
    [InlineData("vitest-default-failed.stdout.txt")]
    public void Output_without_the_failed_test_or_its_passing_file_proves_nothing(string fixture)
        => Assert.Null(ReviewRepeatObservationPolicy.Observe(Fixture(fixture), [Failure]));

    [Fact]
    public void Every_failed_test_needs_proof()
    {
        const string other = "|frontend| src/app/missing.spec.ts > Missing > never ran";

        Assert.Null(ReviewRepeatObservationPolicy.Observe(Fixture("vitest-verbose-passed.stdout.txt"), [Failure, other]));
        Assert.Null(ReviewRepeatObservationPolicy.Observe(Fixture("vitest-default-passed.stdout.txt"), [Failure, other]));
    }

    [Fact]
    public void Jest_pass_line_is_file_level_proof()
    {
        var proof = ReviewRepeatObservationPolicy.Observe(
            "PASS src/lease.test.js (1.2 s)\nTests: 3 passed, 3 total",
            ["src/lease.test.js > lease > renews before expiry"]);

        Assert.Equal(ReviewRepeatObservation.TestFiles, proof?.Level);
    }

    [Theory]
    [InlineData("AgentStudio.Tests.FooTests.Fails")]
    [InlineData("npm script test:ci")]
    [InlineData("<unparsed failure in verify-6>")]
    public void Other_failure_shapes_are_not_test_file_names(string failure)
        => Assert.False(ReviewRepeatObservationPolicy.HasTestFileNames([failure]));
}
