using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Coverage for the named diagnosis a repeating infrastructure cause must
/// produce. AGT-2220 spent four attempts on <c>BaselineUnavailable</c> and left
/// only the classification behind, so nobody could see which base or command
/// kept failing.
/// </summary>
public sealed class ReviewInfrastructureRepeatPolicyTests
{
    private const string BaselineFailure = "BaselineUnavailable";

    [Fact]
    public void A_single_infrastructure_failure_is_not_yet_a_diagnosis()
    {
        var diagnosis = ReviewInfrastructureRepeatPolicy.Diagnose(
            [Attempt("rva_1", BaselineFailure, Reason("b649ff8da", "refs/heads/main"))],
            "refs/heads/main");

        Assert.Null(diagnosis);
    }

    [Fact]
    public void A_repeated_cause_names_the_base_the_ref_and_the_command()
    {
        var diagnosis = ReviewInfrastructureRepeatPolicy.Diagnose(
            [
                Attempt("rva_1", BaselineFailure, Reason("b649ff8da", "refs/heads/main")),
                Attempt("rva_2", BaselineFailure, Reason("b649ff8da", "refs/heads/main")),
            ],
            "refs/heads/main");

        Assert.NotNull(diagnosis);
        Assert.Equal(BaselineFailure, diagnosis.Classification);
        Assert.Equal(2, diagnosis.RepeatCount);
        Assert.Equal(["rva_1", "rva_2"], diagnosis.AttemptIds);
        Assert.Equal("b649ff8da", diagnosis.BaselineSha);
        Assert.Equal("refs/heads/main", diagnosis.IntegrationRef);
        Assert.Equal("verify-2", diagnosis.Step);
        Assert.Equal("sh -lc dotnet test; exit 1", diagnosis.Command);
        Assert.Contains("b649ff8da", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("verify-2", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_trailing_run_of_one_identical_cause_counts()
    {
        var diagnosis = ReviewInfrastructureRepeatPolicy.Diagnose(
            [
                Attempt("rva_1", BaselineFailure, Reason("b649ff8da", "refs/heads/main")),
                Attempt("rva_2", "ToolUnavailable", "git is missing."),
                Attempt("rva_3", BaselineFailure, Reason("b649ff8da", "refs/heads/main")),
            ],
            "refs/heads/main");

        Assert.Null(diagnosis);
    }

    [Fact]
    public void An_attempt_written_before_the_diagnosis_convention_still_counts_as_a_repeat()
    {
        var diagnosis = ReviewInfrastructureRepeatPolicy.Diagnose(
            [
                Attempt("rva_1", BaselineFailure, "Baseline command verify-2 did not complete normally."),
                Attempt("rva_2", BaselineFailure, Reason("b649ff8da", "refs/heads/main")),
            ],
            "refs/heads/main");

        Assert.NotNull(diagnosis);
        Assert.Equal(2, diagnosis.RepeatCount);
        Assert.Equal("b649ff8da", diagnosis.BaselineSha);
    }

    [Fact]
    public void The_planned_ref_stands_in_when_no_attempt_reported_one()
    {
        var diagnosis = ReviewInfrastructureRepeatPolicy.Diagnose(
            [
                Attempt("rva_1", BaselineFailure, "Baseline command verify-2 did not complete normally."),
                Attempt("rva_2", BaselineFailure, "Baseline command verify-2 did not complete normally."),
            ],
            "refs/heads/main");

        Assert.NotNull(diagnosis);
        Assert.Equal("refs/heads/main", diagnosis.IntegrationRef);
        Assert.Null(diagnosis.BaselineSha);
        Assert.Contains("refs/heads/main", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_chain_produces_nothing()
        => Assert.Null(ReviewInfrastructureRepeatPolicy.Diagnose([], "refs/heads/main"));

    [Fact]
    public void Diagnosis_facts_survive_a_command_containing_the_field_separators()
    {
        const string command = "sh -lc cd -- frontend && npm test; echo [done]";
        var reason = ReviewInfrastructureDiagnosis.Append(
            "Baseline command 'verify-2' did not complete normally.",
            [
                new(ReviewInfrastructureDiagnosis.BaseKey, "b649ff8da"),
                new(ReviewInfrastructureDiagnosis.CommandKey, command),
            ]);

        var facts = ReviewInfrastructureDiagnosis.Parse(reason);

        Assert.StartsWith(
            "Baseline command 'verify-2' did not complete normally.",
            reason,
            StringComparison.Ordinal);
        Assert.Equal("b649ff8da", facts[ReviewInfrastructureDiagnosis.BaseKey]);
        Assert.Equal(command, facts[ReviewInfrastructureDiagnosis.CommandKey]);
    }

    [Fact]
    public void A_reason_without_facts_parses_to_an_empty_map()
        => Assert.Empty(ReviewInfrastructureDiagnosis.Parse("Baseline command did not complete."));

    private static ReviewInfrastructureAttemptFact Attempt(
        string attemptId,
        string classification,
        string reason)
        => new(attemptId, classification, reason);

    private static string Reason(string baselineSha, string integrationRef)
        => ReviewInfrastructureDiagnosis.Append(
            "Baseline command 'verify-2' did not complete normally.",
            [
                new(ReviewInfrastructureDiagnosis.BaseKey, baselineSha),
                new(ReviewInfrastructureDiagnosis.RefKey, integrationRef),
                new(ReviewInfrastructureDiagnosis.StepKey, "verify-2"),
                new(ReviewInfrastructureDiagnosis.CommandKey, "sh -lc dotnet test; exit 1"),
            ]);
}
