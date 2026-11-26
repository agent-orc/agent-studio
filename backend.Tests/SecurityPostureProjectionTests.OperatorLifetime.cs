using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class SecurityPostureProjectionTests
{
    [Fact]
    public void Thirty_day_elevated_session_review_is_quiet_repeats_after_keep_and_ignores_ended_sessions()
    {
        var session = new OperatorAgentSession
        {
            Id = "session", CreatedBy = "owner", SecretHash = "hash", CreatedAt = Now.AddDays(-30),
            ExpiresAt = DateTime.MaxValue, KeyExpiresAt = Now.AddMinutes(45), LastUsedAt = Now.AddMinutes(-10),
        };
        SecurityPostureFacts With(OperatorAgentSession value) => Facts() with
        { OperatorAgents = [new OperatorAgentFact("agent", "ops console", false, [value])] };
        const string id = "credentials:elevated-session:agent:session";
        var finding = Finding(SecurityPostureProjection.Build(With(session)), id);
        Assert.StartsWith("Elevated session running 30 days: ops console", finding.Title);
        Assert.False(finding.ActionSoon);
        Assert.Equal(SecurityPostureSeverities.Info, finding.Severity);
        Assert.DoesNotContain(SecurityPostureProjection.Build(With(session with { ReviewedAt = Now })).Findings, row => row.Id == id);
        Assert.Contains(SecurityPostureProjection.Build(With(session with { ReviewedAt = Now.AddDays(-30) })).Findings, row => row.Id == id);
        Assert.DoesNotContain(SecurityPostureProjection.Build(With(session with { RevokedAt = Now })).Findings, row => row.Id == id);
    }
}
