namespace AgentStudio.Git;

/// <summary>
/// Marks the ambient execution context as background index work. Git spawns
/// started inside the scope drop to below-normal priority on Windows, where a
/// bare spawn already costs 70 to 100 ms and the operator's laptop is also
/// running Studio, the Task Server, and the review gates. Nothing on a request
/// path opens this scope, because nothing on a request path spawns git.
/// </summary>
internal static class GitBackgroundWork
{
    private static readonly AsyncLocal<bool> _active = new();

    internal static bool IsActive => _active.Value;

    internal static IDisposable Begin() => new Scope();

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous = _active.Value;

        public Scope() => _active.Value = true;

        public void Dispose() => _active.Value = _previous;
    }
}
