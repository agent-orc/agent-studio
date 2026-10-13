namespace AgentStudio.Diagnostics;

/// <summary>
/// Times one hosted service's start and stop for the host timeline (AGT-3107).
/// It derives from <see cref="BackgroundService"/> only so the host keeps
/// observing the wrapped service's <see cref="BackgroundService.ExecuteTask"/>:
/// a faulted background loop is still logged and still follows
/// <see cref="HostOptions.BackgroundServiceExceptionBehavior"/>. Start and stop
/// calls, their order and their exceptions pass through unchanged. The wrapped
/// instance stays owned (and disposed) by the service provider.
/// </summary>
public sealed class HostTimelineHostedService(IHostedService inner, HostTimelineRecorder recorder)
    : BackgroundService, IHostedLifecycleService
{
    public IHostedService Inner => inner;

    public override Task? ExecuteTask => (inner as BackgroundService)?.ExecuteTask;

    /// <summary>Returns the timed service, or <paramref name="service"/> itself when it is not wrapped.</summary>
    public static IHostedService Unwrap(IHostedService service)
        => service is HostTimelineHostedService timed ? timed.Inner : service;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = recorder.GetTimestamp();
        try
        {
            await inner.StartAsync(cancellationToken);
        }
        finally
        {
            recorder.RecordStart(Name, started);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var started = recorder.GetTimestamp();
        try
        {
            await inner.StopAsync(cancellationToken);
        }
        finally
        {
            recorder.RecordStop(Name, started);
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken)
        => inner is IHostedLifecycleService lifecycle ? lifecycle.StartingAsync(cancellationToken) : Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
        => inner is IHostedLifecycleService lifecycle ? lifecycle.StartedAsync(cancellationToken) : Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken)
        => inner is IHostedLifecycleService lifecycle ? lifecycle.StoppingAsync(cancellationToken) : Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
        => inner is IHostedLifecycleService lifecycle ? lifecycle.StoppedAsync(cancellationToken) : Task.CompletedTask;

    // Never invoked: StartAsync is overridden and forwards to the wrapped service.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    // The provider owns and disposes the wrapped service.
    public override void Dispose() { GC.SuppressFinalize(this); }

    private string Name => inner.GetType().FullName ?? inner.GetType().Name;

    /// <summary>
    /// Replaces every hosted-service registration with a timed decorator at the
    /// same position, so start order and stop order stay the same. The original
    /// registration moves to a private key and keeps its lifetime and disposal.
    /// </summary>
    public static void DecorateAll(IServiceCollection services, HostTimelineRecorder recorder)
    {
        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType != typeof(IHostedService) || descriptor.IsKeyedService) continue;

            var key = new InnerKey(index);
            services.Add(Rekey(descriptor, key));
            services[index] = new ServiceDescriptor(
                typeof(IHostedService),
                provider => new HostTimelineHostedService(
                    provider.GetRequiredKeyedService<IHostedService>(key),
                    recorder),
                descriptor.Lifetime);
        }
    }

    private static ServiceDescriptor Rekey(ServiceDescriptor descriptor, InnerKey key)
    {
        if (descriptor.ImplementationInstance is { } instance)
            return new ServiceDescriptor(typeof(IHostedService), key, instance);
        if (descriptor.ImplementationFactory is { } factory)
            return new ServiceDescriptor(typeof(IHostedService), key, (provider, _) => factory(provider), descriptor.Lifetime);
        return new ServiceDescriptor(typeof(IHostedService), key, descriptor.ImplementationType!, descriptor.Lifetime);
    }

    private sealed record InnerKey(int Index);
}

/// <summary>
/// Default service provider factory that applies <see cref="HostTimelineHostedService"/>
/// as the last step before the container is built, after test fixtures have
/// added or removed hosted services. Used only while the timeline is enabled.
/// </summary>
public sealed class HostTimelineServiceProviderFactory(
    HostTimelineRecorder recorder,
    ServiceProviderOptions options) : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services)
    {
        HostTimelineHostedService.DecorateAll(services, recorder);
        services.AddSingleton(_ => new HostTimelineDisposalProbe(recorder));
        return services;
    }

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
        => containerBuilder.BuildServiceProvider(options);
}
