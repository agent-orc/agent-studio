using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Secrets;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-1, decision D4 option A2: one hash-chained audit writer
/// for the control plane, forwarded to an off-host append-only sink, and a
/// verification job that raises an acute alert on any change, deletion,
/// reordering, gap, or mismatch.
/// </summary>
public sealed class ControlPlaneAuditChainTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "control-plane-audit-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task An_untouched_chain_verifies_and_matches_the_sink()
    {
        var (log, monitor, sink, forwarder, verifier) = Build();
        Fill(log, 12);
        Assert.Equal(12, await forwarder.ForwardPendingAsync(CancellationToken.None));

        var result = await verifier.VerifyAsync(CancellationToken.None);
        Assert.Equal((ControlPlaneAuditIntegrityStates.Verified, 12L, true, false),
            (result.State, result.VerifiedThrough, result.SinkCompared, result.Alert));
        Assert.Empty(monitor.Raised);
        Assert.Equal(12, log.ReadForwardPosition().Sequence);
        Assert.Equal(log.Read(1, 100).Select(entry => entry.Raw), sink.Lines);
    }

    [Theory]
    [InlineData("change")]
    [InlineData("delete-middle")]
    [InlineData("delete-first")]
    [InlineData("delete-last")]
    [InlineData("reorder")]
    [InlineData("insert")]
    public async Task Changing_deleting_or_reordering_a_stored_entry_fails_verification_and_alerts(string tamper)
    {
        var (log, monitor, _, forwarder, verifier) = Build();
        Fill(log, 10);
        await forwarder.ForwardPendingAsync(CancellationToken.None);
        var lines = File.ReadAllLines(log.FilePath).ToList();
        switch (tamper)
        {
            case "change": lines[4] = lines[4].Replace("\"outcome\":\"200\"", "\"outcome\":\"403\"", StringComparison.Ordinal); break;
            case "delete-middle": lines.RemoveAt(4); break;
            case "delete-first": lines.RemoveAt(0); break;
            case "delete-last": lines.RemoveAt(lines.Count - 1); break;
            case "reorder": (lines[3], lines[4]) = (lines[4], lines[3]); break;
            case "insert": lines.Insert(5, lines[5]); break;
        }
        Assert.NotEqual(File.ReadAllLines(log.FilePath), lines);
        File.WriteAllLines(log.FilePath, lines);

        var result = await verifier.VerifyAsync(CancellationToken.None);

        Assert.Equal(ControlPlaneAuditIntegrityStates.Failed, result.State);
        Assert.True(result.Alert);
        var alert = Assert.Single(monitor.Raised);
        Assert.Equal(ControlPlaneAuditAlertCodes.ChainBroken, alert.Code);
        Assert.Single(_channel.Alerts);
    }

    [Fact]
    public async Task Deleting_the_newest_entry_is_detected_after_a_restart_from_the_forward_position()
    {
        var (log, _, _, forwarder, _) = Build();
        Fill(log, 6);
        await forwarder.ForwardPendingAsync(CancellationToken.None);
        File.WriteAllLines(log.FilePath, File.ReadAllLines(log.FilePath).SkipLast(1));

        // A fresh process knows nothing it wrote; the forward position and the sink still do.
        var restarted = new ControlPlaneAuditLog(Path.GetDirectoryName(log.FilePath)!, _clock);
        var monitor = new ControlPlaneAuditMonitor(restarted, HttpsOptions, _clock, _channel);
        var verifier = new ControlPlaneAuditVerifier(restarted, monitor, _sink, _clock);
        var result = await verifier.VerifyAsync(CancellationToken.None);

        Assert.Equal(("truncated", 6L), (result.Problem, result.ProblemSequence));
        Assert.Equal(ControlPlaneAuditAlertCodes.ChainBroken, Assert.Single(monitor.Raised).Code);
    }

    [Fact]
    public async Task A_rewritten_chain_with_valid_hashes_still_fails_against_the_sink()
    {
        var (log, monitor, _, forwarder, verifier) = Build();
        Fill(log, 5);
        await forwarder.ForwardPendingAsync(CancellationToken.None);

        // Someone with host access rewrites entry 3 and recomputes every later hash.
        var entries = log.Read(1, 10);
        File.Delete(log.FilePath);
        File.Delete(log.ForwardPositionPath);
        var rewritten = new ControlPlaneAuditLog(Path.GetDirectoryName(log.FilePath)!, _clock);
        foreach (var entry in entries)
            rewritten.Append(new ControlPlaneAuditRecord(entry.Stream, entry.Actor, entry.Operation,
                entry.Sequence == 3 ? "200" : entry.Outcome, entry.Target, entry.Session, entry.DelegatedBy,
                entry.ParameterDigest, entry.Sequence == 3 ? new { forged = true } : (object?)entry.Details, entry.At));
        Assert.True(rewritten.VerifyChain().Ok);

        var result = await new ControlPlaneAuditVerifier(rewritten, monitor, _sink, _clock).VerifyAsync(CancellationToken.None);
        Assert.Equal(("sink-mismatch", 3L), (result.Problem, result.ProblemSequence));
        Assert.Equal(ControlPlaneAuditAlertCodes.SinkMismatch, Assert.Single(monitor.Raised).Code);
    }

    [Fact]
    public async Task A_sink_that_lost_a_forwarded_entry_is_a_gap()
    {
        var (log, monitor, sink, forwarder, verifier) = Build();
        Fill(log, 8);
        await forwarder.ForwardPendingAsync(CancellationToken.None);
        sink.Drop(4);

        var result = await verifier.VerifyAsync(CancellationToken.None);
        Assert.Equal(("sink-gap", 4L), (result.Problem, result.ProblemSequence));
        Assert.Equal(ControlPlaneAuditAlertCodes.SinkGap, Assert.Single(monitor.Raised).Code);
    }

    [Fact]
    public async Task Entries_reach_the_sink_within_sixty_seconds_at_the_95th_percentile()
    {
        var folder = Path.Combine(_root, "latency");
        var log = new ControlPlaneAuditLog(folder);
        var monitor = new ControlPlaneAuditMonitor(log, HttpsOptions);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ControlPlaneAudit:Sink:PollSeconds"] = "1",
        }).Build();
        var forwarder = new AuditSinkForwarder(log, monitor, _sink, configuration: config);
        await forwarder.StartAsync(CancellationToken.None);
        try
        {
            var appended = new Dictionary<long, long>();
            for (var index = 0; index < 120; index++)
            {
                var entry = log.Append(Record(index));
                appended[entry.Sequence] = Stopwatch.GetTimestamp();
                if (index % 10 == 0) await Task.Delay(20);
            }
            var deadline = Stopwatch.StartNew();
            while (_sink.Count < 120 && deadline.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(10);

            Assert.Equal(120, _sink.Count);
            var latencies = appended.Select(pair => Stopwatch.GetElapsedTime(pair.Value, _sink.ReceivedAt[pair.Key]))
                .Order().ToList();
            var p95 = latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1];
            Assert.True(p95 < TimeSpan.FromSeconds(60), $"p95 forwarding latency was {p95.TotalMilliseconds:0} ms.");
            output.WriteLine($"Forwarding latency over {latencies.Count} entries: p95={p95.TotalMilliseconds:0.0} ms, max={latencies[^1].TotalMilliseconds:0.0} ms");
        }
        finally
        {
            await forwarder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_unreachable_sink_queues_entries_without_loss_and_alerts()
    {
        var (log, monitor, sink, forwarder, _) = Build();
        sink.Reachable = false;
        Fill(log, 7);

        Assert.Equal(0, await forwarder.ForwardPendingAsync(CancellationToken.None));
        Assert.Empty(monitor.Raised);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Fill(log, 3, start: 7);
        Assert.Equal(0, await forwarder.ForwardPendingAsync(CancellationToken.None));

        var alert = Assert.Single(monitor.Raised);
        Assert.Equal((ControlPlaneAuditAlertCodes.SinkUnreachable, 1L), (alert.Code, alert.Sequence));
        Assert.Single(_channel.Alerts);
        var queued = monitor.Forwarding;
        Assert.Equal((10L, 0L, 10L, true), (queued.HeadSequence, queued.ForwardedSequence, queued.Pending, queued.Alert));
        Assert.NotNull(queued.LastError);
        // A second failed attempt does not raise the same alert again.
        await forwarder.ForwardPendingAsync(CancellationToken.None);
        Assert.Single(monitor.Raised);

        sink.Reachable = true;
        Assert.Equal(10, await forwarder.ForwardPendingAsync(CancellationToken.None));
        Assert.Equal(log.Read(1, 100).Select(entry => entry.Raw), sink.Lines);
        var recovered = monitor.Forwarding;
        Assert.Equal((10L, 0L, false, (string?)null), (recovered.ForwardedSequence, recovered.Pending, recovered.Alert, recovered.LastError));
    }

    [Fact]
    public void Existing_audits_are_routed_through_the_writer_without_dropping_fields()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
        }).Build();
        var store = new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, _clock);
        var ownerId = store.Bootstrap(new BootstrapRequest("first.owner", Password, null)).User.Id;
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("ci", null), ownerId);
        var token = store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest("build", null), ownerId);
        var principal = store.AuthenticateServiceAccount(token.Secret)!;
        store.AppendServiceAccountAudit(principal, "GET", "/api/tags", 200, "workspace", "sha256:" + new string('a', 64));
        var apiToken = store.CreateApiToken(new CreateApiTokenRequest(
            "reporting", StudioRoles.Viewer, ["alpha"], _clock.GetUtcNow().UtcDateTime.AddDays(30)), ownerId);
        store.AppendServiceAccountAudit(store.AuthenticateServiceAccount(apiToken.Secret)!, "GET", "/api/tasks", 200);
        store.AppendRunAudit(new RunSecurityAuditEvent(_clock.GetUtcNow().UtcDateTime, "claim", "AL-1", "alpha",
            "human:" + ownerId, "runner:host-a", "cred-1", 7, "ok"));
        new SecretAuditLog(Path.Combine(_root, "secret-audit"), store.ControlPlaneAudit).Append(new SecretAuditEntry
        {
            At = _clock.GetUtcNow().UtcDateTime, Action = SecretAuditActions.Put, Actor = "human:" + ownerId,
            Scope = "project:alpha", Reference = "deploy-key", Outcome = "ok", Version = 2,
        });

        var entries = store.ControlPlaneAudit.Read(1, 100);
        Assert.True(store.ControlPlaneAudit.VerifyChain().Ok);
        Assert.Equal(
            new[] { ControlPlaneAuditStreams.ApiToken, ControlPlaneAuditStreams.Iam, ControlPlaneAuditStreams.Run,
                ControlPlaneAuditStreams.Secret, ControlPlaneAuditStreams.ServiceAccount },
            entries.Select(entry => entry.Stream).Distinct().Order(StringComparer.Ordinal).ToArray());

        // Each routed entry keeps every field of the legacy line it mirrors.
        var legacyRequest = JsonDocument.Parse(File.ReadAllLines(
            Path.Combine(store.SecurityFolder, AccessSecurityStore.ServiceAccountAuditFileName)).Single()).RootElement;
        var request = entries.Single(entry => entry.Stream == ControlPlaneAuditStreams.ServiceAccount);
        Assert.Equal(("GET /api/tags", "200", "workspace"), (request.Operation, request.Outcome, request.Target));
        AssertKeepsFields(legacyRequest, request);
        var legacyIam = File.ReadAllLines(Path.Combine(store.SecurityFolder, AccessSecurityStore.IamAuditFileName));
        var iam = entries.Where(entry => entry.Stream == ControlPlaneAuditStreams.Iam).ToList();
        Assert.Equal(legacyIam.Length, iam.Count);
        foreach (var (line, entry) in legacyIam.Zip(iam)) AssertKeepsFields(JsonDocument.Parse(line).RootElement, entry);
        var run = entries.Single(entry => entry.Stream == ControlPlaneAuditStreams.Run);
        Assert.Equal(("claim", "ok", "project:alpha/task:AL-1"), (run.Operation, run.Outcome, run.Target));
        AssertKeepsFields(JsonDocument.Parse(File.ReadAllLines(Path.Combine(store.SecurityFolder, "run-audit.jsonl")).Single()).RootElement, run);
        Assert.Equal(7, run.Details!.Value.GetProperty("FencingToken").GetInt64());
        var secret = entries.Single(entry => entry.Stream == ControlPlaneAuditStreams.Secret);
        Assert.Equal(("secret.put", "project:alpha/deploy-key", 2), (secret.Operation, secret.Target,
            secret.Details!.Value.GetProperty("version").GetInt32()));
        Assert.All(entries, entry => Assert.DoesNotContain(token.Secret, entry.Raw, StringComparison.Ordinal));
    }

    private static void AssertKeepsFields(JsonElement legacy, ControlPlaneAuditEntry entry)
    {
        foreach (var property in legacy.EnumerateObject())
            Assert.True(entry.Details!.Value.TryGetProperty(property.Name, out var value)
                    && (property.Value.ValueKind == JsonValueKind.Null || value.ToString() == property.Value.ToString()),
                $"Field {property.Name} missing or changed in entry {entry.Sequence}.");
    }

    private static readonly ControlPlaneAuditSinkOptions HttpsOptions =
        new(ControlPlaneAuditSinkOptions.Https, new Uri("https://audit-sink.test/"), null, null);

    private readonly FakeSink _sink = new();
    private readonly RecordingChannel _channel = new();

    private (ControlPlaneAuditLog, ControlPlaneAuditMonitor, FakeSink, AuditSinkForwarder, ControlPlaneAuditVerifier) Build()
    {
        var log = new ControlPlaneAuditLog(Path.Combine(_root, "chain"), _clock);
        var monitor = new ControlPlaneAuditMonitor(log, HttpsOptions, _clock, _channel);
        return (log, monitor, _sink, new AuditSinkForwarder(log, monitor, _sink, _clock),
            new ControlPlaneAuditVerifier(log, monitor, _sink, _clock));
    }

    private static void Fill(ControlPlaneAuditLog log, int count, int start = 0)
    {
        for (var index = start; index < start + count; index++) log.Append(Record(index));
    }

    private static ControlPlaneAuditRecord Record(int index) => new(
        ControlPlaneAuditStreams.OperatorAgent, "operator-agent:opa_1/session:s1", $"GET /api/tasks/card-{index}", "200",
        $"task:card-{index}", "s1", "human:usr_owner", "sha256:" + index.ToString("x64"), new { index });

    /// <summary>An append-only fake sink, idempotent per sequence like the real contract.</summary>
    private sealed class FakeSink : IControlPlaneAuditSink
    {
        private readonly SortedDictionary<long, string> _lines = [];
        private readonly object _gate = new();
        public ConcurrentDictionary<long, long> ReceivedAt { get; } = new();
        public bool Reachable { get; set; } = true;
        public string Kind => "fake";
        public int Count { get { lock (_gate) return _lines.Count; } }
        public IReadOnlyList<string> Lines { get { lock (_gate) return _lines.Values.ToList(); } }

        public Task AppendAsync(IReadOnlyList<ControlPlaneAuditEntry> entries, CancellationToken ct)
        {
            if (!Reachable) throw new HttpRequestException("connection refused");
            lock (_gate)
                foreach (var entry in entries)
                    if (_lines.TryAdd(entry.Sequence, entry.Raw)) ReceivedAt[entry.Sequence] = Stopwatch.GetTimestamp();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ReadAsync(long fromSequence, int limit, CancellationToken ct)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<string>>(_lines.Where(pair => pair.Key >= fromSequence)
                    .Take(limit).Select(pair => pair.Value).ToList());
        }

        public void Drop(long sequence)
        {
            lock (_gate) _lines.Remove(sequence);
        }
    }

    private sealed class RecordingChannel : IControlPlaneAuditAlertChannel
    {
        public ConcurrentQueue<ControlPlaneAuditAlert> Alerts { get; } = new();

        public Task RaiseAsync(ControlPlaneAuditAlert alert, CancellationToken ct)
        {
            Alerts.Enqueue(alert);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
