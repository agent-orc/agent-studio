using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public sealed record WorkspaceArtifactPushRequest(
    string RepositoryRoot,
    string JobId,
    string TargetBranch = "main",
    string? Sha = null,
    string? Project = null);

internal sealed record WorkspaceArtifactPushIntent(WorkspaceArtifactPushRequest Request, string Marker);

/// <summary>
/// Hands push work to the worker. Exact-SHA obligations are flushed to an
/// application-owned outbox before the in-memory channel is signalled.
/// Legacy workspace HEAD requests retain their existing catch-up contract.
/// </summary>
public sealed class WorkspaceArtifactPushQueue
{
    private static readonly ConcurrentDictionary<string, object> OutboxGates = new(StringComparer.Ordinal);
    private readonly string _outbox;
    private readonly ILogger<WorkspaceArtifactPushQueue>? _logger;
    private readonly Channel<WorkspaceArtifactPushRequest> _channel =
        Channel.CreateUnbounded<WorkspaceArtifactPushRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public WorkspaceArtifactPushQueue(IConfiguration? configuration = null,
        ILogger<WorkspaceArtifactPushQueue>? logger = null)
    {
        _logger = logger;
        _outbox = configuration?["WorkspaceArtifacts:PushOutboxPath"]
            ?? LocalStateRoot.Combine("agentstudio", "workspace-artifact-push-outbox",
                Hash(configuration?["TaskRepository"] ?? "default"));
        if (!Path.IsPathFullyQualified(_outbox))
            throw new ArgumentException("The push outbox path must be absolute.", nameof(configuration));
    }

    public ChannelReader<WorkspaceArtifactPushRequest> Reader => _channel.Reader;

    public bool Enqueue(WorkspaceArtifactPushRequest request)
    {
        if (request.Sha != null)
        {
            request = request with { RepositoryRoot = Path.GetFullPath(request.RepositoryRoot) };
            try
            {
                lock (OutboxGates.GetOrAdd(_outbox, _ => new object()))
                {
                    WriteReceipt(ReceiptPath(request), request);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogError(ex, "workspace-artifact-push outbox persistence failed repo={Repo} branch={Branch} sha={Sha}",
                    request.RepositoryRoot, request.TargetBranch, request.Sha);
                return false;
            }
        }
        return _channel.Writer.TryWrite(request);
    }

    // The intent precedes Git commit creation. Its marker in the commit body
    // lets recovery identify the exact approved object even before SHA hand-off.
    internal WorkspaceArtifactPushIntent? Prepare(WorkspaceArtifactPushRequest request)
    {
        var intent = new WorkspaceArtifactPushIntent(request,
            "Agent-Studio-Push-Intent: " + Guid.NewGuid().ToString("N"));
        try
        {
            lock (OutboxGates.GetOrAdd(_outbox, _ => new object()))
                WriteReceipt(IntentPath(intent), intent);
            return intent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "workspace-artifact-push intent persistence failed repo={Repo}", request.RepositoryRoot);
            return null;
        }
    }

    internal IReadOnlyList<WorkspaceArtifactPushIntent> PendingIntents() =>
        ReadReceipts<WorkspaceArtifactPushIntent>("*.intent", intent =>
            intent.Request == null || string.IsNullOrWhiteSpace(intent.Marker)
                ? throw new JsonException("Push intent is incomplete.") : IntentPath(intent));

    internal void CompleteIntent(WorkspaceArtifactPushIntent intent)
    {
        lock (OutboxGates.GetOrAdd(_outbox, _ => new object()))
            File.Delete(IntentPath(intent));
    }

    private string IntentPath(WorkspaceArtifactPushIntent intent) =>
        Path.Combine(_outbox, Hash(intent.Marker) + ".intent");

    private void WriteReceipt<T>(string path, T receipt)
    {
        Directory.CreateDirectory(_outbox);
        if (File.Exists(path)) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, receipt);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal IReadOnlyList<WorkspaceArtifactPushRequest> PendingRequests() =>
        ReadReceipts<WorkspaceArtifactPushRequest>("*.json", request =>
            request.Sha == null ? throw new JsonException("Push outbox receipt has no approved SHA.") : ReceiptPath(request));

    private IReadOnlyList<T> ReadReceipts<T>(string pattern, Func<T, string> identity)
    {
        lock (OutboxGates.GetOrAdd(_outbox, _ => new object()))
        {
            if (!Directory.Exists(_outbox)) return [];
            var pending = new List<T>();
            foreach (var path in Directory.EnumerateFiles(_outbox, pattern).OrderBy(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var receipt = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
                    if (receipt == null || identity(receipt) != path)
                        throw new JsonException("Push outbox receipt does not match its identity.");
                    pending.Add(receipt);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    // Keep damaged evidence for repair while replaying the other obligations.
                    _logger?.LogError(ex, "workspace-artifact-push outbox receipt unreadable path={Path}", path);
                }
            }
            return pending;
        }
    }

    internal bool IsPending(WorkspaceArtifactPushRequest request) =>
        request.Sha != null && File.Exists(ReceiptPath(request));

    internal void Acknowledge(WorkspaceArtifactPushRequest request)
    {
        if (request.Sha == null) return;
        lock (OutboxGates.GetOrAdd(_outbox, _ => new object()))
        {
            var path = ReceiptPath(request);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private string ReceiptPath(WorkspaceArtifactPushRequest request)
    {
        var repository = Path.GetFullPath(request.RepositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) repository = repository.ToUpperInvariant();
        return Path.Combine(_outbox, Hash($"{repository}\n{request.TargetBranch}\n{request.Sha}") + ".json");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
