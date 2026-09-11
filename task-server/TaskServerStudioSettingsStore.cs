using System.Text.Json;

namespace AgentStudio.TaskServer;

/// <summary>
/// Generic durable (scope, key) -&gt; JSON settings row backing the P3
/// administration bundle: orchestrator/prompt config overrides, CLI quota
/// policy, and per-project CLI/lane-sort overrides. One small table instead
/// of one bespoke table per settings kind, matching the legacy backend's
/// equivalent pattern of one small JSON file per settings kind.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<T?> GetSettingAsync<T>(string scope, string key, CancellationToken ct) where T : class
    {
        await using var connection = await OpenReadyAsync(ct);
        var raw = await ScalarAsync(connection, """
            SELECT value_json FROM studio_settings WHERE scope = $scope AND key = $key;
            """, ct, ("$scope", scope), ("$key", key));
        return raw is string json ? JsonSerializer.Deserialize<T>(json) : null;
    }

    public async Task<IReadOnlyDictionary<string, T>> GetSettingsByScopeAsync<T>(
        string scope, CancellationToken ct) where T : class
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT key, value_json FROM studio_settings WHERE scope = $scope;
            """, ("$scope", scope));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var value = JsonSerializer.Deserialize<T>(reader.GetString(1));
            if (value is not null) result[reader.GetString(0)] = value;
        }
        return result;
    }

    public async Task PutSettingAsync<T>(string scope, string key, T value, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_settings(scope, key, value_json, updated_at)
            VALUES ($scope, $key, $value, $now)
            ON CONFLICT(scope, key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at;
            """, ct,
            ("$scope", scope), ("$key", key), ("$value", JsonSerializer.Serialize(value)), ("$now", Iso(UtcNow)));
    }
}
