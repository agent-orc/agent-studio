using System.Threading.Channels;

namespace AgentStudio.Pipeline;

/// <summary>
/// One queued integration-branch push: everything
/// <see cref="MergeIntoDevelopRunner.PushIntegrationBranchAsync"/> needs to push
/// the freshly merged integration branch (e.g. <c>develop</c>) to <c>origin</c>
/// off the request path.
/// <para>
/// <see cref="ApprovedSha"/> is the exact commit the merge gate released. The
/// push targets that object rather than the branch tip, so a merge that landed
/// on the integration branch while this item waited in the queue cannot reach
/// origin under this card's approval. Legacy fixture calls may omit it; the
/// durable restart backstop requires the SHA recorded by the passed merge step.
/// </para>
/// </summary>
public sealed record IntegrationPushRequest(
    string Project,
    string JobId,
    string JobFolderPath,
    string? WatchPath,
    string IntegrationBranch,
    string? ApprovedSha = null);

/// <summary>
/// Hand-off point that lifts the integration-branch <c>git fetch</c> +
/// <c>git push</c> out of the accepted-integration worker (AGT-1999). It is
/// the merge-step twin of <see cref="AgentStudio.Runner.CompletedPushQueue"/>:
/// the background merge post-step (<see cref="MergeIntoDevelopRunner"/>) drops a
/// released SHA here after merge + gate. <see cref="IntegrationPushWorker"/>
/// drains it and performs the push (with the AGT-1944 environmental retry) on a
/// background thread.
/// <see cref="IntegrationPushBackstopHostedService"/> re-drives a successful
/// merge whose channel item was lost during shutdown from the durable pipeline
/// step facts after restart.
/// <para>
/// Unbounded and single-reader by design: volume is one item per accepted task,
/// and the reader is serialized so two pushes of the same integration branch can
/// never race each other. A deferred push is always still correct because the
/// request pins the exact released SHA.
/// </para>
/// </summary>
public sealed class IntegrationPushQueue
{
    private readonly Channel<IntegrationPushRequest> _channel =
        Channel.CreateUnbounded<IntegrationPushRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<IntegrationPushRequest> Reader => _channel.Reader;

    /// <summary>
    /// Enqueue an integration-branch push. Never blocks. Returns false only if
    /// the channel has been completed (shutdown); the local merge already landed,
    /// so the durable backstop pushes it after restart.
    /// </summary>
    public bool Enqueue(IntegrationPushRequest request) => _channel.Writer.TryWrite(request);
}
