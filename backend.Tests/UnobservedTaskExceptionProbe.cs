using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Records the unobserved task exceptions that finalize while it is open
/// (AGT-3755). <see cref="Drain"/> forces the finalizer pass inside the test,
/// so a task the test leaked fails that test with its exception instead of
/// surfacing later on another test's finalizer pass.
/// </summary>
internal sealed class UnobservedTaskExceptionProbe : IDisposable
{
    private readonly List<Exception> _seen = [];

    public UnobservedTaskExceptionProbe()
    {
        TaskScheduler.UnobservedTaskException += Record;
    }

    public IReadOnlyList<Exception> Drain()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        lock (_seen) return _seen.ToArray();
    }

    /// <summary>
    /// Fails with every leaked exception that names <paramref name="marker"/>
    /// and that <paramref name="expected"/> does not accept. Tests run in
    /// parallel, so a finalizer pass can also meet another test's task; the
    /// marker keeps the verdict to this test's code.
    /// </summary>
    public void AssertNoLeak(string marker, Func<Exception, bool>? expected = null)
    {
        var leaked = Drain()
            .Where(error => error.ToString().Contains(marker, StringComparison.Ordinal))
            .Where(error => expected?.Invoke(error) != true)
            .ToArray();
        Assert.True(leaked.Length == 0,
            "The test left a faulted task nobody observed: "
            + string.Join(Environment.NewLine, leaked.Select(error => error.ToString())));
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= Record;

    private void Record(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        lock (_seen) _seen.Add(e.Exception);
    }
}
