using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// One <c>WebApplicationFactory&lt;Program&gt;</c> host for every test of a class
/// (<c>IClassFixture</c>) instead of one boot per test (AGT-3415). A boot costs
/// seconds; AGT-3347 measured 163 s with one host per test against 9 s with a
/// shared host for the same 43 cases. The host boots on first use, so a filtered
/// run that needs no host starts none.
///
/// <para>A test on a shared host <b>may</b>: send requests and use the host's
/// services; create records under names it makes fresh itself (a runner id, a
/// project, a key file named with a new GUID); keep its own files under its own
/// temporary folder.</para>
///
/// <para>A test on a shared host <b>may not</b>: change the host's configuration
/// or the process environment; change a host-wide record that other tests read
/// (a connection, backend settings, a profile); rely on a record it did not create,
/// or on the absence of one whose name it did not make fresh. A name the product
/// fixes, such as the platform's own secret names, is written before it is read,
/// in the same test. A test that needs other settings gets another fixture
/// subclass, not a change to this one.</para>
///
/// <para>Put <see cref="SharedHostTestOrder"/> on the class and run it in
/// reverse and random order: a test that only passes after another one fails there.</para>
/// </summary>
public abstract class SharedHostFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    /// <summary>The fixture's temporary folder; the workspace is inside it.</summary>
    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "shared-host-" + Guid.NewGuid().ToString("N"));

    public string Workspace => Path.Combine(Root, "workspace");

    public WebApplicationFactory<Program> Factory => _factory
        ?? throw new InvalidOperationException("Shared host fixture initialization did not complete.");

    public IServiceProvider Services => Factory.Services;

    public HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>Settings on top of the workspace, visible from the first line of <c>Program.cs</c>.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> Settings() => [];

    public Task InitializeAsync()
    {
        var settings = new Dictionary<string, string?> { ["TaskRepository"] = Workspace };
        foreach (var (key, value) in Settings())
            settings[key] = value;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseTestSettings(settings);
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is { } factory)
            await factory.DisposeAsync();
        TestTempRoot.TryDelete(Root);
    }
}
