namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<bool> DeviceLoginHostExistsAsync(string hostId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ScalarAsync(connection,
            "SELECT 1 FROM runners WHERE host_id = $host AND status = 'active' LIMIT 1;",
            ct, ("$host", hostId)) is not null;
    }

    public async Task<string?> ResolveDeviceLoginHostAsync(string runnerId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ScalarAsync(connection,
            "SELECT host_id FROM runners WHERE id = $runner AND status = 'active';",
            ct, ("$runner", runnerId)) as string;
    }
}
