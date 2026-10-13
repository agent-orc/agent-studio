using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the risk model and planner receive explicit timestamps as pure inputs.
public sealed class BatchGateRiskModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    // Measured delivery shapes of 2026-10-12, from the merge base with origin/develop.
    public static TheoryData<string, int, int, int, bool, double, double> Shapes => new()
    {
        { "documentation-only", 3, 50, 0, false, 0, .12 },
        { "AGT-3048", 23, 367, 0, false, 0, .2999 },
        { "AGT-3069", 20, 505, 1, false, 0, .3499 },
        { "AGT-3033", 23, 411, 1, false, 0, .3499 },
        { "AGT-3045", 29, 513, 4, false, .35, .5499 },
        { "AGT-3070", 28, 900, 9, false, .35, .5499 },
        { "AGT-3032", 82, 5408, 2, false, 0, .5499 },
        { "AGT-3032 prior red", 82, 5408, 2, true, .55, .95 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Recalibrated_score_places_measured_shapes_in_their_band(string shape,
        int paths, int lines, int infrastructure, bool priorRed, double minimum, double maximum)
    {
        var risk = BatchGateRiskModel.Score(Paths(paths, infrastructure,
                shape == "documentation-only"), lines, priorRed, false, false);

        Assert.True(risk >= minimum && risk <= maximum,
            $"{shape}: risk {risk:0.000} is outside [{minimum}, {maximum}].");
    }

    [Fact]
    public void Score_stays_bounded_with_every_input_at_its_maximum()
    {
        Assert.InRange(BatchGateRiskModel.Score([], 0, false, false, false), 0, .95);
        Assert.InRange(BatchGateRiskModel.Score(Paths(500, 500), 200_000, true, true, true), 0, .95);
        Assert.InRange(BatchGateRiskModel.Score([], -10, false, false, false), 0, .95);
        Assert.InRange(BatchGateRiskModel.Unknown(true, true, true), 0, .95);
    }

    [Fact]
    public void Score_never_drops_and_keeps_rising_through_eighty_paths_and_five_thousand_lines()
    {
        var previous = BatchGateRiskModel.Score([], 0, false, false, false);
        for (var paths = 1; paths <= 80; paths++)
        {
            var risk = BatchGateRiskModel.Score(Paths(paths, 0), 0, false, false, false);
            Assert.True(risk > previous, $"{paths} paths did not raise risk.");
            previous = risk;
        }
        previous = BatchGateRiskModel.Score(Paths(10, 0), 0, false, false, false);
        for (var lines = 50; lines <= 5000; lines += 50)
        {
            var risk = BatchGateRiskModel.Score(Paths(10, 0), lines, false, false, false);
            Assert.True(risk > previous, $"{lines} lines did not raise risk.");
            previous = risk;
        }
        previous = BatchGateRiskModel.Score(Paths(20, 0), 400, false, false, false);
        for (var infrastructure = 1; infrastructure <= 20; infrastructure++)
        {
            var risk = BatchGateRiskModel.Score(Paths(20, infrastructure), 400, false, false, false);
            Assert.True(risk >= previous, $"{infrastructure} infrastructure paths lowered risk.");
            previous = risk;
        }
        Assert.True(BatchGateRiskModel.Score(Paths(20, 5), 400, false, false, false)
            > BatchGateRiskModel.Score(Paths(20, 1), 400, false, false, false));
    }

    [Fact]
    public void Unknown_diff_goes_per_task_under_balanced_and_conservative()
    {
        foreach (var policy in new[] { "balanced", "conservative" })
        {
            var plan = BatchGateLookAheadPlanner.Plan(Input(policy,
                Card("UNKNOWN", BatchGateRiskModel.Unknown(false, false, false), [], minutesAgo: 10),
                Card("SMALL", BatchGateRiskModel.Score(["docs/a.md"], 10, false, false, false),
                    ["docs/a.md"])));

            Assert.Equal("per-task", plan.Decision);
            Assert.Equal(["UNKNOWN"], plan.ProposedMembers);
        }
    }

    [Fact]
    public void Balanced_separate_planner_batches_disjoint_recalibrated_cards()
    {
        var plan = BatchGateLookAheadPlanner.Plan(Input("balanced",
            Card("AGT-3048", BatchGateRiskModel.Score(Paths(23, 0, prefix: "a"), 367,
                false, false, false), Paths(23, 0, prefix: "a")),
            Card("AGT-3045", BatchGateRiskModel.Score(Paths(29, 4, prefix: "b"), 513,
                false, false, false), Paths(29, 4, prefix: "b"))));

        Assert.Contains(plan.Decision, new[] { "close", "wait" });
        Assert.Equal(["AGT-3045", "AGT-3048"], plan.ProposedMembers.Order());
        var closed = BatchGateLookAheadPlanner.Plan(Input("balanced",
            Card("AGT-3048", BatchGateRiskModel.Score(Paths(23, 0, prefix: "a"), 367,
                false, false, false), Paths(23, 0, prefix: "a")),
            Card("AGT-3045", BatchGateRiskModel.Score(Paths(29, 4, prefix: "b"), 513,
                false, false, false), Paths(29, 4, prefix: "b"))) with { CloseNow = true });
        Assert.Equal("close", closed.Decision);
        Assert.Equal(["AGT-3045", "AGT-3048"], closed.ProposedMembers.Order());
    }

    [Fact]
    public void Balanced_separate_planner_never_batches_cards_sharing_a_path()
    {
        var first = Paths(23, 0, prefix: "a");
        var second = Paths(28, 4, prefix: "b").Append(first[0]).ToArray();
        var plan = BatchGateLookAheadPlanner.Plan(Input("balanced",
            Card("AGT-3048", BatchGateRiskModel.Score(first, 367, false, false, false), first),
            Card("AGT-3045", BatchGateRiskModel.Score(second, 513, false, false, false), second))
            with { CloseNow = true });

        Assert.False(plan.ProposedMembers.Contains("AGT-3048")
            && plan.ProposedMembers.Contains("AGT-3045"),
            $"{plan.Decision}: {string.Join(", ", plan.ProposedMembers)}");
    }

    private static string[] Paths(int count, int infrastructure, bool documentation = false,
        string prefix = "")
        => Enumerable.Range(0, count).Select(index => documentation ? $"docs/{prefix}{index}.md"
            : index < infrastructure ? $"backend/Features/Pipeline/{prefix}{index}.cs"
            : $"frontend/src/{prefix}{index}.ts").ToArray();

    private static BatchPlanCard Card(string key, double risk, IReadOnlyList<string> paths,
        int minutesAgo = 5)
        => new(key, Now.AddMinutes(-minutesAgo), risk, paths, []);

    private static BatchPlanInput Input(string policy, params BatchPlanCard[] waiting)
        => new(Now, waiting, [], 2, 4, 20, 45, ["urgent"], policy, "separate");
}
