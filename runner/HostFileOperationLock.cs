namespace AgentRunner;

/// <summary>Bounded host-local lock for issuance steps that must survive daemon restarts.</summary>
internal static class HostFileOperationLock
{
    public static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }
        }
    }
}
