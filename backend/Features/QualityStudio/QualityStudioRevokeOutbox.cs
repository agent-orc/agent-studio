using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Secrets;

namespace AgentStudio.QualityStudio;

public sealed record QualityStudioRevokeItem(string Id, string? Sid, string? Sub, int Attempts, DateTimeOffset DueAt);

/// <summary>Durable, bounded delivery of session revocations to Quality Studio.</summary>
public sealed class QualityStudioRevokeOutbox : BackgroundService
{
    private const int MaxAttempts = 180;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly QualityStudioConnectionStore _connections;
    private readonly ISecretProvider _secrets;
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _clock;
    private readonly ILogger<QualityStudioRevokeOutbox> _logger;
    private List<QualityStudioRevokeItem> _items = [];

    public QualityStudioRevokeOutbox(IConfiguration configuration, QualityStudioConnectionStore connections,
        ISecretProvider secrets, IHttpClientFactory clients, TimeProvider clock, ILogger<QualityStudioRevokeOutbox> logger)
    {
        var root = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-studio");
        _path = Path.Combine(root, ".security", "quality-studio-revoke-outbox.json");
        _connections = connections;
        _secrets = secrets;
        _clients = clients;
        _clock = clock;
        _logger = logger;
        if (File.Exists(_path)) _items = JsonSerializer.Deserialize<List<QualityStudioRevokeItem>>(File.ReadAllText(_path), JsonSerializerOptions.Web) ?? [];
    }

    public void EnqueueSid(string sid) => Enqueue(sid, null);
    public void EnqueueSub(string sub) => Enqueue(null, sub);
    public int PendingCount { get { lock (_gate) return _items.Count; } }

    private void Enqueue(string? sid, string? sub)
    {
        lock (_gate)
        {
            _items.Add(new QualityStudioRevokeItem(Guid.NewGuid().ToString("N"), sid, sub, 0, _clock.GetUtcNow()));
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
        QualityStudioRevokeItem? item;
        lock (_gate) item = _items.Where(entry => entry.DueAt <= _clock.GetUtcNow())
            .OrderBy(entry => entry.DueAt).FirstOrDefault();
        if (item is null) return false;
        var delivered = await DeliverAsync(item, ct);
        lock (_gate)
        {
            var index = _items.FindIndex(entry => entry.Id == item.Id);
            if (index < 0) return true;
            if (delivered || item.Attempts + 1 >= MaxAttempts)
            {
                if (!delivered) _logger.LogError("Quality Studio revoke delivery exhausted for item {ItemId}", item.Id);
                _items.RemoveAt(index);
            }
            else
            {
                var seconds = Math.Min(3600, 5 * (1 << Math.Min(item.Attempts, 10)));
                _items[index] = item with { Attempts = item.Attempts + 1, DueAt = _clock.GetUtcNow().AddSeconds(seconds) };
            }
            Persist();
        }
        return true;
    }

    private async Task<bool> DeliverAsync(QualityStudioRevokeItem item, CancellationToken ct)
    {
        var connection = _connections.Get();
        if (connection is null) return false;
        try
        {
            var token = await _secrets.GetForPlatformAsync(SecretReference.Parse(QualityStudioEndpoints.TokenReference), "quality-studio-revoke", ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, connection.InternalBaseUrl + "/api/v1/federation/revoke")
            {
                Content = JsonContent.Create(new { sid = item.Sid, sub = item.Sub })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            request.Headers.Add("X-Client-Id", connection.ServiceClientId);
            using var response = await _clients.CreateClient("quality-studio-integration").SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SecretNotFoundException)
        {
            _logger.LogWarning("Quality Studio revoke delivery will retry for item {ItemId}: {Reason}", item.Id, ex.GetType().Name);
            return false;
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(_path)!,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_items, JsonSerializerOptions.Web));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
