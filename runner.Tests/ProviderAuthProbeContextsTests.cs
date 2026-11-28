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

    [Fact]
    public void Each_probe_gets_a_new_lease_for_the_same_home_and_links_a_later_login()
    {
        var userHome = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(userHome, ".codex"));
        var logs = new List<string>();
        var contexts = new ProviderAuthProbeContexts(
            provider => TaskCleanContextStore.Acquire(
                provider, ProviderAuthProbe.CleanContextIdentity, userHome,
                Path.Combine(_root, "store"), runnerManaged: true),
            logs.Add);

        using var beforeLogin = contexts.Acquire("codex");
        Assert.NotNull(beforeLogin);
        Assert.False(File.Exists(Path.Combine(beforeLogin.HomePath, "auth.json")));
        using var overlapping = contexts.Acquire("codex");
        Assert.NotNull(overlapping);
        Assert.NotSame(beforeLogin, overlapping);
        Assert.Equal(beforeLogin.HomePath, overlapping.HomePath);
        beforeLogin.Dispose();

        var sourceAuth = Path.Combine(userHome, ".codex", "auth.json");
        File.WriteAllText(sourceAuth, "{}");

        using var afterLogin = contexts.Acquire("codex");
        Assert.NotNull(afterLogin);
        Assert.True(afterLogin.Reused);
        Assert.Equal(beforeLogin.HomePath, afterLogin.HomePath);
        Assert.True(File.Exists(Path.Combine(afterLogin.HomePath, "auth.json")));
        Assert.Empty(logs);
    }

    [Fact]
    public void Probe_without_a_relocatable_home_or_with_a_failed_acquire_runs_unisolated()
    {
        var logs = new List<string>();
        var contexts = new ProviderAuthProbeContexts(
            _ => throw new InvalidDataException("foreign home"),
            logs.Add);

        Assert.Null(contexts.Acquire("gemini"));
        Assert.Empty(logs);
        Assert.Null(contexts.Acquire("codex"));
        Assert.Contains(logs, line => line.Contains("foreign home", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* test cleanup is best-effort */ }
    }
}
