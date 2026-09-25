using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<IssuedOperationPermitResponse> IssueOperationPermitAsync(
        IssueOperationPermitRequest request, string actorId, CancellationToken ct)
    {
        RequireAdmission();
        if (!OperationPermitPolicy.Valid(request, _clock.GetUtcNow()))
            throw new ArgumentException("The operation permit request is invalid.");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset validUntil = default;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, request.Subject.RunOrReviewId, ct);
            var run = await ReadRunAsync(connection, transaction, request.Subject.RunOrReviewId, ct);
            if (!OperationPermitPolicy.Active(request.Subject, lease, run, _clock.GetUtcNow()))
                throw new TaskServerConflictException("operation-authority-stale", "The operation requires a current running subject and fence.");
            validUntil = new[] { request.Deadline, new DateTimeOffset(lease!.ExpiresAt, TimeSpan.Zero) }.Min();
            var binding = request with { Deadline = validUntil };
            await ExecuteAsync(connection, "DELETE FROM operation_permits WHERE expires_at <= $now", ct, transaction, ("$now", Iso(UtcNow)));
            var count = Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM operation_permits", ct, transaction));
            if (count >= 4096) throw new TaskServerConflictException("operation-permit-capacity", "The active operation permit bound has been reached.");
            await ExecuteAsync(connection, "INSERT INTO operation_permits(token_hash, binding_json, expires_at) VALUES ($hash, $binding, $expires)",
                ct, transaction, ("$hash", OperationsProtocol.Digest(token)),
                ("$binding", JsonSerializer.Serialize(binding, OperationsProtocol.Json)), ("$expires", Iso(validUntil.UtcDateTime)));
            await AuditAsync(connection, transaction, actorId, "operation.permit-issued", "run", request.Subject.RunOrReviewId,
                JsonSerializer.Serialize(new { request.PrincipalId, request.Actor, request.OperationId, request.AgentId, request.Subject.Fence, request.InputDigest, request.CorrelationId, validUntil }), ct);
        }, ct);
        return new(token, validUntil);
    }

    public async Task<PermitIntrospectionResponse> IntrospectOperationPermitAsync(PermitIntrospectionRequest request, CancellationToken ct)
    {
        if (!AuthorityReady || _mode is AgentStudio.TaskServer.Contracts.TaskServerMode.Maintenance or AgentStudio.TaskServer.Contracts.TaskServerMode.ReadOnly || request.Subject is null || string.IsNullOrWhiteSpace(request.Permit) || request.Permit.Length > 256)
            return new(false, default);
        var result = new PermitIntrospectionResponse(false, default);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var json = await ScalarAsync(connection, "SELECT binding_json FROM operation_permits WHERE token_hash = $hash", ct, transaction,
                ("$hash", OperationsProtocol.Digest(request.Permit))) as string;
            if (json is null) return;
            var binding = JsonSerializer.Deserialize<IssueOperationPermitRequest>(json, OperationsProtocol.Json)!;
            if (binding.PrincipalId != request.PrincipalId || binding.Actor != request.Actor || binding.Subject != request.Subject
                || binding.OperationId != request.OperationId || binding.Version != request.Version || binding.InputDigest != request.InputDigest
                || binding.AgentId != request.AgentId || binding.ResultAudience != request.ResultAudience
                || binding.CorrelationId != request.CorrelationId
                || request.Deadline > binding.Deadline || binding.Deadline <= _clock.GetUtcNow()) return;
            var lease = await ReadLeaseAsync(connection, transaction, binding.Subject.RunOrReviewId, ct);
            var run = await ReadRunAsync(connection, transaction, binding.Subject.RunOrReviewId, ct);
            if (!OperationPermitPolicy.Active(binding.Subject, lease, run, _clock.GetUtcNow())) return;
            result = new(true, new[] { binding.Deadline, new DateTimeOffset(lease!.ExpiresAt, TimeSpan.Zero) }.Min());
        }, ct);
        return result;
    }
}
