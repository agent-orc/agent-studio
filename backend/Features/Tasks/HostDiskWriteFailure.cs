namespace AgentStudio.Tasks;

/// <summary>Preserves a host write failure as infrastructure cause across work admission.</summary>
public static class HostDiskWriteFailure
{
    public static bool IsDiskFull(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is not IOException) continue;
            if ((current.HResult & 0xffff) is 28 or 112 or 39) return true;
            if (current.Message.Contains("no space left", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("out of diskspace", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
