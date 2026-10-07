using AgentStudio.Setup;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

// clock-independent: lastSeenAt and the manifest dates are recorded data; acceptance only checks presence, never age.

[Collection(InstallerProcessStateCollection.Name)]
public sealed class AcceptanceJourneyTests : IDisposable
{
    private const string Identity = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SeenRunner =
        """[{"principalId":"bootstrap-studio","kind":"studio","revokedAt":null,"lastSeenAt":null},""" +
        """{"principalId":"bootstrap-engine","kind":"engine","revokedAt":null,"lastSeenAt":null},""" +
        """{"principalId":"runner:agent-runner-01","kind":"runner","revokedAt":null,"lastSeenAt":"2026-10-05T08:00:00Z"}]""";

    private readonly string root = Directory.CreateTempSubdirectory().FullName;
    private readonly string token;
    private readonly List<string> calls = [];

    public AcceptanceJourneyTests()
    {
        token = Path.Combine(root, "management.token");
        File.WriteAllText(token, "management-secret");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(token, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task Pending_identity_stops_before_backup_and_canary()
    {
        await InstallAsync();
        using var http = Authority(principals: SeenRunner.Replace("\"2026-10-05T08:00:00Z\"", "null"));

        Assert.Equal(AcceptanceJourney.ExitPending,
            await AcceptanceJourney.RunAsync(Request(canary: "exit 9"), http));

        Assert.Equal(["GET /api/v1/management/principals"], calls);
        Assert.Contains(await CheckpointsAsync(), line => Is(line, "identity-bootstrapped", "not reached"));
        Assert.Equal(InstallationManifest.PhaseAwaitingAcceptance, (await ManifestStore.ReadAsync(root))!.Phase);
    }

    [Fact]
    public async Task First_run_creates_and_verifies_a_full_set_then_waits_for_its_rehearsal()
    {
        await InstallAsync();
        using var http = Authority(backupId: "full-1", setHash: "abc123");

        Assert.Equal(AcceptanceJourney.ExitPending,
            await AcceptanceJourney.RunAsync(Request(canary: "exit 9", backupPath: root), http));

        Assert.Equal([
            "GET /api/v1/management/principals",
            "POST /api/v1/management/backups/full",
            "POST /api/v1/management/backups/full/full-1/verify",
        ], calls);
        var lines = await CheckpointsAsync();
        Assert.Contains(lines, line => Is(line, "identity-bootstrapped", "observed"));
        Assert.Contains(lines, line => Is(line, "recovery-set-verified", "observed"));
        Assert.Contains(lines, line => Is(line, "recovery-checkpoint", "not reached")
            && line.Contains("--recovery-checkpoint") && line.Contains("full-1.rehearsal.json"));
        Assert.DoesNotContain(lines, line => line.Contains("authenticated-canary"));
        Assert.Equal(InstallationManifest.PhaseAwaitingAcceptance, (await ManifestStore.ReadAsync(root))!.Phase);
    }

    [Fact]
    public async Task Rehearsed_set_and_passing_canary_complete_the_installation()
    {
        if (OperatingSystem.IsWindows()) return;
        await InstallAsync();
        var (set, setHash) = await RecoverySetAsync("full-1", "inst_original");
        using var http = Authority(backupId: "full-1", setHash: setHash);

        // The canary receives the installation id and authority, never the token value.
        var canary = "test \"$AGENT_STUDIO_INSTALLATION_ID\" = inst_original" +
                     " && test -n \"$AGENT_STUDIO_SERVER_URL\" && test -f \"$AGENT_STUDIO_TOKEN_FILE\"";
        Assert.Equal(0, await AcceptanceJourney.RunAsync(Request(canary, recoveryCheckpoint: set), http));

        Assert.Equal([
            "GET /api/v1/management/principals",
            "POST /api/v1/management/backups/full/full-1/verify",
        ], calls);
        var accepted = (await ManifestStore.ReadAsync(root))!;
        Assert.Equal((InstallationManifest.PhaseComplete, "inst_original"), (accepted.Phase, accepted.InstallationId));
        var lines = await CheckpointsAsync();
        foreach (var checkpoint in new[] { "identity-bootstrapped", "recovery-checkpoint", "authenticated-canary", "accepted" })
            Assert.Contains(lines, line => Is(line, checkpoint, "observed"));
        Assert.DoesNotContain(lines, line => line.Contains("management-secret"));
    }

    [Fact]
    public async Task Failing_canary_is_recorded_and_keeps_acceptance_pending()
    {
        if (OperatingSystem.IsWindows()) return;
        await InstallAsync();
        var (set, setHash) = await RecoverySetAsync("full-1", "inst_original");
        using var http = Authority(backupId: "full-1", setHash: setHash);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AcceptanceJourney.RunAsync(Request("exit 3", recoveryCheckpoint: set), http));

        Assert.Contains("exited 3", error.Message);
        Assert.Contains(await CheckpointsAsync(), line => Is(line, "authenticated-canary", "failed"));
        Assert.Equal(InstallationManifest.PhaseAwaitingAcceptance, (await ManifestStore.ReadAsync(root))!.Phase);
    }

    [Fact]
    public async Task Unconfigured_canary_is_pending_not_passed()
    {
        await InstallAsync();
        var (set, setHash) = await RecoverySetAsync("full-1", "inst_original");
        using var http = Authority(backupId: "full-1", setHash: setHash);

        Assert.Equal(AcceptanceJourney.ExitPending,
            await AcceptanceJourney.RunAsync(Request(canary: null, recoveryCheckpoint: set), http));

        var lines = await CheckpointsAsync();
        Assert.Contains(lines, line => Is(line, "authenticated-canary", "not reached")
            && line.Contains(AcceptanceJourney.CanaryCommandVariable));
        Assert.Equal(InstallationManifest.PhaseAwaitingAcceptance, (await ManifestStore.ReadAsync(root))!.Phase);
    }

    [Fact]
    public async Task Recovery_set_of_another_installation_or_unverified_by_the_authority_is_refused()
    {
        await InstallAsync();
        var (foreign, foreignHash) = await RecoverySetAsync("full-2", "inst_other");
        using var foreignHttp = Authority(backupId: "full-2", setHash: foreignHash);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AcceptanceJourney.RunAsync(Request("true", recoveryCheckpoint: foreign), foreignHttp));

        var (set, _) = await RecoverySetAsync("full-1", "inst_original");
        using var differentHash = Authority(backupId: "full-1", setHash: "different");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AcceptanceJourney.RunAsync(Request("true", recoveryCheckpoint: set), differentHash));
        Assert.Equal(InstallationManifest.PhaseAwaitingAcceptance, (await ManifestStore.ReadAsync(root))!.Phase);
    }

    [Fact]
    public async Task Manifest_changed_during_the_canary_is_not_marked_complete()
    {
        if (OperatingSystem.IsWindows()) return;
        await InstallAsync();
        var (set, setHash) = await RecoverySetAsync("full-1", "inst_original");
        using var http = Authority(backupId: "full-1", setHash: setHash);
        var changed = Path.Combine(root, "changed.json");
        await File.WriteAllTextAsync(changed, JsonSerializer.Serialize(Manifest() with
        {
            ReleaseVersion = "1.3.0",
            Phase = InstallationManifest.PhaseUpdating,
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceJourney.RunAsync(
            Request($"cp '{changed}' '{Path.Combine(root, InstallationManifest.FileName)}'", recoveryCheckpoint: set),
            http));

        Assert.Contains("changed during acceptance", error.Message);
        Assert.Equal(InstallationManifest.PhaseUpdating, (await ManifestStore.ReadAsync(root))!.Phase);
    }

    [Fact]
    public async Task Canary_is_bounded_by_its_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await AcceptanceJourney.RunCanaryAsync("sleep 30", new Dictionary<string, string>(),
            TimeSpan.FromMilliseconds(300), default);
        Assert.True(result.TimedOut);
    }

    [Theory]
    [InlineData("join-host", InstallationManifest.PhaseComplete)]
    [InlineData("one-box", InstallationManifest.PhaseInstalling)]
    public async Task Acceptance_runs_only_on_an_installed_authority(string journey, string phase)
    {
        await ManifestStore.WriteAsync(root, Manifest() with { Journey = journey, Phase = phase });
        using var http = Authority();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceJourney.RunAsync(Request("true"), http));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Rejected_management_token_is_an_invalid_credential_error()
    {
        await InstallAsync();
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.Contains("rejected the management token",
            (await Assert.ThrowsAsync<InvalidOperationException>(
                () => AcceptanceJourney.RunAsync(Request("true"), http))).Message);
    }

    [Fact]
    public async Task Accept_verb_requires_server_url_and_token_file()
    {
        await InstallAsync();
        Assert.Equal(1, await ProductSetup.RunAsync(["accept", "--install-dir", root, "--token-file", token]));
        Assert.Equal(1, await ProductSetup.RunAsync(["accept", "--install-dir", root,
            "--server-url", "https://tasks.wg.internal"]));
        Assert.Equal("accept", ProductCommand.Parse(["accept", "--server-url", "https://a"]).Verb);
    }

    private Task InstallAsync() => ManifestStore.WriteAsync(root, Manifest());

    private static InstallationManifest Manifest()
        => new(InstallationManifest.CurrentSchema, "inst_original", "one-box", "studio", "docker", "1.2.0",
            InstallationManifest.PhaseAwaitingAcceptance, ["task-server-admin"], null, "runner-coding", 2, 2,
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

    private AcceptanceJourney.Request Request(string? canary, string? recoveryCheckpoint = null,
        string? backupPath = null)
        => new(root, "https://tasks.wg.internal", token, recoveryCheckpoint, backupPath, canary,
            TimeSpan.FromMinutes(1));

    private async Task<string[]> CheckpointsAsync()
        => await File.ReadAllLinesAsync(Path.Combine(root, InstallationManifest.CheckpointFileName));

    private static bool Is(string line, string checkpoint, string outcome)
        => line.Contains($"\"checkpoint\":\"{checkpoint}\"") && line.Contains($"\"outcome\":\"{outcome}\"");

    private async Task<(string Set, string SetHash)> RecoverySetAsync(string backupId, string installationId)
    {
        var set = Path.Combine(root, "full", backupId);
        Directory.CreateDirectory(set);
        await File.WriteAllTextAsync(Path.Combine(set, "snapshot.db"), "snapshot");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("snapshot")));
        var setHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"snapshot.db:8:{hash}\n")));
        await File.WriteAllTextAsync(Path.Combine(set, "inventory.json"),
            $$"""{"setSha256":"{{setHash}}","files":[{"relativePath":"snapshot.db","size":8,"sha256":"{{hash}}"}]}""");
        await File.WriteAllTextAsync(Path.Combine(set, "complete.json"), $$"""{"setSha256":"{{setHash}}"}""");
        await File.WriteAllTextAsync(set + ".rehearsal.json",
            $$"""{"backupId":"{{backupId}}","setSha256":"{{setHash}}","installationId":"{{installationId}}","verified":true,"restoredIntoEmptyTarget":true}""");
        return (set, setHash);
    }

    private HttpClient Authority(string principals = SeenRunner, string backupId = "full-1", string setHash = "abc123")
        => new(new Handler(request =>
        {
            Assert.Equal("management-secret", request.Headers.Authorization!.Parameter);
            Assert.Equal("2", request.Headers.GetValues("X-Task-Protocol-Version").Single());
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}");
            var body = path.EndsWith("/principals", StringComparison.Ordinal) ? principals
                : path.EndsWith("/verify", StringComparison.Ordinal)
                    ? $$$"""{"backupId":"{{{backupId}}}","verified":true,"identitySha256":"{{{Identity}}}","summary":{"setSha256":"{{{setHash}}}"}}"""
                    : $$"""{"id":"{{backupId}}","setSha256":"{{setHash}}"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
