using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Runs a merge, promotion or batch verification gate as claimable gate work
/// (DP-S4, dossier D4 option A). The monolith freezes the exact candidate into
/// an immutable bundle, uploads it to the gate authority, and creates a
/// verification subject that binds SHA, plan, policy and bundle digests. An
/// eligible gate host claims it under lease and fence, proves what it ran, and
/// reports. The caller keeps its per-branch merge or rollback lock for the
/// whole wait, so publication stays ordered and owned by the monolith.
/// </summary>
public sealed class ClaimedGateTransport(
    IBatchGateSubjectAuthority gates,
    ClaimedGateCanaryLedger canary,
    TimeProvider time,
    ILogger<ClaimedGateTransport> logger) : IRemoteGateTransport
{
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
        TimeSpan timeout, CancellationToken ct)
    {
        var kind = GateSubjectKinds.ForGateId(request.GateId)
            ?? throw new InvalidOperationException($"Gate '{request.GateId}' has no claimed verification kind.");
        var sha = request.ExpectedSha?.ToLowerInvariant()
            ?? throw new InvalidOperationException("Claimed verification requires an exact commit SHA.");
        var started = time.GetUtcNow();
        var infra = request.InfrastructureTimeout > TimeSpan.Zero
            ? request.InfrastructureTimeout : TimeSpan.FromMinutes(2);
        var transfer = RemoteGateTransport.TransferBudget(infra, GateBundleLimits.MaxBytes);
        var local = Path.Combine(Path.GetTempPath(), "agentstudio-claimed-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        CreateGateSubjectRequest subjectRequest;
        try
        {
            var bundle = await FreezeAsync(request.RepositoryPath, sha, local, infra, transfer, ct)
                .ConfigureAwait(false);
            var verify = await PlanAsync(local, sha, profile, infra, ct).ConfigureAwait(false);
            var repositoryId = request.Project ?? "repository";
            var locator = await gates.UploadBundleAsync(repositoryId, bundle.Path, bundle.Sha256,
                bundle.SizeBytes, transfer, ct).ConfigureAwait(false);
            subjectRequest = Subject(request, kind, repositoryId, sha, verify, profile, locator, timeout, infra,
                time.GetUtcNow().UtcDateTime);
        }
        finally
        {
            TryDelete(local);
        }
        var status = await gates.CreateAsync(subjectRequest, ct).ConfigureAwait(false);
        logger.LogInformation(
            "claimed_gate_subject_created gate={GateId} kind={Kind} subject={SubjectId} expected_sha={ExpectedSha} bundle_sha256={Bundle}",
            request.GateId, kind, status.Subject.SubjectId, sha, subjectRequest.Bundle!.Sha256);
        try
        {
            var acquired = false;
            while (!GateStates.IsTerminal(status.Phase))
            {
                if (!acquired && status.Phase is not (GateStates.Queued or GateStates.InfraRetry))
                {
                    acquired = true;
                    request.OnMachineGateAcquired?.Invoke();
                }
                await Task.Delay(PollInterval, time, ct).ConfigureAwait(false);
                status = await gates.GetAsync(status.Subject.SubjectId, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Claimed gate subject disappeared.");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancel makes the attempt terminal and releases its host grant; the
            // holder's next renewal is fenced and stops its commands.
            try { await gates.CancelAsync(status.Subject.SubjectId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cancel) when (cancel is HttpRequestException or InvalidDataException or IOException)
            {
                SilentCatch.Warn(cancel, "ClaimedGateTransport: cancel claimed gate subject");
            }
            throw;
        }
        var result = ClaimedGateEvidence.Result(status, request.GateId, $"claimed {kind} gate", sha,
            started, time.GetUtcNow()) with { Repository = request.RepositoryPath };
        if (result.Verdict == BuildTestGateVerdict.Ok && ClaimedGateEvidence.MissingFields(result).Count == 0)
            canary.Record(new ClaimedGateCanaryPass(request.GateId, kind, request.Project,
                status.Subject.SubjectId, result.TestedSha!, result.Executor!, time.GetUtcNow()));
        return result;
    }

    internal sealed record FrozenBundle(string Path, string Sha256, long SizeBytes);

    /// <summary>Freezes exactly one commit into a private bare repository and bundle.</summary>
    internal static async Task<FrozenBundle> FreezeAsync(string repositoryPath, string sha, string local,
        TimeSpan infra, TimeSpan transfer, CancellationToken ct)
    {
        var repository = Path.Combine(local, "source.git");
        await RemoteGateProcess.RunAsync("git", ["init", "--bare", "--quiet", repository], local, infra, ct)
            .ConfigureAwait(false);
        await RemoteGateProcess.RunAsync("git",
            ["-C", repository, "fetch", "--no-tags", "--no-recurse-submodules",
                Path.GetFullPath(repositoryPath), sha], local, transfer, ct).ConfigureAwait(false);
        await RemoteGateProcess.RunAsync("git",
            ["-C", repository, "update-ref", "refs/heads/gate-subject", sha], local, infra, ct).ConfigureAwait(false);
        var bundle = Path.Combine(local, "source.bundle");
        await RemoteGateProcess.RunAsync("git",
            ["-C", repository, "bundle", "create", bundle, "refs/heads/gate-subject"], local, transfer, ct)
            .ConfigureAwait(false);
        var size = new FileInfo(bundle).Length;
        if (size > GateBundleLimits.MaxBytes)
            throw new InvalidDataException("Claimed gate bundle exceeds the 1 GiB transport limit.");
        await using var input = File.OpenRead(bundle);
        return new FrozenBundle(bundle, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)
            .ConfigureAwait(false)), size);
    }

    /// <summary>Plans the verify commands from the exact frozen commit, never from the live checkout.</summary>
    private static async Task<IReadOnlyList<VerifyCommand>> PlanAsync(string local, string sha,
        BuildProfile? profile, TimeSpan infra, CancellationToken ct)
    {
        var inspection = Path.Combine(local, "plan");
        await RemoteGateProcess.RunAsync("git",
            ["clone", "--quiet", "--no-checkout", Path.Combine(local, "source.git"), inspection], local, infra, ct)
            .ConfigureAwait(false);
        await RemoteGateProcess.RunAsync("git", ["-C", inspection, "checkout", "--quiet", "--detach", sha],
            local, infra, ct).ConfigureAwait(false);
        return VerifyCommandPlanner.Plan(inspection, profile).Commands;
    }

    internal static CreateGateSubjectRequest Subject(BuildTestGateRequest request, string kind,
        string repositoryId, string sha, IReadOnlyList<VerifyCommand> verify, BuildProfile? profile,
        GateBundleLocator bundle, TimeSpan timeout, TimeSpan infra, DateTime now)
    {
        var seconds = (int)Math.Clamp(Math.Ceiling(timeout.TotalSeconds), 1, 21600);
        var commands = verify.Select((command, index) => new GateCommand(
            $"{command.Kind.ToString().ToLowerInvariant()}-{index + 1}", "bash", ["-lc", command.Command],
            seconds, command.WorkingSubdir)).ToArray();
        if (commands.Length == 0)
            throw new InvalidOperationException("Claimed verification gate has no verify command to run.");
        var plan = new GatePlan("post-build-test-gate", 1, commands, "", seconds,
            GateRules.RequiredCapabilities(commands), 32768, "always");
        var policy = Digest(JsonSerializer.Serialize(new { request.GateId, kind, profile }));
        var queue = request.QueueWaitTimeout ?? timeout + infra;
        // One subject per invocation: a transport retry is a new verification,
        // never a replay of an earlier terminal subject.
        var invocation = Guid.NewGuid().ToString("N");
        return new CreateGateSubjectRequest(
            request.JobId ?? request.Project ?? kind, $"{kind}:{invocation}", repositoryId, null, sha,
            null, null, null, GateRules.PlanHash(plan), policy,
            request.PipelineDefinitionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Digest(JsonSerializer.Serialize(new { invocation, commands = verify.Select(item => item.Command) })),
            plan, now.Add(queue), 2, Kind: kind, Bundle: bundle);
    }

    private static string Digest(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(exception, "ClaimedGateTransport: remove frozen bundle folder");
        }
    }
}
