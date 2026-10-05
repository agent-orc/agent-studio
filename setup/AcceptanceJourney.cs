using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Setup;

/// <summary>
/// Acceptance of an authority installation (one-box or relocated authority).
/// After install, it observes three checkpoints against the running Task
/// Server: bootstrapped service identities with an enrolled runner, a verified
/// full recovery set with its empty-target rehearsal receipt, and the
/// installation's detached canary. Each checkpoint is recorded as observed only
/// when this run saw it, and the installation becomes complete only when all
/// three were observed in the same run.
/// </summary>
internal static class AcceptanceJourney
{
    /// <summary>The D6 updater's canary contract; update and acceptance run the same command.</summary>
    public const string CanaryCommandVariable = "AGENT_ORCHESTRATOR_CANARY_COMMAND";

    /// <summary>Exit code for a run that stopped at a named, pending operator step.</summary>
    public const int ExitPending = 2;

    public static readonly TimeSpan DefaultCanaryTimeout = TimeSpan.FromMinutes(60);

    internal sealed record Request(
        string Root,
        string ServerUrl,
        string TokenFile,
        string? RecoveryCheckpoint,
        string? BackupPath,
        string? CanaryCommand,
        TimeSpan CanaryTimeout);

    public static async Task<int> RunAsync(Request request, HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ManifestStore.ReadAsync(request.Root)
            ?? throw new InvalidOperationException(
                $"No installation manifest exists at {request.Root}. Run install first, or pass --install-dir.");
        if (manifest.Journey is not ("one-box" or "relocate-authority"))
            throw new InvalidOperationException(
                $"Installation {manifest.InstallationId} is a {manifest.Journey} installation. Run accept on the authority host (one-box or relocate-authority).");
        if (manifest.Phase is not (InstallationManifest.PhaseAwaitingAcceptance or InstallationManifest.PhaseComplete))
            throw new InvalidOperationException(
                $"Installation {manifest.InstallationId} is in phase {manifest.Phase}. Finish install or update before acceptance.");
        ProductSetup.ValidateUpstream(request.ServerUrl);
        var token = await AuthorityApi.ReadTokenAsync(request.TokenFile, "Management token file");
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(15) } : null;
        var http = client ?? owned!;
        var api = new Uri(new Uri(request.ServerUrl), "/api/v1/");

        var identity = await IdentityAsync(http, api, token, cancellationToken);
        if (!identity.Observed)
            return await PendingAsync(request.Root, manifest, "identity-bootstrapped", identity.Detail);
        await ManifestStore.CheckpointAsync(request.Root, manifest, "identity-bootstrapped", "observed", identity.Detail);

        var recovery = await RecoveryAsync(http, api, token, manifest, request, cancellationToken);
        if (!recovery.Observed)
            return await PendingAsync(request.Root, manifest, "recovery-checkpoint", recovery.Detail);
        await ManifestStore.CheckpointAsync(request.Root, manifest, "recovery-checkpoint", "observed", recovery.Detail);

        if (string.IsNullOrWhiteSpace(request.CanaryCommand))
            return await PendingAsync(request.Root, manifest, "authenticated-canary",
                $"{CanaryCommandVariable} is not configured. Configure the installation's detached provider canary " +
                "(coding, review and canonical publication), as for update-docker.sh, and rerun accept.");
        var started = Stopwatch.StartNew();
        var canary = await RunCanaryAsync(request.CanaryCommand, new Dictionary<string, string>
        {
            ["AGENT_STUDIO_INSTALLATION_ID"] = manifest.InstallationId,
            ["AGENT_STUDIO_SERVER_URL"] = request.ServerUrl,
            ["AGENT_STUDIO_TOKEN_FILE"] = Path.GetFullPath(request.TokenFile),
        }, request.CanaryTimeout, cancellationToken);
        if (canary.TimedOut || canary.ExitCode != 0)
        {
            var failure = canary.TimedOut
                ? $"The canary did not finish within {request.CanaryTimeout.TotalMinutes:0} minutes and was stopped."
                : $"The canary command exited {canary.ExitCode}.";
            await ManifestStore.CheckpointAsync(request.Root, manifest, "authenticated-canary", "failed", failure);
            throw new InvalidOperationException($"{failure} The installation stays {manifest.Phase}.");
        }

