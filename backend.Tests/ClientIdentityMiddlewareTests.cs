using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the X-Client-Id boundary: writes from unknown clients return
/// 401 client-unknown; writes from known clients pass through; reads
/// stay open even when the header is missing or unknown.
/// </summary>
public class ClientIdentityMiddlewareTests : IDisposable
{
    private readonly string _root;

    public ClientIdentityMiddlewareTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-mw-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private (ClientIdentityMiddleware Mw, ClientIdentityStore Store, BoolBox NextCalled) BuildMiddleware()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Environment:DefaultIdentityName"] = "Mw Default"
            }).Build();
        var store = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        store.EnsureLoaded();
        var nextCalled = new BoolBox();
        RequestDelegate next = ctx =>
        {
            nextCalled.Value = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };
        var mw = new ClientIdentityMiddleware(next, store, NullLogger<ClientIdentityMiddleware>.Instance);
        return (mw, store, nextCalled);
    }

    private static HttpContext BuildContext(string method, string path, string? clientId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (clientId != null) ctx.Request.Headers["X-Client-Id"] = new StringValues(clientId);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task Mutation_WithoutHeader_Returns401()
    {
        var (mw, _, called) = BuildMiddleware();
        var ctx = BuildContext("POST", "/api/tasks", clientId: null);
        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.False(called.Value);
        var body = await ReadBody(ctx);
        Assert.Contains("client-unknown", body);
    }

    [Fact]
    public async Task Mutation_WithUnknownClient_Returns401()
    {
        var (mw, _, called) = BuildMiddleware();
        var ctx = BuildContext("POST", "/api/tasks", clientId: "ghost-client");
        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.False(called.Value);
    }

    [Fact]
    public async Task Mutation_WithKnownClient_PassesThrough()
    {
        var (mw, store, called) = BuildMiddleware();
        var registered = store.Register(new RegisterClientRequest { DisplayName = "Tester" });
        var ctx = BuildContext("POST", "/api/tasks", clientId: registered.Id);
        await mw.InvokeAsync(ctx);

        Assert.True(called.Value);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(registered.Id, ctx.Items["ClientId"]);
    }

    [Fact]
    public async Task Mutation_WithDefaultIdentity_PassesThrough()
    {
        var (mw, _, called) = BuildMiddleware();
        var ctx = BuildContext("PUT", "/api/tasks/foo/title", clientId: DefaultClientIdentity.Id);
        await mw.InvokeAsync(ctx);
        Assert.True(called.Value);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Read_WithoutHeader_PassesThrough()
    {
        var (mw, _, called) = BuildMiddleware();
        var ctx = BuildContext("GET", "/api/tasks", clientId: null);
        await mw.InvokeAsync(ctx);
        Assert.True(called.Value);
    }

    [Fact]
    public async Task Read_WithUnknownClient_PassesThrough()
    {
        var (mw, _, called) = BuildMiddleware();
        var ctx = BuildContext("GET", "/api/tasks/grouped", clientId: "ghost");
        await mw.InvokeAsync(ctx);
        Assert.True(called.Value);
    }

    [Fact]
    public async Task RegistrationEndpoint_StaysOpenForAnonymousWrites()
    {
        var (mw, _, called) = BuildMiddleware();
        var ctx = BuildContext("POST", "/api/clients/register", clientId: null);
        await mw.InvokeAsync(ctx);
        Assert.True(called.Value);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task PullGateMutation_UsesItsServiceCredentialWithoutClientHeader()
    {
        var (mw, _, called) = BuildMiddleware();
        var ctx = BuildContext("POST", "/api/pull-gates/register", clientId: null);
        await mw.InvokeAsync(ctx);

        Assert.True(called.Value);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task NonApiPath_PassesThrough()
    {
        var (mw, _, called) = BuildMiddleware();
        var ctx = BuildContext("POST", "/hubs/jobs/negotiate", clientId: null);
        await mw.InvokeAsync(ctx);
        Assert.True(called.Value);
    }

    private static async Task<string> ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private class BoolBox { public bool Value; }
}
