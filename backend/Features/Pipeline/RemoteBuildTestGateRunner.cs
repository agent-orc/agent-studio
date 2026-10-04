using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    int OverallTimeoutSeconds);

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
/// </summary>
public sealed class RemoteBuildTestGateRunner(
    IRemoteGateTransport transport,
    ILogger<RemoteBuildTestGateRunner> logger) : IBuildTestGateRunner
{
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
        var started = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await transport.RunAsync(request, changedFiles, profile, mode, timeout, ct);
            if (!string.Equals(result.ExpectedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.GateId, request.GateId, StringComparison.Ordinal)
                || (result.Verdict is BuildTestGateVerdict.Ok or BuildTestGateVerdict.Warn or BuildTestGateVerdict.NotApplicable
                    && !string.Equals(result.TestedSha, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)))
                return Failure(request, BuildTestGateFailureKind.MissingSource,
                    "Remote gate evidence does not prove the requested commit and gate.");
            return result with { Repository = request.RepositoryPath };
        }
        catch (OperationCanceledException)
        {
            return Failure(request, BuildTestGateFailureKind.Cancellation,
                "Remote verification was cancelled.") with { DurationMs = started.ElapsedMilliseconds };
        }
        catch (TimeoutException exception)
        {
            return Failure(request, BuildTestGateFailureKind.Timeout, exception.Message)
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
