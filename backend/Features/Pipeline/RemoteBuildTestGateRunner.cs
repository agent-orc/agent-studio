using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

public sealed record RemoteGateInvocation(
    int Version,
    string RunId,
    string BundleSha256,
    BuildTestGateRequest Request,
    IReadOnlyList<string>? ChangedFiles,
    BuildProfile? Profile,
    PostStepMode Mode,
    int TimeoutSeconds,
    int OverallTimeoutSeconds)
{
    public string? CachePath { get; init; }
    /// <summary>A host-verified cache already contains the exact subject; no bundle was sent.</summary>
    public bool SourceFromCache { get; init; }
    public string? SourceCacheKey { get; init; }
    /// <summary>Digest of an incremental pull bundle; the run identity remains BundleSha256.</summary>
    public string? TransferBundleSha256 { get; init; }
    public int GateSlots { get; init; } = 1;
    public int ProcessorCount { get; init; } = 2;
    public string InstallationId { get; init; } = "";
    /// <summary>AGT-3866: age in hours after which the worker prunes host caches; 0 is the default.</summary>
    public int CachePruneAgeHours { get; init; }
}

public sealed record RemoteGateResponse(
    int Version,
    string RunId,
    string BundleSha256,
    BuildTestGateResult Result)
{
    public DateTimeOffset? WorkerCompletedAtUtc { get; init; }
}

