using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the F17 surfaces against regression:
///
///   1. Boot prompt: every section a downstream caller might assume exists
///      (USER PREFERENCES, AVAILABLE TOOLS) is rendered, and when a
///      watched project is the agent-orchestrator codebase itself it gets
///      a self-modification warning.
///   2. Per-turn prompt: a CURRENT USER PREFERENCES block is emitted
///      using the live default-cli/model for the chatting client id,
///      superseding the boot-time block.
///   3. The boot fallback resolves cli/model from the bootstrap identity
///      when no record has set defaults yet, so the block is well-formed
///      on a fresh install.
///
/// Structural unit tests are necessary, not sufficient (per ADR-0007);
/// the live behavioural check belongs in the Playwright suite. These
/// tests stop wording drift between the two emissions and stop the
/// "no tool inventory" regression that produced the user-reported
/// "you have to do it manually in the UI" answer.
/// </summary>
public class OrchestratorPromptUserPreferencesTests : IDisposable
{
    private readonly string _root;
    private readonly string _watchPath;

    public OrchestratorPromptUserPreferencesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ats-f17-prompt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _watchPath = Path.Combine(_root, "agent-taskboard-dev");
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private IConfiguration BuildConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["PromptTemplates:RuntimePath"] = FindPromptRoot(),
                ["WatchPaths:0:Name"] = "agent-taskboard",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath
            })
            .Build();
    }

    private static string FindPromptRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "runtime");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate prompts/runtime from test base directory.");
    }

    private (ClientIdentityStore store, TaskScannerService scanner, IConfiguration config) BuildEnv()
    {
        var config = BuildConfig();
        var store = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        store.EnsureLoaded();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return (store, scanner, config);
    }

    [Fact]
    public void BootPrompt_RendersUserPreferencesAndToolInventory()
    {
        var (store, scanner, config) = BuildEnv();
        // Set a non-default model on the bootstrap identity so we can prove
        // the boot prompt reads from the identity, not a hardcoded literal.
        store.SetDefaults(DefaultClientIdentity.Id, defaultCliType: "codex", defaultModel: "gpt-5");

        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            new GlobalOrchestratorSessionStore(config, NullLogger<GlobalOrchestratorSessionStore>.Instance),
            new StubOrchestratorRunner(),
            scanner,
            config,
            store);

        var prompt = bootstrap.BuildBootPrompt();

        Assert.Contains("=== USER PREFERENCES ===", prompt);
        Assert.Contains("Default CLI: codex", prompt);
        Assert.Contains("Default model: gpt-5", prompt);

        Assert.Contains("=== AVAILABLE TOOLS ===", prompt);
        // Concrete API hints the operator-reported regression required.
        Assert.Contains("POST /api/tasks", prompt);
        Assert.Contains("X-Client-Id", prompt);
        Assert.Contains("Do NOT tell them they have to do it manually in the UI", prompt);
    }

    [Fact]
    public void BootPrompt_FallsBackToHardcodedDefaultsWhenIdentityHasNone()
    {
        var (_, scanner, config) = BuildEnv();
        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            new GlobalOrchestratorSessionStore(config, NullLogger<GlobalOrchestratorSessionStore>.Instance),
            new StubOrchestratorRunner(),
            scanner,
            config,
            identityStore: null);

        var prompt = bootstrap.BuildBootPrompt();

        // No identity store - the block must still be present with the
        // historic claude / opus-4-7 pair so a fresh install boots cleanly.
        Assert.Contains("=== USER PREFERENCES ===", prompt);
        Assert.Contains("Default CLI: claude", prompt);
        Assert.Contains("Default model: " + OrchestratorRunner.DefaultModel, prompt);
    }

    [Fact]
    public void BootPrompt_AnnotatesSelfModificationProjects()
    {
        var (_, scanner, config) = BuildEnv();
        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            new GlobalOrchestratorSessionStore(config, NullLogger<GlobalOrchestratorSessionStore>.Instance),
            new StubOrchestratorRunner(),
            scanner,
            config);

        var prompt = bootstrap.BuildBootPrompt();
        Assert.Contains("this project is the tool itself", prompt);
    }

    [Fact]
    public void IsSelfModificationTarget_OnlyFlagsAgentTaskboardCheckouts()
    {
        var dev = new WatchPathEntry { Name = "agent-taskboard", Path = "/x/agent-taskboard-dev/.orchestrator", RootPath = "/x/agent-taskboard-dev" };
        var stable = new WatchPathEntry { Name = "agent-taskboard", Path = "/x/agent-taskboard-stable/.orchestrator", RootPath = "/x/agent-taskboard-stable" };
        var other = new WatchPathEntry { Name = "runbook", Path = "/x/runbook/.orchestrator", RootPath = "/x/runbook" };

        Assert.True(GlobalOrchestratorBootstrap.IsSelfModificationTarget(dev));
        Assert.True(GlobalOrchestratorBootstrap.IsSelfModificationTarget(stable));
        Assert.False(GlobalOrchestratorBootstrap.IsSelfModificationTarget(other));
    }

    [Fact]
    public void PerTurnUserPreferences_ResolvesPerClientDefaults()
    {
        var (store, _, _) = BuildEnv();
        var alice = store.Register(new RegisterClientRequest { DisplayName = "Alice" });
        store.SetDefaults(alice.Id, defaultCliType: "claude", defaultModel: "claude-opus-4-7");

        var sb = new StringBuilder();
        OrchestratorChatService.AppendCurrentUserPreferences(sb, alice.Id, store);
        var rendered = sb.ToString();

        Assert.Contains("=== CURRENT USER PREFERENCES ===", rendered);
        Assert.Contains("Default CLI: claude", rendered);
        Assert.Contains("Default model: claude-opus-4-7", rendered);
        Assert.Contains($"X-Client-Id when calling /api/* on the user's behalf): {alice.Id}", rendered);
        // Must say it supersedes the boot block so the model doesn't keep
        // the stale boot-time pair around.
        Assert.Contains("supersede", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PerTurnUserPreferences_FallsBackToBootDefaultsWhenClientHasNone()
    {
        var (store, _, _) = BuildEnv();
        // Identity exists but no defaults set; bootstrap identity has codex/gpt-5.
        var bob = store.Register(new RegisterClientRequest { DisplayName = "Bob" });
        store.SetDefaults(DefaultClientIdentity.Id, defaultCliType: "codex", defaultModel: "gpt-5");

        var sb = new StringBuilder();
        OrchestratorChatService.AppendCurrentUserPreferences(sb, bob.Id, store);
        var rendered = sb.ToString();

        Assert.Contains("Default CLI: codex", rendered);
        Assert.Contains("Default model: gpt-5", rendered);
    }

    [Fact]
    public void PerTurnUserPreferences_NullClientId_StillRendersBlock()
    {
        var (store, _, _) = BuildEnv();

        var sb = new StringBuilder();
        OrchestratorChatService.AppendCurrentUserPreferences(sb, clientId: null, store);
        var rendered = sb.ToString();

        Assert.Contains("=== CURRENT USER PREFERENCES ===", rendered);
        // Active client id line is omitted when there's no client to name.
        Assert.DoesNotContain("Active client id", rendered);
        // Fallback to hardcoded defaults when nothing is set anywhere.
        Assert.Contains("Default CLI: claude", rendered);
    }

    /// <summary>Stub runner that no test in this file actually invokes.</summary>
    private sealed class StubOrchestratorRunner : OrchestratorRunner
    {
        public StubOrchestratorRunner()
            : base(logger: NullLogger<OrchestratorRunner>.Instance,
                parsers: null, modelRegistry: null, oneShotRegistry: null)
        {
        }
    }
}