        // The canary can run for a long time; never complete a manifest that
        // another setup run changed meanwhile.
        var current = await ManifestStore.ReadAsync(request.Root);
        if (current is null || current.InstallationId != manifest.InstallationId
            || current.ReleaseVersion != manifest.ReleaseVersion || current.Phase != manifest.Phase)
            throw new InvalidOperationException(
                $"Installation {manifest.InstallationId} changed during acceptance. Rerun accept.");
        await ManifestStore.CheckpointAsync(request.Root, manifest, "authenticated-canary", "observed",
            $"The canary command exited 0 after {started.Elapsed.TotalSeconds:0} s against {request.ServerUrl}.");
        var accepted = current with
        {
            Phase = InstallationManifest.PhaseComplete,
            UpdatedUtc = DateTime.UtcNow,
        };
        await ManifestStore.WriteAsync(request.Root, accepted);
        await ManifestStore.CheckpointAsync(request.Root, accepted, "accepted", "observed",
            "Identity, a verified recovery set with its rehearsal receipt, and the canary were observed in this run.");
        Console.WriteLine($"Installation {accepted.InstallationId} is accepted (phase complete).");
        return 0;
    }

    private static async Task<(bool Observed, string Detail)> IdentityAsync(HttpClient http, Uri api, string token,
        CancellationToken cancellationToken)
    {
        using var principals = await AuthorityApi.SendAsync(http, HttpMethod.Get,
            new Uri(api, "management/principals"), token, cancellationToken);
        var active = principals.RootElement.EnumerateArray()
            .Where(principal => !principal.TryGetProperty("revokedAt", out var revoked)
                                || revoked.ValueKind == JsonValueKind.Null)
            .ToArray();
        var missing = new[] { "studio", "engine", "runner" }
            .Where(kind => !active.Any(principal => principal.GetProperty("kind").GetString() == kind))
            .ToArray();
        if (missing.Length > 0)
            return (false, $"The Task Server has no active {string.Join(", ", missing)} principal. " +
                           "Rerun install to bootstrap the service principals, then rerun accept.");
        var runner = active.FirstOrDefault(principal => principal.GetProperty("kind").GetString() == "runner"
            && principal.TryGetProperty("lastSeenAt", out var seen) && seen.ValueKind == JsonValueKind.String);
        if (runner.ValueKind == JsonValueKind.Undefined)
            return (false, "No runner has contacted the Task Server yet. Enrol the first runner with its coding " +
                           "and review budgets, then rerun accept.");
        return (true, $"The management token was accepted; {active.Length} active principals include studio, " +
                      $"engine and runner {runner.GetProperty("principalId").GetString()}, last seen " +
                      $"{runner.GetProperty("lastSeenAt").GetString()}.");
    }

    private static async Task<(bool Observed, string Detail)> RecoveryAsync(HttpClient http, Uri api, string token,
        InstallationManifest manifest, Request request, CancellationToken cancellationToken)
    {
        string backupId;
        string setHash;
        if (request.RecoveryCheckpoint is { } checkpoint)
        {
            var setPath = Path.GetFullPath(checkpoint);
            backupId = Path.GetFileName(Path.TrimEndingDirectorySeparator(setPath));
            setHash = await RelocationGate.VerifyRecoverySetAsync(setPath, manifest.InstallationId);
        }
        else
        {
            using var created = await AuthorityApi.SendAsync(http, HttpMethod.Post,
                new Uri(api, "management/backups/full"), token, cancellationToken);
            backupId = created.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidDataException("The Task Server returned a full backup set without an id.");
            setHash = created.RootElement.GetProperty("setSha256").GetString()
                ?? throw new InvalidDataException("The Task Server returned a full backup set without a set hash.");
        }

        using var verification = await AuthorityApi.SendAsync(http, HttpMethod.Post,
            new Uri(api, $"management/backups/full/{Uri.EscapeDataString(backupId)}/verify"), token, cancellationToken);
        var result = verification.RootElement;
        var identity = result.TryGetProperty("identitySha256", out var digest) ? digest.GetString() : null;
        if (!result.GetProperty("verified").GetBoolean()
            || result.GetProperty("backupId").GetString() != backupId
            || !string.Equals(result.GetProperty("summary").GetProperty("setSha256").GetString(), setHash,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(identity))
            throw new InvalidDataException($"The Task Server did not verify full backup set {backupId}.");

        if (request.RecoveryCheckpoint is null)
        {
            await ManifestStore.CheckpointAsync(request.Root, manifest, "recovery-set-verified", "observed",
                $"Full set {backupId} was created and verified by the Task Server; set {setHash}, identity {identity}.");
            var setPath = request.BackupPath is null
                ? $"BACKUP_PATH/full/{backupId}"
                : Path.Combine(Path.GetFullPath(request.BackupPath), "full", backupId);
            return (false, $"Rehearse restore of {backupId} into an isolated empty Task Server store, record " +
                           $"{setPath}.rehearsal.json, then rerun accept with --recovery-checkpoint {setPath}.");
        }
        return (true, $"Full set {backupId} was re-hashed by setup and verified by the Task Server (set {setHash}, " +
                      $"identity {identity}); its empty-target rehearsal receipt matches this installation.");
    }

    private static async Task<int> PendingAsync(string root, InstallationManifest manifest, string checkpoint,
        string detail)
    {
        await ManifestStore.CheckpointAsync(root, manifest, checkpoint, "not reached", detail);
        Console.WriteLine($"Acceptance pending at {checkpoint}: {detail}");
        Console.WriteLine($"Installation {manifest.InstallationId} stays {manifest.Phase}.");
        return ExitPending;
    }

    internal static async Task<(int ExitCode, bool TimedOut)> RunCanaryAsync(string command,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command }, CreateNoWindow = true }
            : new ProcessStartInfo("sh") { ArgumentList = { "-c", command }, CreateNoWindow = true };
        start.UseShellExecute = false;
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The canary command could not be started.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            return (process.ExitCode, true);
        }
    }
}
