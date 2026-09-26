using AgentStudio.Cli;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CliFallbackPreferenceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fallback-preference-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider _clock = new(new DateTime(2026, 9, 18, 20, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Preference_IsRuntimePersistent_AndExpiresAtReset()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        var reset = _clock.GetUtcNow().UtcDateTime.AddHours(2);
        var service = new CliFallbackPreferenceService(config, _clock);

        Assert.True(service.Set(CliTypes.Claude, true, reset).Active);
        Assert.True(new CliFallbackPreferenceService(config, _clock).Get(CliTypes.Claude).Active);

        _clock.Advance(TimeSpan.FromHours(2));
        var expired = service.Get(CliTypes.Claude);
        Assert.False(expired.Active);
        Assert.Equal(reset, expired.ExpiresAt);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class MutableTimeProvider(DateTime now) : TimeProvider
    {
        private DateTime _now = now;
        public override DateTimeOffset GetUtcNow() => new(_now);
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
