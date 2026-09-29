using AgentStudio.Operations.Contracts;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public static class OperationPermitPolicy
{
    public static bool Active(OperationSubject subject, LeaseDto? lease, RunDto? run, DateTimeOffset now) =>
        lease is { Status: "active" } && run is { Status: "running" }
        && lease.TaskId == subject.TaskId && run.TaskId == subject.TaskId
        && lease.RunId == subject.RunOrReviewId && lease.Fence == subject.Fence && run.Fence == subject.Fence
        && lease.ExpiresAt > now.UtcDateTime;

    public static bool Valid(IssueOperationPermitRequest request, DateTimeOffset now) =>
        request.Subject is not null && request.Subject.Fence > 0
        && OperationCatalogue.Find(request.OperationId, request.Version) is not null
        && request.ResultAudience == OperationsProtocol.Audience
        && request.Deadline > now && request.Deadline <= now.AddMinutes(5)
        && new[] { request.PrincipalId, request.Actor, request.Subject.TaskId, request.Subject.RunOrReviewId, request.AgentId, request.CorrelationId }
            .All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl))
        && request.InputDigest is { Length: 64 } && request.InputDigest.All(Uri.IsHexDigit);
}
