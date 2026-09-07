using AgentStudio.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaAwareCliOneShotRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atp-quota-one-shot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PipelineCall_CappedPrimary_InvokesEquivalentFallbackImplementation()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var store = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        store.Write([
            Snapshot("codex", 98),
            Snapshot("claude", 34),
        ]);
        var quota = new QuotaService(
            NullLogger<QuotaService>.Instance,
            [new Probe("codex"), new Probe("claude")],
            config,
            store);
        var caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        caps.SetCap("codex", "Weekly", 98);
        var fallback = new CliQuotaFallbackService(config, NullLogger<CliQuotaFallbackService>.Instance);
        fallback.Set(new CliModelRouteProfile
        {
            CliType = "codex",
            FallbackCliType = "claude",
            FallbackModel = "claude-opus-5",
            FallbackThinkingLevel = "high",
        });
        var codex = new CaptureOneShot("codex");
        var claude = new CaptureOneShot("claude");
        var registry = new CliOneShotRegistry(
            [codex, claude],
            quota,
            caps,
            fallback);

        var result = await registry.Require("codex").RunAsync(new CliOneShotRequest(
            "codex",
            "gpt-5.4-mini",
            "review")
        {
            ThinkingLevel = "high",
            StepId = "aspect-code-quality",
        });

        Assert.True(result.Ok);
        Assert.Empty(codex.Requests);
        var request = Assert.Single(claude.Requests);
        Assert.Equal("claude", request.CliType);
        Assert.Equal("claude-opus-5", request.Model);
        Assert.Equal("high", request.ThinkingLevel);
        Assert.Equal("claude", result.EffectiveCliType);
        Assert.True(result.QuotaAdmission?.IsFallback);
    }

    private static QuotaSnapshot Snapshot(string cliType, double usedPct) => new()
    {
        CliType = cliType,
        FetchedAt = DateTime.UtcNow,
        Windows =
        [
            new QuotaWindow
            {
                Label = "Weekly",
                UsedPct = usedPct,
                ResetAt = DateTime.UtcNow.AddDays(2),
                ObservedStartAt = DateTime.UtcNow.AddDays(-5),
            },
        ],
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Probe(string cliType) : IQuotaProbe
    {
        public string CliType { get; } = cliType;
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new QuotaSnapshot { CliType = CliType });
    }

    private sealed class CaptureOneShot(string cliType) : ICliOneShot
    {
        public string CliType { get; } = cliType;
        public List<CliOneShotRequest> Requests { get; } = [];

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var now = DateTime.UtcNow;
            return Task.FromResult(new CliOneShotResult(
                true,
                0,
                "ok",
                string.Empty,
                TimeSpan.Zero,
                "ok",
                null,
                null,
                new AgentMessageLatency(
                    RequestedAt: now,
                    CompletedAt: now,
                    TotalMs: 0),
                null));
        }
    }
}
