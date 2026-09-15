namespace AgentRunner;

/// <summary>
/// Per-attempt build-server namespace for a fenced ReviewAttempt.
///
/// AGT-2831: the attempt workspace already roots every writable path under the
/// attempt directory (<c>HOME</c>, <c>TMPDIR</c>, package caches), but the .NET
/// build servers do not live under <c>TMPDIR</c> on Linux. Roslyn's
/// <c>VBCSCompiler</c> listens on <c>/tmp/&lt;pipename&gt;</c>, where the pipe
/// name is a hash of the compiler directory and the user name, and MSBuild's
/// reusable worker nodes listen on <c>/tmp/MSBuild&lt;pid&gt;</c>. One host, one
/// service account and one SDK therefore produce exactly one compiler server and
/// one pool of reusable nodes no matter how many attempts run concurrently.
///
/// Those servers also outlive the attempt that started them: they inherit that
/// attempt's working directory and keep it after the attempt root is deleted. A
/// later attempt connects to the same socket, the server answers from a deleted
/// tree, and the client blocks in a socket read - a <c>dotnet test</c> tree
/// burning no CPU while it holds a review slot until the command budget expires.
///
/// The fix is to give every attempt its own, server-free build namespace rather
/// than to share one: no reusable MSBuild nodes, no MSBuild server, no shared
/// compilation. Compilation then happens in-process in the node that the attempt
/// itself owns and dies with it.
/// </summary>
public static class ReviewBuildServerIsolation
{
    /// <summary>MSBuild worker nodes exit with the build instead of lingering for reuse.</summary>
    public const string DisableNodeReuse = "MSBUILDDISABLENODEREUSE";

    /// <summary>The dotnet CLI drives MSBuild in-process instead of through a long-lived server.</summary>
    public const string DisableMsBuildServer = "DOTNET_CLI_USE_MSBUILD_SERVER";

    /// <summary>
    /// Read by MSBuild as a global property. <c>false</c> makes CoreCompile invoke
    /// <c>csc</c> directly instead of dispatching to the shared VBCSCompiler socket.
    /// </summary>
    public const string DisableSharedCompilation = "UseSharedCompilation";

    /// <summary>Attempt-local sink for MSBuild's debug and crash files.</summary>
    public const string MsBuildDebugPath = "MSBUILDDEBUGPATH";

    /// <summary>
    /// The variables that fence one attempt's .NET build off from every other
    /// attempt on the host. <paramref name="tempPath"/> is the attempt-local temp
    /// directory the caller already created; it is the only namespaced value,
    /// because the remaining three remove a shared server rather than rename it.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Variables(string tempPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DisableNodeReuse] = "1",
            [DisableMsBuildServer] = "0",
            [DisableSharedCompilation] = "false",
            [MsBuildDebugPath] = tempPath,
        };
    }

    /// <summary>
    /// Applies <see cref="Variables"/> onto an environment the caller is still
    /// building. The isolation always wins: a review plan is immutable evidence
    /// and must never be able to re-enable a host-shared build server.
    /// </summary>
    public static IDictionary<string, string?> ApplyTo(
        IDictionary<string, string?> environment,
        string tempPath)
    {
        foreach (var (key, value) in Variables(tempPath)) environment[key] = value;
        return environment;
    }

    /// <summary>
    /// True when every isolation variable carries its required value. Used by the
    /// attempt's environment evidence so a report proves the fence held.
    /// </summary>
    public static bool IsIsolated(IReadOnlyDictionary<string, string?> environment)
        => environment.TryGetValue(DisableNodeReuse, out var reuse) && reuse == "1"
           && environment.TryGetValue(DisableMsBuildServer, out var server) && server == "0"
           && environment.TryGetValue(DisableSharedCompilation, out var shared)
           && string.Equals(shared, "false", StringComparison.OrdinalIgnoreCase);
}
