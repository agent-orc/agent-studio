extern alias UpdSvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UpdateServiceOptions = UpdSvc::AgentTaskboard.UpdateService.UpdateServiceOptions;
using GitProbe = UpdSvc::AgentTaskboard.UpdateService.GitProbe;
using IGitProbe = UpdSvc::AgentTaskboard.UpdateService.IGitProbe;
using BackendProbe = UpdSvc::AgentTaskboard.UpdateService.BackendProbe;
using IBackendProbe = UpdSvc::AgentTaskboard.UpdateService.IBackendProbe;
using UpdateStatusStore = UpdSvc::AgentTaskboard.UpdateService.UpdateStatusStore;

namespace AgentStudio.Tests;

/// <summary>
/// WebApplicationFactory wrapper around the standalone Update Service so the
/// integration suite can boot the full 9-phase pipeline in-process. The
/// factory wires the real <see cref="UpdSvc::AgentTaskboard.UpdateService.UpdateOrchestrator"/>
/// + <see cref="UpdSvc::AgentTaskboard.UpdateService.UpdateVerifier"/> against
/// a temp <see cref="FakeStableCheckout"/> and the loopback
/// <see cref="FakeBackendHarness"/>. ADR-0031 follow-up.
/// </summary>
public sealed class UpdateServiceTestFactory : WebApplicationFactory<UpdSvc::Program>
{
    private readonly FakeStableCheckout _checkout;
    private readonly FakeBackendHarness _backend;
    private readonly bool _autoRollback;
    private readonly int _doneLingerSeconds;
    private readonly int _healthWaitSeconds;
    private readonly string _mode;
    private readonly bool _requireReleaseManifest;
    private readonly string _frontendUrl;
    private readonly int _frontendWaitSeconds;

    public UpdateServiceTestFactory(
        FakeStableCheckout checkout,
        FakeBackendHarness backend,
        bool autoRollback,
        int doneLingerSeconds = 2,
        int healthWaitSeconds = 10,
        string mode = "scheduled",
        bool requireReleaseManifest = false,
        string? frontendUrl = null,
        int frontendWaitSeconds = 120)
    {
        _checkout = checkout;
        _backend = backend;
        _autoRollback = autoRollback;
        _doneLingerSeconds = doneLingerSeconds;
        _healthWaitSeconds = healthWaitSeconds;
        _mode = mode;
        _requireReleaseManifest = requireReleaseManifest;
        // Default to the fake backend's own listening port so the phase-5
        // frontend port wait is satisfied unless a test deliberately points
        // it somewhere closed.
        _frontendUrl = frontendUrl ?? backend.BaseUrl;
        _frontendWaitSeconds = frontendWaitSeconds;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Bind to dynamic loopback for the in-process TestServer.
                // WebApplicationFactory swaps in TestServer regardless, but
                // we don't want UseUrls to point at the real 5039.
                ["UpdateService:ListenUrl"]          = "http://127.0.0.1:0",
                ["UpdateService:StableCheckoutDir"]  = _checkout.StableDir,
                ["UpdateService:DevspaceDir"]        = _checkout.DevspaceDir,
                ["UpdateService:UpdateScript"]       = "update-stable.sh",
                ["UpdateService:StopScript"]         = "stop-stable.sh",
                ["UpdateService:StartScript"]        = "start-stable.sh",
                ["UpdateService:BashPath"]           = _checkout.BashPath,
                ["UpdateService:BackendUrl"]         = _backend.BaseUrl,
                ["UpdateService:HistoryFile"]        = _checkout.HistoryFile,
                ["UpdateService:RunsDirectory"]      = _checkout.RunsDir,
                ["UpdateService:VersionFile"]        = _checkout.VersionFile,
                ["UpdateService:HealthWaitSeconds"]  = _healthWaitSeconds.ToString(),
                ["UpdateService:DoneLingerSeconds"]  = _doneLingerSeconds.ToString(),
                ["UpdateService:ProbeIntervalSeconds"] = "5",
                ["UpdateService:AutoRollback"]       = _autoRollback ? "true" : "false",
                ["UpdateService:Mode"]               = _mode,
                // The established integration harness exercises the legacy
                // branch-update pipeline. The immutable-release restart drill
                // (AGT-2847) opts in and supplies the candidate manifest and
                // approved tag from the fake checkout.
                ["UpdateService:RequireReleaseManifest"] = _requireReleaseManifest ? "true" : "false",
                ["UpdateService:CandidateManifestFile"] = _checkout.CandidateManifestPath,
                ["UpdateService:ApprovedTagFile"]    = _checkout.ApprovedTagPath,
                ["UpdateService:FrontendUrl"]        = _frontendUrl,
                ["UpdateService:FrontendWaitSeconds"] = _frontendWaitSeconds.ToString(),
            });
        });

        builder.ConfigureServices(services =>
        {
            var options = CreateOptions();

            services.RemoveAll<UpdateServiceOptions>();
            services.AddSingleton(options);

            services.RemoveAll<IGitProbe>();
            services.AddSingleton<IGitProbe>(sp =>
                new GitProbe(options.StableCheckoutDir, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GitProbe>>()));

            services.RemoveAll<IBackendProbe>();
            services.AddSingleton<IBackendProbe>(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackendProbe>>();
                return new BackendProbe(http, options.BackendUrl, options.BackendClientId, logger);
            });

            services.RemoveAll<UpdateStatusStore>();
            services.AddSingleton(sp =>
            {
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UpdateStatusStore>>();
                var git = sp.GetRequiredService<IGitProbe>();
                string ReadVersion()
                {
                    if (!File.Exists(options.VersionFile)) return "unknown";
                    foreach (var line in File.ReadAllLines(options.VersionFile))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0 && !trimmed.StartsWith("#")) return trimmed;
                    }
                    return "unknown";
                }
                return new UpdateStatusStore(options.HistoryFile, git.HeadShort(), ReadVersion, logger, options.Mode);
            });
        });

        return base.CreateHost(builder);
    }

    private UpdateServiceOptions CreateOptions() => new()
    {
        ListenUrl = "http://127.0.0.1:0",
        StableCheckoutDir = _checkout.StableDir,
        DevspaceDir = _checkout.DevspaceDir,
        UpdateScript = "update-stable.sh",
        StopScript = "stop-stable.sh",
        StartScript = "start-stable.sh",
        BashPath = _checkout.BashPath,
        BackendUrl = _backend.BaseUrl,
        HistoryFile = _checkout.HistoryFile,
        RunsDirectory = _checkout.RunsDir,
        VersionFile = _checkout.VersionFile,
        HealthWaitSeconds = _healthWaitSeconds,
        DoneLingerSeconds = _doneLingerSeconds,
        ProbeIntervalSeconds = 5,
        AutoRollback = _autoRollback,
        Mode = _mode,
        RequireReleaseManifest = _requireReleaseManifest,
        CandidateManifestFile = _checkout.CandidateManifestPath,
        ApprovedTagFile = _checkout.ApprovedTagPath,
        FrontendUrl = _frontendUrl,
        FrontendWaitSeconds = _frontendWaitSeconds,
        TriggerToken = null,
    };
}
