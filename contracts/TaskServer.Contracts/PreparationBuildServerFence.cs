namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// AGT-3005: build-server fence for the repository prepare script and every
/// other one-shot .NET command a run spawns.
///
/// <para>
/// On 29.09.2026 <c>agent-runner-01</c> carried 89 <c>MSBuild.dll</c> and
/// <c>VBCSCompiler</c> reuse nodes, 101 hours old and idle, and every
/// <c>dotnet restore --locked-mode</c> in the preparation sat in "Determining
/// projects to restore..." until <c>prepare:timeout</c>. Killing the nodes made
/// the same restore finish in four seconds. The preparation clears its process
/// environment before copying a safe host subset, so the worker's own fence
/// (<c>WorkerBuildServerHygiene</c> in the runner) never reached it.
/// </para>
///
/// <para>
/// Both values are the "no server" spelling: <c>1</c> disables node reuse,
/// <c>0</c> disables the MSBuild server. The merge gate uses the same pair
/// (AGT-2820).
/// </para>
/// </summary>
public static class PreparationBuildServerFence
{
    public const string DisableNodeReuse = "MSBUILDDISABLENODEREUSE";

    public const string DisableMsBuildServer = "DOTNET_CLI_USE_MSBUILD_SERVER";

    public static IReadOnlyDictionary<string, string> Variables { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DisableNodeReuse] = "1",
            [DisableMsBuildServer] = "0",
        };

    /// <summary>Applies the fence; it always overrides an existing value.</summary>
    public static IDictionary<string, string?> ApplyTo(IDictionary<string, string?> environment)
    {
        foreach (var (key, value) in Variables) environment[key] = value;
        return environment;
    }
}

/// <summary>
/// The restore phase a running preparation last reported, read from its output.
/// <c>determining-projects</c> with no later progress is MSBuild contention;
/// <c>package-download</c> is the network.
/// </summary>
public sealed class PreparationPhaseTracker
{
    public const string Starting = "starting";
    public const string DeterminingProjects = "determining-projects";
    public const string PackageDownload = "package-download";
    public const string Restored = "restored";
    public const string Other = "other";

    private const int LastLineLimit = 200;
    private readonly object _gate = new();
    private string _current = Starting;
    private string _lastLine = string.Empty;

    public string Current { get { lock (_gate) return _current; } }

    public string LastLine { get { lock (_gate) return _lastLine; } }

    public void Observe(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return;
        var classified = Classify(trimmed);
        lock (_gate)
        {
            _lastLine = trimmed.Length <= LastLineLimit ? trimmed : trimmed[..LastLineLimit];
            if (classified is not null) _current = classified;
        }
    }

    /// <summary>Pure classification of one output line, or null when it names no phase.</summary>
    public static string? Classify(string line)
    {
        if (line.Contains("Determining projects to restore", StringComparison.OrdinalIgnoreCase))
            return DeterminingProjects;
        if (line.StartsWith("GET http", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("OK http", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("CACHE http", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Installed ", StringComparison.Ordinal) && line.Contains(" from http", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Retrying 'FindPackagesById", StringComparison.Ordinal)
            || line.StartsWith("npm http", StringComparison.OrdinalIgnoreCase))
            return PackageDownload;
        if (line.StartsWith("Restored ", StringComparison.Ordinal)
            || line.Contains("All projects are up-to-date for restore", StringComparison.Ordinal))
            return Restored;
        return null;
    }
}
