using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BreakGlassWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "break-glass-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly AccessSecurityStore _store;
    private readonly string _token;
    private readonly string _agent;
    private readonly string _session;
    private readonly string _factor;

    public BreakGlassWindowTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Secrets:KeyFile"] = _root + ".kek",
            ["Security:Profile"] = "networked",
        }).Build();
        _store = new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var owner = _store.Bootstrap(new BootstrapRequest("sample.owner", "correct horse battery staple!", null));
        _token = owner.SessionToken;
        var human = _store.AuthenticateSession(_token)!;
        _factor = _store.BeginTotpEnrollment(human);
        _store.ConfirmTotpEnrollment(human, SecondFactorCodes.CurrentTotp(_factor, _clock.GetUtcNow().UtcDateTime));
        _agent = _store.CreateOperatorAgent(new CreateOperatorAgentRequest("operator", null), owner.User.Id).Id;
        _session = _store.StartOperatorSession(_agent, new StartOperatorSessionRequest(null, null), owner.User.Id).Session.Id;
    }

    [Fact]
    public void Opening_requires_sealed_credential_and_fresh_second_factor()
    {
        var owner = _store.AuthenticateSession(_token)!;
        Assert.Throws<SecurityOperationException>(() => _store.OpenBreakGlassWindow(owner, "missing", [_session], 2, "PROJ-002"));
        _store.VerifyStepUp(owner, FreshCode());
        var credential = _store.IssueBreakGlassCredential(owner);
        var noFactor = Assert.Throws<SecurityOperationException>(() =>
            _store.OpenBreakGlassWindow(owner, credential, [_session], 2, "PROJ-002"));
        Assert.Equal("step-up-required", noFactor.Code);
        _store.VerifyStepUp(owner, FreshCode());
        Assert.Throws<SecurityOperationException>(() => _store.OpenBreakGlassWindow(owner, "wrong", [_session], 2, "PROJ-002"));
    }

    [Fact]
    public void Named_session_loses_elevation_at_the_two_hour_limit()
    {
        var owner = _store.AuthenticateSession(_token)!;
        _store.VerifyStepUp(owner, FreshCode());
        var credential = _store.IssueBreakGlassCredential(owner);
        _store.VerifyStepUp(owner, FreshCode());
        var window = _store.OpenBreakGlassWindow(owner, credential, [_session], 2, "PROJ-002");
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddHours(2), window.ExpiresAt);
        Assert.Equal(window.Id, _store.ActiveBreakGlassWindow(_session)?.Id);
        _store.VerifyStepUp(owner, FreshCode());
        Assert.Throws<SecurityOperationException>(() => _store.OpenBreakGlassWindow(owner, credential, [_session], 1, "PROJ-002"));
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(_store.ActiveBreakGlassWindow(_session));
        Assert.Throws<SecurityOperationException>(() => _store.OpenBreakGlassWindow(owner, credential, [_session], 3, "PROJ-002"));
    }

    [Fact]
    public void Closing_is_idempotent_and_has_a_separate_host_journal()
    {
        var owner = _store.AuthenticateSession(_token)!;
        _store.VerifyStepUp(owner, FreshCode());
        var credential = _store.IssueBreakGlassCredential(owner);
        _store.VerifyStepUp(owner, FreshCode());
        var window = _store.OpenBreakGlassWindow(owner, credential, [_session], 1, "PROJ-002");
        _store.CloseBreakGlassWindow(window.Id, $"human:{owner.User.Id}");
        _store.CloseBreakGlassWindow(window.Id, $"human:{owner.User.Id}");
        Assert.Null(_store.ActiveBreakGlassWindow(_session));
        Assert.Equal(2, _store.BreakGlassEntries(window.Id).Count);
        Assert.Equal(2, File.ReadLines(Path.Combine(_store.SecurityFolder, AccessSecurityStore.BreakGlassJournalFileName)).Count());
    }

    [Fact]
    public void Closing_creates_exactly_one_review_card_listing_audit_entries()
    {
        var owner = _store.AuthenticateSession(_token)!;
        _store.VerifyStepUp(owner, FreshCode());
        var credential = _store.IssueBreakGlassCredential(owner);
        _store.VerifyStepUp(owner, FreshCode());
        var window = _store.OpenBreakGlassWindow(owner, credential, [_session], 1, "PROJ-002");
        _store.AppendBreakGlassRequestAudit(window, "operator-agent:test/session:test", "POST /api/tasks/sample",
            "ok", "sample", "sha256:test", 200);
        var cards = new RecordingCards();
        var service = new BreakGlassWindowService(_store, cards, new RecordingAlerts(),
            new ControlPlaneAuditSinkOptions(ControlPlaneAuditSinkOptions.Https, new Uri("https://sink.example.test"), null, null), _clock,
            NullLogger<BreakGlassWindowService>.Instance);
        var first = service.Close(window.Id, $"human:{owner.User.Id}");
        var second = service.Close(window.Id, $"human:{owner.User.Id}");
        Assert.Equal(first.ReviewCardId, second.ReviewCardId);
        Assert.Single(cards.Created);
        Assert.Contains("POST /api/tasks/sample", cards.Created[0].Prompt);
        Assert.Contains("break-glass.open", cards.Created[0].Prompt);
        Assert.Contains("break-glass.close", cards.Created[0].Prompt);
        Assert.Contains("Host journal: matches", cards.Created[0].Prompt);
    }

    [Fact]
    public void Offline_host_entries_need_the_factor_and_stop_at_expiry()
    {
        var owner = _store.AuthenticateSession(_token)!;
        _store.VerifyStepUp(owner, FreshCode());
        var credential = _store.IssueBreakGlassCredential(owner);
        var window = _store.OpenBreakGlassWindowOffline(owner.User.Username,
            FreshCode(), credential, [_session], 1, "PROJ-002");
        Assert.Throws<SecurityOperationException>(() => _store.AppendOfflineBreakGlassEntry(window.Id,
            owner.User.Username, "wrong", "service.restart", "sample", "ok"));
        _store.AppendOfflineBreakGlassEntry(window.Id, owner.User.Username,
            FreshCode(), "service.restart", "sample", "ok");
        Assert.Contains(_store.BreakGlassEntries(window.Id), entry => entry.Operation == "service.restart");
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Throws<SecurityOperationException>(() => _store.AppendOfflineBreakGlassEntry(window.Id,
            owner.User.Username, FreshCode(),
            "service.restart", "sample", "ok"));
    }

    [Fact]
    public async Task Opening_alerts_the_owner_once()
    {
        var owner = _store.AuthenticateSession(_token)!;
        _store.VerifyStepUp(owner, FreshCode());
        var credential = _store.IssueBreakGlassCredential(owner);
        _store.VerifyStepUp(owner, FreshCode());
        var alerts = new RecordingAlerts();
        var service = new BreakGlassWindowService(_store, new RecordingCards(), alerts,
            new ControlPlaneAuditSinkOptions(ControlPlaneAuditSinkOptions.Https, new Uri("https://sink.example.test"), null, null),
            _clock, NullLogger<BreakGlassWindowService>.Instance);
        var window = await service.OpenAsync(owner, new OpenBreakGlassRequest(credential, [_session], 1, "PROJ-002"), default);
        await service.TickAsync();
        Assert.Equal(window.Id, _store.ActiveBreakGlassWindow(_session)?.Id);
        Assert.Single(alerts.Raised);
        Assert.Equal("break-glass-open", alerts.Raised[0].Code);
    }

    private string FreshCode()
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        return SecondFactorCodes.CurrentTotp(_factor, _clock.GetUtcNow().UtcDateTime);
    }

    private sealed class RecordingCards : IBreakGlassReviewCardWriter
    {
        public List<(string Id, string Prompt)> Created { get; } = [];
        public bool ValidProject(string project) => true;
        public string? Find(string id) => Created.FirstOrDefault(card => card.Id == id).Id;
        public string? Create(BreakGlassWindow window, string id, string prompt)
        {
            Created.Add((id, prompt));
            return id;
        }
    }

    private sealed class RecordingAlerts : IControlPlaneAuditAlertChannel
    {
        public List<ControlPlaneAuditAlert> Raised { get; } = [];
        public Task RaiseAsync(ControlPlaneAuditAlert alert, CancellationToken ct)
        {
            Raised.Add(alert);
            return Task.CompletedTask;
        }
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { } File.Delete(_root + ".kek"); }
}
