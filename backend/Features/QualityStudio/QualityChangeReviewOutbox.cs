using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.QualityStudio;

public sealed record QualityChangeReviewOutboxItem(
    string Id, string RepositoryId, string CardKey, int Round, string AttemptId,
    JsonElement Document, DateTimeOffset CreatedAt, DateTimeOffset DueAt, int Attempts);

/// <summary>Durable verdict ingest. A restart replays the same document and key.</summary>
public sealed class QualityChangeReviewOutbox : BackgroundService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly string _path;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly QualityStudioConnectionStore _connections;
    private readonly ISecretProvider _secrets;
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _clock;
    private readonly AgentMessageBusBridge _feed;
    private readonly ILogger<QualityChangeReviewOutbox> _logger;
    private List<QualityChangeReviewOutboxItem> _items;

    public QualityChangeReviewOutbox(IConfiguration configuration, QualityStudioConnectionStore connections,
        ISecretProvider secrets, IHttpClientFactory clients, TimeProvider clock,
        AgentMessageBusBridge feed, ILogger<QualityChangeReviewOutbox> logger)
    {
        var root = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root)) root = LocalStateRoot.Combine("agent-studio");
        _path = Path.Combine(root, ".security", "quality-change-review-outbox.json");
        _connections = connections;
        _secrets = secrets;
        _clients = clients;
        _clock = clock;
        _feed = feed;
        _logger = logger;
        _items = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<QualityChangeReviewOutboxItem>>(File.ReadAllText(_path), JsonSerializerOptions.Web) ?? []
            : [];
    }

    public int PendingCount { get { lock (_gate) return _items.Count; } }

    public void Enqueue(string repositoryId, string cardKey, QualityChangeReviewRound round, JsonElement document)
    {
        var key = string.Join('\n', repositoryId, document.GetProperty("baseSha").GetString(),
            document.GetProperty("headSha").GetString(), document.GetProperty("policyHash").GetString(),
            document.GetProperty("ruleSetHash").GetString(), document.GetProperty("catalogueVersion").GetString());
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        lock (_gate)
        {
            if (_items.Any(item => item.Id == id)) return;
            var now = _clock.GetUtcNow();
            _items.Add(new QualityChangeReviewOutboxItem(id, repositoryId, cardKey, round.Round,
                round.AttemptId, document.Clone(), now, now, 0));
            Persist();
        }
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await DeliverDueOnceAsync(stoppingToken)) continue;
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<bool> DeliverDueOnceAsync(CancellationToken ct)
    {
        QualityChangeReviewOutboxItem? item;
        lock (_gate) item = _items.Where(entry => entry.DueAt <= _clock.GetUtcNow())
            .OrderBy(entry => entry.DueAt).FirstOrDefault();
        if (item is null) return false;
        var expired = _clock.GetUtcNow() - item.CreatedAt >= Lifetime;
        var delivered = !expired && await DeliverAsync(item, ct);
        if (expired)
        {
            await _feed.EmitQualityChangeReviewDeliveryExpiredAsync(item.CardKey, item.RepositoryId,
                item.Id, ct);
            _logger.LogError("Quality Studio verdict delivery expired for {CardKey} and {Key}", item.CardKey, item.Id);
        }
        lock (_gate)
        {
            var index = _items.FindIndex(entry => entry.Id == item.Id);
            if (index < 0) return true;
            if (delivered || expired) _items.RemoveAt(index);
            else
            {
                var seconds = Math.Min(3600, 5 * (1 << Math.Min(item.Attempts, 10)));
                _items[index] = item with { Attempts = item.Attempts + 1,
                    DueAt = _clock.GetUtcNow().AddSeconds(seconds) };
            }
            Persist();
        }
        return true;
    }

    private async Task<bool> DeliverAsync(QualityChangeReviewOutboxItem item, CancellationToken ct)
    {
        var connection = _connections.Get();
        if (connection is null) return false;
        try
        {
            var token = await _secrets.GetForPlatformAsync(
                SecretReference.Parse(QualityStudioEndpoints.TokenReference), "quality-change-review", ct);
            using var request = new HttpRequestMessage(HttpMethod.Put,
                connection.InternalBaseUrl + "/api/v1/repos/" + Uri.EscapeDataString(item.RepositoryId) + "/change-reviews")
            {
                Content = JsonContent.Create(new
                {
                    document = item.Document,
                    provenance = new { producer = "agent-studio-review-plane", cardKey = item.CardKey,
                        round = item.Round, attemptId = item.AttemptId }
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            request.Headers.Add("X-Client-Id", connection.ServiceClientId);
            using var response = await _clients.CreateClient("quality-studio-integration").SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SecretNotFoundException)
        {
            _logger.LogWarning("Quality Studio verdict delivery will retry for {Key}: {Reason}", item.Id,
                ex.GetType().Name);
            return false;
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_items, JsonSerializerOptions.Web));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
