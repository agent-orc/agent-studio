using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class RemoteRunResultContractTests
{
    [Fact]
    public void Schema_is_versioned_strict_and_requires_all_infrastructure_phases()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath()));
        var root = schema.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("contentSha256",
            root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("remote-run-result/v1",
            root.GetProperty("properties").GetProperty("schemaVersion")
                .GetProperty("const").GetString());
        Assert.Equal(2,
            root.GetProperty("$defs").GetProperty("tokenValue")
                .GetProperty("oneOf").GetArrayLength());
    }

    [Fact]
    public void Golden_v1_fixture_is_machine_validated()
    {
        var result = RemoteRunResultMigration.ReadAndMigrate(
            File.ReadAllText(FixturePath("v1-valid.json")));

        Assert.Equal(12000, result.WallClockDurationMs);
        Assert.Equal(["Claim", "Run", "Gate", "Review"],
            result.Phases.Select(item => item.Phase));
        Assert.Equal("Unavailable", result.Tokens.Input.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Tokens.Input.UnavailableReason));
    }

    [Fact]
    public void V1_digest_is_independent_of_input_line_endings_and_still_rejects_tampering()
    {
        var fixture = File.ReadAllText(FixturePath("v1-valid.json"));

        var fromLf = RemoteRunResultMigration.ReadAndMigrate(fixture.ReplaceLineEndings("\n"));
        var fromCrLf = RemoteRunResultMigration.ReadAndMigrate(fixture.ReplaceLineEndings("\r\n"));

        Assert.Equal("\n", RemoteRunResultCollector.Json.NewLine);
        Assert.Equal(fromLf.ContentSha256, fromCrLf.ContentSha256);
        var tampered = fixture.Replace("\"seed\": 2200", "\"seed\": 2201", StringComparison.Ordinal)
            .ReplaceLineEndings("\r\n");
        Assert.NotEqual(fixture.ReplaceLineEndings("\r\n"), tampered);
        var error = Assert.Throws<InvalidDataException>(() =>
            RemoteRunResultMigration.ReadAndMigrate(tampered));
        Assert.Contains("digest", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Golden_invalid_fixture_is_rejected()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            RemoteRunResultMigration.ReadAndMigrate(
                File.ReadAllText(FixturePath("v1-invalid-missing-review.json"))));

        Assert.Contains("component", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0_fixture_migrates_missing_tokens_to_explicit_unavailable_values()
    {
        var migrated = RemoteRunResultMigration.ReadAndMigrate(
            File.ReadAllText(FixturePath("v0-migrates.json")));

        Assert.Equal(RemoteRunResultProtocol.V1, migrated.SchemaVersion);
        Assert.Equal(12000, migrated.WallClockDurationMs);
        Assert.All(
            new[] { migrated.Tokens.Input, migrated.Tokens.Output, migrated.Tokens.Cached, migrated.Tokens.Total },
            value =>
            {
                Assert.Equal("Unavailable", value.Status);
                Assert.Equal("legacy-v0-did-not-record-token-telemetry", value.UnavailableReason);
                Assert.Null(value.Value);
            });
        Assert.Equal(RemoteRunResultCollector.ComputeDigest(migrated), migrated.ContentSha256);
    }

    [Fact]
    public void Collector_prefers_monotonic_runner_timing_without_changing_server_authority()
    {
        var reference = ReadValidFixture();
        var claim = reference.Phases.Single(item => item.Phase == "Claim");
        var run = reference.Phases.Single(item => item.Phase == "Run");
        var gate = reference.Phases.Single(item => item.Phase == "Gate");
        var review = reference.Phases.Single(item => item.Phase == "Review");
        var server = new TaskServerRemoteRunEvidence(
            reference.ScenarioId, reference.RunId, reference.Seed,
            reference.StartedAt, reference.FinishedAt, reference.Task,
            reference.Attempts, [claim, gate, review], reference.Outcome,
            reference.Assertions, reference.ChronicleLinks,
            reference.Artifacts.Where(item => item.Source == "TaskServer").ToArray(),
            reference.EvidenceAuthority);
        var runner = new RunnerRemoteRunEvidence(
            reference.RunId, reference.Host, reference.Runner, reference.Components,
            reference.Incident, [run], reference.Tokens,
            reference.Artifacts.Where(item => item.Source == "Runner").ToArray(),
            MonotonicWallDurationMs: 12001);

        var result = new RemoteRunResultCollector(
            new FixedTimeProvider(reference.CollectedAt)).Collect(server, runner);

        Assert.Equal(reference.Task, result.Task);
        Assert.Equal("Monotonic", result.Phases.Single(item => item.Phase == "Run").DurationSource);
        Assert.Equal(12001, result.WallClockDurationMs);
        Assert.Equal(reference.EvidenceAuthority, result.EvidenceAuthority);
        RemoteRunResultValidator.Validate(result);
    }

    [Fact]
    public async Task Store_is_create_once_idempotent_and_rejects_late_stale_writer()
    {
        using var directory = new TempDirectory();
        var stale = ReadValidFixture();
        var newerAttempts = stale.Attempts.Select(item =>
            item with { AuthorityEpoch = item.AuthorityEpoch + 1, LeaseFence = item.LeaseFence + 10 }).ToArray();
        var newer = stale with
        {
            Attempts = newerAttempts,
            EvidenceAuthority = new RemoteRunEvidenceAuthority(
                newerAttempts.Max(item => item.AuthorityEpoch),
                newerAttempts.Max(item => item.LeaseFence)),
            ContentSha256 = string.Empty,
        };
        newer = newer with { ContentSha256 = RemoteRunResultCollector.ComputeDigest(newer) };
        var store = new RemoteRunResultStore(directory.Path);

        Assert.Equal(RemoteRunResultWriteStatus.Created, await store.WriteAsync(newer));
        Assert.Equal(RemoteRunResultWriteStatus.IdempotentReplay, await store.WriteAsync(newer));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(stale));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
        var storedPath = Path.Combine(directory.Path, newer.ScenarioId, $"{newer.RunId}.json");
        var stored = RemoteRunResultMigration.ReadAndMigrate(await File.ReadAllTextAsync(storedPath));
        Assert.Equal(newer.ContentSha256, stored.ContentSha256);
    }

    [Fact]
    public void Missing_telemetry_cannot_be_encoded_as_an_implicit_zero()
    {
        var valid = ReadValidFixture();
        var bad = valid with
        {
            Tokens = valid.Tokens with
            {
                Input = new RemoteRunTokenValue("Unavailable", 0, null),
            },
            ContentSha256 = string.Empty,
        };
        bad = bad with { ContentSha256 = RemoteRunResultCollector.ComputeDigest(bad) };

        var error = Assert.Throws<InvalidDataException>(() =>
            RemoteRunResultValidator.Validate(bad));

        Assert.Contains("explicit reason", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RemoteRunResult ReadValidFixture() =>
        RemoteRunResultMigration.ReadAndMigrate(File.ReadAllText(FixturePath("v1-valid.json")));

    private static string FixturePath(string name) =>
        Path.Combine(ProtocolTests.RepositoryRoot(), "contracts", "fixtures", "remote-run-result", name);

    private static string SchemaPath() =>
        Path.Combine(ProtocolTests.RepositoryRoot(), "docs", "app", "schemas", "remote-run-result.schema.json");

    private sealed class FixedTimeProvider(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}
