using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// One host for every test of a class (AGT-3416).
///
/// <para>A <see cref="TestHostFactory"/> boot costs about 90 seconds of summed
/// time on a loaded gate, and the endpoint classes that built one per test
/// spent most of their minutes there. A derived fixture names the one
/// configuration its class needs in <see cref="ConfigureHost"/> and the class
/// takes it as an <c>IClassFixture</c>: xUnit starts the host before the first
/// test of the class and stops it after the last.</para>
///
/// <para>What one test leaves behind, the next one sees. The class constructor
/// therefore returns the shared state to what a fresh host showed, through a
/// reset the fixture defines for exactly the state its class reads. A test
/// that needs a different configuration does not belong in the class; it
/// goes to a class of its own.</para>
/// </summary>
public abstract class SharedTestHost : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    protected SharedTestHost(string prefix)
    {
        Root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Temp folder for the host's workspace; removed with the host.</summary>
    public string Root { get; }

    public WebApplicationFactory<Program> Factory => _factory
        ?? throw new InvalidOperationException("The shared host has not started.");

    public IServiceProvider Services => Factory.Services;

    /// <summary>The one configuration every test of the class runs against.</summary>
    protected abstract void ConfigureHost(IWebHostBuilder builder);

    public virtual Task InitializeAsync()
    {
        _factory = new TestHostFactory().WithWebHostBuilder(ConfigureHost);
        // Start the host here, so a boot failure fails the fixture rather than
        // whichever test happens to run first.
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public virtual async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        TestTempRoot.TryDelete(Root);
    }
}
