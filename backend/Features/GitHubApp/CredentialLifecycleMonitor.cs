using System.Text.Json;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.GitHubApp;

public interface ICredentialLifecycleNotifier
{
    Task<bool> NotifyAsync(CredentialLifecycleWarningDto warning, CancellationToken ct);
}

public sealed class CredentialLifecycleNotifier(AgentMessageBusBridge bus) : ICredentialLifecycleNotifier
{
    public Task<bool> NotifyAsync(CredentialLifecycleWarningDto warning, CancellationToken ct)
        => bus.EmitCredentialLifecycleWarningAsync(warning, ct);
}

/// <summary>Persists first observations and delivered warning thresholds, never credential material.</summary>
public sealed class CredentialLifecycleMonitor(string path, TimeProvider clock, ICredentialLifecycleNotifier notifier)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record State(Dictionary<string, DateTime> FirstObserved, HashSet<string> Delivered);

    public async Task<CredentialInventoryDto> CompleteAsync(CredentialInventoryDto inventory, bool notify, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(path, ct), Json)!
                : new State(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            var now = clock.GetUtcNow().UtcDateTime;
            var items = inventory.Items.Select(item =>
            {
                var first = now;
                if (item.LifetimeClass == "long-lived" && item.CreatedAt is null && item.RotationBasisAt is null &&
                    !state.FirstObserved.TryGetValue(item.Id, out first))
                    state.FirstObserved[item.Id] = first = now;
                return CredentialLifecyclePolicy.Complete(item with
                {
                    RotationDueAt = item.RotationPlanSource.StartsWith("reported", StringComparison.Ordinal) ? item.RotationDueAt : null,
                }, first);
            }).ToArray();
            var dueIds = CredentialLifecyclePolicy.Warnings(items, now).Select(warning => warning.CredentialId).ToHashSet(StringComparer.Ordinal);
            var result = new CredentialInventoryDto(now, items.Select(item => dueIds.Contains(item.Id) && item.State is "ok" or "unused"
                ? item with { State = "expiring" } : item).ToArray());
            if (notify)
            {
                foreach (var warning in result.Warnings)
                {
                    var key = $"{warning.CredentialId}|{warning.Subject}|{warning.DueAt:O}|{warning.ThresholdDays}";
                    if (state.Delivered.Contains(key)) continue;
                    // Delivery failures leave the key unsent for the next sweep.
                    if (await notifier.NotifyAsync(warning, ct))
                    {
                        state.Delivered.Add(key);
                        if (warning.ThresholdDays == 3)
                            state.Delivered.Add($"{warning.CredentialId}|{warning.Subject}|{warning.DueAt:O}|14");
                        await SaveAsync(state, ct);
                    }
                }
            }
            await SaveAsync(state, ct);
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync(State state, CancellationToken ct)
    {
        OwnerOnlyAccess.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = OwnerOnlyAccess.CreateNewFile(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, state, Json, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
