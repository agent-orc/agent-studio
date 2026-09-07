using System.Xml.Linq;
using Xunit;

namespace AgentStudio.Scenario.Tests;

public sealed class ScenarioReportTests
{
    private static ScenarioRunResult Result(params ScenarioStepResult[] steps)
        => new(
            "deployment-regression",
            "Agent Studio deployment regression scenario",
            ScenarioTargetKind.InProc,
            ScenarioLevel.Full,
            "1.2.3+sha.abc",
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(12.5),
            steps);

    private static ScenarioStepResult Passed(string id)
        => new(
            id, $"Title {id}", "topology-ready", ScenarioStepStatus.Passed,
            ScenarioStepDisposition.Run, TimeSpan.FromSeconds(1.25),
            new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal)
            {
                ["a.fact"] = ScenarioFactValue.Text("x"),
                ["b.reported"] = ScenarioFactValue.Text("Partial"),
            },
            [new ScenarioAssertionOutcome(
                "a.fact", "text:x", "text:x", true, ScenarioAssertionReasons.Satisfied)],
            ["evidence/a.json"],
            null);

    private static ScenarioStepResult Failed(string id)
        => new(
            id, $"Title {id}", "await-claim", ScenarioStepStatus.Failed,
            ScenarioStepDisposition.Run, TimeSpan.FromSeconds(2),
            new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal)
            {
                ["task.state"] = ScenarioFactValue.Text("2-ready"),
            },
            [new ScenarioAssertionOutcome(
                "task.state", "text:3-progress", "text:2-ready", false,
                ScenarioAssertionReasons.ValueMismatch)],
            [],
            "1 of 1 assertions were not satisfied.");

    private static ScenarioStepResult SkippedByTarget(string id)
        => new(
            id, $"Title {id}", "restore-store", ScenarioStepStatus.Skipped,
            ScenarioStepDisposition.SkippedByTarget, TimeSpan.Zero,
            new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal), [], [],
            "declared only for inproc");

    [Fact]
    public void The_junit_report_is_well_formed_xml_with_one_case_per_step()
    {
        var xml = XDocument.Parse(ScenarioJUnitReport.Render(
            Result(Passed("one"), Failed("two"), SkippedByTarget("three"))));

        var suite = xml.Root!.Element("testsuite")!;
        Assert.Equal("3", suite.Attribute("tests")!.Value);
        Assert.Equal("1", suite.Attribute("failures")!.Value);
        Assert.Equal("1", suite.Attribute("skipped")!.Value);
        Assert.Equal(3, suite.Elements("testcase").Count());
    }

    [Fact]
    public void A_failing_case_carries_the_unsatisfied_assertion_so_ci_shows_the_reason()
    {
        var xml = XDocument.Parse(ScenarioJUnitReport.Render(Result(Failed("two"))));

        var failure = xml.Descendants("failure").Single();
        Assert.Contains("task.state", failure.Value);
        Assert.Contains("3-progress", failure.Value);
        Assert.Contains("2-ready", failure.Value);
    }

    [Fact]
    public void A_skipped_case_is_skipped_and_not_a_failure()
    {
        var xml = XDocument.Parse(ScenarioJUnitReport.Render(Result(SkippedByTarget("three"))));

        Assert.Empty(xml.Descendants("failure"));
        Assert.Contains("declared only for inproc", xml.Descendants("skipped").Single()
            .Attribute("message")!.Value);
    }

    [Fact]
    public void The_junit_report_records_the_target_level_and_server_version()
    {
        var xml = XDocument.Parse(ScenarioJUnitReport.Render(Result(Passed("one"))));

        var properties = xml.Descendants("property")
            .ToDictionary(item => item.Attribute("name")!.Value, item => item.Attribute("value")!.Value);
        Assert.Equal("inproc", properties["target"]);
        Assert.Equal("full", properties["level"]);
        Assert.Equal("1.2.3+sha.abc", properties["serverVersion"]);
    }

    [Fact]
    public void Text_that_would_break_xml_is_escaped()
    {
        var result = Result(new ScenarioStepResult(
            "one", "Title", "action", ScenarioStepStatus.Failed, ScenarioStepDisposition.Run,
            TimeSpan.Zero, new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal),
            [], [], "returned <html> & \"quotes\""));

        var xml = XDocument.Parse(ScenarioJUnitReport.Render(result));

        Assert.Contains("<html>", xml.Descendants("failure").Single().Attribute("message")!.Value);
    }

    [Fact]
    public void The_markdown_report_has_one_table_row_per_step_including_skipped_ones()
    {
        var markdown = ScenarioMarkdownReport.Render(
            Result(Passed("one"), Failed("two"), SkippedByTarget("three")));

        Assert.Contains("| 1 | Title one<br>`one` | passed | 1.25 s | [a.json](evidence/a.json) |", markdown);
        Assert.Contains("`two` | failed", markdown);
        Assert.Contains("`three` | skipped (target)", markdown);
    }

    [Fact]
    public void The_markdown_report_states_the_overall_outcome_and_counts()
    {
        var markdown = ScenarioMarkdownReport.Render(Result(Passed("one"), Failed("two")));

        Assert.Contains("- Result: failed (1 passed, 1 failed, 0 skipped)", markdown);
        Assert.Contains("- Target: `inproc`", markdown);
        Assert.Contains("- Level: `full`", markdown);
    }

    [Fact]
    public void A_passing_report_says_so()
    {
        var markdown = ScenarioMarkdownReport.Render(Result(Passed("one")));

        Assert.Contains("- Result: passed (1 passed, 0 failed, 0 skipped)", markdown);
        Assert.DoesNotContain("## Failures", markdown);
    }

    [Fact]
    public void A_failure_section_lists_the_unsatisfied_facts()
    {
        var markdown = ScenarioMarkdownReport.Render(Result(Failed("two")));

        Assert.Contains("## Failures", markdown);
        Assert.Contains("| `task.state` | `text:3-progress` | `text:2-ready` | value-mismatch |", markdown);
    }

    [Fact]
    public void Every_observed_fact_is_listed_so_a_reported_but_unasserted_fact_is_visible()
    {
        var markdown = ScenarioMarkdownReport.Render(Result(Passed("one")));

        Assert.Contains("## Observed facts", markdown);
        Assert.Contains("| `one` | `a.fact` | `text:x` | `text:x` |", markdown);
        // The scenario reports some values without asserting them. They must
        // still reach the report, otherwise tracking their drift is impossible.
        Assert.Contains("| `one` | `b.reported` | - | `text:Partial` |", markdown);
    }

    [Fact]
    public void An_expectation_whose_fact_was_never_published_still_appears()
    {
        var step = new ScenarioStepResult(
            "one", "Title", "action", ScenarioStepStatus.Failed, ScenarioStepDisposition.Run,
            TimeSpan.Zero, new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal),
            [new ScenarioAssertionOutcome(
                "absent.fact", "text:x", "<missing>", false,
                ScenarioAssertionReasons.FactMissing)],
            [], "1 of 1 assertions were not satisfied.");

        var markdown = ScenarioMarkdownReport.Render(Result(step));

        Assert.Contains("| `one` | `absent.fact` | `text:x` | `<missing>` |", markdown);
    }

    [Fact]
    public void A_fact_asserted_both_exactly_and_by_floor_renders_instead_of_throwing()
    {
        var step = new ScenarioStepResult(
            "one", "Title", "action", ScenarioStepStatus.Passed, ScenarioStepDisposition.Run,
            TimeSpan.Zero,
            new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal)
            {
                ["events.total"] = ScenarioFactValue.Number(3),
            },
            [
                new ScenarioAssertionOutcome(
                    "events.total", "number:3", "number:3", true,
                    ScenarioAssertionReasons.Satisfied),
                new ScenarioAssertionOutcome(
                    "events.total", ">= 1", "number:3", true,
                    ScenarioAssertionReasons.Satisfied),
            ],
            [], null);

        var markdown = ScenarioMarkdownReport.Render(Result(step));

        Assert.Contains("| `one` | `events.total` | `number:3 and >= 1` | `number:3` |", markdown);
    }

    [Fact]
    public void Rendering_the_same_result_twice_produces_identical_bytes()
    {
        var result = Result(Passed("one"), Failed("two"), SkippedByTarget("three"));

        Assert.Equal(ScenarioMarkdownReport.Render(result), ScenarioMarkdownReport.Render(result));
        Assert.Equal(ScenarioJUnitReport.Render(result), ScenarioJUnitReport.Render(result));
    }

    [Fact]
    public void A_run_with_no_failing_step_passes_even_when_steps_were_skipped()
    {
        Assert.True(Result(Passed("one"), SkippedByTarget("three")).Passed);
        Assert.False(Result(Passed("one"), Failed("two")).Passed);
    }
}
