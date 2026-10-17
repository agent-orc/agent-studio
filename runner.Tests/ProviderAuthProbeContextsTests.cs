using AgentRunner;
using AgentStudio.CliHosting;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderAuthProbeContextsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "provider-auth-probe-contexts-tests",
        Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Probe_reacquires_its_context_after_a_credential_generation_change()
    {
        var userHome = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(userHome, ".codex"));
        var acquisitions = 0;
        var logs = new List<string>();
        var contexts = new ProviderAuthProbeContexts(
            provider =>
            {
                acquisitions++;
                return TaskCleanContextStore.Acquire(
                    provider, ProviderAuthProbe.CleanContextIdentity, userHome,
                    Path.Combine(_root, "store"), nowUtc: _now);
            },
            provider => ProviderCredentialMonitor.Inspect(
                provider, userHome, new Dictionary<string, string?>()).CredentialGeneration,
            logs.Add);

        var beforeLogin = contexts.Get("codex");
        Assert.NotNull(beforeLogin);
        Assert.False(File.Exists(Path.Combine(beforeLogin.HomePath, "auth.json")));
        _now = _now.AddMinutes(5);
        Assert.Same(beforeLogin, contexts.Get("codex"));

        var sourceAuth = Path.Combine(userHome, ".codex", "auth.json");
        File.WriteAllText(sourceAuth, "{}");
        _now = _now.AddDays(2);
        File.SetLastWriteTimeUtc(sourceAuth, _now.UtcDateTime);

        var afterLogin = contexts.Get("codex");

        Assert.NotNull(afterLogin);
        Assert.NotSame(beforeLogin, afterLogin);
        Assert.Equal(beforeLogin.HomePath, afterLogin.HomePath);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(afterLogin.HomePath, "auth.json")));
        Assert.Same(afterLogin, contexts.Get("codex"));
        Assert.Equal(2, acquisitions);
        Assert.Empty(logs);
    }

    [Fact]
    public void Probe_without_a_relocatable_home_or_with_a_failed_acquire_runs_unisolated()
    {
        var logs = new List<string>();
        var contexts = new ProviderAuthProbeContexts(
            _ => throw new InvalidDataException("foreign home"),
            _ => null,
            logs.Add);

        Assert.Null(contexts.Get("gemini"));
        Assert.Empty(logs);
        Assert.Null(contexts.Get("codex"));
        Assert.Contains(logs, line => line.Contains("foreign home", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* test cleanup is best-effort */ }
    }
}
