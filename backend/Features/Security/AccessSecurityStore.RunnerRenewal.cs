using System.Security.Cryptography;
using AgentStudio.TaskServer.Contracts;
using Protocol = AgentStudio.TaskServer.Contracts.RunnerCredentialRenewalProtocol;

namespace AgentStudio.Security;

public sealed partial class AccessSecurityStore
{
    internal const string RunnerRenewalReplay = "runner.credential-renewal-replay";

    public bool RunnerRenewalEnabled => SecurityProfiles.IsNetworked(_configuration)
        && _configuration.GetValue("Security:RunnerCredentialRenewalEnabled", true);

    public RunnerCredentialRenewalStatus? DescribeRunnerRenewal(RunnerServiceIdentity runner)
        => DescribeRunnerRenewal(runner, RunnerRenewalEnabled, Now);

    internal static RunnerCredentialRenewalStatus? DescribeRunnerRenewal(
        RunnerServiceIdentity runner, bool installationEnabled, DateTime now)
    {
        if (!runner.Id.StartsWith("runner_", StringComparison.Ordinal)) return null;
        var current = CurrentRunnerCredential(runner);
        var enabled = installationEnabled && !runner.RenewalDisabled
            && runner is { RevokedAt: null, RetiredAt: null } && current is { RevokedAt: null, ExpiresAt: not null };
        var due = enabled ? RenewalDueAt(current!) : null;
        DateTime? failingSince = null;
        string? reason = null;
        if (due is { } next)
        {
            var window = current!.ExpiresAt!.Value - next;
            var missedAt = next.AddTicks(Math.Min(TimeSpan.FromMinutes(15).Ticks, window.Ticks / 10));
            failingSince = current.RenewalFailingSince is { } reportedAt && reportedAt < missedAt
                ? reportedAt : now >= missedAt ? missedAt : null;
            if (failingSince is not null)
                reason = current.RenewalFailure switch
                {
                    Protocol.FileUnavailable => "The runner could not write its credential file.",
                    Protocol.RequestFailed => "The runner could not register its successor.",
                    _ => "The runner has not proved a successor.",
                };
        }
        return new(enabled, installationEnabled, !runner.RenewalDisabled, current?.ExpiresAt,
            runner.Credentials.Max(credential => credential.RenewalProvenAt), due, failingSince, reason,
            runner.Credentials.Max(credential => credential.RenewalReplayAt));
    }