public interface IRemoteGateTransport
{
    Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile,
        PostStepMode mode, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The backend owns gate orchestration, but never executes a build or test.
/// The explicit remote worker uses the same exact-subject gate implementation.
/// Missing transport, failed transport, or mismatched evidence fails closed.
/// AGT-3792: every attempt reports to the one gate host health state, so the
/// transport failures of any gate (batch, per-task or merge) mark the host
/// unreachable and any answer from the host ends the outage.
/// </summary>
public sealed class RemoteBuildTestGateRunner(
    IRemoteGateTransport transport,
    ILogger<RemoteBuildTestGateRunner> logger,
    TaskGateCancellationRegistry? cancellationRegistry = null,
    GateHostOutageMonitor? hostHealth = null, SshGateHostProbe? diskMonitor = null,
    PostMergeModeService? postMergeMode = null, IConfiguration? configuration = null,
    ILoggerFactory? loggers = null) : IBuildTestGateRunner
{
    internal Func<string, IRemoteGateTransport>? RelocationTransportFactory { get; init; }

    internal const int MaxTransportAttempts = 3;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static bool ValidSha(string? value) => value is not null
        && Regex.IsMatch(value, "^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$");

    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile,
        PostStepMode mode, TimeSpan timeout, CancellationToken ct)
    {
        if (mode == PostStepMode.Off)
            return new(BuildTestGateVerdict.Skipped, null, 0, "", "mode=off", false, false);
        if (request.Project is { } project && postMergeMode?.ReadState(project) is { } state)
            request = request with { Quarantines = state.Quarantines };
        if (!request.RequireExactSubject || !ValidSha(request.ExpectedSha))
            return Failure(request, BuildTestGateFailureKind.MissingSource,
                "Remote verification requires an exact commit SHA.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(12))
            return Failure(request, BuildTestGateFailureKind.Environment,
                "Remote verification requires a positive run budget of at most 12 hours.");
        var relocation = GateEnvironmentRelocationScope.For(request);
        var selectedTransport = relocation is null ? transport : RelocationTransportFactory?.Invoke(relocation.Section)
            ?? new RemoteGateTransport(configuration ?? throw new InvalidOperationException("Relocation requires gate configuration."),
                (loggers ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger<RemoteGateTransport>(),
                relocation.Section, new GateSlotUsageTracker(configuration));
        if (relocation is not null) request = request with { DisableVerdictCache = true };
        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var registration = cancellationRegistry?.Register(request.Project, request.JobId, gateCancellation,
            cancelOnIntegration: !request.DeliveryIntegratedAtStart);
        // The caller's token stays the shutdown signal; the registry only
        // cancels a gate whose card was completed or integrated meanwhile.
        var gateToken = gateCancellation.Token;
        var started = Stopwatch.StartNew();
        var retryReasons = new List<string>();
        var priorStages = new Dictionary<string, long>();
        var incompleteTimings = false;
        BuildTestGateResult Finish(BuildTestGateResult result, int attempts, bool incomplete = false)
            => result with
            {
                GateAttempts = attempts,
                GateRetryReasons = [.. retryReasons, .. result.GateRetryReasons],
                StageDurationsMs = GateStageTimings.Sum(priorStages, result.StageDurationsMs),
                StageTimingsIncomplete = incompleteTimings || incomplete || result.StageTimingsIncomplete
                    || attempts > 1 && result.StageTimingsVersion == 0,
            };
        for (var attempt = 1; attempt <= MaxTransportAttempts; attempt++)
        {
            try
            {
                gateToken.ThrowIfCancellationRequested();
                if (relocation is null && diskMonitor is not null)
                    await diskMonitor.WaitForDiskRecoveryAsync(
                        reason => logger.LogWarning("gate-disk-admission {Reason}", reason), gateToken);
                var result = await selectedTransport.RunAsync(request, changedFiles, profile, mode, timeout, gateToken);
                if (!string.Equals(result.ExpectedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(result.GateId, request.GateId, StringComparison.Ordinal)
                    || (result.Verdict is BuildTestGateVerdict.Ok or BuildTestGateVerdict.Warn or BuildTestGateVerdict.NotApplicable
                        && !string.Equals(result.TestedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)))
                    return Finish(Failure(request, BuildTestGateFailureKind.MissingSource,
                        "Remote gate evidence does not prove the requested commit and gate."), attempt, incomplete: true);
                var transportFailure = result.Verdict == BuildTestGateVerdict.Fail
                    && GateFailureTriagePolicy.ContainsTransportMarker(result.Reason + "\n" + result.Output)
                    && GateFailureTriagePolicy.FailingItems(result.Output).Count == 0;
                if (relocation is null) ReportHost(transportFailure, result.Reason + "\n" + result.Output);
                if (attempt < MaxTransportAttempts && transportFailure)
                {
                    retryReasons.Add("Transport response required another gate attempt.");
                    priorStages = GateStageTimings.Sum(priorStages, result.StageDurationsMs);
                    incompleteTimings |= result.StageTimingsIncomplete || result.StageTimingsVersion == 0;
                    logger.LogWarning(
                        "remote_gate_transport_retry gate={GateId} expected_sha={ExpectedSha} attempt={Attempt}",
                        request.GateId, request.ExpectedSha, attempt);
                    continue;
                }
                // AGT-3792: a gate whose host never answered judged nothing. Every
                // gate path (batch, per-card, pre-main full suite) reads the kind,
                // so the transport is a gate environment failure on all of them.
                return Finish(transportFailure
                    ? result with { Repository = request.RepositoryPath, FailureKind = BuildTestGateFailureKind.Environment }
                    : result with { Repository = request.RepositoryPath }, attempt);
            }
            catch (OperationCanceledException)
            {
                return Finish(Failure(request, BuildTestGateFailureKind.Cancellation,
                    "Remote verification was cancelled.") with
                    {
                        DurationMs = started.ElapsedMilliseconds,
                        CancellationReason = ct.IsCancellationRequested ? GateCancellationReasons.Shutdown
                            : gateToken.IsCancellationRequested ? GateCancellationReasons.Superseded
                            : GateCancellationReasons.Operator,
                    }, attempt, incomplete: true);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or System.Net.Sockets.SocketException)
            {
                logger.LogWarning(exception,
                    "remote_gate_transport_failed gate={GateId} expected_sha={ExpectedSha} attempt={Attempt}",
                    request.GateId, request.ExpectedSha, attempt);
                if (relocation is null) ReportHost(true, exception.Message);
                incompleteTimings = true;
                if (attempt < MaxTransportAttempts)
                {
                    retryReasons.Add("Transport exception required another gate attempt: " + exception.GetType().Name + ".");
                    continue;
                }
                return Finish(Failure(request, BuildTestGateFailureKind.Environment, "Remote gate transport failure after bounded retries: " + exception.Message)
                    with { DurationMs = started.ElapsedMilliseconds }, attempt, incomplete: true);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "remote_gate_transport_failed gate={GateId} expected_sha={ExpectedSha}",
                    request.GateId, request.ExpectedSha);
                return Finish(Failure(request, BuildTestGateFailureKind.Environment,
                    "Remote verification could not complete: " + exception.Message)
                    with { DurationMs = started.ElapsedMilliseconds }, attempt, incomplete: true);
            }
        }
        throw new InvalidOperationException("The bounded transport retry loop did not return a verdict.");
    }

    /// <summary>
    /// A transport failure that names an unanswering host counts toward the
    /// unreachable mark; any other answer means the host was reached.
    /// </summary>
    private void ReportHost(bool transportFailure, string evidence)
    {
        if (hostHealth is null) return;
        if (!transportFailure)
        {
            hostHealth.Reachable();
            return;
        }
        if (MonolithTransitionAdapter.GateTransportFailureKind(evidence) is not null)
            hostHealth.TransportFailed(evidence.Trim());
    }

    internal static BuildTestGateResult Failure(BuildTestGateRequest request,
        BuildTestGateFailureKind kind, string reason) => new(
            BuildTestGateVerdict.Fail, null, 0, reason, reason, false, false)
        {
            FailureKind = kind,
            GateId = request.GateId,
            ExpectedSha = request.ExpectedSha,
            Repository = request.RepositoryPath,
            Executor = request.Executor,
            AttemptChainId = request.AttemptChainId,
        };
}

/// <summary>Platform-owned placement for one integration replay, confined to its asynchronous call chain.</summary>
internal sealed class GateEnvironmentRelocationScope : IDisposable
{
    private static readonly AsyncLocal<GateEnvironmentRelocationScope?> Current = new();
    private readonly GateEnvironmentRelocationScope? _prior;
    private readonly string _project;
    private readonly string _jobId;
    internal string Section { get; }

    internal GateEnvironmentRelocationScope(string section, string project, string jobId)
    {
        Section = section;
        _project = project;
        _jobId = jobId;
        _prior = Current.Value;
        Current.Value = this;
    }

    internal static GateEnvironmentRelocationScope? For(BuildTestGateRequest request)
        => Current.Value is { } scope && request.Project == scope._project && request.JobId == scope._jobId
            ? scope : null;

    public void Dispose() => Current.Value = _prior;
}
