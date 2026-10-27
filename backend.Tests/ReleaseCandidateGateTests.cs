using AgentStudio.Deploy;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReleaseCandidateGateTests
{
    private static readonly DateTimeOffset Finished = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReleaseSuiteVerdict Green = new("Project", new('a', 40), new('b', 64), "green", Finished);

    [Theory]
    [InlineData("required", true, "green", true)]
    [InlineData("required", true, "red", false)]
    [InlineData("required", true, "missing", false)]
    [InlineData("required", false, "green", false)]
    [InlineData("optional", true, "missing", true)]
    [InlineData("optional", true, "red", false)]
    [InlineData("optional", false, "missing", false)]
    [InlineData("off", false, "missing", true)]
    public void Release_admission_follows_setting_post_merge_and_exact_verdict(
        string mode, bool postMergeGreen, string verdictStatus, bool expected)
    {
        var verdict = verdictStatus == "missing" ? null : Green with { Status = verdictStatus };
        Assert.Equal(expected, ReleaseCandidateGatePolicy.Allows(mode, postMergeGreen, verdict));
    }

    [Fact]
    public void Delivery_setting_parses_and_rejects_unknown_values()
    {
        var head = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), ".agent-studio", "project.yml"))
            + "\ndelivery:\n  fullSuite: ";
        var valid = ProjectDefinitionReader.Parse(head + "required\n");
        Assert.Equal("required", valid.Definition?.DeliveryFullSuite);
        var invalid = ProjectDefinitionReader.Parse(head + "sometimes\n");
        Assert.Contains(invalid.Issues, issue => issue.Code == "delivery-full-suite-invalid");
        Assert.Equal("required", ReleaseCandidateGatePolicy.Mode(null, productionDeclared: true));
        Assert.Equal("optional", ReleaseCandidateGatePolicy.Mode(null, productionDeclared: false));
        Assert.Equal("off", ReleaseCandidateGatePolicy.Mode("off", productionDeclared: true));
    }
}
