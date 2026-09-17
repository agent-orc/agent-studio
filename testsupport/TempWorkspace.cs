namespace AgentStudio.TestSupport;

/// <summary>
/// One fixture's temporary directory, removed again when the fixture is
/// disposed (AGT-2858).
///
/// Use it instead of a raw <c>Path.Combine(Path.GetTempPath(), prefix + guid)</c>
/// plus a hand-written <c>Dispose</c>: the delete here tolerates read-only Git
/// objects and a file a child process has not released yet, and it never throws,
/// so a teardown problem can neither fail nor mask a green test.
///
/// <code>
/// private readonly TempWorkspace _workspace = new("atp-pickup-atomicity");
/// public void Dispose() => _workspace.Dispose();
/// </code>
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string prefix)
    {
        Path = TestTempRoot.Create(prefix);
    }

    /// <summary>Absolute path of the created directory.</summary>
    public string Path { get; }

    /// <summary>A path below this workspace, with the parent directories created.</summary>
    public string Directory(params string[] segments)
    {
        var path = Combine(segments);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A path below this workspace, without creating anything.</summary>
    public string Combine(params string[] segments)
        => System.IO.Path.Combine([Path, .. segments]);

    public void Dispose() => TestTempRoot.TryDelete(Path);

    public override string ToString() => Path;
}
