namespace AgentStudio.Pipeline;

internal enum BatchGateWriteStage { Starting, Completed }

/// <summary>
/// Fault-injection seam of the batch authority (AGT-3563). Every durable write
/// of the batch store and of the card-local ownership marker reports here
/// before it starts and after it completed. A test installs an observer for its
/// own asynchronous flow and throws from it to model a process that died right
/// after one write and therefore performs no later write.
/// </summary>
internal static class BatchGateDurableWrites
{
    private static readonly AsyncLocal<Action<string, BatchGateWriteStage>?> Observer = new();

    internal static Action<string, BatchGateWriteStage>? ObserverForTesting
    {
        get => Observer.Value;
        set => Observer.Value = value;
    }

    internal static void Starting(string path) => Observer.Value?.Invoke(path, BatchGateWriteStage.Starting);

    internal static void Completed(string path) => Observer.Value?.Invoke(path, BatchGateWriteStage.Completed);
}
