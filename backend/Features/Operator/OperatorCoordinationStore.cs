using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using AgentStudio.Host;
using AgentStudio.Security;

namespace AgentStudio.Operator;

public sealed record OperatorLogRequest(string? Project, string? Kind, string? Text,
    IReadOnlyList<string>? CardKeys, IReadOnlyList<string>? AuditIds);

public sealed record OperatorLogEntry(long Cursor, string Actor, DateTime TimeUtc, string Project,
    string Kind, string Text, IReadOnlyList<string> CardKeys, IReadOnlyList<string> AuditIds);

public sealed record CardAttentionLease(string CardKey, string Holder, DateTime ExpiresAtUtc);

/// <summary>Installation journal and short card attention leases under TaskRepository/.operator.</summary>
public sealed class OperatorCoordinationStore(IConfiguration configuration, TimeProvider clock,
    IHttpClientFactory? clients = null)
{
    private static readonly Regex TokenPattern = new(@"(?<![A-Za-z0-9])(?:api|sat|oas|inv|rnr|enr|ssn|otl)\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]{8,})?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly object _gate = new();
    private string Folder => Path.Combine(configuration["TaskRepository"]
        ?? throw new InvalidOperationException("TaskRepository is required."), ".operator");
    private string LogPath => Path.Combine(Folder, "operator-log.jsonl");
    private string LeasesPath => Path.Combine(Folder, "attention-leases.json");

    public static string Redact(string text) => TokenPattern.Replace(
        CredentialRedactor.Redact(text).Replace("[REDACTED_CREDENTIAL]", "[redacted]", StringComparison.Ordinal),
        "[redacted]");

    public async Task<OperatorLogEntry> AppendAsync(string actor, OperatorLogRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Length > 16_000
            || request.Kind?.Trim().ToLowerInvariant() is not ("observed" or "decided" or "did" or "handoff"))
            throw new ArgumentException("A project, valid kind and text up to 16000 characters are required.");
        if (clients is null || !TaskServerPlaneProxy.IsConfigured(configuration)) return Append(actor, request);
        using var outbound = new HttpRequestMessage(HttpMethod.Post, "/api/v1/studio/operator-log")
        {
            Content = JsonContent.Create(new
            {
                request.Project, request.Kind, Text = Redact(request.Text ?? ""), request.CardKeys,
                request.AuditIds,
            }),
        };
        outbound.Headers.TryAddWithoutValidation("X-Actor-Id", actor);
        using var response = await clients.CreateClient(TaskServerPlaneProxy.ClientName).SendAsync(outbound, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OperatorLogEntry>(cancellationToken: ct)
            ?? throw new IOException("The Task Server returned an empty operator log entry.");
    }

    public async Task<IReadOnlyList<OperatorLogEntry>> ReadAsync(string? project, long after,
        CancellationToken ct = default)
    {
        if (clients is null || !TaskServerPlaneProxy.IsConfigured(configuration)) return Read(project, after);
        var query = $"?after={after}" + (project is null ? "" : "&project=" + Uri.EscapeDataString(project));
        using var response = await clients.CreateClient(TaskServerPlaneProxy.ClientName)
            .GetAsync("/api/v1/studio/operator-log" + query, ct);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<OperatorLogPage>(cancellationToken: ct);
        return page?.Entries ?? [];
    }

    private sealed record OperatorLogPage(IReadOnlyList<OperatorLogEntry> Entries);

    public OperatorLogEntry Append(string actor, OperatorLogRequest request)
    {
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is not ("observed" or "decided" or "did" or "handoff"))
            throw new ArgumentException("kind must be observed, decided, did, or handoff.");
        if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Length > 16_000)
            throw new ArgumentException("project and text are required; text is limited to 16000 characters.");
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            var cursor = File.Exists(LogPath) ? File.ReadLines(LogPath).LongCount() + 1 : 1;
            var entry = new OperatorLogEntry(cursor, actor, clock.GetUtcNow().UtcDateTime,
                request.Project.Trim(), kind, Redact(request.Text.Trim()),
                (request.CardKeys ?? []).Select(value => Redact(value.Trim())).Where(value => value.Length > 0).Distinct().Take(50).ToArray(),
                (request.AuditIds ?? []).Select(value => Redact(value.Trim())).Where(value => value.Length > 0).Distinct().Take(50).ToArray());
            File.AppendAllText(LogPath, JsonSerializer.Serialize(entry) + "\n");
            return entry;
        }
    }

    public IReadOnlyList<OperatorLogEntry> Read(string? project, long after, int limit = 200)
    {
        lock (_gate)
        {
            if (!File.Exists(LogPath)) return [];
            return File.ReadLines(LogPath)
                .Select(line => JsonSerializer.Deserialize<OperatorLogEntry>(line))
                .Where(entry => entry is not null && entry.Cursor > after
                    && (project is null || entry.Project == "*"
                        || string.Equals(entry.Project, project, StringComparison.OrdinalIgnoreCase)))
                .Take(Math.Clamp(limit, 1, 1000)).Cast<OperatorLogEntry>().ToArray();
        }
    }

    public CardAttentionLease? Current(string cardKey)
    {
        lock (_gate) return LoadLeases().GetValueOrDefault(cardKey) is { } lease
            && lease.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime ? lease : null;
    }

    public CardAttentionLease? Acquire(string cardKey, string actor, bool owner)
    {
        lock (_gate)
        {
            var leases = LoadLeases();
            var current = leases.GetValueOrDefault(cardKey);
            if (current is not null && current.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime
                && current.Holder != actor && !owner)
                return null;
            var lease = new CardAttentionLease(cardKey, actor, clock.GetUtcNow().UtcDateTime.AddMinutes(10));
            leases[cardKey] = lease;
            SaveLeases(leases);
            return lease;
        }
    }

    public async Task OwnerActionAsync(string cardKey, string project, string owner, CancellationToken ct = default)
    {
        CardAttentionLease? previous = null;
        lock (_gate)
        {
            var leases = LoadLeases();
            if (!leases.TryGetValue(cardKey, out var lease) || lease.Holder == owner
                || lease.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime) return;
            leases.Remove(cardKey);
            SaveLeases(leases);
            previous = lease;
        }
        await AppendAsync(owner, new OperatorLogRequest(project, "handoff",
            $"Owner took over card {cardKey} from {previous.Holder}.", [cardKey], []), ct);
    }

    public static string Revision(string folderPath)
    {
        var path = Path.Combine(folderPath, "task.json");
        if (!File.Exists(path)) path = Path.Combine(folderPath, "job.json");
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        return "\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";
    }

    private Dictionary<string, CardAttentionLease> LoadLeases()
        => File.Exists(LeasesPath)
            ? JsonSerializer.Deserialize<Dictionary<string, CardAttentionLease>>(File.ReadAllText(LeasesPath))
                ?? new(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    private void SaveLeases(Dictionary<string, CardAttentionLease> leases)
    {
        Directory.CreateDirectory(Folder);
        var temporary = LeasesPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(leases));
        File.Move(temporary, LeasesPath, true);
    }
}
