namespace AgentStudio.Git;

/// <summary>Ambient deadline for one background repository index computation.</summary>
internal static class GitProcessBudget
{
    private static readonly AsyncLocal<Budget?> Current = new();

    internal static CancellationToken Token => Current.Value?.Token ?? default;

    internal static IDisposable Begin(CancellationToken token)
    {
        var prior = Current.Value;
        Current.Value = new Budget(token);
        return new Scope(() => Current.Value = prior);
    }

    internal static IDisposable Acquire()
    {
        var budget = Current.Value;
        if (budget is null) return new Scope(() => { });
        budget.ProcessSlots.Wait(budget.Token);
        return new Scope(() => budget.ProcessSlots.Release());
    }

    private sealed record Budget(CancellationToken Token)
    {
        internal readonly SemaphoreSlim ProcessSlots = new(4, 4);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
