namespace AgentStudio.TaskServer.Contracts;

/// <summary>The mechanical ceiling for one coding process. It does not size the card.</summary>
public static class CodingEmergencyLimit
{
    public const int DefaultSeconds = 24 * 60 * 60;
    public const int MaximumSeconds = 7 * DefaultSeconds;

    public static (int Seconds, string Source) Resolve(int? projectSeconds, int? workspaceSeconds)
    {
        if (projectSeconds is > 0 and <= MaximumSeconds) return (projectSeconds.Value, "project");
        if (workspaceSeconds is > 0 and <= MaximumSeconds) return (workspaceSeconds.Value, "workspace");
        return (DefaultSeconds, "platform");
    }
}
