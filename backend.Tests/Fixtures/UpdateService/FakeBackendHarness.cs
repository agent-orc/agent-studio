using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests;

/// <summary>
/// Hosted-in-process fake of the main backend, bound to a dynamic loopback
/// port. The Update Service's <see cref="UpdSvc::AgentTaskboard.UpdateService.BackendProbe"/>
/// connects to this via plain HTTP, so every probe + verifier path the
/// orchestrator runs is exercised end-to-end. Behaviour toggles
/// (<see cref="HealthzReturns503"/>, <see cref="ProbeReturns503"/>) let the
/// integration suite drive the failure-injection / auto-rollback / manual-
/// rollback scenarios required by the ADR-0031 follow-up task.
/// </summary>
public sealed class FakeBackendHarness : IAsyncDisposable
{
    private WebApplication? _app;

    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public bool HealthzReturns503 { get; set; }
    /// <summary>
    /// When &gt; 0, /healthz returns 503 until this many seconds have passed
    /// since <see cref="StartAsync"/>, then 200. Simulates a slow-but-alive
    /// cold compile so the integration suite can prove the extended restart
    /// health-wait budget treats it as success rather than a false failure.
    /// </summary>
    public double HealthzDelaySeconds { get; set; }
    private DateTime _startedAtUtc;
    public bool ProbeReturns503 { get; set; }
    /// <summary>
    /// When &gt; 0, the first N calls to <c>/api/_internal/probe</c> return
    /// 503 and subsequent calls return 200. Lets the auto-rollback positive
    /// case fail the forward db-touch then succeed during rollback's
    /// re-run of the matrix, without needing a manual flip between the two.
    /// </summary>
    public int ProbeFailFirstN { get; set; }
    public int ProbeCallCount;
    /// <summary>
    /// F58: when &gt; 0, the first N calls to <c>/api/tasks/grouped</c> return
    /// 503 and subsequent calls return 200. Lets the integration suite verify
    /// that the verifier's retry logic recovers from a cold-start delay.
    /// </summary>
    public int JobsGroupedFailFirstN { get; set; }
    public int JobsGroupedCallCount;
    /// <summary>
    /// AGT-2847 restart drill: when set, <c>/api/system/version</c> serves the
    /// JSON in this file verbatim instead of the fixed legacy shape. The fake
    /// start script writes it the same way <c>BuildIdentity.Load</c> picks its
    /// manifest (ATP_BUILD_MANIFEST first, the checkout copy second), so the
    /// suite can observe which identity a restarted backend would report.
    /// </summary>
    public string? RuntimeIdentityFile { get; set; }

    public Dictionary<string, string> ProjectModes { get; } = new()
    {
        ["agent-taskboard"] = "auto-continuous",
    };
    public List<(string Project, string Mode)> ModeWrites { get; } = new();

    public async Task StartAsync()
    {
        _startedAtUtc = DateTime.UtcNow;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0);
        });
        var app = builder.Build();

        app.MapGet("/healthz", () =>
        {
            if (HealthzReturns503)
                return Results.Json("err", statusCode: 503);
            if (HealthzDelaySeconds > 0 && (DateTime.UtcNow - _startedAtUtc).TotalSeconds < HealthzDelaySeconds)
                return Results.Json("still compiling", statusCode: 503);
            return Results.Text("\"ok\"", "application/json");
        });
        app.MapGet("/api/system/version", () =>
        {
            var identityFile = RuntimeIdentityFile;
            if (!string.IsNullOrEmpty(identityFile) && File.Exists(identityFile))
                return Results.Text(File.ReadAllText(identityFile), "application/json");
            return Results.Json(new
            {
                version = "2026.07.11-1200+aaaaaaa",
                commit = "aaaaaaa",
                deployedAt = "2026-07-11T12:00:00Z"
            });
        });

        app.MapGet("/api/runner/status", () =>
        {
            var projects = ProjectModes.ToDictionary(
                kv => kv.Key,
                kv => (object)new { mode = kv.Value });
            return Results.Json(new { projects });
        });

        app.MapGet("/api/tasks/grouped", () =>
        {
            var call = System.Threading.Interlocked.Increment(ref JobsGroupedCallCount);
            if (JobsGroupedFailFirstN > 0 && call <= JobsGroupedFailFirstN)
                return Results.Json(new { error = "cold start" }, statusCode: 503);
            return Results.Json(new
            {
                preparation = Array.Empty<object>(),
                ready = Array.Empty<object>(),
                progress = Array.Empty<object>(),
            });
        });

        app.MapGet("/api/tasks", () => Results.Json(new[] { new { id = "demo" } }));
        app.MapGet("/api/clients", () => Results.Json(new[] { new { id = "fake-client" } }));
        app.MapGet("/api/cli/quota", () => Results.Json(new { ok = true }));

        app.MapPost("/api/_internal/probe", async (HttpContext ctx) =>
        {
            var call = System.Threading.Interlocked.Increment(ref ProbeCallCount);
            if (ProbeReturns503)
                return Results.Json(new { error = "probe disabled" }, statusCode: 503);
            if (ProbeFailFirstN > 0 && call <= ProbeFailFirstN)
                return Results.Json(new { error = "probe transient" }, statusCode: 503);
            // Echo the request body verbatim so the verifier's sentinel check
            // round-trips. ADR-0031 phase-6 db-touch requires the body to
            // contain the run-scoped sentinel value.
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(body);
                return Results.Json(doc.RootElement.Clone());
            }
            catch
            {
                return Results.Json(new { echoed = body });
            }
        });

        app.MapPut("/api/runner/{project}/mode", async (string project, HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var mode = doc.RootElement.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
            var name = Uri.UnescapeDataString(project);
            ProjectModes[name] = mode;
            ModeWrites.Add((name, mode));
            return Results.Ok();
        });

        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()!.Addresses;
        var first = addresses.First();
        Port = new Uri(first).Port;
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
        }
    }
}
