using System.Threading.Channels;

namespace AgentStudio.Security;

/// <summary>
/// Tells the Owner about a new approval request (security model SM-5): one
/// operator-feed event in Studio and, when configured, one push notification.
/// Both carry only the link to the request; deciding still needs the Owner's
/// Studio session and a step-up. Delivery runs off the request path from a
/// bounded queue, and a failed delivery is logged without retry: the request
/// stays visible in Settings &gt; Access either way.
/// </summary>
public sealed class OperatorApprovalNotifier(
    OperatorApprovalOptions options,
    IOperatorApprovalPush push,
    AgentStudio.Bus.AgentMessageBusBridge? bus = null,
    ILogger<OperatorApprovalNotifier>? logger = null) : BackgroundService
{
    private const int Capacity = 256;
    private sealed record Notice(OperatorApprovalPushMessage Message, string Operation);
    private readonly Channel<Notice> _queue = Channel.CreateBounded<Notice>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>Queues the notice for a newly opened request; never blocks the caller.</summary>
    public void Enqueue(OperatorApprovalResponse approval)
    {
        if (!_queue.Writer.TryWrite(new(Message(approval, options), approval.Operation)))
            logger?.LogWarning("Operator approval notice for {ApprovalId} was not queued", approval.Id);
    }

    public void EnqueueBatch(OperatorBatch batch)
        => _queue.Writer.TryWrite(new(new(batch.Id, "Operator batch confirmation requested",
            $"{batch.Actor} asks to run {batch.Class} for {batch.Items.Count} cards. Decide before {batch.ExpiresAt:HH:mm} UTC.",
            $"{options.StudioUrl?.ToString().TrimEnd('/')}/#{batch.Link}"), batch.Class));

    /// <summary>The message both channels carry: what is asked, by whom, until when, and the link.</summary>
    public static OperatorApprovalPushMessage Message(OperatorApprovalResponse approval, OperatorApprovalOptions options)
        => new(approval.Id, "Operator approval requested",
            $"{approval.PrincipalName ?? approval.PrincipalId} asks to run {approval.Operation} ({approval.OperationTitle}). "
            + $"Decide in Studio before {approval.ExpiresAt:HH:mm} UTC.",
            options.Link(approval.Id));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var approval in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var message = approval.Message;
                await DeliverAsync("operator feed", message.ApprovalId, ct => bus is null ? Task.CompletedTask
                    : bus.EmitOperatorApprovalRequestedAsync(message.ApprovalId, approval.Operation, message.Message, message.Url, ct), stoppingToken);
                if (push.Configured)
                    await DeliverAsync("push", message.ApprovalId, ct => push.SendAsync(message, ct), stoppingToken);
            }
        }
        catch (OperationCanceledException exception) when (stoppingToken.IsCancellationRequested)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "OperatorApprovalNotifier: stopped on shutdown");
        }
    }

    private async Task DeliverAsync(string channel, string approvalId, Func<CancellationToken, Task> send, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await send(timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger?.LogWarning(exception, "Operator approval notice for {ApprovalId} did not reach the {Channel}", approvalId, channel);
        }
    }
}

public static class OperatorApprovalServices
{
    /// <summary>The approval link options, the optional push channel, and the notifier that drives both.</summary>
    public static IServiceCollection AddOperatorApprovals(this IServiceCollection services)
    {
        services.AddScoped<OperatorBatchService>();
        services.AddSingleton<AgentStudio.Operator.IOperatorDecisionFactSource, OperatorApprovalFactSource>();
        services.AddSingleton(sp => OperatorApprovalOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient(HttpsOperatorApprovalPush.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IOperatorApprovalPush>(sp => new HttpsOperatorApprovalPush(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpsOperatorApprovalPush.HttpClientName),
            sp.GetRequiredService<OperatorApprovalOptions>()));
        services.AddSingleton(sp => new OperatorApprovalNotifier(
            sp.GetRequiredService<OperatorApprovalOptions>(), sp.GetRequiredService<IOperatorApprovalPush>(),
            sp.GetService<AgentStudio.Bus.AgentMessageBusBridge>(), sp.GetService<ILogger<OperatorApprovalNotifier>>()));
        services.AddHostedService(sp => sp.GetRequiredService<OperatorApprovalNotifier>());
        return services;
    }
}
