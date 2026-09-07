using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaAwareOneShotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atp-quota-one-shot-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("pipeline-post-step")]
    [InlineData("orchestrator-chat")]
    public async Task Shared_dispatch_resolves_pipeline_and_chat_to_catalogue_equivalent(string source)
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Quota:TtlSeconds"] = "600",
            })
            .Build();
        var now = DateTime.UtcNow;
        var cacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        cacheStore.Write(
        [
            Snapshot(CliTypes.Codex, 98, now),
            Snapshot(CliTypes.Claude, 34, now),
        ]);
        var quota = new QuotaService(
            NullLogger<QuotaService>.Instance,
            Array.Empty<IQuotaProbe>(),
            config,
            cacheStore);
        var admission = new QuotaAdmissionService(
            quota,
            new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config),
            new CliQuotaFallbackService(config, NullLogger<CliQuotaFallbackService>.Instance),
            new CliQuotaWaitPolicyService(NullLogger<CliQuotaWaitPolicyService>.Instance, config),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config));
        var codex = new RecordingOneShot(CliTypes.Codex);
        var claude = new RecordingOneShot(CliTypes.Claude);
        var registry = new CliOneShotRegistry([codex, claude], admission);

        var result = await registry.Require(CliTypes.Codex).RunAsync(new CliOneShotRequest(
            CliTypes.Codex,
            ModelIds.Gpt54Mini,
            "review this")
        {
            ThinkingLevel = "high",
            Source = source,
            Project = "Example",
        });

        Assert.Null(codex.LastRequest);
        Assert.NotNull(claude.LastRequest);
        Assert.Equal(CliTypes.Claude, claude.LastRequest!.CliType);
        Assert.Equal(ModelIds.ClaudeSonnet5, claude.LastRequest.Model);
        Assert.Equal("medium", claude.LastRequest.ThinkingLevel);
        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, result.QuotaAdmission?.Outcome);
        Assert.Equal(CliTypes.Claude, result.EffectiveCliType);
    }

    [Fact]
    public void Compatibility_envelope_uses_effective_cross_family_model()
    {
        var now = DateTime.UtcNow;
        var result = CliOneShotResult.SpawnFailure("fixture", now, now) with
        {
            ParsedText = "VERDICT: PASS",
            EffectiveModel = ModelIds.Gpt56Sol,
        };

        using var json = JsonDocument.Parse(
            CliOneShotCompatibility.ToClaudeResultEnvelope(result, ModelIds.ClaudeOpus5));

        Assert.Equal("VERDICT: PASS", json.RootElement.GetProperty("result").GetString());
        Assert.Equal(ModelIds.Gpt56Sol, json.RootElement.GetProperty("model").GetString());
    }

    private static QuotaSnapshot Snapshot(string cli, double usedPct, DateTime now)
        => new()
        {
            CliType = cli,
            FetchedAt = now,
            Windows =
            [
                new QuotaWindow
                {
                    Label = "Weekly",
                    UsedPct = usedPct,
                    ObservedStartAt = now.AddDays(-3),
                    ResetAt = now.AddDays(4),
                },
            ],
        };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private sealed class RecordingOneShot(string cliType) : ICliOneShot
    {
        public string CliType { get; } = cliType;
        public CliOneShotRequest? LastRequest { get; private set; }

        public Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            var now = DateTime.UtcNow;
            return Task.FromResult(CliOneShotResult.SpawnFailure("fixture", now, now));
        }
    }
}
