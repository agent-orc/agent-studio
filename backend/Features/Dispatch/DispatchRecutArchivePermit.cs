namespace AgentStudio.Dispatch;

/// <summary>Application-issued permit, rechecked inside the lane writer. Never accepted from JSON.</summary>
public sealed class DispatchRecutArchivePermit
{
    private readonly Func<TaskInfo, bool> _allows;
    internal DispatchRecutArchivePermit(Func<TaskInfo, bool> allows) => _allows = allows;
    internal bool Allows(TaskInfo card) => _allows(card);
}
