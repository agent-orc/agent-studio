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
    BuildTestGateResult Result);

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
    GateHostOutageMonitor? hostHealth = null) : IBuildTestGateRunner
{
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
        if (!request.RequireExactSubject || !ValidSha(request.ExpectedSha))
            return Failure(request, BuildTestGateFailureKind.MissingSource,
                "Remote verification requires an exact commit SHA.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(12))
            return Failure(request, BuildTestGateFailureKind.Environment,
                "Remote verification requires a positive run budget of at most 12 hours.");
        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var registration = cancellationRegistry?.Register(request.Project, request.JobId, gateCancellation,
            cancelOnIntegration: !request.DeliveryIntegratedAtStart);
        // The caller's token stays the shutdown signal; the registry only
        // cancels a gate whose card was completed or integrated meanwhile.
        var gateToken = gateCancellation.Token;
        var started = Stopwatch.StartNew();
        for (var attempt = 1; attempt <= MaxTransportAttempts; attempt++)
        {
            try
            {
                gateToken.ThrowIfCancellationRequested();
                var result = await transport.RunAsync(request, changedFiles, profile, mode, timeout, gateToken);
                if (!string.Equals(result.ExpectedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(result.GateId, request.GateId, StringComparison.Ordinal)
                    || (result.Verdict is BuildTestGateVerdict.Ok or BuildTestGateVerdict.Warn or BuildTestGateVerdict.NotApplicable
                        && !string.Equals(result.TestedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)))
                    return Failure(request, BuildTestGateFailureKind.MissingSource,
                        "Remote gate evidence does not prove the requested commit and gate.");
                var transportFailure = result.Verdict == BuildTestGateVerdict.Fail
                    && GateFailureTriagePolicy.ContainsTransportMarker(result.Reason + "\n" + result.Output)
                    && GateFailureTriagePolicy.FailingItems(result.Output).Count == 0;
                ReportHost(transportFailure, result.Reason + "\n" + result.Output);
                if (attempt < MaxTransportAttempts && transportFailure)
                {
                    logger.LogWarning(
                        "remote_gate_transport_retry gate={GateId} expected_sha={ExpectedSha} attempt={Attempt}",
                        request.GateId, request.ExpectedSha, attempt);
                    continue;
                }
                // AGT-3792: a gate whose host never answered judged nothing. Every
                // gate path (batch, per-card, pre-main full suite) reads the kind,
                // so the transport is a gate environment failure on all of them.
                return transportFailure
                    ? result with { Repository = request.RepositoryPath, FailureKind = BuildTestGateFailureKind.Environment }
                    : result with { Repository = request.RepositoryPath };
            }
            catch (OperationCanceledException)
            {
                return Failure(request, BuildTestGateFailureKind.Cancellation,
                    "Remote verification was cancelled.") with
                    {
                        DurationMs = started.ElapsedMilliseconds,
                        CancellationReason = ct.IsCancellationRequested ? GateCancellationReasons.Shutdown
                            : gateToken.IsCancellationRequested ? GateCancellationReasons.Superseded
                            : GateCancellationReasons.Operator,
                    };
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or System.Net.Sockets.SocketException)
            {
                logger.LogWarning(exception,
                    "remote_gate_transport_failed gate={GateId} expected_sha={ExpectedSha} attempt={Attempt}",
                    request.GateId, request.ExpectedSha, attempt);
                ReportHost(true, exception.Message);
                if (attempt < MaxTransportAttempts) continue;
                return Failure(request, BuildTestGateFailureKind.Environment, "Remote gate transport failure after bounded retries: " + exception.Message)
                    with { DurationMs = started.ElapsedMilliseconds };
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "remote_gate_transport_failed gate={GateId} expected_sha={ExpectedSha}",
                    request.GateId, request.ExpectedSha);
                return Failure(request, BuildTestGateFailureKind.Environment,
                    "Remote verification could not complete: " + exception.Message)
                    with { DurationMs = started.ElapsedMilliseconds };
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
