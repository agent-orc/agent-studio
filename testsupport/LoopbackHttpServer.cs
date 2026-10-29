using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.TestSupport;

/// <summary>An HTTP test server whose ephemeral port stays bound for its entire lifetime.</summary>
public sealed class LoopbackHttpServer : IDisposable, IAsyncDisposable
{
    private readonly WebApplication _app;
    private bool _disposed;

    public LoopbackHttpServer(RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.Run(handler);
        _app.StartAsync().GetAwaiter().GetResult();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        BaseUrl = addresses?.Single() ?? throw new InvalidOperationException("Loopback server has no bound address.");
    }

    public string BaseUrl { get; }
    public int Port => new Uri(BaseUrl).Port;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _app.DisposeAsync();
    }
}
