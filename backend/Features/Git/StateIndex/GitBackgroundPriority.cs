using System.Diagnostics;

namespace AgentStudio.Git;

/// <summary>
/// Marks the calling flow as background git work. The process runner drops the
/// child's scheduling priority on Windows while the flag is set, so the index
/// loses the CPU to the operator's own shell instead of competing with it.
/// Windows-only by design: a bare git spawn already costs ~70-100 ms there, and
/// that is where the operator laptop feels the index.
/// </summary>
internal static class GitBackgroundPriority
{
    private static readonly AsyncLocal<bool> _low = new();

    internal static bool IsLow => _low.Value;

    internal static IDisposable Enter(bool enabled) => new Scope(enabled);

    /// <summary>
    /// Applies the ambient priority to a started child. Failure is expected and
    /// ignored: a git process that already exited cannot be reprioritized, and
    /// priority is an optimisation, never a correctness requirement.
    /// </summary>
    internal static void Apply(Process process)
    {
        if (!OperatingSystem.IsWindows() || !_low.Value) return;
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            SilentCatch.Note(ex, "GitBackgroundPriority: child priority could not be lowered");
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous;

        public Scope(bool enabled)
        {
            _previous = _low.Value;
            if (enabled) _low.Value = true;
        }

        public void Dispose() => _low.Value = _previous;
    }
}
