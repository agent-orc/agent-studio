using AgentStudio.Cli;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ScopedQuotaProjectionTests
{
    [Fact]
    public void Scoped_report_omits_raw_samples_without_mutating_the_cache()
    {
        var cached = new QuotaReport
        {
            Snapshots = [new QuotaSnapshot { CliType = "codex", RawSample = "account@example.test" }],
        };

        var scoped = ScopedQuotaProjection.Redact(cached);

        Assert.Null(Assert.Single(scoped.Snapshots).RawSample);
        Assert.Equal("account@example.test", Assert.Single(cached.Snapshots).RawSample);
    }
}