    public RunnerServiceIdentity SetRunnerRenewal(string runnerId, bool enabled)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.Find(item => item.Id == runnerId && item.RevokedAt is null && item.RetiredAt is null
                && item.Id.StartsWith("runner_", StringComparison.Ordinal))
                ?? throw new SecurityOperationException(404, "runner-not-found", "Active runner not found.");
            var updated = runner with { RenewalDisabled = !enabled };
            SaveRunnerLocked(state, updated);
            return updated;
        }
    }

    internal void RenewRunnerCredential(RunnerPrincipal actor, RunnerCredentialRenewalRequest request)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.Find(item => item.Id == actor.RunnerId);
            var predecessor = runner?.Credentials.Find(item => item.Id == actor.CredentialId);
            var now = Now;
            if (runner is not null && predecessor?.SupersededAt is not null)
                RecordRunnerRenewalReplayLocked(state, runner, predecessor, now);
            if (runner is null || predecessor is null || !IsRunnerCredentialLive(runner, predecessor, now))
                throw new SecurityOperationException(401, "runner-authentication-required", "A current runner credential is required.");

            var pending = PendingSuccessor(runner, predecessor);
            var status = RunnerRenewalState(runner, predecessor, now);
            if (status is Protocol.Disabled or Protocol.Scheduled)
                throw new SecurityOperationException(409, status == Protocol.Disabled ? "renewal-disabled" : "renewal-too-early",
                    status == Protocol.Disabled ? "Automatic renewal is off for this credential." : "The renewal window has not opened.");

            if (request is { SuccessorId: null, SuccessorHash: null, Failure: Protocol.FileUnavailable or Protocol.RequestFailed })
            {
                if (predecessor.RenewalFailure != request.Failure)
                    SaveRunnerCredentialLocked(state, runner, predecessor with
                    {
                        RenewalFailingSince = predecessor.RenewalFailingSince ?? now,
                        RenewalFailure = request.Failure,
                    });
                return;
            }

            if (request.Failure is not null || !Protocol.IsCredentialId(request.SuccessorId)
                || request.SuccessorHash is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigitLower))
                throw new SecurityOperationException(400, "invalid-renewal", "A successor id and its verifier are required.");
            if (pending is not null)
            {
                if (pending.Id == request.SuccessorId && CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(pending.SecretHash), Convert.FromHexString(hash))) return;
                throw new SecurityOperationException(409, "renewal-already-staged", "This credential already has a staged successor.");
            }
            if (state.Runners.Any(item => item.Credentials.Exists(credential => credential.Id == request.SuccessorId)))
                throw new SecurityOperationException(409, "credential-id-in-use", "The successor id is already in use.");

            var successor = new RunnerCredential
            {
                Id = request.SuccessorId!,
                SecretHash = hash,
                Scopes = [.. predecessor.Scopes],
                CreatedAt = now,
                ExpiresAt = predecessor.ExpiresAt,
                PredecessorId = predecessor.Id,
            };
            SaveRunnerLocked(state, runner with { Credentials = [.. runner.Credentials, successor] });
        }
    }

    private static bool IsRunnerCredentialLive(RunnerServiceIdentity runner, RunnerCredential credential, DateTime now)
        => runner is { RevokedAt: null, RetiredAt: null }
           && credential.RevokedAt is null && credential.SupersededAt is null
           && (credential.ExpiresAt is null || credential.ExpiresAt > now)
           && (!IsPending(credential) || runner.Credentials.Exists(parent => parent.Id == credential.PredecessorId
               && parent.RevokedAt is null && parent.SupersededAt is null && parent.ExpiresAt > now));

    private static bool IsPending(RunnerCredential credential)
        => credential.PredecessorId is not null && credential.RenewalProvenAt is null;

    private static RunnerCredential? PendingSuccessor(RunnerServiceIdentity runner, RunnerCredential predecessor)
        => runner.Credentials.FindLast(item => item.PredecessorId == predecessor.Id && IsPending(item));

    internal static RunnerCredential? CurrentRunnerCredential(RunnerServiceIdentity runner)
        => runner.Credentials.FindLast(item => !IsPending(item) && item.RevokedAt is null && item.SupersededAt is null)
           ?? runner.Credentials.FindLast(item => !IsPending(item));

    private static DateTime? RenewalDueAt(RunnerCredential credential)
        => credential.ExpiresAt is { } end ? end.AddTicks(-(end - credential.CreatedAt).Ticks / 3) : null;

    private string RunnerRenewalState(RunnerServiceIdentity runner, RunnerCredential credential, DateTime now)
    {
        if (PendingSuccessor(runner, credential) is not null)
            return credential.RenewalFailingSince is null ? Protocol.Due : Protocol.Failing;
        if (!RunnerRenewalEnabled || runner.RenewalDisabled || !runner.Id.StartsWith("runner_", StringComparison.Ordinal)
            || CurrentRunnerCredential(runner)?.Id != credential.Id || RenewalDueAt(credential) is not { } next)
            return Protocol.Disabled;
        if (now < next) return Protocol.Scheduled;
        return credential.RenewalFailingSince is null ? Protocol.Due : Protocol.Failing;
    }

    private RunnerServiceIdentity ProveRunnerSuccessorLocked(
        SecurityState state, RunnerServiceIdentity runner, RunnerCredential successor, DateTime now)
    {
        var updated = runner with
        {
            Credentials = runner.Credentials.Select(item =>
            {
                if (item.Id == successor.Id) return successor;
                if (item.Id == successor.PredecessorId) return item with { SupersededAt = now };
                return item;
            }).ToList(),
        };
        SaveRunnerLocked(state, updated);
        return updated;
    }

    private void RecordRunnerRenewalReplayLocked(
        SecurityState state, RunnerServiceIdentity runner, RunnerCredential predecessor, DateTime now)
    {
        if (predecessor.RenewalReplayAt is not null) return;
        ControlPlaneAudit.Append(new(ControlPlaneAuditStreams.Iam, runner.Id, RunnerRenewalReplay, "refused",
            Target: $"runner:{runner.Id}", Details: new { PredecessorId = predecessor.Id }, At: now));
        SaveRunnerCredentialLocked(state, runner, predecessor with { RenewalReplayAt = now });
    }

    private void SaveRunnerCredentialLocked(SecurityState state, RunnerServiceIdentity runner, RunnerCredential credential)
        => SaveRunnerLocked(state, runner with
        {
            Credentials = runner.Credentials.Select(item => item.Id == credential.Id ? credential : item).ToList(),
        });

    private void SaveRunnerLocked(SecurityState state, RunnerServiceIdentity runner)
        => SaveLocked(state with { Runners = state.Runners.Select(item => item.Id == runner.Id ? runner : item).ToList() });

    private static string RunnerChainRoot(RunnerServiceIdentity runner, RunnerCredential credential)
    {
        while (credential.PredecessorId is { } id && runner.Credentials.Find(item => item.Id == id) is { } parent)
            credential = parent;
        return credential.Id;
    }
}
